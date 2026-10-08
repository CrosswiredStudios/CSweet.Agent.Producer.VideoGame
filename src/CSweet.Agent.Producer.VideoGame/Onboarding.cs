using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;

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

            var managerName = context.Identity?.ManagerDisplayName;
            // The kickoff names the next step for both kinds of manager: a Creative Director hands over
            // the accepted pitch and GDD, while an owner-manager can start from a short brief.
            await context.Platform.Communication.SendDirectMessageAsync(managerId,
                "I have joined as Producer and report to you. If you already have an accepted game vision, " +
                "please share the accepted pitch and high-level GDD with me so we can refine the production brief " +
                "and plan staffing together. Otherwise, tell me what you want to make, the first useful milestone, " +
                "and any time, budget, or team constraints you already know. A short brief is enough to " +
                "start a project proposal and a small hiring plan.",
                $"{key}:manager", cancellationToken);
            // Record what the Producer now waits for (before the onboarding record, so a retry rewrites it),
            // letting attention reviews follow up with the manager and escalate to the owner.
            var watch = new ProducerHandoffWatch(managerId, managerName, onboarded.HiringOrganizationUserId,
                onboarded.ConversationId, DateTimeOffset.UtcNow, 0, null, null);
            await new RevisionSafeProjectState(context.Platform).MergeAsync<ProducerHandoffWatch>(
                HandoffFollowUpPolicy.StateKey, HandoffFollowUpPolicy.SchemaId, 1, _ => watch,
                new Dictionary<string, string>(), $"{key}:handoff-watch", cancellationToken);
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