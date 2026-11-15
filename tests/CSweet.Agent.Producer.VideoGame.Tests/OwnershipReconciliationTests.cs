using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Fact]
    public async Task AChangedPlanningContractReleasesOnlyTheMatchingBlockOnceAndDoesNotLoopOnAnotherFailure()
    {
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var source = new PersonalTodoWorkContext(WorkstreamId: Guid.NewGuid(), TeamId: Guid.NewGuid(), SourceFingerprint: "approved");
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Revision = 4,
            CorrelationId = "producer-planning:contract", WorkContext = source, Status = PersonalTodoStatuses.Blocked,
            BlockReason = SpecialistAgent.PlanningDecisionBlockMarker, UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var requeues = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active", r.SourceRevisions,
                    [], "", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory(
                [new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [item])])))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (_, _) => {
                requeues++; item = item with { Revision = item.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
                return Task.FromResult(item); // Simulate another failure after the one permitted reassessment.
            });
        var context = runtime.CreateContext();
        var changed = await SpecialistAgent.PlanningContractChangedAtAsync(source.WorkstreamId!.Value, "approved", context, default);
        await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Plan", "Plan", "High", source with { SourceFingerprint = "other" }, context, default, dependencyChangedAt: changed);
        Assert.Equal(0, requeues);
        for (int n = 0; n < 3; n++)
        {
            Assert.Equal(changed, await SpecialistAgent.PlanningContractChangedAtAsync(source.WorkstreamId.Value, "approved", context, default));
            await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Plan", "Plan", "High", source, context, default, dependencyChangedAt: changed);
        }
        Assert.Equal(1, requeues); Assert.Single(states);
    }

    [Fact]
    public async Task ReconciliationReplaysOneCommissionBindsAdviceToAcceptedSourcesAndRetiresResolvedFindings()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var state = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId,
            f.Cycle.PlanningFingerprint, null, null, null, null, f.Artifact.Digest, null, DateTimeOffset.UtcNow);
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff",
            Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var identity = new AgentIdentity(f.Person.OrganizationUserId.ToString(), "Gabriel", null, null, null, [], null, null, null);
        AgentCoordinationSession? session = null; var starts = 0;
        f.Runtime.RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, (r, _) => {
            starts++;
            session = f.Session with { Id = Guid.NewGuid(), Status = AgentCoordinationStatuses.Active,
                Turns = [new(Guid.NewGuid(), 1, f.Person.OrganizationUserId, "Reconcile", "Complete", DateTimeOffset.UtcNow,
                    new(r.Artifact!.Type, r.Artifact.SchemaVersion, r.Artifact.Key, r.Artifact.PageOrdinal, r.Artifact.IsFinalPage, r.Artifact.Payload, "request"))] };
            return Task.FromResult(session);
        }).RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(session!))
          .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => {
              Assert.Contains("No user decision is requested", r.Description);
              Assert.Contains("Accountable: Victor", r.Description);
              return Task.FromResult(JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid() });
          });
        var review = new SpecialistAgent.OwnershipReview([], [new("Conditional escalation responsibility", "Confirm the existing owner.", ["final"])]);
        var context = f.Runtime.CreateContext(identity: identity);
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, review, f.Outline, f.Member, handoff, context, default);
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, review, f.Outline, f.Member, handoff, context, default);
        Assert.Equal(1, starts); Assert.Single(state.PlanningReconciliations); f.Runtime.AssertCollaborationsTracked();
        var reference = state.PlanningReconciliations[0];
        session = session! with { Status = AgentCoordinationStatuses.Completed, Turns = [.. session!.Turns,
            new(Guid.NewGuid(), 2, f.Person.OrganizationUserId, "Advice", "Complete", DateTimeOffset.UtcNow,
                new(WorkContributions.AnswerType, "1.0", reference.Key, 1, true,
                    JsonSerializer.SerializeToElement(new WorkContributionAnswer("Existing constraint owns it", "Victor evaluates feasibility and escalates an evidenced conflict.", "Measure only when the build exists.")), "advice"))] };
        var advice = await SpecialistAgent.ReadOwnershipContributionsAsync(state, f.Evidence, handoff, context, default);
        Assert.Equal("Responsibility", Assert.Single(advice).Kind); Assert.Equal(new[] { "final" }, advice[0].ScopeKeys);
        Assert.Empty(await SpecialistAgent.ReadOwnershipContributionsAsync(state, f.Evidence, handoff with { AcceptedRevisionId = Guid.NewGuid() }, context, default));
        Assert.Empty(await SpecialistAgent.ReadOwnershipContributionsAsync(state, f.Evidence with { OutlineDigest = "superseded" }, handoff, context, default));
        DecisionRequest? followUp = null;
        f.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
                followUp = r; return Task.FromResult(JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Id = Guid.NewGuid() });
            });
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, review, f.Outline, f.Member, handoff, context, default);
        Assert.Contains(advice[0].Text, followUp!.Summary); Assert.Equal("work-planning", followUp.AuthorityRuleKey);
        Assert.Equal(1, starts);
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, new([], []), f.Outline, f.Member, handoff, context, default);
        Assert.Empty(state.OwnershipHeldProposalKeys); Assert.False(state.PlanningReconciliations[0].Active);
        Assert.Single(await SpecialistAgent.ReadOwnershipContributionsAsync(state, f.Evidence, handoff, context, default));
        var portfolio = new ProducerOperatingState { PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState> { [state.WorkstreamId] = state } };
        f.Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "portfolio", 1, "Active",
                new Dictionary<string, string>(), [], "", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(portfolio), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
        session = session! with { Status = "Blocked" }; // Obsolete failures must not remain current issues.
        var card = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Title = "Receive planning reconciliation",
            CorrelationId = TrackedCollaboration.Correlation(session.Id), WorkContext = new(BoardId: state.BoardId) };
        Assert.Equal(PersonalTodoResult.Completed("The planning finding was resolved or superseded; its advice remains in the audit history."),
            await new SpecialistAgent().HandlePersonalTodoAsync(card, context, default));
    }

    [Fact]
    public async Task EvidencedConflictsGoToDelegatedManagementWithSpecificImpactAndIdempotentChoices()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var state = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId,
            f.Cycle.PlanningFingerprint, null, null, null, null, f.Artifact.Digest, null, DateTimeOffset.UtcNow);
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff",
            Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var requirements = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["final"]),
            new("two", "Decision", "Provided audio is forbidden.", ["final"]) };
        var review = new SpecialistAgent.OwnershipReview([], [new("Resolve incompatible audio requirements", "Both requirements cannot hold.", ["final"]) { ConflictEvidence = requirements }]);
        var requests = new Dictionary<string, DecisionRequest>();
        f.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
                Assert.Equal("work-planning", r.AuthorityRuleKey); Assert.Contains("Affected work:", r.Summary);
                Assert.Contains("No user decision is requested", r.Summary);
                Assert.All(requirements, e => Assert.Contains(e.Text, r.Summary));
                Assert.Equal(new[] { "provide-direction", "continue-current-plan", "request-more-evidence" }, r.Options.Select(o => o.Id));
                requests[r.IdempotencyKey] = r;
                return Task.FromResult(JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Id = Guid.NewGuid() });
            });
        for (int n = 0; n < 3; n++)
            state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, review, f.Outline, f.Member, handoff, f.Runtime.CreateContext(), default);
        Assert.Single(requests); Assert.Empty(f.Starts); Assert.Equal(new[] { "final" }, state.OwnershipHeldProposalKeys);
    }

    [Fact]
    public void BoardHoldsCoverAffectedDescendantsAndConsumersWithoutHoldingPrerequisitesOrUnrelatedWork()
    {
        var root = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = Guid.NewGuid(),
            ProposalProvenance = new(Guid.NewGuid(), "current", "affected") };
        var child = root with { Id = Guid.NewGuid(), ParentItemId = root.Id, ProposalProvenance = null };
        var consumer = root with { Id = Guid.NewGuid(), ProposalProvenance = null,
            Planning = new([], []) { DependencyItemIds = [child.Id] } };
        var independent = root with { Id = Guid.NewGuid(), ProposalProvenance = null,
            Planning = new([], []) { DependencyItemIds = [Guid.NewGuid()] } };
        var state = new ProducerPlanningCycleState(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "plan", null, null, null, null, "current", null, DateTimeOffset.UtcNow)
            { OwnershipHeldProposalKeys = ["affected"] };
        var held = SpecialistAgent.OwnershipHeldItems(state, [root, child, consumer, independent]);
        Assert.Equal(new[] { root.Id, child.Id, consumer.Id }.Order(), held.Order());
        state = state with { ConfirmedHoldItems = held };
        Assert.True(SpecialistAgent.CanPrepareWithPlanningDecisions(independent, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(child, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(consumer, state));
        Assert.Empty(SpecialistAgent.OwnershipHeldItems(state with { TechnicalProposalDigest = "obsolete" }, [root, child, consumer, independent]));
        Assert.Empty(SpecialistAgent.OwnershipHeldItems(state with { OwnershipHeldProposalKeys = [] }, [root, child, consumer, independent]));
    }
}
