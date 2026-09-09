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

    internal static bool NeedsPlanningFormatRecovery(AgentCoordinationSession session) =>
        session.SourceKind == "Board" && session.Status == "Blocked" &&
        session.Turns.Any(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId &&
            x.Disposition == "Blocked" && x.Content == "Technical planning returned invalid JSON; revise the proposal.") &&
        !session.Turns.Any(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId && x.Artifact is not null);
    internal static bool NeedsDesignerHierarchyRecovery(AgentCoordinationSession session)
    {
        if (session.SourceKind != "Board" || session.Status != "Completed") return false;
        var artifact = session.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId)
            ?.Artifact;
        if (artifact is not { IsFinalPage: true, Type: "video-game.production.designer-backlog-proposal.v1" })
            return false;
        var proposal = artifact.Payload.Deserialize<GameDesignerBacklogProposalV1>();
        return proposal?.PlayerOutcomes.Any(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Feature &&
            string.IsNullOrWhiteSpace(x.ParentProposalKey)) == true;
    }
    private static string[] ReplyTypes(string? requestType) => requestType switch
    {
        "video-game.production.planning-cycle.v1" =>
            ["video-game.production.technical-delivery-proposal.v1", "video-game.production.designer-backlog-proposal.v1"],
        "video-game.production.role-estimate-request.v1" =>
            ["video-game.production.role-estimate-capacity-proposal.v1"],
        "video-game.production.qa-readiness-request.v1" =>
            ["video-game.production.qa-sprint-readiness-assessment.v1"],
        _ => []
    };

    private static bool IsPlanningRequest(string? type) => ReplyTypes(type).Length > 0;

    private static AgentCoordinationTurnResult HandlePlanningReply(AgentCoordinationTurnRequest request)
    {
        var reply = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId);
        if (reply?.Disposition == "Blocked")
            return AgentCoordinationTurnResult.Blocked($"Specialist planning requires resolution: {reply.Content}");
        var cycle = request.Transcript.Last(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
            IsPlanningRequest(x.Artifact?.Type)).Artifact!;
        if (request.WorkContext is null || reply?.Artifact is not { IsFinalPage: true } proposal ||
            proposal.Key != cycle.Key || !ReplyTypes(cycle.Type).Contains(proposal.Type))
            return AgentCoordinationTurnResult.Blocked("Planning needs a complete specialist proposal bound to the requested cycle and project context.");
        return AgentCoordinationTurnResult.Completed("Specialist planning proposal received. The owning commitment will validate scope, provenance, estimates, staffing and readiness before advancing work; receipt does not approve hiring or delivery.");
    }
}
