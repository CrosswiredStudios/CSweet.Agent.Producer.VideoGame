using System.Text.Json;
using CSweet.Agent.SDK;
using Xunit;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningReplyTests
{
    [Theory]
    [InlineData("complete", "Completed")]
    [InlineData("blocked", "Blocked")]
    [InlineData("wrong-key", "Blocked")]
    [InlineData("partial", "Blocked")]
    [InlineData("missing-context", "Blocked")]
    public async Task PlanningReplyDoesNotEnterPitchRefinement(string scenario, string expected)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director");
        var turns = new List<AgentCoordinationTurn> {
            new(Guid.NewGuid(), 0, self.OrganizationUserId, "Continue", "Plan", DateTimeOffset.UtcNow,
                new("video-game.production.planning-cycle.v1", "1.0", "cycle", 1, true, JsonSerializer.SerializeToElement(new {}), "digest")),
            new(Guid.NewGuid(), 1, target.OrganizationUserId, scenario == "blocked" ? "Blocked" : "Completed", "Need an engine decision", DateTimeOffset.UtcNow,
                scenario == "blocked" ? null : new("video-game.production.technical-delivery-proposal.v1", "1.0",
                    scenario == "wrong-key" ? "other" : "cycle", 1, scenario != "partial", JsonSerializer.SerializeToElement(new {}), "digest")) };
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 2, 2, "Planning", "Delivery", [], self, target, true, turns)
            { SourceKind = "Board", WorkContext = scenario == "missing-context" ? null :
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(request, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal(expected, result.Disposition);
        if (scenario == "blocked") Assert.Contains("Need an engine decision", result.Content);
        Assert.DoesNotContain("Pitch refinement", result.Content);
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("semantic", false)]
    [InlineData("active", false)]
    [InlineData("context-present", false)]
    public void ContextRecoveryOnlyReplacesTheKnownLegacyFailure(string scenario, bool expected)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director");
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            self, target, "Planning", "Delivery", [], scenario == "active" ? "Active" : "Blocked", 3, 3, null, false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), 1, target.OrganizationUserId, "Blocked", scenario == "semantic" ? "Need an engine decision" :
                "Planning requires the exact workstream and planning fingerprint.", DateTimeOffset.UtcNow)])
            { SourceKind = "Board", WorkContext = scenario == "context-present" ?
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) : null };
        Assert.Equal(expected, SpecialistAgent.NeedsPlanningContextRecovery(session));
    }
}

