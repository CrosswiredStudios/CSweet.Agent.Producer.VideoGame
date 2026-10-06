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
                if (stage is null) continue;
                var request = CorrectableDeliveryRetry(board.Id, execution.Id, stage);
                if (request is null && stage.LatestOutcome?.Diagnostics?.Contains(TicketConversations.Discussion.Waiting) == true)
                {
                    var comments = await TicketConversations.Discussion.ReadAsync(board.Id, item.WorkItemId, context, token);
                    request = AnsweredDiscussionRetry(board.Id, execution.Id, item.WorkItemId, stage, comments);
                }
                if (request is null) continue;
                await context.Platform.Work.RetryBlockedStageAsync(request, token);
            }
        }
    }

    internal static RetryWorkStageExecutionRequest? AnsweredDiscussionRetry(Guid boardId, Guid executionId, Guid itemId,
        WorkStageExecutionResponse stage, IReadOnlyList<WorkItemComment> comments)
    {
        if (stage.Status != "Blocked" || stage.LatestOutcome?.Disposition != WorkExecutionDispositions.Blocked ||
            stage.LatestOutcome.Diagnostics?.Contains(TicketConversations.Discussion.Waiting) != true ||
            stage.AssignmentRevision < 1 || stage.AttemptCount < 1 || stage.AttemptCount >= stage.MaximumAttempts) return null;
        TicketConversations.Discussion.ResponseWait? wait;
        try { wait = stage.LatestOutcome.Output.Deserialize<TicketConversations.Discussion.ResponseWait>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
        if (wait is null || wait.BoardId != boardId || wait.ItemId != itemId ||
            !comments.Any(c => c.Id == wait.CommentId && c.Revision == wait.CommentRevision && c.Kind == "discussion.request" &&
                c.AuthorKind == "AgentInstallation" && c.AuthorSubjectId == stage.AgentInstallationId)) return null;
        var correlation = TicketConversations.Discussion.Correlation(wait.CommentId, wait.CommentRevision);
        if (!comments.Any(c => c.Kind == "discussion.reply" && c.CausationId == correlation &&
            c.AuthorKind == "AgentInstallation" && c.AuthorSubjectId == wait.RespondingInstallationId)) return null;
        return new(boardId, executionId, stage.Id, $"producer-discussion-resume:{stage.Id:N}:{correlation}",
            "The requested teammate replied on the ticket. Read the discussion and continue the existing assignment; all review and validation requirements remain in force.")
            { ExpectedAssignmentRevision = stage.AssignmentRevision };
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
            foreach (var item in execution.Items.Where(x => x.Status is "Blocked" or "Failed"))
            {
                var stage = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
                if ((DecisionEscalation(item, stage) ?? DispatchBlockerEscalation(item, stage) ??
                        UnhandledBlockerEscalation(board.Id, execution.Id, item, stage, DateTimeOffset.UtcNow)) is not { } escalation) continue;
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

    /// <summary>
    /// The platform refused to hand a ticket to its assignee (for example, the assignee isn't a participant of the
    /// project), so no attempt ran and there is no specialist outcome to act on. The platform enrolls staffing hires
    /// itself, so what remains needs someone with authority over the project: raise it once with the exact reason.
    /// </summary>
    internal static (string Key, string Content)? DispatchBlockerEscalation(WorkItemExecutionResponse item, WorkStageExecutionResponse? stage)
    {
        if (stage is null || item.Status != "Blocked" || stage.Status != "Blocked" || stage.LatestOutcome is not null ||
            stage.AttemptCount != 0 || stage.PrincipalKind != "AgentInstallation") return null;
        var reason = (string.IsNullOrWhiteSpace(stage.LastError) ? item.BlockedReason : stage.LastError)?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return null;
        if (reason.Length > 2500) reason = reason[..2497] + "...";
        var id = item.ItemIdentifier;
        var content = $"{id} can't start, and nothing on the team can clear it without you.\n\n{reason}\n\n" +
            "The platform didn't hand the ticket to its assignee, so no work or attempt was spent. " +
            (reason.Contains("project.", StringComparison.Ordinal)
                ? "If the assignee should work on this project, add them under Projects → Manage members. "
                : "") +
            $"Once it's fixed, reply `Retry ticket {id}: <what changed>`, or `Amend ticket {id}: <your decision>` to change the plan.";
        // One escalation per distinct reason for this never-started stage.
        return ($"{DecisionEscalationPrefix}{stage.Id:N}:dispatch:{AcceptanceDigest(reason)[..16]}", content);
    }

    /// <summary>How long a specific recovery path (format retry, role repair, decision routing) gets before the fallback.</summary>
    internal static readonly TimeSpan UnhandledBlockerGrace = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The fallback that keeps any stopped ticket from stalling unseen. A Blocked or Failed stage that no specific
    /// recovery step has moved within <see cref="UnhandledBlockerGrace"/> is raised to my manager once per attempt and
    /// reason, with the reason and the replies that move it: retry, amend, or replan. Whatever state a ticket ends up
    /// in, someone who can decide hears about it.
    /// </summary>
    internal static (string Key, string Content)? UnhandledBlockerEscalation(Guid boardId, Guid executionId,
        WorkItemExecutionResponse item, WorkStageExecutionResponse? stage, DateTimeOffset now)
    {
        if (stage is null || item.Status is not ("Blocked" or "Failed") || stage.Status is not ("Blocked" or "Failed")) return null;
        if (now - stage.UpdatedAt < UnhandledBlockerGrace) return null;
        if (CorrectableDeliveryRetry(boardId, executionId, stage) is not null) return null; // I retry those myself.
        var reason = new[] { stage.LatestOutcome?.Summary, stage.LastError, stage.LastSummary, item.BlockedReason }
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "No reason was recorded on the stage.";
        if (reason.Length > 2500) reason = reason[..2497] + "...";
        var id = item.ItemIdentifier;
        var state = stage.Status == "Failed" ? "marked failed" : "blocked";
        var exhausted = stage.MaximumAttempts > 0 && stage.AttemptCount >= stage.MaximumAttempts;
        var content = $"{id} has been {state} since {stage.UpdatedAt:yyyy-MM-dd HH:mm} UTC, and none of my recovery steps apply, " +
            $"so the team can't move it forward without a decision.\n\n{reason}\n\n" +
            (exhausted
                ? $"This stage has used its attempt budget. Reply `Replan ticket {id}: <direction>` to carry it into a replanned sprint, "
                : $"Once the cause is addressed, reply `Retry ticket {id}: <what changed>`, ") +
            $"or `Amend ticket {id}: <your decision>` to change what the ticket requires.";
        return ($"{DecisionEscalationPrefix}{stage.Id:N}:stalled:{stage.AttemptCount}:{AcceptanceDigest(reason)[..16]}", content);
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
