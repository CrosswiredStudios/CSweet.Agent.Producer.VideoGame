using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task<WorkBoardSummary> EnsureProductionBoardAsync(
        WorkstreamDetail workstream, Guid teamId, AgentRuntimeContext context, CancellationToken token)
    {
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        var board = boards.SingleOrDefault(x => x.WorkstreamId == workstream.Id && !x.IsArchived);
        board ??= await context.Platform.Work.CreateBoardAsync(new CreateWorkBoardRequest(
            workstream.Name, $"Producer-managed delivery for {workstream.Outcome}", $"producer-board:{workstream.Id:N}")
        {
            TeamId = teamId, WorkstreamId = workstream.Id,
            Key = $"VG{workstream.Id:N}"[..12].ToUpperInvariant(), ProfileKey = "video-game-production-board.v2"
        }, token);
        if (!string.IsNullOrWhiteSpace(workstream.ProfileDefinitionDigest))
            _ = await context.Platform.Work.ConfigureProfileOrchestrationAsync(new ConfigureProfileOrchestrationRequest(
                workstream.Id, board.Id, board.Revision, workstream.ProfileDefinitionDigest,
                $"producer-profile:{workstream.Id:N}:{workstream.ProfileDefinitionDigest}"), token);
        return board;
    }

    private static WorkTechnicalDelegationRecommendation TechnicalLeadershipRequirement() =>
        new("specialist-execution", VideoGameRoleKeys.TechnicalDirector, ["work.execution.run.v1"], null, true,
            "Assess the accepted brief, identify technical risks, and decompose deliverable work before implementation hiring.")
        { PreferredSpecializationKeys = [VideoGameSpecializationKeys.Development] };

    internal static async Task EnsureDraftSprintAsync(Guid boardId, string digest, AgentRuntimeContext context, CancellationToken token)
    {
        var sprints = await context.Platform.Work.ListSprintsAsync(boardId, token);
        if (sprints.Any(x => x.Status is "Planned" or "Active")) return;
        var sequence = sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;
        await context.Platform.Work.CreateSprintAsync(new CreateWorkSprintRequest(boardId,
            $"Production Sprint {sequence}",
            $"Draft against accepted scope {digest}. Scope and capacity remain provisional until specialist estimates and readiness checks are recorded.",
            DateTimeOffset.UtcNow.Date, DateTimeOffset.UtcNow.Date.AddDays(14),
            $"producer-draft-sprint:{boardId:N}:{sequence}") { Sequence = sequence }, token);
    }

    private static async Task PopulateDraftScopeAsync(Guid boardId, AgentRuntimeContext context, CancellationToken token)
    {
        var sprint = (await context.Platform.Work.ListSprintsAsync(boardId, token))
            .Where(x => x.Status == "Planned" && !(x.CapacityPoints > 0)).OrderBy(x => x.Sequence).FirstOrDefault();
        if (sprint is null) return;
        var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
        if (board.Items.Any(x => x.SprintId == sprint.Id)) return;
        // A bounded tentative increment is inspectable during hiring, without inventing estimates or capacity.
        foreach (var item in board.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Executable &&
                     x.SprintId is null && x.ProposalProvenance is not null && x.Status is not "Done" and not "Completed")
                     .OrderBy(x => x.Planning?.DependencyItemIds.Count ?? 0).ThenBy(x => x.Rank).Take(8))
            await context.Platform.Work.SetItemSprintAsync(new SetWorkItemSprintRequest(boardId, item.Id, sprint.Id,
                item.Revision, $"producer-draft-scope:{sprint.Id:N}:{item.Id:N}"), token);
    }

    private static async Task ProposeCoverageAsync(Guid workstreamId, Guid? boardId, AgentTeamContext roster,
        IReadOnlyList<WorkTechnicalDelegationRecommendation> requirements, AgentRuntimeContext context, CancellationToken token, string? acceptedBriefDigest = null)
    {
        var missing = requirements.Where(r => RoleTaxonomy.SelectAssignment(roster.Members,
                new WorkAssignmentRequirements(r.RequiredRoleKey, r.RequiredSpecializationKeys,
                    r.PreferredSpecializationKeys, r.RequiredCapabilityKeys)) is null)
            .GroupBy(x => x.RequiredRoleKey, StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var producerId) ||
            !Guid.TryParse(context.Identity?.ManagerEmployeeId, out var directorId))
            throw new InvalidOperationException("Staffing proposals require an accountable Producer and manager.");
        var requests = (await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(), token)).Requests
            .Where(x => x.TeamId == Guid.Parse(roster.TeamId)).OrderByDescending(x => x.CreatedAt).ToList();
        if (requests.Any(x => x.RequesterOrganizationUserId == producerId && x.Status == "Pending")) return;
        var approved = requests.FirstOrDefault(x => x.Status == "Approved");
        var roles = approved?.Roles.ToList() ?? [];
        var proposed = false;
        foreach (var group in missing)
        {
            // An approved unfulfilled slot remains the hiring system's responsibility, not another new hire.
            if (roles.Any(x => x.RoleKey == group.Key)) continue;
            roles.Add(new ResourceChangeRole(group.Key, "video-game-team", group.Key,
                $"Cover {group.Count()} scoped planning/delivery requirement(s): {string.Join("; ", group.Select(x => x.Rationale).Distinct()).Truncate(700)}",
                1, group.Key == VideoGameRoleKeys.TechnicalDirector ? 1 : 3, "Next planned increment",
                group.SelectMany(x => x.RequiredCapabilityKeys).Append("work.execution.run.v1").Distinct().ToList(),
                false, producerId, null)
            {
                TeamId = Guid.Parse(roster.TeamId), RoleCategoryKey = group.Key,
                PreferredSpecializationKeys = group.SelectMany(x => x.RequiredSpecializationKeys.Concat(x.PreferredSpecializationKeys)).Distinct().ToList()
            });
            proposed = true;
        }
        if (!proposed) return;
        var evidenceRevision = boardId.HasValue
            ? (await context.Platform.Work.ReadFlowMetricsAsync(new ReadWorkFlowMetricsRequest(boardId.Value)
                { WorkstreamId = workstreamId, TeamId = Guid.Parse(roster.TeamId) }, token)).SourceRevision
            : acceptedBriefDigest ?? throw new InvalidOperationException("Initial staffing requires the exact accepted production brief.");
        var fingerprint = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new
            { workstreamId, roster.Revision, roles, evidenceRevision }));
        var chat = await context.Platform.Communication.SendDirectAgentMessageAsync(directorId,
            $"I propose delivery coverage for {string.Join(", ", missing.Select(x => x.Key))} on workstream {workstreamId:D}. " +
            "The proposal is based on the accepted brief. Team-board planning begins when technical leadership is hired.",
            $"producer-coverage-chat:{fingerprint}", token);
        await context.Platform.ProposeResourceChangeAsync(new ResourceChangeProposalRequest(chat.ChatId, Guid.Empty,
            "Deliver the accepted game scope with the smallest team that covers its work.",
            "Add one installation per uncovered capability. Retain the approved team; do not pre-hire unused disciplines.",
            roster.Revision, roles, ["Initial coverage is based on accepted scope, not historical velocity."],
            ["Hiring and spending require their separate approvals.", "Execution retains ticket-level eligibility and readiness gates."],
            approved?.Id, $"producer-coverage:{fingerprint}")
        {
            TeamId = Guid.Parse(roster.TeamId), WorkstreamId = workstreamId, ExpectedTeamRevision = roster.Revision,
            Evidence = [new ResourceChangeEvidence("scope-capability-gap", evidenceRevision,
                $"Accepted brief {acceptedBriefDigest ?? "linked through board planning"}; board {boardId?.ToString("D") ?? "deferred until technical leadership is hired"}; missing roles: {string.Join(", ", missing.Select(x => x.Key))}.")],
            AlternativesConsidered = ["Reuse qualified team installations.", "Sequence work before adding parallel capacity.", "Defer optional specialist work."],
            ExpectedEffect = "Unblock the named planning or backlog responsibilities; continue all independently ready work."
        }, token);
    }

    private static async Task BindAvailableWorkAsync(Guid boardId, AgentTeamContext roster, string profileDigest,
        AgentRuntimeContext context, CancellationToken token)
    {
        var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
        var planned = (await context.Platform.Work.ListSprintsAsync(boardId, token)).Where(x => x.Status == "Planned").Select(x => x.Id).ToHashSet();
        foreach (var item in board.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Executable &&
                     (x.SprintId is null || planned.Contains(x.SprintId.Value)) && x.ProposalProvenance is not null && x.Planning is not null))
        {
            var assignments = item.StageAssignments.ToList();
            var owner = item.AccountableOrganizationUserId;
            var changed = false;
            foreach (var recommendation in MissingDelegations(item))
            {
                var requirements = new WorkAssignmentRequirements(recommendation.RequiredRoleKey,
                    recommendation.RequiredSpecializationKeys, recommendation.PreferredSpecializationKeys, recommendation.RequiredCapabilityKeys);
                var selected = RoleTaxonomy.SelectAssignment(roster.Members, requirements);
                if (selected is null || !Guid.TryParse(selected.Teammate.EmployeeId, out var selectedOwner)) continue;
                var matched = requirements.RequiredSpecializationKeys.Concat(requirements.PreferredSpecializationKeys
                    .Where(x => selected.Teammate.SpecializationKeys.Contains(x))).Distinct().OrderBy(x => x).ToList();
                var fingerprint = ProducerPolicyFingerprint.Digest($"{item.Id:N}:{recommendation.StageKey}:{item.PlanningRevision}:{roster.Revision}:{selected.Teammate.AgentInstallationId}");
                assignments.RemoveAll(x => x.StageKey == recommendation.StageKey);
                assignments.Add(new WorkStageAssignment(recommendation.StageKey, "AgentInstallation", selectedOwner, selected.Teammate.AgentInstallationId)
                {
                    Requirements = requirements,
                    SelectionEvidence = new WorkAssignmentSelectionEvidence(selected.Teammate.AgentInstallationId!.Value,
                        roster.Revision, profileDigest, matched, fingerprint, DateTimeOffset.UtcNow)
                });
                if (recommendation.StageKey == "specialist-execution") owner = selectedOwner;
                changed = true;
            }
            if (!changed) continue;
            var bindingKey = ProducerPolicyFingerprint.Digest($"{item.Id:N}:{item.Revision}:{roster.Revision}:" +
                string.Join("|", assignments.OrderBy(x => x.StageKey).Select(x => $"{x.StageKey}:{x.AgentInstallationId}")));
            await context.Platform.Work.RevisePlanningAsync(new ReviseWorkItemPlanningRequest(boardId, item.Id,
                item.Title, item.Description, item.ParentItemId, item.Planning!, item.Revision, item.PlanningRevision,
                $"producer-bind:{bindingKey}")
            {
                ProposalProvenance = item.ProposalProvenance, AccountableOrganizationUserId = owner,
                StageAssignments = assignments
            }, token);
        }
    }

    internal static WorkStageAssignment? PrimaryExecutionAssignment(WorkItem item)
    {
        var matches = item.StageAssignments.Where(x => x.StageKey == "specialist-execution").Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool HasStaffedExecution(WorkItem item) =>
        PrimaryExecutionAssignment(item) is { AgentInstallationId: not null, Requirements: not null, SelectionEvidence: not null };

    internal static IReadOnlyList<WorkTechnicalDelegationRecommendation> MissingDelegations(WorkItem item) =>
        (item.Planning?.DelegationRecommendations ?? []).Where(r => !item.StageAssignments.Any(a =>
            a.StageKey == r.StageKey && a.AgentInstallationId is not null && a.Requirements?.RequiredRoleKey == r.RequiredRoleKey &&
            a.SelectionEvidence is not null)).ToArray();

    internal static IReadOnlyList<WorkItem> EligibleCandidateScope(IReadOnlyList<WorkItem> candidates, IReadOnlyList<WorkItem> allItems)
    {
        var completed = allItems.Where(x => x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
        var eligible = candidates.Where(x => !completed.Contains(x.Id) && HasStaffedExecution(x) && MissingDelegations(x).Count == 0).ToList();
        while (true)
        {
            var ids = eligible.Select(x => x.Id).ToHashSet();
            var removed = eligible.RemoveAll(x => x.Planning?.DependencyItemIds.Any(id => !completed.Contains(id) && !ids.Contains(id)) == true);
            if (removed == 0) return eligible;
        }
    }
}

internal static class StaffingText
{
    public static string Truncate(this string text, int length) => text.Length <= length ? text : text[..length];
}
