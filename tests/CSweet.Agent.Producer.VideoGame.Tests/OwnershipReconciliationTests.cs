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
    public async Task RewordedFindingsShareOneCommissionAndAdviceNeedsAnOwnedResolutionBeforeRelease()
    {
        var f = new Fixture(adaptive: true, independent: true); var producer = Guid.NewGuid();
        var registry = new IssueRegistry(f.Runtime);
        var state = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId,
            f.Cycle.PlanningFingerprint, null, null, null, null, f.Artifact.Digest, null, DateTimeOffset.UtcNow);
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var legacy = new PlanningReconciliationReference("legacy", Guid.NewGuid(), Guid.Parse(f.Member.EmployeeId), f.Evidence.OutlineDigest, ["final"]);
        state = state with { PlanningReconciliations = [legacy] };
        var review = new SpecialistAgent.OwnershipReview([], [new("Conditional escalation responsibility", "Confirm the owner.", ["final"]) { ObligationKey = "escalation" }]);
        var context = f.Runtime.CreateContext(identity: new AgentIdentity(producer.ToString(), "Gabriel", null, null, null, [], null, null, null));
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, review, f.Outline, f.Member, handoff, context, default);
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence,
            review with { Gaps = [review.Gaps[0] with { Statement = "Name who handles conditional escalation" }] }, f.Outline, f.Member, handoff, context, default);
        Assert.Equal(1, registry.CommissionCalls); Assert.Single(registry.Issues);
        Assert.Equal(new[] { legacy.SessionId }, registry.LastCommission!.SupersededSessionIds);
        Assert.False(state.PlanningReconciliations[0].Active); Assert.Equal(new[] { "final" }, state.OwnershipHeldProposalKeys);
        Assert.DoesNotContain("foundation", state.OwnershipHeldProposalKeys);
        var commissioned = registry.Commissions.Single();
        registry.Commissions[0] = commissioned with { Status = "AdviceReceived" };
        var request = new WorkContributionRequest(state.WorkstreamId, state.BoardId, "issue-commission:" + commissioned.Id.ToString("N"), "Question", [new(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)]);
        var session = f.Session with { Id = commissioned.SessionId!.Value, Status = AgentCoordinationStatuses.Completed,
            Initiator = f.Session.Initiator with { OrganizationUserId = producer },
            Target = f.Session.Target with { OrganizationUserId = Guid.Parse(f.Member.EmployeeId) },
            BoardSource = new(state.BoardId), Turns = [
                new(Guid.NewGuid(), 1, producer, "Request", "Continue", DateTimeOffset.UtcNow,
                    new(WorkContributions.RequestType, "1.0", request.ScopeKey, 1, true, JsonSerializer.SerializeToElement(request), "request")),
                new(Guid.NewGuid(), 2, Guid.Parse(f.Member.EmployeeId), "Completed", "Advice", DateTimeOffset.UtcNow,
                    new(WorkContributions.AnswerType, "1.0", request.ScopeKey, 1, true, JsonSerializer.SerializeToElement(new WorkContributionAnswer("Existing owner", "Victor owns evidenced escalation.", "Measure when available.")), "advice")) ] };
        f.Runtime.RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(session));
        f.Runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(
            new WorkBoardDetail(new(state.BoardId, "Game", "", false, false, 1, []) { WorkstreamId = state.WorkstreamId, ManagerOrganizationUserId = producer }, [], [])))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(
                new ArtifactDocument(handoff.ArtifactId, "Brief", "scope", "Accepted", handoff.AcceptedRevisionId, null, handoff.AcceptedRevisionId, null, null,
                    [new(handoff.AcceptedRevisionId, 1, null, "Approved scope", handoff.RevisionDigest, "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = state.WorkstreamId }));
        // Old and new references remain readable, but only an exact accepted source can supply current evidence.
        var advice = await SpecialistAgent.ReadOwnershipContributionsAsync(state with { PlanningReconciliations = [] }, f.Evidence, handoff, context, default);
        Assert.Single(advice);
        // The specialist's valid answer survived the old Producer's misrouted finalization.
        session = session with { Turns = [.. session.Turns, new(Guid.NewGuid(), 3, producer, "Blocked",
            "Pitch refinement requires the exact pitch, accepted GDD, project/team context and Video Game Director manager.", DateTimeOffset.UtcNow)] };
        Assert.Single(await SpecialistAgent.ReadOwnershipContributionsAsync(state, f.Evidence, handoff, context, default));
        Assert.Empty(await SpecialistAgent.ReadOwnershipContributionsAsync(state with { PlanningReconciliations = [] }, f.Evidence, handoff with { AcceptedRevisionId = Guid.NewGuid() }, context, default));
        Assert.Equal("Open", registry.Issues.Single().Status); // Advice is not a resolution receipt.
        var owned = new SpecialistAgent.OwnershipReview([new("Conditional escalation responsibility", ["approved"],
            [new("final", "Named escalation responsibility")]) { ObligationKey = "escalation" }], []);
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, owned, f.Outline, f.Member, handoff, context, default);
        Assert.Equal("Resolved", registry.Issues.Single().Status); Assert.Empty(state.OwnershipHeldProposalKeys);
        Assert.Equal(1, registry.CommissionCalls);
    }

    [Fact]
    public async Task ExistingOwnedResponsibilityRetiresLegacyTrackingWithoutCommissioningAnotherConsultation()
    {
        var f = new Fixture(adaptive: true, independent: true); var registry = new IssueRegistry(f.Runtime);
        var state = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId,
            f.Cycle.PlanningFingerprint, null, null, null, null, f.Artifact.Digest, null, DateTimeOffset.UtcNow);
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var legacy = new PlanningReconciliationReference("old-finding", Guid.NewGuid(), Guid.Parse(f.Member.EmployeeId), f.Evidence.OutlineDigest, ["final"]);
        state = state with { PlanningReconciliations = [legacy] };
        var original = new WorkContributionRequest(state.WorkstreamId, state.BoardId, legacy.Key, "Confirm who owns escalation", [new(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)]);
        var session = f.Session with { Id = legacy.SessionId, Status = AgentCoordinationStatuses.Failed, BoardSource = new(state.BoardId),
            Initiator = f.Session.Initiator with { OrganizationUserId = f.Person.OrganizationUserId }, Target = f.Session.Target with { OrganizationUserId = legacy.SpecialistId },
            Turns = [new(Guid.NewGuid(), 1, f.Person.OrganizationUserId, "Request", "Continue", DateTimeOffset.UtcNow,
                new(WorkContributions.RequestType, "1.0", legacy.Key, 1, true, JsonSerializer.SerializeToElement(original), "request"))] };
        f.Runtime.RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(session));
        var owned = new SpecialistAgent.OwnershipReview([new("Escalation responsibility", ["approved"], [new("final", "The Technical Director owns escalation")]) { ObligationKey = "escalation" }], []);
        var context = f.Runtime.CreateContext(identity: new AgentIdentity(f.Person.OrganizationUserId.ToString(), "Gabriel", null, null, null, [], null, null, null));
        state = await SpecialistAgent.ReconcileOwnershipFindingsAsync(f.Cycle, state, f.Evidence, owned, f.Outline, f.Member, handoff, context, default);
        Assert.False(state.PlanningReconciliations[0].Active); Assert.Empty(state.OwnershipHeldProposalKeys);
        Assert.Equal(0, registry.CommissionCalls); Assert.Equal("Resolved", Assert.Single(registry.Issues).Status);
        Assert.Equal(AgentCoordinationStatuses.Failed, session.Status); // Failure history is not rewritten as success.
    }

    [Fact]
    public async Task EvidencedConflictsGoToDelegatedManagementWithSpecificImpactAndIdempotentChoices()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var registry = new IssueRegistry(f.Runtime);
        var state = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId,
            f.Cycle.PlanningFingerprint, null, null, null, null, f.Artifact.Digest, null, DateTimeOffset.UtcNow);
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff",
            Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var requirements = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["final"]),
            new("two", "Decision", "Provided audio is forbidden.", ["final"]) };
        var review = new SpecialistAgent.OwnershipReview([], [new("Resolve incompatible audio requirements", "Both requirements cannot hold.", ["final"]) { ConflictEvidence = requirements, ObligationKey = "audio-conflict" }]);
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
    private sealed class IssueRegistry
    {
        public List<WorkIssueCaseResponse> Issues { get; } = [];
        public List<WorkIssueCommissionResponse> Commissions { get; } = [];
        public int CommissionCalls { get; private set; }
        public CommissionWorkIssuesRequest? LastCommission { get; private set; }
        public IssueRegistry(AgentTestRuntime runtime)
        {
            runtime.RegisterCapability<ReadWorkIssuesRequest, WorkIssueDirectory>(WorkIssueCapabilities.Read,
                (_, _) => Task.FromResult(new WorkIssueDirectory(Issues.ToArray(), Commissions.ToArray())))
            .RegisterCapability<ReconcileWorkIssuesRequest, WorkIssueDirectory>(WorkIssueCapabilities.Reconcile, (r, _) => {
                foreach (var update in r.Issues) {
                    var prior = Issues.SingleOrDefault(i => i.ObligationKey == update.ObligationKey);
                    if (prior is not null) Assert.Equal(prior.Revision, update.ExpectedRevision);
                    var next = new WorkIssueCaseResponse(prior?.Id ?? Guid.NewGuid(), r.BoardId, Guid.NewGuid(), update.ObligationKey,
                        update.Statement, update.Detail, update.Status, update.Phase, Guid.NewGuid(), update.NextAction,
                        r.Source, update.ScopeKeys, (prior?.Links ?? []).Concat(update.Links).Distinct().ToArray(), update.ResolutionEvidence, (prior?.Revision ?? 0) + 1, DateTimeOffset.UtcNow);
                    Issues.RemoveAll(i => i.ObligationKey == update.ObligationKey); Issues.Add(next);
                }
                return Task.FromResult(new WorkIssueDirectory(Issues.ToArray(), Commissions.ToArray()));
            }).RegisterCapability<CommissionWorkIssuesRequest, WorkIssueCommissionResponse>(WorkIssueCapabilities.Commission, (r, _) => {
                CommissionCalls++; LastCommission = r;
                var row = new WorkIssueCommissionResponse(Guid.NewGuid(), r.BoardId, r.TargetEmployeeId, r.IssueIds, r.Source, "Dispatched", Guid.NewGuid(), null, 1);
                Commissions.Add(row); return Task.FromResult(row);
            });
        }
    }

}
