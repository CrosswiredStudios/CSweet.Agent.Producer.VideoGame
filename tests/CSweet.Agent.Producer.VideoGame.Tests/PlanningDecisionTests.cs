using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningDecisionTests
{
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
        await SpecialistAgent.EnsurePlanningDecisionsAsync(workstream, board, "cycle",
            ["Choose browser targets", "Choose browser targets", "Set QA thresholds"], handoff,
            runtime.CreateContext(), default);
        Assert.Equal(2, requested.Count);
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

    [Fact]
    public void NoQuestionsCreateNoDecision()
    {
        var handoff = new ProducerAcceptedHandoff(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        Assert.Empty(SpecialistAgent.PlanningDecisionRequests(handoff.WorkstreamId, Guid.NewGuid(), "cycle", [], handoff));
    }
}
