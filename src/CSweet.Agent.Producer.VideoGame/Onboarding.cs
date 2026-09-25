using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private async Task HandleOnboardedAsync(AgentEventEnvelope message,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var onboarded = message.Data.Deserialize<AgentOnboardedEvent>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (onboarded is null || onboarded.OrganizationId == Guid.Empty ||
            onboarded.AgentOrganizationUserId == Guid.Empty || onboarded.ConversationId == Guid.Empty ||
            onboarded.HiringOrganizationUserId == Guid.Empty ||
            !Guid.TryParse(context.BusinessId, out var organizationId) || organizationId != onboarded.OrganizationId ||
            !Guid.TryParse(context.Identity?.EmployeeId, out var employeeId) || employeeId != onboarded.AgentOrganizationUserId)
            return;

        var key = $"producer-onboarding:{message.EventId:N}";
        var prior = await context.Platform.ReadOperatingStateAsync<ProducerOnboardingState>(key, cancellationToken);
        if (prior is null)
        {
            if (!Guid.TryParse(context.Identity?.ManagerEmployeeId, out var managerId) || managerId == Guid.Empty)
                throw new InvalidOperationException("Producer onboarding requires an authoritative manager.");

            await context.Platform.Communication.SendDirectMessageAsync(managerId,
                "I have joined as Producer. Tell me what you want to make, the first useful milestone, " +
                "and any time, budget, or team constraints you already know. A short brief is enough to " +
                "start a project proposal and a small hiring plan. I can work directly for you; " +
                "a Creative Director, pitch, and GDD are optional unless you choose that process.",
                $"{key}:manager", cancellationToken);
            await context.Platform.Communication.SendMessageAsync(onboarded.ConversationId,
                "I am ready to start from your direction. Share a lightweight goal and I can propose " +
                "the project and the smallest justified team. Project creation and hiring will follow " +
                "the platform's approval steps.",
                $"{key}:owner", cancellationToken);
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(key,
                "video-game.producer-onboarding.v1", 1, "Active", new Dictionary<string, string>(), [], key, [],
                message.EventId, JsonSerializer.SerializeToElement(new ProducerOnboardingState(managerId,
                    onboarded.ConversationId, DateTimeOffset.UtcNow)), null, key), cancellationToken);
        }
        // Acknowledgement follows durable messages and state, so interrupted delivery can retry safely.
        await context.Platform.Lifecycle.CompleteOnboardingAsync(message, cancellationToken);
    }
}

internal sealed record ProducerOnboardingState(Guid ManagerId, Guid ConversationId, DateTimeOffset ContactedAt);