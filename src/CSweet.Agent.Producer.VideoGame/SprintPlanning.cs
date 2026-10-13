using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static WorkSprint? PlannedSprint(IReadOnlyList<WorkSprint> sprints) =>
        sprints.Where(x => x.Status == "Planned").OrderBy(x => x.Sequence).ThenBy(x => x.Id).FirstOrDefault();

    internal static int PlanningSprintSequence(IReadOnlyList<WorkSprint> sprints) =>
        PlannedSprint(sprints)?.Sequence ?? sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;

    // A recovery generation avoids the old completed commitments that reused Sprint 1's result.
    internal static string SprintEstimationCorrelation(Guid board, string digest, int sequence) =>
        $"{EstimationCommitmentPrefix}{board:N}:{digest}:{sequence}:sprint-v2";

    internal static string SprintCreationKey(Guid board, string digest, int sequence) =>
        $"producer-sprint:{board:N}:{digest}:{sequence}:v2";

    internal static bool IsSprintCandidate(WorkItem item, IReadOnlyList<WorkSprint> sprints) =>
        item.ExecutionMode == WorkItemExecutionModes.Executable &&
        item.Status is not ("Done" or WorkStatuses.Completed or WorkStatuses.Cancelled) &&
        (item.SprintId is null || sprints.Any(x => x.Id == item.SprintId && x.Status == "Planned")) &&
        item.ProposalProvenance is not null;
}
