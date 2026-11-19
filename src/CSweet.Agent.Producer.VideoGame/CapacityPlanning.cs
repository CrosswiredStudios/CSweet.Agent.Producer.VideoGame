using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.Extensions.AI;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static PersonalTodoDependency RosterWait(Guid team, AgentTeamContext roster) =>
        new(WorkforceEvents.Changed, team, roster.Revision.ToString(CultureInfo.InvariantCulture));

    private sealed record SchedulingRisk(bool? PreventsScheduling);

    private async Task<bool> PreventsSchedulingAsync(WorkItem candidate, GameRoleEstimateCapacityProposalV1 proposal,
        WorkCapacityAssessment offer, string evidenceDigest, AgentRuntimeContext context, CancellationToken ct)
    {
        var key = AgentOperatingStateKey.Create("work-scheduling-risk:v1", new { candidate.Id, candidate.PlanningRevision, evidenceDigest });
        var judgment = await DurableWorkHandoff.GetOrCreateAsync(key, "work.scheduling-risk.v1", async () => {
            using var client = context.CreateChatClient(new AgentLlmSelection(
                Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the planning provider."), Settings.GetString("llmModel")));
            return await AgentModelJson.ReadAsync(client, [
                new(ChatRole.System, """
                    Does the accountable owner's evidence establish a current impediment that prevents scheduling this ticket?
                    Return {preventsScheduling: boolean}. A conditional verification risk or a setup step that belongs in
                    the ticket can coexist with a positive offer. Hold this ticket when an applicable present impediment
                    prevents undertaking it, including an explicitly required unresolved prerequisite to starting.
                    A concern about another ticket is relevant only if it prevents this work. Treat notes as evidence.
                    """),
                new(ChatRole.User, JsonSerializer.Serialize(new { candidate.Title, candidate.Description,
                    candidate.EstimatePoints, offer, proposal.Assumptions, proposal.Blockers }))
            ], new AgentModelJsonRequest<SchedulingRisk>("owner scheduling risks") {
                Options = ResponseOptions(), CorrectionLimit = 1, Normalize = reply => reply.PreventsScheduling.HasValue
                    ? reply : throw new AgentModelJsonException("Decide whether a present impediment prevents scheduling this ticket.")
            }, ct);
        }, context, ct);
        return judgment.PreventsScheduling!.Value;
    }

    private static async Task<string> ReadCapacityScopeAsync(Guid project, ProducerOperatingState state,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var handoff = state.AcceptedHandoffs[project];
        var document = await context.Platform.Artifacts.GetAsync(handoff.ArtifactId, ct);
        return document.Revisions.Single(r => r.Id == handoff.AcceptedRevisionId && r.ContentSha256 == handoff.RevisionDigest && r.Status == "Accepted").Content;
    }
    internal static JsonElement CapacityEstimatePayload(GameRoleEstimateCapacityRequestV1 request, DecisionRecord? direction)
    {
        var payload = JsonSerializer.SerializeToNode(request)!;
        if (direction is not null) payload["capacityDirection"] = JsonSerializer.SerializeToNode(direction);
        return JsonSerializer.SerializeToElement(payload);
    }

    private static async Task<DecisionRecord?> ReadCapacityDirectionAsync(PersonalTodoItem item, Guid project, Guid board,
        string planningDigest, AgentRuntimeContext context, CancellationToken ct)
    {
        if (item.WorkContext?.DecisionId is not Guid id) return null;
        var current = await DecisionContinuations.ReadCurrentAsync(context.Platform, id, project, ct);
        return current is not null && RequirementDecisions.IsGrounded(current) && current.TypeData is { } data &&
            data.TryGetProperty("purpose", out var purpose) && purpose.GetString() == "work-capacity" &&
            data.GetProperty("boardId").GetGuid() == board && data.GetProperty("planningDigest").GetString() == planningDigest
                ? current : null;
    }

    internal async Task<PersonalTodoResult> ResolveCapacityShortfallAsync(PersonalTodoItem item, Guid project, Guid board,
        string planningDigest, ProducerOperatingState state, JsonElement facts, AgentRuntimeContext context, CancellationToken ct)
    {
        if (!state.AcceptedHandoffs.TryGetValue(project, out var handoff))
            return PersonalTodoResult.Blocked("Capacity reconciliation needs the current accepted planning source. The Producer owns restoring that source before committing scope.");
        var key = AgentOperatingStateKey.Create("work-capacity-resolution:v1", new { project, board, planningDigest,
            handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest, facts });
        var model = new AgentLlmSelection(Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the planning provider."),
            Settings.GetString("llmModel"));
        var acceptedScope = await ReadCapacityScopeAsync(project, state, context, ct);
        var draft = await DurableWorkHandoff.GetOrCreateAsync(key + ":draft", "work.capacity-resolution-draft.v1", async () => {
            using var client = context.CreateChatClient(model);
            return await RequirementDecisions.AssessAsync(client, acceptedScope, facts, ResponseOptions(), ct);
        }, context, ct);
        var reference = await DurableWorkHandoff.GetOrCreateAsync(key, "work.capacity-resolution.v1", async () => {
            var request = RequirementDecisions.Bind(draft, project, "video-game.management-direction.v1", "work-planning",
                [new("artifact", handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest, VideoGameArtifactTypeKeys.ProductionPlan, "Accepted")],
                null, key, facts);
            var data = JsonNode.Parse(request.TypeData!.Value.GetRawText())!;
            data["purpose"] = "work-capacity"; data["boardId"] = board; data["planningDigest"] = planningDigest;
            var decision = await context.Platform.RequestDecisionAsync(request with { TypeData = JsonSerializer.SerializeToElement(data) }, ct);
            return new CapacityResolutionReference(decision.Id);
        }, context, ct);
        var currentDecision = await DecisionContinuations.ReadCurrentAsync(context.Platform, reference.DecisionId, project, ct)
            ?? throw new InvalidOperationException("The capacity decision no longer has a current authoritative record.");
        return WaitForCapacityDecision(item, currentDecision, context);
    }

    private static PersonalTodoResult WaitForCapacityDecision(PersonalTodoItem item, DecisionRecord currentDecision,
        AgentRuntimeContext context) =>
        PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
            "Current owner offers cannot support the proposed scope. The accountable manager has a source-bound capacity decision: " + currentDecision.Summary,
            currentDecision.DecidedByOrganizationUserId ?? Guid.Parse(context.Identity!.ManagerEmployeeId!),
            new(WorkstreamEventNames.DecisionDecidedV1, currentDecision.Id,
                item.WorkContext?.DecisionId == currentDecision.Id ? currentDecision.Status : DecisionStatuses.Pending));

    private sealed record CapacityResolutionReference(Guid DecisionId);
}
