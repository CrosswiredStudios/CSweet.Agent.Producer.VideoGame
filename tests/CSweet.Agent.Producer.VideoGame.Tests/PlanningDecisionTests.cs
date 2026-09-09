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
            Assert.Equal("material-management-direction", request.AuthorityRuleKey);
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
        var revised = SpecialistAgent.PlanningDecisionRequests(stream, board, "new-cycle", ["Choose browser targets"], handoff);
        Assert.DoesNotContain(revised[0].IdempotencyKey, first.Select(x => x.IdempotencyKey));
    }

    [Fact]
    public void NoQuestionsCreateNoDecision()
    {
        var handoff = new ProducerAcceptedHandoff(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sha",
            "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        Assert.Empty(SpecialistAgent.PlanningDecisionRequests(handoff.WorkstreamId, Guid.NewGuid(), "cycle", [], handoff));
    }
}
