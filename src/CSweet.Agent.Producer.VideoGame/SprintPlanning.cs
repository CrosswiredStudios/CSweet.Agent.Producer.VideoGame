using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record SprintPreparation(string Scope, string Digest);
    private static string PreparationKey(Guid sprint) => "producer-sprint-preparation:" + sprint.ToString("N");
    private static string PreparedScope(IEnumerable<WorkItem> tickets, long rosterRevision) =>
        ProducerPolicyFingerprint.Digest(System.Text.Json.JsonSerializer.Serialize(new { rosterRevision,
            tickets = tickets.OrderBy(t => t.Id).Select(t => new { t.Id, t.PlanningRevision, t.EstimatePoints,
                estimate = t.EstimateProvenance?.SourceDigest, author = PrimaryExecutionAssignment(t)?.AgentInstallationId }) }));

    internal static IReadOnlyList<WorkTechnicalDelegationRecommendation> MissingSprintStaff(WorkItem ticket,
        AgentTeamContext roster, Guid? manager, bool adaptive)
    {
        var missing = new List<WorkTechnicalDelegationRecommendation>();
        var declared = ticket.Planning?.DelegationRecommendations ?? [];
        var author = PrimaryExecutionAssignment(ticket);
        var authorRole = declared.FirstOrDefault(d => d.StageKey == "specialist-execution")?.RequiredRoleKey ??
            author?.Requirements?.RequiredRoleKey;
        var label = ticket.Identifier ?? ticket.Title;
        foreach (var requirement in declared)
        {
            var assignment = ticket.StageAssignments.SingleOrDefault(a => a.StageKey == requirement.StageKey);
            var needed = new WorkAssignmentRequirements(requirement.RequiredRoleKey, requirement.RequiredSpecializationKeys,
                requirement.PreferredSpecializationKeys, requirement.RequiredCapabilityKeys);
            if (!roster.Members.Any(m => m.AgentInstallationId == assignment?.AgentInstallationId &&
                m.EmployeeId == assignment?.OrganizationUserId?.ToString() && RoleTaxonomy.IsEligible(m, needed)))
                missing.Add(requirement with { Rationale = label + ": " + requirement.Rationale });
        }
        if (authorRole is null) return missing; // The draft needs an author before it can be committed.
        if (!declared.Any(d => d.StageKey == "specialist-execution") && author?.Requirements is { } authorRequirements &&
            !roster.Members.Any(m => m.AgentInstallationId == author.AgentInstallationId && m.EmployeeId == author.OrganizationUserId?.ToString() &&
                RoleTaxonomy.IsEligible(m, authorRequirements)))
            missing.Add(new("specialist-execution", authorRole, authorRequirements.RequiredCapabilityKeys, null, true,
                label + ": The recorded author is unavailable."));
        bool Independent(AgentTeammate member) => member.AgentInstallationId != author?.AgentInstallationId &&
            member.EmployeeId != author?.OrganizationUserId?.ToString();
        void Require(string stage, string role, string reason, bool excludeAuthor, Guid? exact = null)
        {
            var requirements = new WorkAssignmentRequirements(role, [], [], [WorkManagementCapabilityNames.ExecutionRun]);
            if (missing.Any(d => d.StageKey == stage) || roster.Members.Any(m =>
                (!exact.HasValue || m.EmployeeId == exact.ToString()) && (!excludeAuthor || Independent(m)) &&
                RoleTaxonomy.IsEligible(m, requirements))) return;
            missing.Add(new(stage, role, requirements.RequiredCapabilityKeys, null, true, label + ": " + reason));
        }
        if (!adaptive)
        {
            Require("quality", "game-quality-assurance", "Sprint readiness needs QA.", false);
            return missing;
        }
        var qaAuthor = RoleTaxonomy.CoreRoleKey(authorRole) == "software-qa";
        if (qaAuthor || !PlannedDeliveryReview.NeedsSpecialist(ticket, authorRole) && manager != author?.OrganizationUserId)
            Require("quality", "manager", "The accountable manager must be available for independent document acceptance.", true, manager);
        else
            Require("quality", "game-quality-assurance", "Independent verification is required for this deliverable.", true);
        if (PlannedDeliveryReview.DeliversCode(ticket, authorRole))
            Require("technical-review", "game-technical-director", "Integrated code requires technical review.", true);
        return missing;
    }

    internal static WorkSprint? PlannedSprint(IReadOnlyList<WorkSprint> sprints) =>
        sprints.Where(x => x.Status == "Planned").OrderBy(x => x.Sequence).ThenBy(x => x.Id).FirstOrDefault();

    internal static int PlanningSprintSequence(IReadOnlyList<WorkSprint> sprints) =>
        PlannedSprint(sprints)?.Sequence ?? sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;

    // A recovery generation avoids the old completed commitments that reused Sprint 1's result.
    internal static string SprintEstimationCorrelation(Guid board, string digest, int sequence) =>
        $"{EstimationCommitmentPrefix}{board:N}:{digest}:{sequence}:sprint-v5";

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
