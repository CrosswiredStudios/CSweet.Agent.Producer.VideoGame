using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static readonly JsonSerializerOptions ProjectFoundationJson = new(JsonSerializerDefaults.Web);
    private const string SubmittedProjectsKey = "producer-submitted-project-proposals";
    private sealed record SubmittedProjects { public IReadOnlyList<Guid> ProposalIds { get; init; } = []; }
    private sealed record ProjectRecovery(string Status);

    private static async Task RememberSubmittedProjectAsync(Guid proposalId, AgentRuntimeContext context, CancellationToken token) =>
        _ = await new RevisionSafeProjectState(context.Platform).MergeAsync<SubmittedProjects>(SubmittedProjectsKey,
            "producer.submitted-projects.v1", 1, prior => new() { ProposalIds = (prior?.ProposalIds ?? []).Append(proposalId).Distinct().TakeLast(100).ToArray() },
            new Dictionary<string, string>(), $"remember-project:{proposalId:N}", token);

    internal async Task RecoverSubmittedProjectsAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var index = await context.Platform.ReadOperatingStateAsync<SubmittedProjects>(SubmittedProjectsKey, token);
        foreach (var id in index?.Payload.ProposalIds ?? [])
        {
            var reviews = await context.Platform.InvokeAsync<object, JsonElement[]>("platform.project-approval.read.v1", new { proposalId = id }, token);
            if (reviews.Length != 1 || reviews[0].GetProperty("status").GetString() == "Pending") continue;
            var review = reviews[0];
            if (!review.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.Object ||
                decision.GetProperty("decision").GetString() != "RequestRevision") continue;
            await CrosswiredStudios.VideoGame.PitchCollaboration.PitchProtocol.CachedAsync($"producer-project-revision:{id:N}", context, async () =>
            {
                var template = review.GetProperty("binding").GetProperty("payload").Deserialize<WorkstreamPlanProposalV2Request>(ProjectFoundationJson)!;
                if (template.ProfileKey == "video-game-manager-brief.v1" &&
                    !(template.ProfileData.TryGetProperty("separateNewProject", out var separate) && separate.ValueKind == JsonValueKind.True))
                {
                    var portfolio = await context.Platform.ReadPortfolioAsync(new(), token);
                    if (portfolio.Workstreams.Any(x => x.Workstream.AccountableManagerOrganizationUserId == template.AccountableManagerOrganizationUserId &&
                        x.Workstream.Status is not ("Completed" or "Cancelled")))
                        return new ProjectRecovery("ContinueExistingProject");
                }
                var prepared = await PrepareProjectDraftAsync($"producer-project-revision-plan:{id:N}",
                    template with { IdempotencyKey = $"producer-project-revised:{id:N}" },
                    new { feedback = decision.GetProperty("comment").GetString() }, "producer-project-revision", context, token);
                if (prepared.Plan is null) throw new InvalidOperationException(prepared.Error);
                var proposal = await context.Platform.ProposeWorkstreamAsync(prepared.Plan, token);
                if (proposal.ApprovalId is not { } revisedId) throw new InvalidOperationException("Revised proposal has no approval ID.");
                await RememberSubmittedProjectAsync(revisedId, context, token);
                return new ProjectRecovery("Revised");
            }, token);
        }
    }

    private async Task<AgentCoordinationTurnResult> ProposeProjectFoundationAsync(AgentCoordinationTurnRequest request,
        AgentRuntimeContext context, CancellationToken token)
    {
        var original = request.Transcript.FirstOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId &&
            x.Artifact?.Type == "video-game.project-foundation.request.v1")?.Artifact;
        if (original is null || !Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) || manager != request.Counterpart.OrganizationUserId)
            return AgentCoordinationTurnResult.Blocked("Project setup requires direction from the reporting manager.");
        var template = original.Payload.Deserialize<WorkstreamPlanProposalV2Request>(ProjectFoundationJson)
            ?? throw new InvalidOperationException("Project template unavailable.");
        if (template.AccountableManagerOrganizationUserId != request.Self.OrganizationUserId)
            return AgentCoordinationTurnResult.Blocked("The proposal must assign this Producer as delivery lead.");
        var feedback = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == manager &&
            x.Artifact?.Type == "video-game.project-foundation.decision.v1")?.Artifact;
        if (feedback is not null && feedback.Payload.GetProperty("decisionKind").GetString() != "RequestRevision")
            return AgentCoordinationTurnResult.Completed("The project review is recorded; production waits for the authoritative created project.");
        if (request.IsFinalization) return AgentCoordinationTurnResult.Blocked("Project review remains incomplete; no project creation is assumed.");
        var cacheKey = $"project-foundation-plan:{request.SessionId:N}:{request.TurnOrdinal}";
        var commandKey = $"producer-project-foundation:{request.SessionId:N}:{request.TurnOrdinal}";
        string? revisionFeedback = null;
        if (feedback is not null)
        {
            var previousId = feedback.Payload.GetProperty("proposalId").GetGuid();
            var reviews = await context.Platform.InvokeAsync<object, JsonElement[]>("platform.project-approval.read.v1", new { proposalId = previousId }, token);
            if (reviews.Length != 1 || !reviews[0].TryGetProperty("decision", out var decision) ||
                decision.ValueKind != JsonValueKind.Object || decision.GetProperty("decision").GetString() != "RequestRevision")
                return AgentCoordinationTurnResult.Blocked("Project revision requires the authoritative manager revision decision.");
            template = reviews[0].GetProperty("binding").GetProperty("payload").Deserialize<WorkstreamPlanProposalV2Request>(ProjectFoundationJson)!;
            if (template.AccountableManagerOrganizationUserId != request.Self.OrganizationUserId)
                return AgentCoordinationTurnResult.Blocked("The revision must retain this Producer as delivery lead.");
            // The attention recovery path and the collaboration must submit the same revision command.
            cacheKey = $"producer-project-revision-plan:{previousId:N}";
            commandKey = $"producer-project-revised:{previousId:N}";
            revisionFeedback = decision.GetProperty("comment").GetString();
        }
        var source = new List<string>();
        foreach (var evidence in template.InitialEvidence.Where(x => x.Kind == "artifact"))
        {
            var accepted = await context.Platform.Artifacts.ReadAcceptedAsync(new(evidence.ResourceId, evidence.RevisionId!.Value, evidence.Digest!), token);
            source.Add(accepted.Revision.Content);
        }
        var prepared = await PrepareProjectDraftAsync(
            cacheKey, template with { IdempotencyKey = commandKey },
            new { source, feedback = revisionFeedback }, "producer-project-proposal", context, token,
            request.Transcript.Any(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
                x.Disposition == AgentCoordinationDispositions.Blocked) ? $"{request.SessionId:N}:{request.TurnOrdinal}" : "initial");
        if (prepared.Plan is null) return AgentCoordinationTurnResult.Blocked(prepared.Error!);
        MutationResponse result;
        try
        {
            result = await context.Platform.ProposeWorkstreamAsync(prepared.Plan, token);
        }
        catch (PlatformCapabilityException ex) when (ex.Code == PlatformCapabilityErrorCode.ValidationFailed)
        {
            return AgentCoordinationTurnResult.Blocked(
                "The platform rejected the Producer project proposal request. " +
                "The manager must check existing approvals and this validation error before retrying collaboration: " + ex.Message);
        }
        if (result.ApprovalId is not { } proposalId) return AgentCoordinationTurnResult.Blocked("Project proposal did not return an approval ID.");
        await RememberSubmittedProjectAsync(proposalId, context, token);
        return AgentCoordinationTurnResult.Continue("Submitted the project proposal for manager review.",
            new AgentCoordinationArtifactSubmission("video-game.project-foundation.proposal.v1", "1.0", original!.Key, 1, true,
                JsonSerializer.SerializeToElement(new { proposalId }, ProjectFoundationJson)));
    }
}
