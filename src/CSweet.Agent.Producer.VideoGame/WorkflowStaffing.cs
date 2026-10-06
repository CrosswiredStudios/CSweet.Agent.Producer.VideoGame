using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private const string WorkflowRecoveryPrefix = "producer-workflow-recovery:";

    internal static IReadOnlyList<WorkTechnicalDelegationRecommendation> WorkflowRequirements(WorkOrchestrationPolicyRevision policy)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(policy.InitialStageKey);
        while (pending.TryPop(out var key))
            if (reachable.Add(key))
                foreach (var edge in policy.Transitions.Where(x => x.FromStageKey == key)) pending.Push(edge.ToStageKey);
        return policy.Stages.Where(s => reachable.Contains(s.Key) && s.StageType == "AgentExecution")
            .Select(s => new WorkTechnicalDelegationRecommendation(s.Key, s.Key switch
            {
                "technical-review" or "merge-decision" => "game-technical-director",
                "quality" => "game-quality-assurance",
                "development" => "software-developer",
                "specialist-execution" => "game-engineer",
                _ => throw new InvalidOperationException($"The Producer needs an explicit role mapping for workflow stage '{s.Key}'.")
            }, ["work.execution.run.v1"], null, true, $"Required by the approved workflow: {s.Name}."))
            .ToArray();
    }

    internal static IReadOnlyList<WorkStageAssignment> PreserveStartedAssignments(WorkItem ticket,
        IReadOnlyList<WorkStageAssignment> proposed, WorkItemExecutionResponse? execution)
    {
        if (execution is null) return proposed;
        var started = execution.Stages.Where(s => s.AttemptCount > 0 || s.Status is not ("Pending" or "Blocked"))
            .Select(s => s.StageKey).ToHashSet(StringComparer.Ordinal);
        return proposed.Where(a => !started.Contains(a.StageKey))
            .Concat(ticket.StageAssignments.Where(a => started.Contains(a.StageKey))).ToArray();
    }

    // Re-read board, roster and execution for every recovery. An event is a wake hint,
    // never a grant to change ownership. Host revision checks arbitrate dispatch races.
    internal static async Task<string?> StaffWorkflowAsync(WorkBoardSummary board, AgentTeamContext roster,
        string profileDigest, AgentRuntimeContext context, CancellationToken token)
    {
        if (board.ManagerOrganizationUserId is not { } manager || manager.ToString() != context.Identity?.EmployeeId || board.WorkstreamId is not { } project)
            return "Workflow staffing requires the current board manager and project.";
        var configured = await context.Platform.Work.ConfigureProfileOrchestrationAsync(new(project, board.Id,
            board.Revision, profileDigest, $"producer-profile:{project:N}:{profileDigest}"), token);
        var requirements = WorkflowRequirements(configured.Policy);
        var detail = await context.Platform.Work.ReadBoardAsync(board.Id, token);
        if (configured.Policy.Stages.Any(x => x.Key == "task-integration"))
        {
            var repositories = await context.Platform.SourceControl.ListTeamRepositoryOptionsAsync(new(board.TeamId!.Value), token);
            if (repositories.Count > 1) return "Select explicit repository bindings before configuring this release; multiple authorized repositories are available.";
            var repository = repositories.SingleOrDefault();
            await HierarchicalProjectDelivery.PrepareAsync(project, board.Id, manager, roster,
                repository?.RepositoryId ?? Guid.Empty, repository?.DefaultBranch ?? "", profileDigest, false, context, token);
            return null;
        }
        var sprints = await context.Platform.Work.ListSprintsAsync(board.Id, token);
        var executions = new Dictionary<Guid, WorkSprintExecutionResponse>();
        foreach (var sprint in sprints.Where(s => s.Status is "Active" or "Paused"))
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, sprint.Id), token);
            if (execution is null || execution.PolicyRevisionId != configured.Policy.RevisionId)
                return "The active sprint policy differs from the project workflow; manager review is required.";
            executions[sprint.Id] = execution;
        }
        var gaps = new List<string>();
        var missingCoverage = new List<WorkTechnicalDelegationRecommendation>();
        foreach (var ticket in detail.Items.Where(t => t.ExecutionMode == WorkItemExecutionModes.Executable &&
                     t.Planning is not null && t.Status is not ("Done" or "Completed" or "Cancelled") &&
                     (t.SprintId is null || sprints.Any(s => s.Id == t.SprintId && s.Status is "Planned" or "Active" or "Paused"))))
        {
            var execution = ticket.SprintId is { } sprintId && executions.TryGetValue(sprintId, out var active)
                ? active.Items.SingleOrDefault(i => i.WorkItemId == ticket.Id) : null;
            var proposed = RefreshAssignments(ticket, roster, profileDigest, requirements);
            var assignments = PreserveStartedAssignments(ticket, proposed, execution);
            var missing = requirements.Where(r => !assignments.Any(a => a.StageKey == r.StageKey)).ToArray();
            if (missing.Length > 0)
            {
                missingCoverage.AddRange(missing);
                gaps.Add($"{ticket.Title}: assign {string.Join(", ", missing.Select(x => x.StageKey))}");
                continue;
            }
            var changed = JsonSerializer.Serialize(assignments.OrderBy(a => a.StageKey), ManagerJson) !=
                JsonSerializer.Serialize(ticket.StageAssignments.OrderBy(a => a.StageKey), ManagerJson);
            var staffingBlock = execution?.Stages.Any(s => s.StageKey == execution.CurrentStageKey &&
                s.Status == "Blocked" && s.LastError == "staffing.assignment_missing") == true;
            if (!changed && !staffingBlock) continue;
            var key = $"producer-workflow-bind:{ticket.Id:N}:{ticket.Revision}:{roster.Revision}";
            if (ticket.Delivery is not null && ticket.AccountableOrganizationUserId is { } owner)
                await context.Platform.Work.FinalizeItemDeliveryAsync(new(board.Id, ticket.Id, ticket.Delivery,
                    owner, assignments, ticket.Revision, key), token);
            else if (execution is null)
                await context.Platform.Work.RevisePlanningAsync(new(board.Id, ticket.Id, ticket.Title, ticket.Description,
                    ticket.ParentItemId, ticket.Planning!, ticket.Revision, ticket.PlanningRevision, key)
                {
                    ProposalProvenance = ticket.ProposalProvenance,
                    AccountableOrganizationUserId = ticket.AccountableOrganizationUserId ??
                        assignments.FirstOrDefault(a => a.StageKey is "development" or "specialist-execution")?.OrganizationUserId,
                    StageAssignments = assignments
                }, token);
            else gaps.Add($"{ticket.Title}: active work has no approved delivery specification; replan is required.");
        }
        if (missingCoverage.Count > 0)
            await ProposeCoverageAsync(project, board.Id, roster, missingCoverage, context, token);
        return gaps.Count == 0 ? null : string.Join("; ", gaps);
    }

    private static async Task QueueWorkflowRecoveryAsync(WorkBoardSummary board, string profileDigest,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (board.TeamId is not { } team || board.WorkstreamId is not { } project ||
            board.ManagerOrganizationUserId is not { } manager || manager.ToString() != context.Identity?.EmployeeId) return;
        var detail = await context.Platform.Work.ReadBoardAsync(board.Id, token);
        if (!detail.Items.Any(t => t.ExecutionMode == WorkItemExecutionModes.Executable &&
                t.Status is not ("Done" or "Completed" or "Cancelled"))) return;
        var roster = await ReadManagerTeamAsync(team, context, token);
        var fingerprint = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new
        {
            board.Id, profileDigest, rosterRevision = roster?.Revision,
            items = detail.Items.OrderBy(t => t.Id).Select(t => new { t.Id, t.Revision })
        }, ManagerJson));
        var prefix = WorkflowRecoveryPrefix + board.Id.ToString("N") + ":";
        var outstanding = (await context.Platform.PersonalTodo.ListAsync(token)).Boards.SelectMany(b => b.Items)
            .FirstOrDefault(t => t.ArchivedAt is null && t.CorrelationId?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                t.Status is not ("Done" or "Completed" or "Cancelled"));
        if (outstanding?.Wait is not null && outstanding.WorkContext?.SourceFingerprint == fingerprint) return;
        await EnsureCommitmentAsync(outstanding?.CorrelationId ?? prefix + fingerprint[..16],
            "Maintain delivery assignments and recover stopped tickets",
            "Assign every required future stage from the current team. Preserve started work and published commits. " +
            "Recover missing assignments; use bounded retries for known correctable failures and escalate remaining decisions.",
            WorkPriorities.Critical, new(WorkstreamId: project, TeamId: team, BoardId: board.Id,
                SourceFingerprint: fingerprint), context, token);
    }

    private async Task<PersonalTodoResult> ReconcileWorkflowRecoveryAsync(PersonalTodoItem commitment,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (commitment.WorkContext?.BoardId is not { } boardId || commitment.WorkContext.TeamId is not { } team ||
            commitment.WorkContext.WorkstreamId is not { } project)
            return PersonalTodoResult.Blocked("Recovery requires the authoritative board, project and team.");
        var board = (await context.Platform.Work.ReadBoardAsync(boardId, token)).Board;
        if (board.TeamId != team || board.WorkstreamId != project || board.ManagerOrganizationUserId is not { } manager || manager.ToString() != context.Identity?.EmployeeId)
            return PersonalTodoResult.Blocked("The Producer no longer manages this board and team.");
        var workstream = await context.Platform.ReadWorkstreamAsync(new(project), token);
        var roster = await ReadManagerTeamAsync(team, context, token);
        if (roster is null) return PersonalTodoResult.Blocked("The approved team is unavailable.");
        var gap = await StaffWorkflowAsync(board, roster, workstream.ProfileDefinitionDigest ?? "", context, token);
        await RetryCorrectableDeliveryFailuresAsync(board, context, token);
        await EscalateDecisionBlockersAsync(board, context, token);
        await ReviewBoardDeliveriesAsync(board, context, token);
        var current = await context.Platform.Work.ReadBoardAsync(boardId, token);
        if (gap is not null || current.Items.Any(t => t.Status is "Blocked" or "Failed"))
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(UnhandledBlockerGrace),
                gap is null ? "Remaining blockers require their recorded retry or management decision; recovery will recheck current state."
                    : "Workflow staffing needs attention: " + gap);
        return PersonalTodoResult.Completed("Workflow assignments reconciled; completed work preserved and stopped stages reviewed.");
    }
}
