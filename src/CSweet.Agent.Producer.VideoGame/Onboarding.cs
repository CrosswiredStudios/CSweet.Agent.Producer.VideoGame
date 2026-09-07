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

            await context.Platform.Communication.SendDirectAgentMessageAsync(managerId,
                "I have joined as Producer and am ready to understand the project. Please share the accepted pitch and GDD " +
                "through our project handoff. I will clarify scope, success criteria, constraints, risks and delivery needs " +
                "with you, retain the shared production brief and accepted understanding in durable state, then propose " +
                "the smallest team justified by the work. Please surface any project setup blocker while we prepare the handoff.",
                $"{key}:manager", cancellationToken);
            await context.Platform.Communication.SendMessageAsync(onboarded.ConversationId,
                "I have contacted the Creative Director to begin the project handoff. I will build and retain our shared " +
                "understanding, then recommend staffing based on the accepted scope. Detailed brief refinement begins " +
                "when project setup is ready; any required setup approval remains visible in Approvals.",
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