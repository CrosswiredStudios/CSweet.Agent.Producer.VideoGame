using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    public override async Task<IncidentOwnerReport?> GenerateIncidentOwnerReportAsync(ManagerIncidentContext review,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (review.Diagnostics.CurrentState is not { } state) return null;
        // A stopped service call cannot be repaired by passive planning callbacks. This
        // deterministic assessment also corrects old cached model reports for this fault.
        if (QaOperationIncidentReport(review) is { } qaReport) return qaReport;
        if (TransportIncidentReport(review) is { } transportReport) return transportReport;
        if (review.Incident.OwnerReport is { RequiresOwnerInput: not null } saved && saved.SourceHash == state.Hash)
            return saved;
        // The shared SDK path is presentation-only. No role-local budget or fallback
        // cache can take over the durable corrective execution lifecycle.
        try { return await base.GenerateIncidentOwnerReportAsync(review, context, token); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return ManagerIncidentCoordinator.FactualFallback(review); }
    }

    internal static string IncidentReportEvidence(ManagerIncidentContext review)
    {
        static string Bound(string? value, int length) => value is null ? "" : value[..Math.Min(value.Length, length)];
        var state = review.Diagnostics.CurrentState!;
        return JsonSerializer.Serialize(new
        {
            project = Bound(state.ProjectName, 160),
            reason = Bound(review.Incident.Reason, 600),
            current = state.Facts.Take(12).Select(x => new { x.Kind, x.SourceId, Detail = Bound(x.Detail, 800) }),
            historical = review.Diagnostics.Evidence.DistinctBy(x => x.Detail).TakeLast(6)
                .Select(x => new { x.Kind, x.SourceId, Detail = Bound(x.Detail, 400) }),
            limitations = review.Diagnostics.Limitations.Take(4).Select(x => Bound(x, 300)),
            missingEvidence = Bound(review.Incident.MissingEvidence, 600),
            moreEvidenceExists = state.Facts.Count > 12 || review.Diagnostics.Evidence.Count > 6 || review.Diagnostics.NextOffset.HasValue
        });
    }

    internal static IncidentOwnerReport? QaOperationIncidentReport(ManagerIncidentContext review)
    {
        if (review.Incident.Status is "Resolved" or "Superseded" || review.Diagnostics.CurrentState is not { } state ||
            !state.Facts.Any(x => x.Kind == "WorkStageExecution" &&
                (x.Detail.Contains("Failed", StringComparison.Ordinal) || x.Detail.Contains("Blocked", StringComparison.Ordinal)) &&
                x.Detail.Contains("The QA compute operation is unavailable or its generation changed.", StringComparison.Ordinal))) return null;
        return ManagerIncidentCoordinator.FactualFallback(review)! with
        {
            Title = "QA stopped on an operation tracking mismatch",
            Summary = "QA could not continue because its saved compute operation was unavailable or belonged to a different environment generation. This error alone does not establish a broken browser environment or a product defect. The independent QA verdict remains incomplete.",
            Recovery = "Preserve the integrated source and completed technical review. Verify the current QA package and operation before retrying the stopped quality stage.",
            NextStep = "Producer: verify the QA update is active, then resume the existing quality stage. Recheck outstanding assertions against the exact integrated build.",
            RequiresOwnerInput = false, OwnerInputReason = null, Options = [], RecommendedOptionId = ""
        };
    }

    internal static IncidentOwnerReport? TransportIncidentReport(ManagerIncidentContext review)
    {
        if (review.Incident.Status is "Resolved" or "Superseded" || review.Diagnostics.CurrentState is not { } state ||
            !state.Facts.Any(x => x.Detail.Contains("Blocked", StringComparison.OrdinalIgnoreCase) &&
                (x.Detail.Contains("MCP service rejected the request", StringComparison.OrdinalIgnoreCase) ||
                 x.Detail.Contains("runtime.transport", StringComparison.OrdinalIgnoreCase)))) return null;
        var report = ManagerIncidentCoordinator.FactualFallback(review);
        return report is null ? null : report with
        {
            Title = "Implementation stopped after a platform service error",
            Recovery = "Source may be checkpointed. The failed operation and service recovery need verification; no repair or restart is confirmed.",
            NextStep = "Producer: obtain the failed operation's diagnostics from the runtime owner, verify service recovery, then retry the exact stopped stage from its checkpoint.",
            RequiresOwnerInput = false, OwnerInputReason = null, Options = [], RecommendedOptionId = ""
        };
    }
}
