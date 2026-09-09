using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class OwnEstimateReviewTests
{
    private static readonly AgentCoordinationParticipant Producer = new(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
    private static readonly AgentCoordinationParticipant Qa = new(Guid.NewGuid(), Guid.NewGuid(), "QA", "QA");
    private static GameRoleEstimateCapacityRequestV1 Scope() => new(Guid.NewGuid(), VideoGameRoleKeys.Producer,
        3, "planning", [new(Guid.NewGuid(), "Decision log", VideoGameRoleKeys.Producer, ["Record decisions"],
            ["Accepted decisions have evidence"], [], [], "package", "assignment", null, null)], "fingerprint");
    private static AgentCoordinationTurn Turn(AgentCoordinationParticipant who, string type, object payload) =>
        new(Guid.NewGuid(), 0, who.OrganizationUserId, "Continue", "Estimate", DateTimeOffset.UtcNow,
            new(type, "1.0", "fingerprint", 1, true, JsonSerializer.SerializeToElement(payload), "digest"));
    private static AgentCoordinationTurnRequest Request(bool qa, params AgentCoordinationTurn[] turns) =>
        new(Guid.NewGuid(), turns.Length, turns.Length, "Own estimates", "Review scope", [], qa ? Qa : Producer,
            qa ? Producer : Qa, false, turns) { SourceKind = "Board",
            WorkContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
    [Fact]
    public async Task ProducerAuthorsProposalAndContinuesForIndependentReview()
    {
        var scope = Scope();
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(Request(false,
            Turn(Producer, "video-game.production.role-estimate-request.v1", scope),
            Turn(Qa, "video-game.production.role-estimate-request.v1", scope)),
            new AgentTestRuntime().CreateContext(), default);
        Assert.Equal("Continue", result.Disposition);
        var proposal = result.Artifact!.Payload.Deserialize<GameRoleEstimateCapacityProposalV1>()!;
        Assert.Equal(VideoGameRoleKeys.Producer, proposal.RoleKey);
        Assert.Equal(scope.WorkItems[0].WorkItemId, Assert.Single(proposal.Estimates).WorkItemId);
        Assert.True(proposal.Estimates[0].EstimatePoints > 0);
    }

    [Fact]
    public async Task FinalReviewAcknowledgementDoesNotRegenerateEstimates()
    {
        var scope = Scope();
        var proposal = new GameRoleEstimateCapacityProposalV1(scope.BoardId, scope.RoleKey, 3, "planning",
            [new(scope.WorkItems[0].WorkItemId, 3, "medium")], 3, [], [], "proposal");
        var request = Request(false,
            Turn(Producer, "video-game.production.role-estimate-request.v1", scope),
            Turn(Qa, "video-game.production.role-estimate-capacity-proposal.v1", proposal)) with { IsFinalization = true };
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(request, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal("Completed", result.Disposition);
        Assert.Null(result.Artifact);
    }

    [Fact]
    public void EstimateEvidenceComesFromOwnerRatherThanReviewersCopy()
    {
        var original = Turn(Producer, "video-game.production.role-estimate-capacity-proposal.v1", new {});
        var copy = Turn(Qa, "video-game.production.role-estimate-capacity-proposal.v1", new {});
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            Producer, Qa, "Estimates", "Scope", [], "Completed", 4, 4, null, false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [original, copy]);
        Assert.Same(original.Artifact, SpecialistAgent.OwnedEstimateArtifact(session, Producer.AgentInstallationId));
        Assert.Null(SpecialistAgent.OwnedEstimateArtifact(session, Guid.NewGuid()));
    }

    [Fact]
    public void OwnWorkUsesQaWhileOtherSpecialistsRemainTheirOwnEstimateSource()
    {
        var owner = new AgentTeammate(Producer.OrganizationUserId.ToString(), "Producer", "Agent", null, null, "Self", "Online") {
            AgentInstallationId = Producer.AgentInstallationId
        };
        var qa = new AgentTeammate(Qa.OrganizationUserId.ToString(), "QA", "Agent", null, null, "Peer", "Online") {
            AgentInstallationId = Qa.AgentInstallationId, RuntimeEligibility = "Eligible",
            DeclaredRoleKeys = [VideoGameRoleKeys.QualityAssurance]
        };
        var roster = new AgentTeamContext("team", "team", "Team", 12, "", "", [owner, qa], [], 2, false);
        Assert.Same(qa, SpecialistAgent.EstimatePartner(owner, roster, Producer.AgentInstallationId.ToString("D")));
        Assert.Same(qa, SpecialistAgent.EstimatePartner(qa, roster, Producer.AgentInstallationId.ToString("D")));
        Assert.Null(SpecialistAgent.EstimatePartner(owner, roster with { Members = [owner] }, Producer.AgentInstallationId.ToString("D")));
    }
}
