using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
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
        var own = (await context.Platform.PersonalTodo.ListAsync(ct)).Boards.SelectMany(b => b.Items).Single(i => i.Id == item.Id);
        if (own.WorkContext?.DecisionId != currentDecision.Id)
            await context.Platform.PersonalTodo.UpdateAsync(new(own.Id, own.Title, own.Description, own.Priority, own.DueDate,
                own.Revision, AgentOperatingStateKey.Create("capacity-dependency:v1", new { itemId = own.Id, own.Revision, decisionId = currentDecision.Id })) {
                    WorkContext = own.WorkContext! with { DecisionId = currentDecision.Id }
                }, ct);
        return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
            "Current owner offers cannot support the proposed scope. The accountable manager has a source-bound capacity decision: " + currentDecision.Summary,
            currentDecision.DecidedByOrganizationUserId ?? Guid.Parse(context.Identity!.ManagerEmployeeId!));
    }

    private sealed record CapacityResolutionReference(Guid DecisionId);
}
