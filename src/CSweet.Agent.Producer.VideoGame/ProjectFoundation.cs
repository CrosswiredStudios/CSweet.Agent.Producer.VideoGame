using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
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
                var model = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer model."),
                    Settings.GetString("llmModel"), new AgentLlmInvocationContext(null, null, "producer-project-revision")));
                var response = await model.GetResponseAsync([
                    new ChatMessage(ChatRole.System, "Revise the exact project proposal in response to manager feedback. Return the complete proposal JSON. Refine outcome, rationale, successCriteria and initialMilestones only; retain accepted scope, ownership, team, evidence, profile and authority. Do not assert approval or invent spending data."),
                    new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new { template, feedback = decision.GetProperty("comment").GetString() }, ProjectFoundationJson))
                ], ResponseOptions(), token);
                var raw = response.Text.Trim().Trim('`'); if (raw.StartsWith("json", StringComparison.OrdinalIgnoreCase)) raw = raw[4..].Trim();
                var draft = JsonSerializer.Deserialize<WorkstreamPlanProposalV2Request>(raw, ProjectFoundationJson) ?? throw new InvalidOperationException("Revised proposal unavailable.");
                var proposal = await context.Platform.ProposeWorkstreamAsync(template with { Outcome = draft.Outcome, Rationale = draft.Rationale,
                    SuccessCriteria = draft.SuccessCriteria, InitialMilestones = draft.InitialMilestones, IdempotencyKey = $"producer-project-revised:{id:N}" }, token);
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
        var source = new List<string>();
        foreach (var evidence in template.InitialEvidence.Where(x => x.Kind == "artifact"))
        {
            var accepted = await context.Platform.Artifacts.ReadAcceptedAsync(new(evidence.ResourceId, evidence.RevisionId!.Value, evidence.Digest!), token);
            source.Add(accepted.Revision.Content);
        }
        // Persist the model's exact proposed command before submission; duplicate deliveries reuse it.
        var plan = await CrosswiredStudios.VideoGame.PitchCollaboration.PitchProtocol.CachedAsync(
            $"project-foundation-plan:{request.SessionId:N}:{request.TurnOrdinal}", context, async () =>
            {
                var model = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer model."),
                    Settings.GetString("llmModel"), new AgentLlmInvocationContext(null, null, "producer-project-proposal")));
                var response = await model.GetResponseAsync([
                    new ChatMessage(ChatRole.System, """
                        Prepare the project creation proposal as delivery lead. Return the complete supplied
                        WorkstreamPlanProposalV2Request JSON, refining outcome, rationale, successCriteria and
                        initialMilestones against accepted evidence and manager feedback. Preserve all identity,
                        profile, team, supervisor, evidence, authority, budget and date fields exactly. Do not
                        expand accepted scope. Unspecified budgets are allowed: current default spending authority
                        is unlimited while real-money spending is unavailable. The manager reviews the submitted
                        plan and can request changes or escalate exceptions. Never assert that approval occurred.
                        """),
                    new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new { template, source, feedback = feedback?.Payload }, ProjectFoundationJson))
                ], ResponseOptions(), token);
                var raw = response.Text.Trim().Trim('`');
                if (raw.StartsWith("json", StringComparison.OrdinalIgnoreCase)) raw = raw[4..].Trim();
                var draft = JsonSerializer.Deserialize<WorkstreamPlanProposalV2Request>(raw, ProjectFoundationJson)
                    ?? throw new InvalidOperationException("Producer project proposal unavailable.");
                return template with { Outcome = draft.Outcome, Rationale = draft.Rationale, SuccessCriteria = draft.SuccessCriteria,
                    InitialMilestones = draft.InitialMilestones, IdempotencyKey = $"producer-project-foundation:{request.SessionId:N}:{request.TurnOrdinal}" };
            }, token);
        var result = await context.Platform.ProposeWorkstreamAsync(plan, token);
        if (result.ApprovalId is not { } proposalId) return AgentCoordinationTurnResult.Blocked("Project proposal did not return an approval ID.");
        return AgentCoordinationTurnResult.Continue("Submitted the project proposal for manager review.",
            new AgentCoordinationArtifactSubmission("video-game.project-foundation.proposal.v1", "1.0", original!.Key, 1, true,
                JsonSerializer.SerializeToElement(new { proposalId }, ProjectFoundationJson)));
    }
}
