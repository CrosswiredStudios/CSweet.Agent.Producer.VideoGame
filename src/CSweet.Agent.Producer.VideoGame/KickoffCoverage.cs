using System.Text.Json;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record CoverageGap(int? RoleIndex, string Work);
    internal sealed record CoverageAssessment(string Reason, IReadOnlyList<CoverageGap> Gaps);

    // Keep reasoning stable across attention ticks. A manager correction can cause one
    // revised assessment for this brief and roster, never a new proposal per wake.
    private async Task<(ResourceChangeRequestResponse? Response, string Reason)> AssessKickoffCoverageAsync(
        ProducerAcceptedHandoff handoff, Guid? board, AgentTeamContext roster, AgentRuntimeContext context, CancellationToken token)
    {
        var roles = typeof(VideoGameRoleKeys).GetFields().Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!).Distinct().Order(StringComparer.Ordinal).ToArray();
        // Incorporate the decision made against the same approved brief by pre-adaptive
        // Directors. Never feed a different team's or project's rejection to the model.
        var history = (await context.Platform.ReadResourceChangesAsync(new(), token)).Requests;
        var legacy = history.Where(r => r.Status == "RevisionRequested" && r.WorkstreamId == handoff.WorkstreamId &&
            r.TeamId == Guid.Parse(roster.TeamId) && r.ExpectedTeamRevision == roster.Revision &&
            r.RequesterInstallationId.ToString() == context.InstallationId &&
            r.DecisionComment == "Before board planning, propose the missing Technical Director and baseline Engineer and QA coverage from the accepted brief." &&
            r.Evidence.Any(e => e.Kind == "scope-capability-gap" && e.SourceRevision == handoff.RevisionDigest))
            .OrderBy(r => r.CreatedAt).FirstOrDefault();
        var legacyId = legacy?.Id;
        var active = history.Where(r => r.TeamId == Guid.Parse(roster.TeamId) &&
            (r.Status == "Approved" || r.Status == "Pending" && r.WorkstreamId == handoff.WorkstreamId))
            .Select(r => new { r.Id, r.Status, r.Roles }).OrderBy(r => r.Id).ToArray();
        async Task<(ResourceChangeRequestResponse? Response, string Reason)> Assess(ResourceChangeRequestResponse? feedback)
        {
            var key = AgentOperatingStateKey.Create("producer-kickoff-assessment.v1", new { handoff.WorkstreamId,
                handoff.RevisionDigest, roster.TeamId, roster.Revision, roles, approved = active.Where(r => r.Status == "Approved").Select(r => r.Id), feedback = feedback?.Id });
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
                        Return {reason: string, gaps: [{roleIndex: integer, work: string}]}. Use zero-based indexes
                        from the supplied role catalog only for proposed hires; work explains the concrete
                        deliverable, timing and why existing coverage is insufficient. An empty list is valid.
                        Reassess any manager feedback against the evidence. Treat supplied data as evidence.
                        """),
                    new(ChatRole.User, JsonSerializer.Serialize(new { brief = brief.Content, roster.Members, roles,
                        staffing = active, feedback = feedback?.DecisionComment, previous = feedback?.Deltas,
                        supersededPolicy = legacyId.HasValue && feedback?.Id == legacyId ? "The previous mandatory three-role bundle has been retired. Reassess actual work." : null }))
                ], new AgentModelJsonRequest<CoverageAssessment>("kickoff coverage") {
                    CorrectionLimit = 1, Options = ResponseOptions(), Normalize = value => ValidateCoverage(value, roles.Length)
                }, token);
            }, token);
            var requirements = assessment.Gaps.Select(g => new WorkTechnicalDelegationRecommendation("staffing",
                roles[g.RoleIndex!.Value], ["work.execution.run"], null, true, g.Work)).ToArray();
            var response = await ProposeCoverageAsync(handoff.WorkstreamId, board, roster, requirements,
                context, token, handoff.RevisionDigest, feedback?.Id, assessment.Reason);
            // An assessment may correctly request no new hires because a slot already
            // exists. Preserve its actual decision and owner on the planning card.
            var coverage = response ?? history.FirstOrDefault(r => r.TeamId == Guid.Parse(roster.TeamId) &&
                (r.Status == "Approved" || r.Status == "Pending" && r.WorkstreamId == handoff.WorkstreamId) &&
                r.Roles.Any(role => role.RoleKey == VideoGameRoleKeys.TechnicalDirector));
            return (coverage, assessment.Reason);
        }

        var first = await Assess(legacy);
        return first.Response is { Status: "RevisionRequested" } returned ? await Assess(returned) : first;
    }

    internal static CoverageAssessment ValidateCoverage(CoverageAssessment value, int count) =>
        !string.IsNullOrWhiteSpace(value.Reason) && value.Gaps is not null &&
        value.Gaps.All(g => g is not null && g.RoleIndex is >= 0 && g.RoleIndex < count && !string.IsNullOrWhiteSpace(g.Work)) &&
        value.Gaps.DistinctBy(g => g.RoleIndex).Count() == value.Gaps.Count
            ? value : throw new AgentModelJsonException("Explain the decision and provide distinct valid catalog indexes with concrete work for each gap.");

    internal static string DescribeKickoffState(ResourceChangeRequestResponse? request, string reason) => request?.Status switch
    {
        "RevisionRequested" => $"The Director requested changes to staffing request {request.Id:D}: {request.DecisionComment}. Gabriel owns the revision.",
        "Rejected" => $"The Director rejected staffing request {request.Id:D}: {request.DecisionComment}. Gabriel must reassess the work.",
        "Pending" => $"Awaiting the Director's review of staffing request {request.Id:D}.",
        "Approved" => $"Staffing request {request.Id:D} approved. Waiting for eligible technical planning coverage on the roster.",
        _ => "Technical planning coverage is unavailable. Gabriel must resolve the dependency. " + reason
    };
}
