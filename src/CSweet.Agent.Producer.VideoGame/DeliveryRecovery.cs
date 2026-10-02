using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private async Task RetryCorrectableDeliveryFailuresAsync(WorkBoardSummary board, AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var self) || board.ManagerOrganizationUserId != self) return;
        foreach (var sprint in (await context.Platform.Work.ListSprintsAsync(board.Id, token)).Where(x => x.Status == "Active"))
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, SprintId: sprint.Id), token);
            if (execution?.Status != "Active") continue;
            foreach (var item in execution.Items.Where(x => x.Status == "Blocked"))
            {
                var stage = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
                if (stage is null || CorrectableDeliveryRetry(board.Id, execution.Id, stage) is not { } request) continue;
                await context.Platform.Work.RetryBlockedStageAsync(request, token);
            }
        }
    }

    /// <summary>Cross-agent convention: a Blocked outcome carrying this diagnostic needs a management decision.</summary>
    internal const string DecisionRequiredDiagnostic = "decision-required:v1";
    private const string DecisionEscalationPrefix = "producer-decision:";

    /// <summary>
    /// A producer does not sit on a blocker only the owner can clear. When a specialist or QA reports that a ticket
    /// needs a scope, criteria, environment or tooling decision, raise it to my manager once per blocked attempt,
    /// with the exact commands that let the owner amend the ticket or direct a retry.
    /// </summary>
    private async Task EscalateDecisionBlockersAsync(WorkBoardSummary board, AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var self) || board.ManagerOrganizationUserId != self ||
            !Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) || manager == Guid.Empty || manager == self) return;
        foreach (var sprint in (await context.Platform.Work.ListSprintsAsync(board.Id, token)).Where(x => x.Status == "Active"))
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, SprintId: sprint.Id), token);
            if (execution?.Status != "Active") continue;
            foreach (var item in execution.Items.Where(x => x.Status == "Blocked"))
            {
                var stage = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
                if (DecisionEscalation(item, stage) is not { } escalation) continue;
                try
                {
                    if (await context.Platform.ReadOperatingStateAsync<ProducerDecisionEscalation>(escalation.Key, token) is not null) continue;
                    await context.Platform.Communication.SendDirectMessageAsync(manager, escalation.Content, escalation.Key, token);
                    await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(escalation.Key,
                        "video-game.producer-decision-escalation.v1", 1, "Active", new Dictionary<string, string>(), [], escalation.Key, [],
                        Guid.NewGuid(), JsonSerializer.SerializeToElement(new ProducerDecisionEscalation(item.ItemIdentifier, stage!.Id,
                            stage.AttemptCount, manager, DateTimeOffset.UtcNow)), null, escalation.Key), token);
                }
                catch (PlatformCapabilityException)
                {
                    // Escalation is advisory; the platform has already notified the board manager. Retry on the next scan.
                }
            }
        }
    }

    internal static (string Key, string Content)? DecisionEscalation(WorkItemExecutionResponse item, WorkStageExecutionResponse? stage)
    {
        var outcome = stage?.LatestOutcome;
        if (stage is null || item.Status != "Blocked" || stage.Status != "Blocked" ||
            outcome?.Disposition != WorkExecutionDispositions.Blocked ||
            !(outcome.Diagnostics ?? []).Contains(DecisionRequiredDiagnostic, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(outcome.Summary)) return null;
        var summary = outcome.Summary.Trim();
        if (summary.Length > 2500) summary = summary[..2497] + "...";
        var id = item.ItemIdentifier;
        var content = $"{id} is blocked and needs your decision. Nothing more can be done on it in code or QA until you decide.\n\n" +
            $"{summary}\n\n" +
            $"To change or defer the affected criteria, reply `Amend ticket {id}: <your decision>`. I'll apply it through the normal " +
            "replanning, estimate and readiness flow, keeping completed work and independent QA. " +
            (stage.AttemptCount < stage.MaximumAttempts
                ? $"Once the missing environment or direction is available, reply `Retry ticket {id}: <what changed>`."
                : $"This stage has used its attempt budget, so a plain retry is not available; reply `Replan ticket {id}: <direction>` to carry it into a replanned sprint.");
        // One escalation per blocked attempt; a new attempt that blocks again is a new decision request.
        return ($"{DecisionEscalationPrefix}{stage.Id:N}:{stage.AttemptCount}", content);
    }

    // Rejections raised by the specialist's own deliverable validator (SubstantiveOutputValidator). They describe
    // the generated document, not missing evidence, authority or QA, so another generation attempt can fix them.
    private static readonly string[] CorrectableDeliverableFailures =
    [
        "The deliverable is missing required section '",
        "The deliverable contains unresolved placeholder text",
        "The durable deliverable is too short to be substantive"
    ];

    internal static bool IsCorrectableDeliverableFailure(string reason) =>
        CorrectableDeliverableFailures.Any(prefix => reason.StartsWith(prefix, StringComparison.Ordinal));

    internal static RetryWorkStageExecutionRequest? CorrectableDeliveryRetry(Guid boardId, Guid executionId, WorkStageExecutionResponse stage)
    {
        // Retry only structural generation failures, never missing real-world evidence, authority,
        // failed QA, or unavailable dependencies. The host still enforces current ownership and budget.
        var reason = stage.LatestOutcome?.Summary;
        if (stage.Status != "Blocked" || stage.StageKey != "specialist-execution" ||
            stage.PrincipalKind != "AgentInstallation" || stage.AgentInstallationId is null ||
            stage.LatestOutcome?.Disposition != WorkExecutionDispositions.Blocked ||
            stage.AssignmentRevision < 1 || stage.AttemptCount < 1 || stage.AttemptCount >= stage.MaximumAttempts ||
            reason is null || !IsCorrectableDeliverableFailure(reason)) return null;
        // Deliberately independent of attempt number: duplicates, reconnects, and a repeated identical
        // formatting defect reuse the host's receipt instead of repeatedly spending attempts.
        var key = $"producer-format-retry:{stage.Id:N}:{AcceptanceDigest(reason)}";
        return new(boardId, executionId, stage.Id, key,
            "Retry the deliverable that failed its own structural validation within the existing stage attempt budget; substantive acceptance remains required.")
        { ExpectedAssignmentRevision = stage.AssignmentRevision };
    }
}

internal sealed record ProducerDecisionEscalation(string ItemIdentifier, Guid StageExecutionId, int AttemptCount,
    Guid ManagerEmployeeId, DateTimeOffset EscalatedAt);
