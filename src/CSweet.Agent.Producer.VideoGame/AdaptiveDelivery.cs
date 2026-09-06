using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
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

    private static async Task ProposeCoverageAsync(Guid workstreamId, Guid boardId, AgentTeamContext roster,
        IReadOnlyList<WorkTechnicalDelegationRecommendation> requirements, AgentRuntimeContext context, CancellationToken token)
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
        var metrics = await context.Platform.Work.ReadFlowMetricsAsync(new ReadWorkFlowMetricsRequest(boardId)
            { WorkstreamId = workstreamId, TeamId = Guid.Parse(roster.TeamId) }, token);
        var fingerprint = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new
            { workstreamId, roster.Revision, roles, metrics.SourceRevision }));
        var chat = await context.Platform.Communication.SendDirectAgentMessageAsync(directorId,
            $"I propose delivery coverage for {string.Join(", ", missing.Select(x => x.Key))} on workstream {workstreamId:D}. " +
            "The proposal is based on scoped work; backlog and sprint drafting continue during hiring.",
            $"producer-coverage-chat:{fingerprint}", token);
        await context.Platform.ProposeResourceChangeAsync(new ResourceChangeProposalRequest(chat.ChatId, Guid.Empty,
            "Deliver the accepted game scope with the smallest team that covers its work.",
            "Add one installation per uncovered capability. Retain the approved team; do not pre-hire unused disciplines.",
            roster.Revision, roles, ["Initial coverage is based on accepted scope, not historical velocity."],
            ["Hiring and spending require their separate approvals.", "Execution retains ticket-level eligibility and readiness gates."],
            approved?.Id, $"producer-coverage:{fingerprint}")
        {
            TeamId = Guid.Parse(roster.TeamId), WorkstreamId = workstreamId, ExpectedTeamRevision = roster.Revision,
            Evidence = [new ResourceChangeEvidence("scope-capability-gap", metrics.SourceRevision,
                $"Board {boardId:D}; missing roles: {string.Join(", ", missing.Select(x => x.Key))}.")],
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
                     (x.SprintId is null || planned.Contains(x.SprintId.Value)) && x.StageAssignments.Count == 0 && x.ProposalProvenance is not null && x.Planning is not null))
        {
            var recommendation = item.Planning!.DelegationRecommendations.SingleOrDefault();
            if (recommendation is null) continue;
            var requirements = new WorkAssignmentRequirements(recommendation.RequiredRoleKey,
                recommendation.RequiredSpecializationKeys, recommendation.PreferredSpecializationKeys,
                recommendation.RequiredCapabilityKeys);
            var selected = RoleTaxonomy.SelectAssignment(roster.Members, requirements);
            if (selected is null || !Guid.TryParse(selected.Teammate.EmployeeId, out var owner)) continue;
            var matched = requirements.RequiredSpecializationKeys.Concat(requirements.PreferredSpecializationKeys
                .Where(x => selected.Teammate.SpecializationKeys.Contains(x))).Distinct().OrderBy(x => x).ToList();
            var fingerprint = ProducerPolicyFingerprint.Digest($"{item.Id:N}:{item.PlanningRevision}:{roster.Revision}:{selected.Teammate.AgentInstallationId}");
            await context.Platform.Work.RevisePlanningAsync(new ReviseWorkItemPlanningRequest(boardId, item.Id,
                item.Title, item.Description, item.ParentItemId, item.Planning, item.Revision, item.PlanningRevision,
                $"producer-bind:{fingerprint}")
            {
                ProposalProvenance = item.ProposalProvenance, AccountableOrganizationUserId = owner,
                StageAssignments = [new WorkStageAssignment("specialist-execution", "AgentInstallation", owner, selected.Teammate.AgentInstallationId)
                {
                    Requirements = requirements,
                    SelectionEvidence = new WorkAssignmentSelectionEvidence(selected.Teammate.AgentInstallationId!.Value,
                        roster.Revision, profileDigest, matched, fingerprint, DateTimeOffset.UtcNow)
                }]
            }, token);
        }
    }

    internal static IReadOnlyList<WorkItem> EligibleCandidateScope(IReadOnlyList<WorkItem> candidates, IReadOnlyList<WorkItem> allItems)
    {
        var completed = allItems.Where(x => x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
        var eligible = candidates.Where(x => !completed.Contains(x.Id) && x.StageAssignments.Count == 1 &&
            x.StageAssignments[0].Requirements is not null && x.StageAssignments[0].SelectionEvidence is not null).ToList();
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
