using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    // Only the current authenticated message authorizes recovery, never history or model output.
    internal static bool TryReadRetryDirection(string message, out string ticket, out string reason)
    {
        var match = Regex.Match(message.Trim(), @"\ARetry ticket ([A-Za-z0-9]+-[0-9]+):\s*(.{1,1000})\z",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        ticket = match.Success ? match.Groups[1].Value : "";
        reason = match.Success ? match.Groups[2].Value.Trim() : "";
        return match.Success && reason.Length > 0;
    }

    private async Task<string> RetryDirectedTicketAsync(string identifier, string reason,
        Guid turnId, AgentRuntimeContext context, CancellationToken token, bool replan = false, bool amend = false)
    {
        var producer = Guid.Parse(context.Identity!.EmployeeId);
        var boards = (await context.Platform.Work.ListBoardsAsync(cancellationToken: token))
            .Where(x => !x.IsArchived && x.ManagerOrganizationUserId == producer).ToArray();
        if (boards.Length > 32) throw new InvalidOperationException("Recovery discovery exceeds the supported board limit.");
        var matches = new List<(WorkSprintExecutionResponse Execution, WorkItemExecutionResponse Item)>();
        foreach (var board in boards)
        {
            var sprints = (await context.Platform.Work.ListSprintsAsync(board.Id, token)).Where(x => x.Status == "Active").ToArray();
            if (sprints.Length > 32) throw new InvalidOperationException("Recovery discovery exceeds the supported sprint limit.");
            foreach (var sprint in sprints)
            {
                var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, SprintId: sprint.Id), token);
                if (execution?.Status != "Active" || execution.BoardId != board.Id || execution.SprintId != sprint.Id) continue;
                matches.AddRange(execution.Items.Where(x => string.Equals(x.ItemIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                    .Select(x => (execution, x)));
            }
        }
        if (matches.Count != 1) return "The ticket does not identify exactly one current execution on a board I manage. No retry was requested.";
        var (current, item) = matches[0];
        var stage = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
        if (amend) return await QueueScopeAmendmentAsync(current, item, stage, reason, turnId, context, token);
        if (replan) return await QueueSprintRecoveryAsync(current, item, stage, reason, context, token);
        if (stage is null || item.Status is not ("Blocked" or "Failed") ||
            stage.Status is not ("Blocked" or "Failed") || stage.PrincipalKind != "AgentInstallation" ||
            stage.AgentInstallationId is null || stage.AssignmentRevision < 1 || stage.AttemptCount < 1 ||
            stage.AttemptCount >= stage.MaximumAttempts)
            return $"{item.ItemIdentifier} has no eligible blocked agent stage with remaining attempts. No retry was requested.";
        var result = await context.Platform.Work.RetryBlockedStageAsync(new(current.BoardId, current.Id, stage.Id,
            $"producer-directed-retry:{turnId:N}:{stage.Id:N}", reason)
            { ExpectedAssignmentRevision = stage.AssignmentRevision }, token);
        return $"Retry requested for {item.ItemIdentifier}; the platform reports {result.Status}. Existing attempts and acceptance requirements are retained.";
    }
}
