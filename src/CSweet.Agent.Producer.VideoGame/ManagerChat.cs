using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private sealed record ManagerHiringRole(string RoleKey, string Title, string Purpose, int Priority);
    private sealed record ManagerTurnPlan(
        string Response, bool CreateProject, string? ProjectName, string? ProjectOutcome,
        IReadOnlyList<ManagerHiringRole>? TeamRoles);

    private async Task HandleManagerMessageAsync(
        AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var incoming = message.Data.Deserialize<CommunicationMessageReceivedEvent>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (incoming is null || incoming.ProviderProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(incoming.ConversationId) || incoming.TurnId == Guid.Empty)
            return;

        await using var stream = context.CreateTurnStream(
            incoming.ConversationId, incoming.TurnId, incoming.Attempt);
        try
        {
            var managerId = Guid.TryParse(context.Identity?.ManagerEmployeeId, out var parsedManager)
                ? parsedManager : Guid.Empty;
            var senderId = incoming.Context is not null &&
                incoming.Context.TryGetValue(CommunicationMessageContextKeys.SenderOrganizationUserId, out var sender)
                && Guid.TryParse(sender, out var parsedSender) ? parsedSender : Guid.Empty;
            var isManager = managerId != Guid.Empty && senderId == managerId;
            var provider = Settings.GetGuid("llmProviderId") ?? incoming.ProviderProfileId;
            var client = context.CreateChatClient(new AgentLlmSelection(
                provider, Settings.GetString("llmModel"),
                new AgentLlmInvocationContext(Guid.Parse(incoming.ConversationId), incoming.TurnId, "producer-manager-chat")));
            var answer = await client.GetResponseAsync([
                new ChatMessage(ChatRole.System, """
                    You are the game Producer. Respond to the current manager or colleague's actual request.
                    A project can start from a short verbal brief. Never require a Creative Director, accepted
                    pitch, GDD, or formal handoff unless the manager requests that workflow or a specific
                    execution gate actually requires it. For a new game or demo, propose a small team based
                    on the work described, with role purpose and hiring order. Treat unconfirmed details as
                    assumptions, and ask only for details that block the next action. Do not claim a project
                    or team was created, hiring was approved, or work began without a platform result.
                    Return one JSON object with: response (a concise Markdown answer including a
                    concrete lightweight hiring plan when requested), createProject (true only when
                    the current sender explicitly asks to create a new project), projectName (short
                    specific name when createProject is true), projectOutcome (one sentence), and teamRoles (a short array of roleKey, title,
                    purpose, priority, only when the manager asks for a hiring/team plan). Use
                    canonical game role keys such as game-designer, game-engineer, technical-artist,
                    game-quality-assurance, and game-technical-director. Recommend only roles
                    justified by this demo; do not assume every project needs a technical or
                    creative director.
                    """),
                new ChatMessage(ChatRole.User, incoming.Message)
            ], ResponseOptions(), cancellationToken);
            var raw = answer.Text?.Trim() ?? string.Empty;
            var plan = ParseManagerTurn(raw);
            var reply = plan.Response;
            if (string.IsNullOrWhiteSpace(reply))
                reply = "I received the request. Please restate the project goal so I can prepare a concrete plan.";

            if (isManager && plan.CreateProject &&
                !string.IsNullOrWhiteSpace(plan.ProjectName) &&
                !string.IsNullOrWhiteSpace(plan.ProjectOutcome) &&
                Guid.TryParse(context.Identity?.EmployeeId, out var producerId))
            {
                try
                {
                    var result = await ProposeManagerProjectAsync(
                        plan.ProjectName, plan.ProjectOutcome, producerId, incoming.TurnId,
                        context, cancellationToken);
                    reply += result.ApprovalId.HasValue
                        ? $"\n\nI submitted the project setup proposal for approval (request {result.ApprovalId:D}). The approved project can hold delivery work once a team is attached."
                        : "\n\nI submitted the project setup proposal for approval.";
                }
                catch (PlatformCapabilityException exception)
                {
                    reply += $"\n\nI could not submit project setup: {exception.Message}. The hiring plan above is a proposal; no project or hires have been created.";
                }
            }
            if (isManager && plan.TeamRoles is { Count: > 0 } &&
                Guid.TryParse(context.Identity?.EmployeeId, out var staffingProducerId))
            {
                try
                {
                    var request = await ProposeManagerTeamAsync(plan, staffingProducerId,
                        incoming, context, cancellationToken);
                    reply += $"\n\nI submitted the proposed team and hiring order for manager review (request {request.Id:D}). Hiring remains pending your decision.";
                }
                catch (PlatformCapabilityException exception)
                {
                    reply += $"\n\nI could not submit the team plan: {exception.Message}. No hires have been approved.";
                }
            }
            await stream.CommitAsync(reply, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await stream.FailAsync("I could not complete this turn. Please retry it.", cancellationToken);
        }
    }

    private static ManagerTurnPlan ParseManagerTurn(string raw)
    {
        var fence = new string((char)96, 3);
        if (raw.StartsWith(fence, StringComparison.Ordinal))
        {
            var firstLine = raw.IndexOf('\n');
            var closing = raw.LastIndexOf(fence, StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine)
                raw = raw[(firstLine + 1)..closing].Trim();
        }
        try
        {
            var plan = JsonSerializer.Deserialize<ManagerTurnPlan>(raw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (plan is not null && !string.IsNullOrWhiteSpace(plan.Response))
                return plan;
        }
        catch (JsonException) { }
        return new ManagerTurnPlan(raw, false, null, null, null);
    }

    private static Task<ResourceChangeRequestResponse> ProposeManagerTeamAsync(
        ManagerTurnPlan plan, Guid producerId, CommunicationMessageReceivedEvent incoming,
        AgentRuntimeContext context, CancellationToken token)
    {
        var allowed = typeof(VideoGameRoleKeys).GetFields().Where(x => x.IsLiteral)
            .Select(x => (string)x.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var roles = (plan.TeamRoles ?? []).Where(x => allowed.Contains(x.RoleKey) &&
                x.RoleKey != VideoGameRoleKeys.Producer &&
                !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Purpose))
            .DistinctBy(x => x.RoleKey).Take(6)
            .Select(x => new ResourceChangeRole(x.RoleKey, "game-delivery",
                x.Title.Trim()[..Math.Min(x.Title.Trim().Length, 256)],
                x.Purpose.Trim()[..Math.Min(x.Purpose.Trim().Length, 2048)],
                1, Math.Clamp(x.Priority, 1, 100), "Next planned increment",
                ["work.execution.run.v1"], false, producerId, null)
            { RoleCategoryKey = x.RoleKey }).ToList();
        if (roles.Count == 0)
            throw new PlatformCapabilityException(PlatformCapabilities.ResourceChangePropose,
                PlatformCapabilityErrorCode.ValidationFailed, "The suggested roles did not match available game roles.");
        var teamName = string.IsNullOrWhiteSpace(plan.ProjectName)
            ? "Game delivery team" : plan.ProjectName.Trim() + " team";
        if (teamName.Length > 160) teamName = teamName[..160];
        return context.Platform.ProposeResourceChangeAsync(new ResourceChangeProposalRequest(
            Guid.Parse(incoming.ConversationId), incoming.TurnId,
            plan.ProjectOutcome ?? "Deliver the manager's requested game milestone.",
            "Start with the smallest set of specialist roles justified by the manager's brief.",
            1, roles,
            ["The initial scope is a lightweight manager brief; details will be refined during work."],
            ["Hiring and spending require manager approval.", "Execution waits for staffed roles and readiness evidence."],
            null, $"producer-manager-team:{incoming.TurnId:N}")
        {
            TeamKey = $"producer-team-{incoming.TurnId:N}",
            TeamName = teamName,
            TeamDescription = plan.ProjectOutcome
        }, token);
    }
    private static Task<MutationResponse> ProposeManagerProjectAsync(
        string name, string outcome, Guid producerId, Guid turnId,
        AgentRuntimeContext context, CancellationToken token)
    {
        var title = name.Trim();
        if (title.Length > 160) title = title[..160];
        var goal = outcome.Trim();
        if (goal.Length > 4000) goal = goal[..4000];
        var initialTeamId = Guid.TryParse(context.Identity?.TeamContext?.TeamId, out var teamId)
            ? teamId : (Guid?)null;
        var metadata = new VideoGameProjectMetadataV1(
            title, "To be defined", ["To be defined"], "To be validated",
            "Technical choice pending", "To be defined",
            ["Deliver the manager's stated demo outcome"],
            "To be defined", false, false,
            ["Readable presentation", "Configurable controls"], []);
        var authority = new WorkstreamAuthorityEnvelope(
            0.05m, 14,
            typeof(VideoGameRoleKeys).GetFields().Where(x => x.IsLiteral)
                .Select(x => (string)x.GetRawConstantValue()!).ToList(),
            ["funding-exception", "material-strategy-change", "legal-commitment",
             "publication", "launch", "sunset"],
            ["work-planning", "routine-staffing", "build", "validation",
             "preview", "evaluation", "gate-submit"], null);
        return context.Platform.ProposeWorkstreamAsync(new WorkstreamPlanProposalV2Request(
            title, goal,
            ["A playable demo demonstrates the manager's requested game loop.",
             "Visual effects and performance are validated on the agreed target."],
            VideoGameLifecyclePhases.Concept, producerId, initialTeamId, [],
            ["game-production", "game-design", "software-delivery", "quality-assurance"],
            null, null, null, null,
            "The reporting manager requested a lightweight project start and a small delivery team.",
            $"producer-manager-project:{turnId:N}",
            "video-game-manager-brief.v1", 1,
            JsonSerializer.SerializeToElement(metadata,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            authority, [], []), token);
    }
}