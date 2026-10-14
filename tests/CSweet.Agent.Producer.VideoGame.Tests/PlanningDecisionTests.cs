using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningDecisionTests
{
    [Fact]
    public void BlockerReportsExactEvidenceRequestAndExcludesResolvedOrForeignQuestions()
    {
        var stream = Guid.NewGuid();
        var playtest = PlanningDecision(stream, "Confirm channel, scheduling window and budget for n>=10 playtesters.");
        var audio = PlanningDecision(stream, "Confirm audio contractor.") with { SelectedOptionId = "continue-current-plan" };
        var foreign = playtest with { Id = Guid.NewGuid(), WorkstreamId = Guid.NewGuid(), Rationale = "FOREIGN" };
        var stale = playtest with { Id = Guid.NewGuid(), Evidence = [] , Rationale = "STALE" };
        var superseded = playtest with { Id = Guid.NewGuid(), SupersededByDecisionId = playtest.Id, Rationale = "SUPERSEDED" };
        var decisions = new[] { playtest, audio, foreign, stale, superseded };
        var questions = SpecialistAgent.UnresolvedPlanningQuestions([playtest.Summary, audio.Summary], decisions, stream, "accepted-sha");
        var blocker = SpecialistAgent.DescribePlanningBlocker(questions, decisions, stream, "accepted-sha", "Gabriel Reyes");
        Assert.Contains(playtest.Summary, blocker);
        Assert.Contains(playtest.Id.ToString("D"), blocker);
        Assert.Contains("Decided; selected option: request-more-evidence", blocker);
        Assert.Contains(playtest.Rationale!, blocker);
        Assert.Contains("Production follow-up: Gabriel Reyes", blocker);
        Assert.Contains("collect the evidence", blocker);
        Assert.Contains("only if the answer changes accepted scope", blocker);
        Assert.DoesNotContain(audio.Summary, blocker);
        Assert.DoesNotContain("FOREIGN", blocker);
        Assert.DoesNotContain("STALE", blocker);
        Assert.DoesNotContain("SUPERSEDED", blocker);
    }

    [Theory]
    [InlineData("unchanged", 0)]
    [InlineData("decision-changed", 1)]
    [InlineData("missing-decision", 1)]
    [InlineData("foreign-decision", 0)]
    [InlineData("changed-scope", 1)]
    [InlineData("legacy-state", 1)]
    public async Task ReviewsRetainUnchangedDecisionBlocksAndRecoverOnCurrentEvidence(string scenario, int expectedRequeues)
    {
        var stream = Guid.NewGuid(); var team = Guid.NewGuid(); var board = Guid.NewGuid();
        var decision = PlanningDecision(stream, "Confirm external playtest logistics.");
        string[] questions = [decision.Summary];
        var cycle = new ProducerPlanningCycleState(stream, board, team, "scope", Guid.NewGuid(), null, null,
            null, null, "reconciled", DateTimeOffset.UtcNow) {
            OutstandingAuthorityQuestions = questions,
            AuthorityDecisionFingerprint = scenario == "legacy-state" ? null :
                SpecialistAgent.PlanningDecisionFingerprint(questions, [decision], stream, "accepted-sha")
        };
        var state = new ProducerOperatingState {
            PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState> { [stream] = cycle },
            AcceptedHandoffs = new Dictionary<Guid, ProducerAcceptedHandoff> {
                [stream] = new(stream, Guid.NewGuid(), Guid.NewGuid(), "accepted-sha", "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow)
            }
        };
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with {
            Id = Guid.NewGuid(), Revision = 4, Status = PersonalTodoStatuses.Blocked,
            CorrelationId = "producer-planning:scope",
            BlockReason = "Repeated issue: " + SpecialistAgent.DescribePlanningBlocker(questions, [decision], stream, "accepted-sha", "Gabriel"),
            WorkContext = new(WorkstreamId: stream, TeamId: team, SourceFingerprint: "scope")
        };
        IReadOnlyList<DecisionRecord> current = scenario switch {
            "decision-changed" => [decision with { Revision = 3, SelectedOptionId = "continue-current-plan" }],
            "missing-decision" => [],
            "foreign-decision" => [decision, decision with { Id = Guid.NewGuid(), WorkstreamId = Guid.NewGuid() }],
            _ => [decision]
        };
        var requeues = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [item])])))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (_, _) =>
                Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), "producer", "producer", 1, "Active", new Dictionary<string, string>(), [], "state", [], Guid.Empty,
                    JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (request, _) => {
                Assert.Equal(stream, request.WorkstreamId);
                return Task.FromResult(current);
            })
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (request, _) => {
                Assert.Equal(item.Revision, request.ExpectedRevision);
                requeues++;
                item = item with { Status = PersonalTodoStatuses.Ready, Revision = item.Revision + 1 };
                return Task.FromResult(item);
            });
        for (var i = 0; i < 3; i++)
            await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Plan", "Plan", "High",
                item.WorkContext! with { SourceFingerprint = scenario == "changed-scope" ? "new-scope" : "scope" }, runtime.CreateContext(), default);
        Assert.Equal(expectedRequeues, requeues);
    }

    private static DecisionRecord PlanningDecision(Guid stream, string question) => new(
        Guid.NewGuid(), stream, "video-game.management-direction.v1", question, "work-planning", [], "provide-direction",
        "request-more-evidence", DecisionStatuses.Decided,
        "Producer must confirm recruitment channel, scheduling window and compensation budget. Only external playtesting is gated; prototype, content and QA may proceed.",
        [new("artifact", Guid.NewGuid(), Guid.NewGuid(), "accepted-sha", "video-game.production-plan.v1", "Accepted")],
        null, null, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void DistinctQuestionsGetStableDecisionsWithExactAcceptedEvidence()
    {
        var stream = Guid.NewGuid();
        var board = Guid.NewGuid();
        var handoff = new ProducerAcceptedHandoff(stream, Guid.NewGuid(), Guid.NewGuid(), "accepted-sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 2, DateTimeOffset.UtcNow);
        var first = SpecialistAgent.PlanningDecisionRequests(stream, board, "cycle",
            ["Choose browser targets", "Set playtest criteria", "Choose browser targets", " "], handoff);
        var repeat = SpecialistAgent.PlanningDecisionRequests(stream, board, "cycle",
            ["Set playtest criteria", " Choose browser targets "], handoff);
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(x => x.IdempotencyKey).Order(), repeat.Select(x => x.IdempotencyKey).Order());
        Assert.Equal(2, first.Select(x => x.IdempotencyKey).Distinct().Count());
        foreach (var request in first)
        {
            Assert.Equal(stream, request.WorkstreamId);
            Assert.Equal("work-planning", request.AuthorityRuleKey);
            Assert.Equal("provide-direction", request.RecommendedOptionId);
            Assert.Null(request.DueAt);
            Assert.Null(request.SupersedesDecisionId);
            var evidence = Assert.Single(request.Evidence);
            Assert.Equal(handoff.ArtifactId, evidence.ResourceId);
            Assert.Equal(handoff.AcceptedRevisionId, evidence.RevisionId);
            Assert.Equal(handoff.RevisionDigest, evidence.Digest);
            Assert.Equal("video-game.production-plan.v1", evidence.TypeKey);
            Assert.Equal(board, request.TypeData!.Value.GetProperty("boardId").GetGuid());
            Assert.Equal(handoff.PlanningPackageId, request.TypeData.Value.GetProperty("planningPackageId").GetGuid());
            Assert.Contains(request.Options, x => x.Id == "request-more-evidence");
            Assert.InRange(request.IdempotencyKey.Length, 1, 160);
        }
        var nextCycle = SpecialistAgent.PlanningDecisionRequests(stream, board, "new-cycle", ["Choose browser targets"], handoff);
        Assert.Contains(nextCycle[0].IdempotencyKey, first.Select(x => x.IdempotencyKey));
        var revisedBrief = SpecialistAgent.PlanningDecisionRequests(stream, board, "new-cycle", ["Choose browser targets"],
            handoff with { RevisionDigest = "new-accepted-sha" });
        Assert.DoesNotContain(revisedBrief[0].IdempotencyKey, first.Select(x => x.IdempotencyKey));
    }

    [Fact]
    public async Task Planning_decisions_progress_without_a_second_direct_chat_turn()
    {
        var workstream = Guid.NewGuid();
        var board = Guid.NewGuid();
        var handoff = new ProducerAcceptedHandoff(workstream, Guid.NewGuid(), Guid.NewGuid(), "accepted-sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var requested = new List<DecisionRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest,
                (request, _) =>
                {
                    requested.Add(request);
                    var now = DateTimeOffset.UtcNow;
                    return Task.FromResult(new DecisionRecord(Guid.NewGuid(), workstream, request.TypeKey,
                        request.Summary, request.AuthorityRuleKey, request.Options,
                        request.RecommendedOptionId, null, DecisionStatuses.Pending, null,
                        request.Evidence, null, null, null, 1, now, now));
                });
        var recorded = await SpecialistAgent.EnsurePlanningDecisionsAsync(workstream, board, "cycle",
            ["Choose browser targets", "Choose browser targets", "Set QA thresholds"], handoff,
            runtime.CreateContext(), default);
        Assert.Equal(2, requested.Count);
        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, x => {
            var blocker = SpecialistAgent.DescribePlanningBlocker([x.Summary], recorded, workstream, "accepted-sha", "Producer");
            Assert.Contains(x.Id.ToString("D"), blocker);
            Assert.Contains("Pending; selected option: none", blocker);
        });
    }

    [Fact]
    public async Task LegacyOwnerQuestionIsSupersededByManagerPlanningDecision()
    {
        var workstream = Guid.NewGuid();
        var handoff = new ProducerAcceptedHandoff(workstream, Guid.NewGuid(), Guid.NewGuid(), "accepted-sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var old = new DecisionRecord(Guid.NewGuid(), workstream, "video-game.management-direction.v1",
            "Choose browser targets", "material-management-direction", [], "provide-direction", null,
            DecisionStatuses.Pending, null, [], null, null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        DecisionRequest? submitted = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([old]))
            .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest,
                (request, _) => { submitted = request; return Task.FromResult(old); });
        await SpecialistAgent.EnsurePlanningDecisionsAsync(workstream, Guid.NewGuid(), "cycle",
            [old.Summary], handoff, runtime.CreateContext(), default);
        Assert.NotNull(submitted);
        Assert.Equal("work-planning", submitted.AuthorityRuleKey);
        Assert.Equal(old.Id, submitted.SupersedesDecisionId);
    }

    [Fact]
    public void OnlyDecidedManagerDirectionForCurrentAcceptedBriefReopensPlanning()
    {
        var workstream = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        DecisionRecord Direction(string status, string option, string digest) => new(
            Guid.NewGuid(), workstream, "video-game.management-direction.v1", "Choose browser targets",
            "work-planning", [], "provide-direction", option, status, "Target desktop browsers first.",
            [new EvidenceReference("artifact", Guid.NewGuid(), Guid.NewGuid(), digest,
                "video-game.production-plan.v1", "Accepted")], null, null, null, 2, now, now);
        var current = Direction(DecisionStatuses.Decided, "provide-direction", "accepted-sha");
        var selected = SpecialistAgent.ResolvedPlanningDirections([
            current, Direction(DecisionStatuses.Pending, "provide-direction", "accepted-sha"),
            Direction(DecisionStatuses.Decided, "request-more-evidence", "accepted-sha"),
            Direction(DecisionStatuses.Decided, "provide-direction", "old-sha")
        ], workstream, "accepted-sha");
        Assert.Single(selected);
        Assert.Contains(current.Id.ToString("D"), selected[0]);
        Assert.Contains("Target desktop browsers first", selected[0]);
    }

    [Theory]
    [InlineData("accepted-sha", "continue-current-plan", "Decided", "Choose browser targets", true)]
    [InlineData("old-sha", "continue-current-plan", "Decided", "Choose browser targets", false)]
    [InlineData("accepted-sha", "request-more-evidence", "Decided", "Choose browser targets", false)]
    [InlineData("accepted-sha", "continue-current-plan", "Pending", "Choose browser targets", false)]
    [InlineData("accepted-sha", "continue-current-plan", "Decided", "Unrelated question", false)]
    public void ContinuationClosesOnlyItsExactAcceptedQuestionWithoutReplanning(string digest, string option,
        string status, string question, bool resolved)
    {
        var stream = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var decision = new DecisionRecord(Guid.NewGuid(), stream, "video-game.management-direction.v1", question,
            "work-planning", [], "provide-direction", option, status, "Use the accepted baseline.",
            [new EvidenceReference("artifact", Guid.NewGuid(), Guid.NewGuid(), digest, "video-game.production-plan.v1", "Accepted")],
            null, null, null, 1, now, now);
        var questions = SpecialistAgent.UnresolvedPlanningQuestions(["Choose browser targets"], [decision], stream, "accepted-sha");
        Assert.Equal(resolved ? 0 : 1, questions.Count);
        Assert.Empty(SpecialistAgent.ResolvedPlanningDirections([decision], stream, "accepted-sha"));
    }

    [Fact]
    public void NoQuestionsCreateNoDecision()
    {
        var handoff = new ProducerAcceptedHandoff(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        Assert.Empty(SpecialistAgent.PlanningDecisionRequests(handoff.WorkstreamId, Guid.NewGuid(), "cycle", [], handoff));
    }
}
