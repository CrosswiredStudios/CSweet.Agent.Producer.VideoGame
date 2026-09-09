using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static AgentCoordinationArtifactSubmission PlanningArtifact(GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<ArtifactPackageMemberDigest> members) => CollaborationActions.WithDocuments(
            new("video-game.production.planning-cycle.v1", "1.0", cycle.PlanningFingerprint, 1, true,
                JsonSerializer.SerializeToElement(cycle)),
            members.Select(x => new CollaborationDocumentReference(x.ArtifactId, x.AcceptedRevisionId, x.Sha256)).ToArray());

    internal static bool NeedsPlanningDocumentRecovery(AgentCoordinationSession session) =>
        session.SourceKind == "Board" && session.Status == "Failed" &&
        session.FinalSummary?.Contains("capability=platform.artifact-package.read.v1;", StringComparison.Ordinal) == true &&
        !session.Turns.Any(x => x.Artifact?.Payload.TryGetProperty("documentReferences", out _) == true) &&
        !session.Turns.Any(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId && x.Artifact is not null);

    internal static bool NeedsPlanningContextRecovery(AgentCoordinationSession session) =>
        session.SourceKind == "Board" && session.WorkContext is null && session.Status == "Blocked" &&
        session.Turns.Any(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId &&
            x.Disposition == "Blocked" && x.Content == "Planning requires the exact workstream and planning fingerprint.") &&
        !session.Turns.Any(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId && x.Artifact is not null);

    private static AgentCoordinationTurnResult HandlePlanningReply(AgentCoordinationTurnRequest request)
    {
        var reply = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId);
        if (reply?.Disposition == "Blocked")
            return AgentCoordinationTurnResult.Blocked($"Specialist planning requires resolution: {reply.Content}");
        var cycle = request.Transcript.First(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
            x.Artifact?.Type == "video-game.production.planning-cycle.v1").Artifact!;
        if (request.WorkContext is null || reply?.Artifact is not { IsFinalPage: true } proposal ||
            proposal.Key != cycle.Key || proposal.Type is not
                ("video-game.production.technical-delivery-proposal.v1" or "video-game.production.designer-backlog-proposal.v1"))
            return AgentCoordinationTurnResult.Blocked("Planning needs a complete specialist proposal bound to the requested cycle and project context.");
        return AgentCoordinationTurnResult.Completed("Specialist planning proposal received. The planning commitment will validate its scope, provenance and staffing before publishing backlog work; receipt does not approve hiring or delivery.");
    }
}
