using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static WorkSprint? PlannedSprint(IReadOnlyList<WorkSprint> sprints) =>
        sprints.Where(x => x.Status == "Planned").OrderBy(x => x.Sequence).ThenBy(x => x.Id).FirstOrDefault();

    internal static int PlanningSprintSequence(IReadOnlyList<WorkSprint> sprints) =>
        PlannedSprint(sprints)?.Sequence ?? sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;

    // A recovery generation avoids the old completed commitments that reused Sprint 1's result.
    internal static string SprintEstimationCorrelation(Guid board, string digest, int sequence) =>
        $"{EstimationCommitmentPrefix}{board:N}:{digest}:{sequence}:sprint-v3";

    internal static IReadOnlyList<WorkItem> PendingSprintCandidates(IReadOnlyList<WorkItem> items, IReadOnlyList<WorkSprint> sprints) =>
        items.Where(item => IsSprintCandidate(item, sprints)).ToArray();

    internal static PersonalTodoResult EmptySprintCandidateScope(IReadOnlyList<WorkItem> pending,
        ProducerPlanningCycleState cycle, IReadOnlyList<DecisionRecord> decisions,
        string acceptedBriefDigest, string producerName)
    {
        if (pending.Count == 0) return PersonalTodoResult.Completed("No unscheduled executable leaves remain in the reconciled candidate scope.");
        return PersonalTodoResult.Blocked(
            $"Sprint preparation cannot continue for {pending.Count} unfinished ticket(s), including " +
            string.Join(", ", pending.Take(8).Select(item => item.Identifier ?? item.Id.ToString("D"))) + ". " +
            "These tickets have not been dispatched; the pending planning direction must be incorporated before estimates and QA readiness.\n\n" +
            DescribePlanningBlocker(cycle.OutstandingAuthorityQuestions, decisions, cycle.WorkstreamId, acceptedBriefDigest, producerName));
    }

    internal static string SprintCreationKey(Guid board, string digest, int sequence) =>
        $"producer-sprint:{board:N}:{digest}:{sequence}:v2";

    internal static bool IsSprintCandidate(WorkItem item, IReadOnlyList<WorkSprint> sprints) =>
        item.ExecutionMode == WorkItemExecutionModes.Executable &&
        item.Status is not ("Done" or WorkStatuses.Completed or WorkStatuses.Cancelled) &&
        (item.SprintId is null || sprints.Any(x => x.Id == item.SprintId && x.Status == "Planned")) &&
        item.ProposalProvenance is not null;
}
