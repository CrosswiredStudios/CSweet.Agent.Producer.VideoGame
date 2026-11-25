using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string PlanningDecisionBlockMarker = "Planning needs specific decision follow-up.";

    private static bool AppliesToBoard(DecisionRecord decision, Guid board) =>
        !decision.Evidence.Any(e => e.Kind == "board" && e.ResourceId != board);

    private static bool IsPlanningDirection(DecisionRecord decision, Guid workstreamId, string acceptedBriefDigest) =>
        decision.WorkstreamId == workstreamId && decision.TypeKey == "video-game.management-direction.v1" &&
        decision.AuthorityRuleKey is "work-planning" or "material-strategy-change" or "material-management-direction" &&
        decision.Status != DecisionStatuses.Superseded && decision.SupersededByDecisionId is null &&
        decision.Evidence.Any(e => e.TypeKey == "video-game.production-plan.v1" &&
            e.Digest == acceptedBriefDigest && e.Status == "Accepted");

    private static IEnumerable<DecisionRecord> CurrentPlanningDecisions(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        decisions.Where(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) &&
            questions.Any(q => MatchesQuestion(x, q, decisions)));

    private static bool MatchesQuestion(DecisionRecord decision, string question, IReadOnlyList<DecisionRecord> decisions)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(decision.Id))
        {
            if (decision.Summary.Trim() == question.Trim()) return true;
            // Earlier Producers rewrote the summary and retained the original question as optional evidence.
            if (decision.TypeData is { ValueKind: JsonValueKind.Object } data &&
                data.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Object &&
                facts.TryGetProperty("authorityQuestion", out var original) && original.ValueKind == JsonValueKind.String &&
                original.GetString()?.Trim() == question.Trim()) return true;
            var parent = decisions.FirstOrDefault(d => d.Id == decision.SupersedesDecisionId &&
                d.SupersededByDecisionId == decision.Id && d.WorkstreamId == decision.WorkstreamId && d.TypeKey == decision.TypeKey);
            if (parent is null) break;
            decision = parent;
        }
        return false;
    }

    internal static string PlanningDecisionFingerprint(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new {
            workstreamId, acceptedBriefDigest,
            questions = questions.Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            decisions = CurrentPlanningDecisions(questions, decisions, workstreamId, acceptedBriefDigest)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision, x.Status, x.SelectedOptionId, x.Rationale })
        }));

    private static async Task RequeuePlanningAfterDirectionAsync(ProducerOperatingState state, AgentRuntimeContext context,
        CancellationToken token)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var owner) ||
            !state.PlanningCycles.Values.Any(cycle => cycle.TechnicalDirectorSessionId is not null)) return;
        var directory = await context.Platform.PersonalTodo.ListAsync(token);
        foreach (var item in directory.Boards.Where(b => b.OwnerOrganizationUserId == owner).SelectMany(b => b.Items))
        {
            if (item.ArchivedAt is not null || !IsSettledPlanningBlock(item, item.CorrelationId ?? "") ||
                item.BlockReason!.StartsWith("Repeated issue:", StringComparison.Ordinal) ||
                item.WorkContext?.WorkstreamId is not Guid project || !state.AcceptedHandoffs.TryGetValue(project, out var handoff) ||
                !state.PlanningCycles.TryGetValue(project, out var cycle) || cycle.TechnicalDirectorSessionId is not Guid session ||
                cycle.BoardId != item.WorkContext.BoardId || cycle.TeamId != item.WorkContext.TeamId ||
                cycle.PlanningFingerprint != item.WorkContext.SourceFingerprint) continue;

            var decisions = await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: project), token);
            var corrections = decisions.Where(d => IsPlanningDirection(d, project, handoff.RevisionDigest) &&
                AppliesToBoard(d, cycle.BoardId) && d.Status == DecisionStatuses.Decided && d.UpdatedAt > item.UpdatedAt &&
                d.SelectedOptionId is not (null or "request-more-evidence") && !string.IsNullOrWhiteSpace(d.Rationale) &&
                d.Evidence.Any(e => e.Kind == "artifact" && e.ResourceId == handoff.ArtifactId && e.RevisionId == handoff.AcceptedRevisionId &&
                    e.TypeKey == "video-game.production-plan.v1" && e.Digest == handoff.RevisionDigest && e.Status == "Accepted") &&
                (d.SelectedOptionId == "provide-direction" || d.Evidence.Any(e =>
                    e.Kind == "work-item" && e.ResourceId == item.Id || e.Kind == "coordination" && e.ResourceId == session)))
                .OrderBy(d => d.Id).Select(d => new { d.Id, d.Revision }).ToArray();
            if (corrections.Length == 0) continue;
            var stopped = await context.Platform.Communication.ReadCoordinationAsync(session, token);
            if (stopped.Status != AgentCoordinationStatuses.Blocked || stopped.Initiator.OrganizationUserId != owner ||
                stopped.BoardSource?.BoardId != cycle.BoardId || IsTechnicalPlanningGenerationBlock(stopped)) continue;

            var fingerprint = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new { session, corrections }));
            if (!await AuthorizeContinuationRecoveryAsync(item, fingerprint, context, token)) continue;
            try
            {
                await context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision,
                    $"planning-direction:{item.Id:N}:{fingerprint}") { ReconcileOnly = true }, token);
            }
            catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.Conflict)
            {
                // The current owner, ticket or revision changed. A later wake rereads it before acting.
            }
        }
    }

    private static async Task<bool> PlanningDecisionsUnchangedAsync(PersonalTodoItem item,
        PersonalTodoWorkContext source, AgentRuntimeContext context, CancellationToken token)
    {
        if (source.WorkstreamId is not { } workstreamId || item.WorkContext?.WorkstreamId != workstreamId ||
            item.WorkContext.TeamId != source.TeamId || item.WorkContext.SourceFingerprint != source.SourceFingerprint)
            return false;
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), token);
        if (state?.Payload.PlanningCycles.TryGetValue(workstreamId, out var cycle) != true || cycle is null ||
            (item.CorrelationId?.StartsWith(EstimationCommitmentPrefix, StringComparison.Ordinal) == true
                ? cycle.ReconciledDigest : cycle.PlanningFingerprint) != source.SourceFingerprint || cycle.AuthorityDecisionFingerprint is null ||
            !state.Payload.AcceptedHandoffs.TryGetValue(workstreamId, out var handoff))
            return false;
        var decisions = await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: workstreamId), token);
        return cycle.AuthorityDecisionFingerprint == PlanningDecisionFingerprint(
            cycle.OutstandingAuthorityQuestions, decisions.Where(d => AppliesToBoard(d, cycle.BoardId)).ToArray(), workstreamId, handoff.RevisionDigest);
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
            var matches = current.Where(x => MatchesQuestion(x, question, decisions)).OrderBy(x => x.Id).ToArray();
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
        lines.Add("\nA new accepted brief is needed only if the answer changes accepted scope. Declared engineering prerequisites and research spikes may proceed through estimates and QA readiness while decision-dependent implementation remains gated. Retry after the evidence or decision changes; unchanged reviews retain this blocker.");
        return string.Join("\n", lines);
    }

    internal static IReadOnlyList<DecisionRequest> PlanningDecisionRequests(Guid workstreamId, Guid boardId,
        IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff) =>
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
                    handoff.RevisionDigest, "video-game.production-plan.v1", "Accepted"),
                    new EvidenceReference("board", boardId, null, null, WorkBoardCapabilities.Read, "Current")],
                null, "Affected scope remains uncommitted until the decision is incorporated into accepted planning evidence.",
                null, "producer-planning-decision:v3:" + ProducerPolicyFingerprint.Digest(
                    JsonSerializer.Serialize(new { workstreamId, boardId, handoff.RevisionDigest, question })))
            ).ToArray();

    internal static IReadOnlyList<string> ResolvedPlanningDirections(
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        decisions.Where(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) && x.Status == DecisionStatuses.Decided &&
                (x.SelectedOptionId == "provide-direction" || x.Options.Any(o => o.Id == x.SelectedOptionId)) &&
                !string.IsNullOrWhiteSpace(x.Rationale))
            .OrderBy(x => x.Id)
            .Select(x => $"Decision {x.Id:D}: {x.Summary}\n{x.Options.FirstOrDefault(o => o.Id == x.SelectedOptionId)?.Label ?? x.SelectedOptionId}. {x.Rationale}")
            .ToArray();

    // Continuing the accepted brief resolves a question without replacing the specialist's
    // immutable proposal. Exact question and accepted-source binding prevent unrelated
    // decisions from approving scope. Material direction still requires specialist revision.
    internal static IReadOnlyList<string> UnresolvedPlanningQuestions(IReadOnlyList<string> questions,
        IReadOnlyList<DecisionRecord> decisions, Guid workstreamId, string acceptedBriefDigest) =>
        questions.Where(question => !decisions.Any(x => IsPlanningDirection(x, workstreamId, acceptedBriefDigest) &&
            x.Status == DecisionStatuses.Decided && x.SelectedOptionId == "continue-current-plan" &&
            !string.IsNullOrWhiteSpace(x.Rationale) && MatchesQuestion(x, question, decisions)))
            .Distinct(StringComparer.Ordinal).ToArray();

    internal static async Task<IReadOnlyList<DecisionRecord>> EnsurePlanningDecisionsAsync(Guid workstreamId, Guid boardId,
        IReadOnlyList<string> questions, ProducerAcceptedHandoff handoff,
        AgentRuntimeContext context, CancellationToken token)
    {
        var existing = (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: workstreamId), token))
            .Where(d => AppliesToBoard(d, boardId)).ToArray();
        var current = existing.ToList();
        foreach (var request in PlanningDecisionRequests(workstreamId, boardId,
            UnresolvedPlanningQuestions(questions, existing, workstreamId, handoff.RevisionDigest), handoff))
        {
            // Owner-routed decisions may supersede the original manager question.
            // Preserve the recorded answer and pass its direction to the specialist;
            // provide-direction does not itself waive unresolved acceptance questions.
            if (CurrentPlanningDecisions([request.Summary], current, workstreamId, handoff.RevisionDigest)
                .Any(x => x.Status == DecisionStatuses.Decided || x.AuthorityRuleKey != "material-management-direction")) continue;
            // Replace earlier owner-routed planning questions with a manager-owned
            // decision. Keep the old record and card as superseded audit history.
            var legacy = existing.FirstOrDefault(x => x.TypeKey == request.TypeKey && x.WorkstreamId == workstreamId &&
                x.Status == DecisionStatuses.Pending && x.SupersededByDecisionId is null &&
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
