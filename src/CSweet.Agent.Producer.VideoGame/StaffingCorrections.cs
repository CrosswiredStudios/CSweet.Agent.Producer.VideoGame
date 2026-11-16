using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task EnsureStaffingCorrectionCommitmentsAsync(Guid project, Guid team, Guid? board,
        AgentRuntimeContext context, CancellationToken token)
    {
        var state = await context.Platform.ReadResourceChangeContextAsync(token);
        foreach (var correction in state.Corrections.Where(x => x.WorkstreamId == project && x.TeamId == team &&
            x.RequesterOrganizationUserId.ToString() == context.Identity?.EmployeeId && x.Status == "Pending"))
        {
            var decisionAt = state.Requests.SingleOrDefault(x => x.Id == correction.ReplacementRequestId)?.DecidedAt;
            await EnsureCommitmentAsync($"producer-staffing-correction:{correction.Id:N}", "Correct the requested staffing capacity",
                correction.Reason, "High", new PersonalTodoWorkContext(WorkstreamId: project, TeamId: team, BoardId: board,
                    SourceFingerprint: correction.Id.ToString("N")), context, token, dependencyChangedAt: decisionAt ?? correction.CreatedAt);
        }
    }

    private async Task<PersonalTodoResult> ReconcileStaffingCorrectionAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        var current = await context.Platform.ReadResourceChangeContextAsync(token);
        var correction = current.Corrections.SingleOrDefault(x => item.CorrelationId == $"producer-staffing-correction:{x.Id:N}" &&
            x.RequesterOrganizationUserId.ToString() == context.Identity?.EmployeeId && x.TeamId == item.WorkContext?.TeamId &&
            x.WorkstreamId == item.WorkContext?.WorkstreamId && x.Status == "Pending");
        if (correction is null) return PersonalTodoResult.Completed("The staffing correction no longer requires a replacement proposal. Continue from the current approved plan and actual roster.");
        var portfolio = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"), token);
        if (correction.WorkstreamId is not { } project || correction.TeamId is not { } team ||
            portfolio?.Payload.AcceptedHandoffs.TryGetValue(project, out var handoff) != true)
            return PersonalTodoResult.Blocked("The accountable manager must restore the accepted project handoff before staffing can be corrected.");
        var roster = (await context.Platform.ReadTeamRosterAsync(new TeamRosterV2Request(team, project, 1, 100), token)).Team;
        if (roster is null) return PersonalTodoResult.Blocked("The accountable manager must restore access to the current team roster.");
        var revised = await AssessKickoffCoverageAsync(handoff!, item.WorkContext?.BoardId, roster, context, token);
        return revised.Response is { Status: "Pending" } proposal
            ? PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                $"Staffing correction proposal {proposal.Id:D} is awaiting the accountable manager's review.", proposal.ManagerOrganizationUserId,
                StaffingReviews.Dependency(proposal))
            : PersonalTodoResult.Blocked(DescribeKickoffState(revised.Response, revised.Reason));
    }
}
