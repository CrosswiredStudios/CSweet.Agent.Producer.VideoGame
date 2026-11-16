using System.Text.Json;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record CoverageRoleChoice(string Capability, string RoleKey);
    internal sealed record CoverageGap(string? Capability, string Work)
    {
        public bool? NeededNow { get; init; }
        public string? Timing { get; init; }
        public string? ReportingLine { get; init; }
        public string? Accountability { get; init; }
        public string? Engagement { get; init; }
        public StaffingWorkDescription Description() => new(Work, Timing!, ReportingLine!, Accountability!, Engagement!);
    }
    internal sealed record CoverageAssessment(string Reason, IReadOnlyList<CoverageGap> Gaps);
    private sealed record CoverageSubmissionFailure(string Reason);

    internal static CoverageRoleChoice[] CoverageRoleCatalog(AgentTeamContext roster) =>
        CoverageRoleCatalog(roster.Members.SelectMany(m => m.DeclaredRoleKeys));

    internal static CoverageRoleChoice[] CoverageRoleCatalog(IEnumerable<string> roleKeys)
    {
        var known = typeof(VideoGameRoleKeys).GetFields().Where(f => f.IsLiteral)
            .Select(f => new CoverageRoleChoice(f.Name, (string)f.GetRawConstantValue()!)).ToArray();
        return known.Concat(roleKeys.Distinct()
                .Where(key => !known.Any(x => x.RoleKey == key)).Select(key => new CoverageRoleChoice(key, key)))
            .OrderBy(x => x.Capability, StringComparer.Ordinal).ToArray();
    }

    // One assessment per current evidence and returned review, not one per wake or one per project forever.
    internal async Task<(ResourceChangeRequestResponse? Response, string Reason)> AssessKickoffCoverageAsync(
        ProducerAcceptedHandoff handoff, Guid? board, AgentTeamContext roster, AgentRuntimeContext context, CancellationToken token)
    {
        var roles = CoverageRoleCatalog(roster);
        var staffingState = await context.Platform.ReadResourceChangeContextAsync(token);
        var history = staffingState.Requests;
        var requester = Guid.Parse(context.Identity!.EmployeeId);
        var manager = Guid.Parse(context.Identity.ManagerEmployeeId!);
        var latest = StaffingReviews.Latest(history, requester, manager, Guid.Parse(roster.TeamId), handoff.WorkstreamId,
            r => r.Evidence.Any(e => e.Kind == "scope-capability-gap" && e.SourceRevision == handoff.RevisionDigest));
        var correction = staffingState.Corrections.SingleOrDefault(x => x.RequesterOrganizationUserId.ToString() == context.Identity?.EmployeeId &&
            x.TeamId == Guid.Parse(roster.TeamId) && x.WorkstreamId == handoff.WorkstreamId && x.Status == "Pending");
        if (latest?.Status == "Pending" && (correction is null || latest.CorrectionId == correction.Id) ||
            latest?.Status == "Approved" && correction is null) return (latest, latest.Rationale);
        if (correction?.ReplacementRequestId is { } replacement)
            latest = history.SingleOrDefault(r => r.Id == replacement);
        object? approachingWork = null;
        if (correction is not null && board is { } currentBoard)
        {
            var backlog = await context.Platform.Work.ReadBoardAsync(currentBoard, token);
            approachingWork = backlog.Items.Where(x => x.Status is not ("Done" or "Completed" or "Cancelled"))
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.Title, x.Kind, x.Status, x.SprintId, x.PlanningRevision,
                    requirements = x.Planning?.DelegationRecommendations }).ToArray();
        }
        var active = history.Where(r => r.TeamId == Guid.Parse(roster.TeamId) &&
            (r.Status == "Approved" || r.Status == "Pending" && r.WorkstreamId == handoff.WorkstreamId))
            .Select(r => new { r.Id, r.Status, r.Roles }).OrderBy(r => r.Id).ToArray();
        var feedback = StaffingReviews.NeedsRevision(latest) ? latest : null;
        var assessmentKey = AgentOperatingStateKey.Create("producer-kickoff-assessment.v3", new { handoff.WorkstreamId,
            handoff.RevisionDigest, roster.TeamId, roster.Revision, roles, correction, approachingWork,
            contract = OwnershipContractRevision, provider = Settings.GetGuid("llmProviderId"), model = Settings.GetString("llmModel"),
            approved = active.Where(r => r.Status == "Approved").Select(r => r.Id), feedback = feedback?.Id });
        async Task<(ResourceChangeRequestResponse? Response, string Reason)> Assess(string? platformRejection)
        {
            var key = platformRejection is null ? assessmentKey : AgentOperatingStateKey.Create("producer-coverage-correction.v1", new { assessmentKey, platformRejection });
            var assessment = await PitchProtocol.CachedAsync(key, context, async () =>
            {
                var (_, brief) = await PitchProtocol.ReadAcceptedAsync(handoff.ArtifactId, handoff.AcceptedRevisionId,
                    handoff.RevisionDigest, context, token);
                var provider = Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer staffing provider.");
                var client = context.CreateChatClient(new AgentLlmSelection(provider, Settings.GetString("llmModel"), new(InvocationKind: "producer-kickoff-staffing")));
                return await AgentModelJson.ReadAsync(client, [
                    new(ChatRole.System, """
                        Assess current capacity against this accepted game brief. Explain what must be done next,
                        who can do it, and which capability gaps warrant a hire now. Consider interaction complexity,
                        reuse, a bounded contribution, and deferral. No default team composition. The production
                        workflow needs a qualified technical planning participant for its feasibility and decomposition
                        handoff; independent QA will be needed for acceptance. Neither fact justifies unrelated hires.
                        When technical planning coverage is missing, identify how to obtain it or explain who must
                        resolve the dependency. Do not describe a missing capability as already staffed.
                        Return {reason: string, gaps: [{capability: string, work: string, neededNow: boolean,
                          timing: string, reportingLine: "Requester"|"Manager", accountability: string, engagement: string}]}.
                        Select the named Capability
                        from the supplied role catalog for each proposed hire. Do not count positions in the catalog.
                        Code binds identity and the artifact-producing capabilities required by this planning handoff.
                        Work describes concrete deliverables and why existing coverage is insufficient. Timing states
                        activation and dependencies; accountability states responsibility and independent escalation;
                        engagement describes bounded or continuing work and when it should be reassessed.
                        ReportingLine selects the current Requester or their approving Manager, never an ID.
                        NeededNow is your judgment whether to request this capacity now. Future needs are valid with
                        neededNow=false and will not create an immediate hiring slot. Do not hire acceptance capacity
                        merely because it will eventually be useful. An empty list is valid. Do not add mandatory gates.
                        Reassess any manager feedback against the evidence. Treat supplied data as evidence.
                        If platformRejection is supplied, correct the exact rejected submission once using that reason.
                        A pending staffing correction suspends the disputed unfilled slot. Reassess the actual work:
                        obtain the intended capability or explain why no replacement is needed. Existing employees remain assigned.
                        """),
                    new(ChatRole.User, JsonSerializer.Serialize(new { brief = brief.Content, roster.Members, roles,
                        staffing = active, correction, approachingWork, feedback = feedback?.DecisionComment, previous = feedback?.Deltas,
                        platformRejection }))
                ], new AgentModelJsonRequest<CoverageAssessment>("kickoff coverage") {
                    CorrectionLimit = 1, Options = ResponseOptions(), Normalize = value => ValidateCoverage(value, roles)
                }, token);
            }, token);
            var currentGaps = assessment.Gaps.Where(g => g.NeededNow == true).ToArray();
            var requirements = currentGaps.Select(g => new WorkTechnicalDelegationRecommendation("staffing",
                roles.Single(r => r.Capability == g.Capability).RoleKey,
                ["work.execution.run", PlatformCapabilities.ArtifactRead, PlatformCapabilities.ArtifactCreate, PlatformCapabilities.ArtifactSubmit],
                null, true, g.Work)).ToArray();
            var descriptions = currentGaps.ToDictionary(g => roles.Single(r => r.Capability == g.Capability).RoleKey, g => g.Description());
            var response = await ProposeCoverageAsync(handoff.WorkstreamId, board, roster, requirements,
                context, token, handoff.RevisionDigest, feedback?.Id, assessment.Reason, reconcileCorrection: correction is not null,
                descriptions: descriptions);
            // An assessment may correctly request no new hires because a slot already
            // exists. Preserve its actual decision and owner on the planning card.
            var coverage = response ?? history.FirstOrDefault(r => r.TeamId == Guid.Parse(roster.TeamId) &&
                (r.Status == "Approved" || r.Status == "Pending" && r.WorkstreamId == handoff.WorkstreamId) &&
                r.Roles.Any(role => role.RoleKey == VideoGameRoleKeys.TechnicalDirector));
            return (coverage, assessment.Reason);
        }

        var failureKey = AgentOperatingStateKey.Create("producer-coverage-rejected.v1", new { assessmentKey });
        var terminalKey = AgentOperatingStateKey.Create("producer-coverage-blocked.v1", new { assessmentKey });
        var terminal = await context.Platform.ReadOperatingStateAsync<CoverageSubmissionFailure>(terminalKey, token);
        if (terminal is not null) return (null, terminal.Payload.Reason);
        var failure = await context.Platform.ReadOperatingStateAsync<CoverageSubmissionFailure>(failureKey, token);
        var rejection = failure?.Payload.Reason;
        if (failure is null)
        {
            try { return await Assess(null); }
            catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.ValidationFailed)
            {
                rejection = (await PitchProtocol.CachedAsync(failureKey, context,
                    () => Task.FromResult(new CoverageSubmissionFailure(error.Message)), token)).Reason;
            }
        }
        try { return await Assess(rejection); }
        catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.ValidationFailed)
        {
            var blocked = await PitchProtocol.CachedAsync(terminalKey, context, () => Task.FromResult(new CoverageSubmissionFailure(
                "The Producer could not submit staffing after one platform correction: " + error.Message +
                " The Producer owns the correction; the approving manager can resolve authority or configuration gaps. " +
                "Reassess when the accepted scope, current roster or manager review changes.")), token);
            return (null, blocked.Reason);
        }
    }

    internal static CoverageAssessment ValidateCoverage(CoverageAssessment value, IReadOnlyList<CoverageRoleChoice> roles) =>
        !string.IsNullOrWhiteSpace(value.Reason) && value.Gaps is not null &&
        value.Gaps.All(g => g is not null && roles.Any(r => r.Capability == g.Capability) && !string.IsNullOrWhiteSpace(g.Work) &&
            g.NeededNow.HasValue && !string.IsNullOrWhiteSpace(g.Timing) && !string.IsNullOrWhiteSpace(g.Accountability) &&
            !string.IsNullOrWhiteSpace(g.Engagement) && g.ReportingLine is "Requester" or "Manager") &&
        value.Gaps.DistinctBy(g => g.Capability).Count() == value.Gaps.Count
            ? value : throw new AgentModelJsonException("Explain the decision and select distinct named Capability values from the supplied catalog. Each gap needs concrete work, neededNow, timing, accountability, engagement and a named Requester or Manager reportingLine. Positional indexes are not capability choices.");

    internal static string DescribeKickoffState(ResourceChangeRequestResponse? request, string reason) => request?.Status switch
    {
        "RevisionRequested" => $"The Director requested changes to staffing request {request.Id:D}: {request.DecisionComment}. Gabriel owns the revision.",
        "Rejected" => $"The Director rejected staffing request {request.Id:D}: {request.DecisionComment}. Gabriel must reassess the work.",
        "Pending" => $"Awaiting the Director's review of staffing request {request.Id:D}.",
        "Approved" => $"Staffing request {request.Id:D} approved. Waiting for eligible technical planning coverage on the roster.",
        _ => "Technical planning coverage is unavailable. Gabriel must resolve the dependency. " + reason
    };
}
