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

    internal static RetryWorkStageExecutionRequest? CorrectableDeliveryRetry(Guid boardId, Guid executionId, WorkStageExecutionResponse stage)
    {
        // Retry only structural generation failures, never missing real-world evidence, authority,
        // failed QA, or unavailable dependencies. The host still enforces current ownership and budget.
        var reason = stage.LatestOutcome?.Summary;
        if (stage.Status != "Blocked" || stage.StageKey != "specialist-execution" ||
            stage.PrincipalKind != "AgentInstallation" || stage.AgentInstallationId is null ||
            stage.LatestOutcome?.Disposition != WorkExecutionDispositions.Blocked ||
            stage.AssignmentRevision < 1 || stage.AttemptCount < 1 || stage.AttemptCount >= stage.MaximumAttempts ||
            reason is null || !reason.StartsWith("The deliverable is missing required section '", StringComparison.Ordinal)) return null;
        // Deliberately independent of attempt number: duplicates, reconnects, and a repeated identical
        // formatting defect reuse the host's receipt instead of repeatedly spending attempts.
        var key = $"producer-format-retry:{stage.Id:N}:{AcceptanceDigest(reason)}";
        return new(boardId, executionId, stage.Id, key,
            "Retry the structurally incomplete deliverable within the existing stage attempt budget; substantive acceptance remains required.")
        { ExpectedAssignmentRevision = stage.AssignmentRevision };
    }
}
