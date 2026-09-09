using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static AgentTeammate? EstimatePartner(AgentTeammate owner, AgentTeamContext roster, string selfInstallation) =>
        owner.AgentInstallationId?.ToString("D") == selfInstallation
            ? SelectRoleMember(roster, VideoGameRoleKeys.QualityAssurance)
            : owner;

    internal static AgentCoordinationArtifact? OwnedEstimateArtifact(AgentCoordinationSession session, Guid? installation)
    {
        var author = new[] { session.Initiator, session.Target }.SingleOrDefault(x => x.AgentInstallationId == installation);
        return author is null ? null : session.Turns.LastOrDefault(x =>
            x.SpeakerOrganizationUserId == author.OrganizationUserId &&
            x.Artifact?.Type == "video-game.production.role-estimate-capacity-proposal.v1" &&
            x.Artifact.IsFinalPage)?.Artifact;
    }

    internal static bool IsOwnEstimateInvitation(AgentCoordinationTurnRequest request)
    {
        if (request.SourceKind != "Board" || request.IsFinalization || request.WorkContext is null) return false;
        var turn = request.Transcript.LastOrDefault();
        if (turn?.SpeakerOrganizationUserId != request.Counterpart.OrganizationUserId ||
            turn.Artifact is not { Type: "video-game.production.role-estimate-request.v1", IsFinalPage: true } artifact)
            return false;
        var proposal = artifact.Payload.Deserialize<GameRoleEstimateCapacityRequestV1>();
        return proposal?.RoleKey == VideoGameRoleKeys.Producer &&
            proposal.RequestFingerprint == artifact.Key;
    }
}
