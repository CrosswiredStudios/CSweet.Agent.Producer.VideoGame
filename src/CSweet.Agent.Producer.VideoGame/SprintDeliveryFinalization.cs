using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task<string?> FinalizeSprintDeliveriesAsync(
        PersonalTodoItem commitment, Guid boardId, Guid sprintId,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var board = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
        var scope = board.Items.Where(x => x.SprintId == sprintId &&
            x.ExecutionMode == WorkItemExecutionModes.Executable).ToArray();
        var pending = scope.Where(x => x.Delivery is null).ToArray();
        if (pending.Length == 0) return null;
        if (commitment.WorkContext?.WorkstreamId is not { } projectId ||
            board.Board.WorkstreamId != projectId || commitment.WorkContext.TeamId is not { } teamId ||
            board.Board.TeamId != teamId)
            return "Sprint delivery finalization requires the current project board and approved team.";

        // Select only an approved team repository. Reuse an existing binding in
        // this sprint, or the team's sole option; never invent a repository choice.
        var repositories = await context.Platform.SourceControl.ListTeamRepositoryOptionsAsync(new(teamId), cancellationToken);
        var boundIds = scope.Where(x => x.Delivery is not null).Select(x => x.Delivery!.RepositoryId).Distinct().ToArray();
        var repository = boundIds.Length == 1
            ? repositories.SingleOrDefault(x => x.RepositoryId == boundIds[0])
            : boundIds.Length == 0 && repositories.Count == 1 ? repositories[0] : null;
        if (repository is null || string.IsNullOrWhiteSpace(repository.DefaultBranch))
            return "Sprint delivery finalization is waiting for an unambiguous approved team repository with a base branch.";
        foreach (var ticket in pending)
        {
            if (ticket.Status != WorkStatuses.Ready || ticket.Planning is not { } planning ||
                planning.Requirements.Count == 0 || planning.AcceptanceCriteria.Count == 0 ||
                ticket.AccountableOrganizationUserId is null || ticket.StageAssignments.Count == 0)
                return $"Sprint delivery finalization is waiting for current planning, a Ready state, an accountable owner and approved stage assignments on {ticket.Title}.";
        }
        foreach (var ticket in pending)
        {
            var planning = ticket.Planning!;
            var delivery = new WorkItemDeliverySpecification(repository.RepositoryId,
                planning.Requirements, planning.AcceptanceCriteria, planning.Constraints)
            {
                BaseBranch = repository.DefaultBranch,
                DependencyItemIds = planning.DependencyItemIds
            };
            // The host rechecks current approvals, assignment eligibility and
            // the expected revision. Finalization never bypasses sprint preflight.
            await context.Platform.Work.FinalizeItemDeliveryAsync(new(boardId, ticket.Id, delivery,
                ticket.AccountableOrganizationUserId!.Value, ticket.StageAssignments, ticket.Revision,
                $"producer-sprint-finalize:{sprintId:N}:{ticket.Id:N}:{ticket.Revision}"), cancellationToken);
        }
        return null;
    }
}
