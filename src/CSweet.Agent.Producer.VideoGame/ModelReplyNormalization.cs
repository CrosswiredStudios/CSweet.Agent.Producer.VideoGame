using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

// Model replies are read with AgentModelJson: these steps derive what follows from the model's own judgment and
// name, for the model to correct in the same conversation, only what code cannot settle.
public sealed partial class SpecialistAgent
{
    private static readonly string[] BriefHeadings =
        ["Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions"];

    internal static DeliveryAcceptanceDecision NormalizeAcceptance(DeliveryAcceptanceDecision decision, IReadOnlyList<string> criteria)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(decision.Summary)) problems.Add("summary is empty");
        var reviewed = new List<DeliveryCriterionReview>();
        foreach (var review in (decision.Criteria ?? []).Where(x => x is not null))
        {
            var criterion = MatchCriterion(review.Criterion, criteria);
            if (criterion is null)
                problems.Add($"\"{ClipText(review.Criterion)}\" is not one of the supplied acceptance criteria; copy each criterion exactly");
            else if (reviewed.Any(x => x.Criterion == criterion))
                problems.Add($"criterion \"{ClipText(criterion)}\" is reviewed more than once");
            else
            {
                if (string.IsNullOrWhiteSpace(review.Evidence)) problems.Add($"criterion \"{ClipText(criterion)}\" has no evidence");
                reviewed.Add(review with { Criterion = criterion });
            }
        }
        foreach (var missing in criteria.Where(x => reviewed.All(r => r.Criterion != x)))
            problems.Add($"criterion \"{ClipText(missing)}\" is not reviewed");

        var repair = new List<string>();
        foreach (var named in decision.RoleRepairCriteria ?? [])
        {
            var criterion = MatchCriterion(named, criteria);
            if (criterion is null) problems.Add($"role repair criterion \"{ClipText(named)}\" is not a supplied acceptance criterion");
            else if (!repair.Contains(criterion)) repair.Add(criterion);
        }
        if (decision.RequiresRoleReplanning && repair.Count == 0 && (decision.RoleRepairCriteria ?? []).Count == 0)
            problems.Add("requiresRoleReplanning needs roleRepairCriteria naming the unsatisfied criteria that must move to engineering or QA");
        if (!decision.RequiresRoleReplanning && repair.Count > 0)
            problems.Add("roleRepairCriteria is set but requiresRoleReplanning is false; set it to true or leave the list empty");
        foreach (var criterion in repair.Where(c => reviewed.Any(x => x.Criterion == c && x.Satisfied)))
            problems.Add($"role repair criterion \"{ClipText(criterion)}\" is marked satisfied");

        // An unsatisfied criterion or a role conflict outweighs an "approved" label.
        var unsatisfied = reviewed.Where(x => !x.Satisfied).ToArray();
        var approved = decision.Approved && unsatisfied.Length == 0 && !decision.RequiresRoleReplanning;
        var findings = (decision.Findings ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (approved && findings.Count > 0)
            problems.Add("approved with findings: move observations into the summary, or set approved to false if they must be fixed");
        if (!approved && findings.Count == 0) findings.AddRange(unsatisfied.Select(x => $"{x.Criterion}: {x.Evidence}"));
        if (!approved && findings.Count == 0) problems.Add("a rejection needs at least one actionable finding");
        if (problems.Count > 0) throw new AgentModelJsonException(string.Join("; ", problems));

        var result = decision with
        {
            Approved = approved,
            Findings = findings,
            Criteria = reviewed,
            RoleRepairCriteria = decision.RequiresRoleReplanning ? repair : null
        };
        ValidateAcceptance(result, criteria); // Holds by construction; the same rule guards cached decisions.
        return result;
    }

    internal static ProducerReview NormalizeProducerReview(ProducerReview review)
    {
        var problems = new List<string>();
        var questions = (review.Questions ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (questions.Length > 12) problems.Add($"there are {questions.Length} questions; merge them into at most 12");
        if (string.IsNullOrWhiteSpace(review.Rationale)) problems.Add("rationale is empty");
        var draft = review.DraftMarkdown ?? "";
        if (string.IsNullOrWhiteSpace(draft)) problems.Add("draftMarkdown is empty");
        else
        {
            var missing = BriefHeadings.Where(h => !draft.Contains($"## {h}", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (missing.Length > 0) problems.Add($"draftMarkdown is missing the headings {string.Join(", ", missing.Select(h => "## " + h))}");
            if (draft.Length > 48000) problems.Add($"draftMarkdown has {draft.Length} characters; shorten it to at most 48000");
        }
        // Open questions outweigh a "ready" label: readiness requires none.
        var ready = review.Ready && questions.Length == 0;
        if (!ready && questions.Length == 0)
            problems.Add("not ready needs at least one concrete planning question; otherwise set ready to true with a rationale");
        if (problems.Count > 0) throw new AgentModelJsonException(string.Join("; ", problems));
        var result = review with { Ready = ready, Questions = questions, DraftMarkdown = draft };
        if (result.Ready && !PitchProtocol.CanPlan(result))
            throw new AgentModelJsonException("ready requires an empty questions array, a sufficient rationale and a complete brief");
        return result;
    }

    internal static ScopeAmendmentPlan NormalizeScopeAmendment(ScopeAmendmentPlan plan, IReadOnlyList<WorkItem> items)
    {
        var problems = new List<string>();
        var edits = (plan.Replacements ?? []).Where(x => x is not null).ToArray();
        if (edits.Length == 0) problems.Add("replacements is empty; return at least one exact replacement");
        if (edits.Length > 128) problems.Add($"there are {edits.Length} replacements; return at most 128");
        var byId = items.ToDictionary(x => x.Id);
        var seen = new HashSet<(Guid, string, string)>();
        for (var i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            if (!byId.TryGetValue(edit.ItemId, out var item))
            {
                problems.Add($"replacement #{i + 1} names item {edit.ItemId}, which is not on this board");
                continue;
            }
            var label = $"replacement #{i + 1} ({(string.IsNullOrWhiteSpace(item.Identifier) ? item.Id.ToString("D") : item.Identifier)} {edit.Field})";
            if (item.Status is "Done" or "Completed" or "Cancelled") { problems.Add($"{label} changes finished or cancelled work"); continue; }
            if (item.Planning is not { } planning) { problems.Add($"{label} targets an item without planning"); continue; }
            IReadOnlyList<string>? entries = edit.Field switch
            {
                "requirements" => planning.Requirements,
                "acceptanceCriteria" => planning.AcceptanceCriteria,
                "constraints" => planning.Constraints ?? Array.Empty<string>(),
                _ => null
            };
            if (entries is null) { problems.Add($"{label}: field must be requirements, acceptanceCriteria or constraints"); continue; }
            var matches = entries.Count(x => x == edit.Original);
            if (matches != 1)
                problems.Add(matches == 0 ? $"{label}: original must be copied exactly from one current entry" : $"{label}: original matches {matches} entries");
            if (string.IsNullOrWhiteSpace(edit.Replacement) || edit.Replacement == edit.Original)
                problems.Add($"{label}: replacement must be new, non-empty text");
            else if (entries.Contains(edit.Replacement, StringComparer.Ordinal))
                problems.Add($"{label}: replacement is already an entry");
            if (!seen.Add((edit.ItemId, edit.Field, edit.Original ?? ""))) problems.Add($"{label} repeats another replacement");
        }
        if (problems.Count > 0) throw new AgentModelJsonException(string.Join("; ", problems));
        return plan with { Replacements = edits };
    }

    // Exact text first; then a unique match ignoring case, spacing, list markers and a final period.
    private static string? MatchCriterion(string? text, IReadOnlyList<string> criteria)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (criteria.Contains(text, StringComparer.Ordinal)) return text;
        var key = CriterionKey(text);
        var candidates = criteria.Where(x => CriterionKey(x) == key).Distinct(StringComparer.Ordinal).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string CriterionKey(string text) =>
        Regex.Replace(Regex.Replace(text.Trim(), @"^(?:[-*•]|\d+[.)])\s+", ""), @"\s+", " ").TrimEnd('.', ' ').ToLowerInvariant();

    private static string ClipText(string? text) => text is null ? "" : text.Length <= 80 ? text : text[..77] + "...";
}
