using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static IReadOnlyList<DecisionRequest> PlanningDecisionRequests(Guid workstreamId, Guid boardId,
        string fingerprint, IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff) =>
        questions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).Select(question => new DecisionRequest(
                workstreamId, "video-game.management-direction.v1", question, "material-management-direction",
                [
                    new("provide-direction", "Provide authoritative direction", "Specify the binding direction and any changes required in the accepted brief."),
                    new("continue-current-plan", "Continue within accepted scope", "Explain how the current authority and accepted brief resolve this question."),
                    new("request-more-evidence", "Request more evidence", "Name the evidence needed; affected work remains blocked.")
                ],
                "provide-direction",
                [new EvidenceReference("artifact", handoff.ArtifactId, handoff.AcceptedRevisionId,
                    handoff.RevisionDigest, "video-game.production-plan.v1", "Accepted")],
                null, "Affected scope remains uncommitted until the decision is incorporated into accepted planning evidence.",
                null, "producer-planning-decision:" + ProducerPolicyFingerprint.Digest(
                    JsonSerializer.Serialize(new { workstreamId, fingerprint, question })),
                JsonSerializer.SerializeToElement(new { boardId, planningFingerprint = fingerprint,
                    planningPackageId = handoff.PlanningPackageId, planningPackageVersion = handoff.PlanningPackageVersion }))
            ).ToArray();

    private static async Task EnsurePlanningDecisionsAsync(Guid workstreamId, Guid boardId,
        string fingerprint, IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff,
        AgentRuntimeContext context, CancellationToken token)
    {
        foreach (var request in PlanningDecisionRequests(workstreamId, boardId, fingerprint, questions, handoff))
        {
            var decision = await context.Platform.RequestDecisionAsync(request, token);
            if (decision.Status != DecisionStatuses.Pending ||
                !Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager)) continue;
            await context.Platform.Communication.SendDirectMessageAsync(manager,
                $"Planning decision {decision.Id:D} requires authoritative direction: {decision.Summary}. " +
                $"Review it in the Workstream decisions for {workstreamId:D}. Update the accepted brief with the resolution before committing affected scope.",
                $"producer-decision-notice:{decision.Id:N}",
                new AgentWorkContext(Guid.Parse(context.BusinessId), workstreamId, null, boardId,
                    null, null, null, decision.Id, null, null), token);
        }
    }
}
