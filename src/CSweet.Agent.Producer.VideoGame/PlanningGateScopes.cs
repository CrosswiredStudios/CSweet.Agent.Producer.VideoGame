using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    // Compatibility policy for the canonical final-style question and approved outline keys.
    // Exact identities are intentional: arbitrary titles, prose or missing scope cannot approve work.
    internal const string FinalVisualStyleQuestion = "Director must select the final neon visual style from the two to three readable procedural style options. Until selected, final procedural asset production and final art-dependent scope freeze remain blocked or constrained.";
    internal static readonly string[] FinalVisualStyleScopes = [
        "final-procedural-asset-production", "final-creative-scope-record-and-content-freeze", "presentation-polish-pass"
    ];

    internal static ProducerPlanningCycleState ScopePlanningGates(ProducerPlanningCycleState cycle,
        IReadOnlyList<WorkItem> items, IReadOnlyList<DecisionRecord> decisions, string acceptedBriefDigest)
    {
        cycle = cycle with { DecisionIndependentProposalKeys = null };
        if (string.IsNullOrWhiteSpace(acceptedBriefDigest) || string.IsNullOrWhiteSpace(cycle.TechnicalProposalDigest) ||
            cycle.OutstandingAuthorityQuestions.Count == 0 ||
            cycle.OutstandingAuthorityQuestions.Any(question => question.Trim() != FinalVisualStyleQuestion)) return cycle;
        var current = CurrentPlanningDecisions(cycle.OutstandingAuthorityQuestions, decisions, cycle.WorkstreamId, acceptedBriefDigest).ToArray();
        if (current.Length != 1 || current[0].AuthorityRuleKey != "work-planning" || current[0].Status != DecisionStatuses.Decided ||
            current[0].SelectedOptionId != "request-more-evidence" || string.IsNullOrWhiteSpace(current[0].Rationale)) return cycle;

        var bound = items.Where(item => item.ProposalProvenance?.ArtifactDigest == cycle.TechnicalProposalDigest).ToArray();
        if (items.Count > 4096 || items.Select(item => item.Id).Distinct().Count() != items.Count ||
            bound.GroupBy(item => item.ProposalProvenance!.ProposalItemKey, StringComparer.Ordinal).Any(group => group.Count() != 1)) return cycle;
        var roots = FinalVisualStyleScopes.Select(key => bound.SingleOrDefault(item => item.ProposalProvenance!.ProposalItemKey == key)).ToArray();
        if (roots.Any(root => root is null || root.ExecutionMode != WorkItemExecutionModes.Container)) return cycle;
        var blocked = roots.Select(root => root!.Id).ToHashSet();
        // A final-art gate applies to its descendants and every transitive consumer, not their prerequisites.
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items)
                if (!blocked.Contains(item.Id) && (item.ParentItemId is { } parent && blocked.Contains(parent) ||
                    item.Planning?.DependencyItemIds.Any(blocked.Contains) == true))
                    changed |= blocked.Add(item.Id);
        } while (changed);
        return cycle with { DecisionIndependentProposalKeys = bound.Where(item => !blocked.Contains(item.Id))
            .Select(item => item.ProposalProvenance!.ProposalItemKey).ToHashSet(StringComparer.Ordinal) };
    }

    internal static IReadOnlySet<Guid> ConfirmedCeoHolds(ProducerPlanningCycleState cycle, IReadOnlyList<WorkItem> items,
        IReadOnlyList<DecisionRecord> decisions, ProducerAcceptedHandoff handoff)
    {
        var held = new HashSet<Guid>();
        foreach (var receipt in decisions.Where(d => d.AuthorityRuleKey == "material-strategy-change" &&
            (d.Status == DecisionStatuses.Pending || d.Status == DecisionStatuses.Decided && d.SelectedOptionId == "pause-affected-work") &&
            d.SupersededByDecisionId == null && IsPlanningDirection(d, cycle.WorkstreamId, handoff.RevisionDigest)))
        {
            // Only Marcus's explicit escalation of this exact Producer proposal can hold tickets.
            var parent = decisions.SingleOrDefault(d => d.Id == receipt.SupersedesDecisionId && d.WorkstreamId == cycle.WorkstreamId &&
                d.AuthorityRuleKey == "work-planning" && d.TypeKey == receipt.TypeKey);
            if (parent?.TypeData is not { ValueKind: System.Text.Json.JsonValueKind.Object } original ||
                receipt.TypeData is not { ValueKind: System.Text.Json.JsonValueKind.Object } data ||
                !original.TryGetProperty("kind", out var originalKind) || originalKind.ToString() != "ceo-hold" ||
                !original.TryGetProperty("boardId", out var proposedBoard) || !Guid.TryParse(proposedBoard.ToString(), out var sourceBoard) ||
                sourceBoard != cycle.BoardId ||
                !data.TryGetProperty("kind", out var kind) || kind.ToString() != "ceo-hold" ||
                !data.TryGetProperty("boardId", out var board) || !Guid.TryParse(board.ToString(), out var targetBoard) || targetBoard != cycle.BoardId ||
                !original.TryGetProperty("adaptiveReview", out var originalReview) || !data.TryGetProperty("adaptiveReview", out var review) ||
                originalReview.ToString() != review.ToString() || !original.TryGetProperty("itemIds", out var proposed) ||
                proposed.ValueKind != System.Text.Json.JsonValueKind.Array || !data.TryGetProperty("itemIds", out var confirmed) ||
                confirmed.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            var allowed = proposed.EnumerateArray().Select(x => x.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in confirmed.EnumerateArray())
                if (Guid.TryParse(value.ToString(), out var id) && allowed.Contains(id.ToString()) && items.Any(i => i.Id == id && i.ExecutionMode == WorkItemExecutionModes.Executable)) held.Add(id);
        }
        // Dependency closure prevents a ready consumer starting while its required ticket is held.
        bool added;
        do
        {
            added = false;
            foreach (var i in items.Where(i => i.ParentItemId is Guid parent && held.Contains(parent) || i.Planning?.DependencyItemIds.Any(held.Contains) == true))
                added |= held.Add(i.Id);
        } while (added);
        return held;
    }

    private static async Task<ProducerPlanningCycleState> ReadScopedPlanningGatesAsync(ProducerPlanningCycleState cycle,
        ProducerOperatingState portfolio, WorkBoardDetail board, AgentRuntimeContext context, CancellationToken token)
    {
        if (cycle.AdaptivePlanning && portfolio.AcceptedHandoffs.TryGetValue(cycle.WorkstreamId, out var adaptiveHandoff))
        {
            var current = await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: cycle.WorkstreamId), token);
            cycle = cycle with { ConfirmedHoldItems = ConfirmedCeoHolds(cycle, board.Items, current, adaptiveHandoff) };
        }
        if (cycle.OutstandingAuthorityQuestions.Count == 0 ||
            cycle.OutstandingAuthorityQuestions.Any(question => question.Trim() != FinalVisualStyleQuestion) ||
            !portfolio.AcceptedHandoffs.TryGetValue(cycle.WorkstreamId, out var handoff) ||
            board.Board.Id != cycle.BoardId || board.Board.WorkstreamId != cycle.WorkstreamId) return cycle;
        var decisions = await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: cycle.WorkstreamId), token);
        return ScopePlanningGates(cycle, board.Items, decisions, handoff.RevisionDigest);
    }
}
