using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string PlanningDecisionBlockMarker = "Planning needs specific decision follow-up.";

    private static bool IsPlanningDirection(DecisionRecord decision, Guid workstreamId, string acceptedBriefDigest) =>
        decision.WorkstreamId == workstreamId && decision.TypeKey == "video-game.management-direction.v1" &&
        decision.AuthorityRuleKey is "work-planning" or "material-strategy-change" or "material-management-direction" &&
        decision.Status != DecisionStatuses.Superseded && decision.SupersededByDecisionId is null &&
        decision.Evidence.Any(e => e.TypeKey == "video-game.production-plan.v1" &&
            e.Digest == acceptedBriefDigest && e.Status == "Accepted");

    private static IEnumerable<DecisionRecord> CurrentPlanningDecisions(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        decisions.Where(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) &&
            questions.Any(q => q.Trim() == x.Summary.Trim()));

    internal static string PlanningDecisionFingerprint(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new {
            workstreamId, acceptedBriefDigest,
            questions = questions.Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            decisions = CurrentPlanningDecisions(questions, decisions, workstreamId, acceptedBriefDigest)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision, x.Status, x.SelectedOptionId, x.Rationale })
        }));

    private static async Task<bool> PlanningDecisionsUnchangedAsync(PersonalTodoItem item,
        PersonalTodoWorkContext source, AgentRuntimeContext context, CancellationToken token)
    {
        if (source.WorkstreamId is not { } workstreamId || item.WorkContext?.WorkstreamId != workstreamId ||
            item.WorkContext.TeamId != source.TeamId || item.WorkContext.SourceFingerprint != source.SourceFingerprint)
            return false;
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), token);
        if (state?.Payload.PlanningCycles.TryGetValue(workstreamId, out var cycle) != true || cycle is null ||
            cycle.PlanningFingerprint != source.SourceFingerprint || cycle.AuthorityDecisionFingerprint is null ||
            !state.Payload.AcceptedHandoffs.TryGetValue(workstreamId, out var handoff))
            return false;
        var decisions = await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: workstreamId), token);
        return cycle.AuthorityDecisionFingerprint == PlanningDecisionFingerprint(
            cycle.OutstandingAuthorityQuestions, decisions, workstreamId, handoff.RevisionDigest);
    }

    internal static string DescribePlanningBlocker(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest, string producerName)
    {
        var current = CurrentPlanningDecisions(questions, decisions, workstreamId, acceptedBriefDigest).ToArray();
        var lines = new List<string> {
            PlanningDecisionBlockMarker,
            "Draft backlog and staffing proposal are recorded; the following questions remain unresolved.",
            $"Production follow-up: {(string.IsNullOrWhiteSpace(producerName) ? "Producer / project manager" : producerName)}."
        };
        foreach (var question in questions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal))
        {
            lines.Add($"\nQuestion: {question}");
            var matches = current.Where(x => x.Summary.Trim() == question).OrderBy(x => x.Id).ToArray();
            if (matches.Length == 0)
                lines.Add("Decision record: unavailable. Re-read the project decisions before requesting direction.");
            foreach (var decision in matches)
            {
                lines.Add($"Decision {decision.Id:D}: {decision.Status}; selected option: {decision.SelectedOptionId ?? "none"}.");
                if (!string.IsNullOrWhiteSpace(decision.Rationale))
                    lines.Add($"Recorded direction / missing evidence: {decision.Rationale.Trim()}");
                lines.Add(decision.SelectedOptionId == "request-more-evidence" && decision.Status == DecisionStatuses.Decided
                    ? "Next action: collect the evidence named in the recorded direction and return it for a follow-up decision. A request for evidence does not approve the affected scope."
                    : decision.SelectedOptionId == "provide-direction" && decision.Status == DecisionStatuses.Decided
                        ? "Next action: reconcile the specialist proposal with the recorded direction; retain any question the revised proposal still leaves open."
                        : "Next action: obtain an authoritative answer to this question, with a rationale tied to the accepted brief.");
            }
        }
        lines.Add("\nA new accepted brief is needed only if the answer changes accepted scope. This planning gate currently limits sprint preparation to research spikes while any authority question remains; the affected work described above may be narrower. Retry after the evidence or decision changes; unchanged reviews retain this blocker.");
        return string.Join("\n", lines);
    }

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
        decisions.Where(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) && x.Status == DecisionStatuses.Decided &&
                x.SelectedOptionId == "provide-direction" &&
                !string.IsNullOrWhiteSpace(x.Rationale))
            .OrderBy(x => x.Id)
            .Select(x => $"Decision {x.Id:D}: {x.Summary} => {x.SelectedOptionId}. {x.Rationale}")
            .ToArray();

    // Continuing the accepted brief resolves a question without replacing the specialist's
    // immutable proposal. Exact question and accepted-source binding prevent unrelated
    // decisions from approving scope. Material direction still requires specialist revision.
    internal static IReadOnlyList<string> UnresolvedPlanningQuestions(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        questions.Where(question => !decisions.Any(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) &&
            x.Status == DecisionStatuses.Decided && x.SelectedOptionId == "continue-current-plan" &&
            !string.IsNullOrWhiteSpace(x.Rationale) && x.Summary.Trim() == question.Trim()))
            .Distinct(StringComparer.Ordinal).ToArray();

    internal static async Task<IReadOnlyList<DecisionRecord>> EnsurePlanningDecisionsAsync(Guid workstreamId, Guid boardId,
        string fingerprint, IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff,
        AgentRuntimeContext context, CancellationToken token)
    {
        var existing = await context.Platform.ReadDecisionsAsync(
            new ReadDecisionRequest(WorkstreamId: workstreamId), token);
        var current = existing.ToList();
        foreach (var request in PlanningDecisionRequests(workstreamId, boardId, fingerprint,
            UnresolvedPlanningQuestions(questions, existing, workstreamId, handoff.RevisionDigest), handoff))
        {
            // Owner-routed decisions may supersede the original manager question.
            // Preserve the recorded answer and pass its direction to the specialist;
            // provide-direction does not itself waive unresolved acceptance questions.
            if (CurrentPlanningDecisions([request.Summary], current, workstreamId, handoff.RevisionDigest)
                .Any(x => x.Status == DecisionStatuses.Decided)) continue;
            // Replace earlier owner-routed planning questions with a manager-owned
            // decision. Keep the old record and card as superseded audit history.
            var legacy = existing.FirstOrDefault(x => x.TypeKey == request.TypeKey &&
                x.AuthorityRuleKey == "material-management-direction" &&
                string.Equals(x.Summary, request.Summary, StringComparison.Ordinal));
            var recorded = await context.Platform.RequestDecisionAsync(request with {
                SupersedesDecisionId = legacy?.Id
            }, token);
            current.RemoveAll(x => x.Id == recorded.Id || x.Id == recorded.SupersedesDecisionId);
            current.Add(recorded);
        }
        return current;
    }
}
