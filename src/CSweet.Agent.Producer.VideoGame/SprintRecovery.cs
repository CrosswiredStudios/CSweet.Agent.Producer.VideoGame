using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private const string SprintRecoveryPrefix = "producer-sprint-recovery:";

    internal static bool CanRecoverExhaustedSprint(WorkSprintExecutionResponse execution, Guid itemId, Guid stageId)
    {
        var item = execution.Items.SingleOrDefault(x => x.WorkItemId == itemId);
        var stage = item?.Stages.SingleOrDefault(x => x.Id == stageId &&
            x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
        return execution.Status == "Active" && item?.Status is "Blocked" or "Failed" &&
            stage?.Status is "Blocked" or "Failed" && stage.PrincipalKind == "AgentInstallation" &&
            stage.AgentInstallationId.HasValue && stage.AttemptCount >= stage.MaximumAttempts &&
            stage.MaximumAttempts > 0 && stage.AssignmentRevision > 0 &&
            !execution.Items.Any(x => x.Status is "Running" or "WaitingForApproval" ||
                x.Stages.Any(s => s.Status is "Running" or "Dispatching" or "WaitingForApproval"));
    }

    private static async Task<string> QueueSprintRecoveryAsync(WorkSprintExecutionResponse execution,
        WorkItemExecutionResponse item, WorkStageExecutionResponse? stage, string reason,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (stage is null || !CanRecoverExhaustedSprint(execution, item.WorkItemId, stage.Id))
            return "Replanning requires an exhausted current agent stage and no running work or pending approval. No sprint was changed.";
        var board = await context.Platform.Work.ReadBoardAsync(execution.BoardId, token);
        if (board.Board.ManagerOrganizationUserId?.ToString() != context.Identity?.EmployeeId ||
            board.Board.WorkstreamId is not { } project || board.Board.TeamId is not { } team)
            throw new InvalidOperationException("Recovery requires my current project/team board assignment.");
        var key = SprintRecoveryPrefix + stage.Id.ToString("N");
        var saved = await ReadRoleRepairRequestAsync(key, context, token);
        if (saved is null)
        {
            var sprints = await context.Platform.Work.ListSprintsAsync(execution.BoardId, token);
            saved = new(execution.BoardId, project, team, execution.SprintId, stage.Id, item.WorkItemId,
                [], [reason], board.Items, sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1, DateTimeOffset.UtcNow)
                { InfrastructureRecovery = true };
            await PersistRoleRepairRequestAsync(key, saved, context, token);
        }
        await EnsureCommitmentAsync(key, "Recover exhausted sprint after infrastructure repair",
            "Preserve completed work, scope and attempt history. Carry unfinished tickets into one planned sprint and refresh readiness before execution.",
            WorkPriorities.Critical, new PersonalTodoWorkContext { WorkstreamId = project, TeamId = team,
                BoardId = execution.BoardId, SprintId = execution.SprintId, WorkItemId = item.WorkItemId, SourceFingerprint = key }, context, token);
        return "Queued durable sprint recovery. Existing attempts remain in history; the replacement sprint must pass estimates and readiness before starting.";
    }

    private static async Task<PersonalTodoResult> ReconcileSprintRecoveryAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        var request = await ReadRoleRepairRequestAsync(item.CorrelationId!, context, token);
        if (request?.InfrastructureRecovery != true || item.WorkContext?.BoardId != request.BoardId ||
            item.WorkContext?.WorkItemId != request.WorkItemId)
            return PersonalTodoResult.Blocked("The exact recovery request is unavailable.");
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        if (!boards.Any(x => x.Id == request.BoardId && !x.IsArchived &&
                x.ManagerOrganizationUserId?.ToString() == context.Identity?.EmployeeId))
            return PersonalTodoResult.Blocked("The Producer no longer manages the recovery board.");
        await PrepareRoleRepairSprintAsync(request, context, token);
        return PersonalTodoResult.Completed("Unfinished scope has been carried to the replacement sprint. Normal estimates, QA readiness and preflight govern its execution.");
    }
}
