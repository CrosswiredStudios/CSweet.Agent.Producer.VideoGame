using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

/// <summary>
/// Records a plan's per-task browser choice on the ticket, where the engineer binds it, the technical reviewer checks it
/// and QA reproduces it. Super Awesome Games (2026-11-10): without one shared declaration each role read different text.
/// </summary>
internal static class TicketVerification
{
    /// <summary>Null (a proposal from before the field existed) stays undeclared so agents keep their previous rules.</summary>
    internal static IReadOnlyList<WorkItemVerificationRequirement>? FromProposal(string? browserVerification) => browserVerification switch
    {
        GameBrowserVerification.Canvas => [new(WorkItemVerificationKinds.BrowserSmoke) { RequireCanvas = true }],
        GameBrowserVerification.Page => [new(WorkItemVerificationKinds.BrowserSmoke)],
        GameBrowserVerification.None => [],
        _ => null
    };

    /// <summary>
    /// The browser choice plus, when the plan names criteria needing live interaction evidence, a
    /// <see cref="WorkItemVerificationKinds.BrowserInteraction"/> requirement for exactly those criteria. Indexes that do
    /// not name one of the proposal's criteria, or interaction without a browser build, are dropped rather than recorded.
    /// </summary>
    internal static IEnumerable<string> ApprovedCriterionEvidence(GameProposedWorkItemV1 proposal) =>
        (proposal.CriterionBindings ?? []).Select(b => $"Approved criterion {b.Key} [{b.SourceProposalKey}]: {b.ApprovedText} | Evidence: " +
            (b.RequiresInteraction ? "live browser interaction on the candidate" : "independent candidate verification"));

    internal static IReadOnlyList<WorkItemVerificationRequirement>? FromProposal(GameProposedWorkItemV1 proposal)
    {
        var verifications = FromProposal(proposal.BrowserVerification);
        var indexes = (proposal.InteractionCriteria ?? []).Where(i => i >= 0 && i < proposal.AcceptanceCriteria.Count).Distinct().Order().ToArray();
        if (proposal.CriterionBindings is { } bindings)
        {
            if (bindings.Count != proposal.AcceptanceCriteria.Count ||
                bindings.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count() != bindings.Count ||
                bindings.Any(b => string.IsNullOrWhiteSpace(b.Key) || string.IsNullOrWhiteSpace(b.SourceProposalKey) ||
                    proposal.AcceptanceCriteria.Count(text => text == b.ApprovedText) != 1) ||
                bindings.Select(b => b.ApprovedText).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
                throw new InvalidOperationException("Ticket acceptance criteria differ from their bound wording.");
            var interaction = bindings.Where(b => b.RequiresInteraction).Select(b => b.ApprovedText).ToHashSet(StringComparer.Ordinal);
            indexes = proposal.AcceptanceCriteria.Select((text, index) => (text, index))
                .Where(pair => interaction.Contains(pair.text)).Select(pair => pair.index).ToArray();
            if (indexes.Length > 0 && verifications is not { Count: > 0 })
                throw new InvalidOperationException("Interaction evidence needs a runnable browser candidate.");
        }
        if (verifications is not { Count: > 0 } || indexes.Length == 0) return verifications;
        return [.. verifications, new(WorkItemVerificationKinds.BrowserInteraction) { CriterionIndexes = indexes }];
    }
}
