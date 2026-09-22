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
                workstreamId, "video-game.management-direction.v1", question, "work-planning",
                [
                    new("provide-direction", "Provide authoritative direction", "Specify binding direction within the accepted brief and delegated project authority."),
                    new("continue-current-plan", "Continue within accepted scope", "Explain how the current authority and accepted brief resolve this question."),
                    new("request-more-evidence", "Request more evidence", "Name the evidence needed; affected work remains blocked.")
                ],
                "provide-direction",
                [new EvidenceReference("artifact", handoff.ArtifactId, handoff.AcceptedRevisionId,
                    handoff.RevisionDigest, "video-game.production-plan.v1", "Accepted")],
                null, "Affected scope remains uncommitted until the decision is incorporated into accepted planning evidence.",
                null, "producer-planning-decision:v2:" + ProducerPolicyFingerprint.Digest(
                    JsonSerializer.Serialize(new { workstreamId, handoff.RevisionDigest, question })),
                JsonSerializer.SerializeToElement(new { boardId, planningFingerprint = fingerprint,
                    planningPackageId = handoff.PlanningPackageId, planningPackageVersion = handoff.PlanningPackageVersion }))
            ).ToArray();

    internal static IReadOnlyList<string> ResolvedPlanningDirections(
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        decisions.Where(x => x.WorkstreamId == workstreamId &&
                x.TypeKey == "video-game.management-direction.v1" &&
                x.AuthorityRuleKey == "work-planning" && x.Status == DecisionStatuses.Decided &&
                x.SelectedOptionId is not null and not "request-more-evidence" &&
                !string.IsNullOrWhiteSpace(x.Rationale) &&
                x.Evidence.Any(e => e.TypeKey == "video-game.production-plan.v1" &&
                    e.Digest == acceptedBriefDigest))
            .OrderBy(x => x.Id)
            .Select(x => $"Decision {x.Id:D}: {x.Summary} => {x.SelectedOptionId}. {x.Rationale}")
            .ToArray();

    internal static async Task EnsurePlanningDecisionsAsync(Guid workstreamId, Guid boardId,
        string fingerprint, IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff,
        AgentRuntimeContext context, CancellationToken token)
    {
        var existing = await context.Platform.ReadDecisionsAsync(
            new ReadDecisionRequest(WorkstreamId: workstreamId, PendingOnly: true), token);
        foreach (var request in PlanningDecisionRequests(workstreamId, boardId, fingerprint, questions, handoff))
        {
            // Replace earlier owner-routed planning questions with a manager-owned
            // decision. Keep the old record and card as superseded audit history.
            var legacy = existing.FirstOrDefault(x => x.TypeKey == request.TypeKey &&
                x.AuthorityRuleKey == "material-management-direction" &&
                string.Equals(x.Summary, request.Summary, StringComparison.Ordinal));
            _ = await context.Platform.RequestDecisionAsync(request with {
                SupersedesDecisionId = legacy?.Id
            }, token);
        }
    }
}
