using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    // QA executes validation and delivers evidence, not the implementation under test.
    internal static string ProposalDeliveryKind(string authorRole) =>
        authorRole == "game-quality-assurance" ? "Artifact" : "Code";

    internal static bool NeedsQaReportPlanningRepair(WorkItem item)
    {
        if (item.ExecutionMode != WorkItemExecutionModes.Executable || item.Delivery is not null ||
            item.Status is not ("Ready" or "Open" or "Backlog" or "Planned") || item.Planning is not { DeliveryKind: "Code" } planning)
            return false;
        var role = planning.DelegationRecommendations.FirstOrDefault(x => x.StageKey == "specialist-execution")?.RequiredRoleKey;
        var assignedRole = item.StageAssignments.FirstOrDefault(x => x.StageKey == "specialist-execution")?.Requirements?.RequiredRoleKey;
        return (role ?? assignedRole) == "game-quality-assurance" &&
            (assignedRole is null || assignedRole == "game-quality-assurance");
    }

    internal static async Task RepairQaReportPlanningAsync(WorkBoardSummary board,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (board.ManagerOrganizationUserId?.ToString() != context.Identity?.EmployeeId || board.WorkstreamId is null)
            throw new InvalidOperationException("QA report planning repair requires the current project board manager.");
        var detail = await context.Platform.Work.ReadBoardAsync(board.Id, token);
        var candidates = detail.Items.Where(NeedsQaReportPlanningRepair).ToArray();
        if (candidates.Length == 0) return;
        var sprints = await context.Platform.Work.ListSprintsAsync(board.Id, token);
        foreach (var candidate in candidates)
        {
            if (candidate.SprintId is { } sprintId)
            {
                if (!sprints.Any(x => x.Id == sprintId && x.Status == "Planned")) continue;
                // A stopped or cancelled execution is historical work, not permission to reclassify it.
                // SDK 3.62.2's nullable convenience read rejects a legitimate JSON null.
                // Use the public capability client and typed request to distinguish absence from errors.
                var execution = await context.Platform.InvokeAsync<ReadWorkOrchestrationRequest, JsonElement>(
                    WorkOrchestrationCapabilities.Read, new(board.Id, sprintId), token);
                if (execution.ValueKind != JsonValueKind.Null) continue;
            }
            var current = await context.Platform.Work.ReadItemAsync(new(board.Id, candidate.Id), token);
            if (!NeedsQaReportPlanningRepair(current) || current.SprintId != candidate.SprintId) continue;
            await context.Platform.Work.RevisePlanningAsync(new(board.Id, current.Id, current.Title,
                current.Description, current.ParentItemId, current.Planning! with { DeliveryKind = "Artifact" },
                current.Revision, current.PlanningRevision,
                $"producer-qa-report-kind:{current.Id:N}:{current.Revision}:{current.PlanningRevision}")
            {
                ProposalProvenance = current.ProposalProvenance,
                AccountableOrganizationUserId = current.AccountableOrganizationUserId,
                StageAssignments = current.StageAssignments
            }, token);
        }
    }
}
