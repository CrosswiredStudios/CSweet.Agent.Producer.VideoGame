using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

/// <summary>
/// What the Producer is waiting for before any project exists: its manager's handoff (accepted pitch
/// and GDD, or a short brief). Durable, so a restarted or upgraded Producer still knows the next step.
/// </summary>
internal sealed record ProducerHandoffWatch(
    Guid ManagerId,
    string? ManagerName,
    Guid? OwnerId,
    Guid? OwnerConversationId,
    DateTimeOffset WaitingSince,
    int Nudges,
    DateTimeOffset? LastNudgeAt,
    DateTimeOffset? EscalatedAt);

internal enum HandoffFollowUpAction { None, NudgeManager, EscalateToOwner }

internal static class HandoffFollowUpPolicy
{
    public const string StateKey = "producer-handoff-watch";
    public const string SchemaId = "video-game.producer-handoff-watch.v1";
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(30);
    public const int MaximumNudges = 2;

    /// <summary>
    /// A watch without a manager or start time is not usable (for example, a missing or foreign
    /// operating-state payload); the caller restarts the watch instead of acting on it.
    /// </summary>
    public static bool IsValid(ProducerHandoffWatch? watch) =>
        watch is not null && watch.ManagerId != Guid.Empty && watch.WaitingSince != default;

    public static HandoffFollowUpAction Decide(ProducerHandoffWatch watch, DateTimeOffset now)
    {
        if (now - (watch.LastNudgeAt ?? watch.WaitingSince) < Patience) return HandoffFollowUpAction.None;
        // A human manager who hired the Producer directly gets one reminder and no escalation.
        var managerIsOwner = watch.OwnerId == watch.ManagerId;
        if (watch.Nudges < (managerIsOwner ? 1 : MaximumNudges)) return HandoffFollowUpAction.NudgeManager;
        return !managerIsOwner && watch.EscalatedAt is null && watch.OwnerConversationId.HasValue
            ? HandoffFollowUpAction.EscalateToOwner
            : HandoffFollowUpAction.None;
    }

    public static string NudgeMessage(ProducerHandoffWatch watch) =>
        "Following up on my kickoff: I still have no project or accepted brief to plan from, so the next step is " +
        "yours. Please share the accepted pitch and high-level GDD (or a short brief if there isn't one yet). " +
        "I check again automatically on my reviews; nothing needs to be resent.";

    public static string EscalationMessage(ProducerHandoffWatch watch)
    {
        var manager = watch.ManagerName ?? "my manager";
        return $"Status: I've been waiting since {watch.WaitingSince:yyyy-MM-dd HH:mm} UTC for {manager} to hand over " +
            $"the project (accepted pitch and high-level GDD) and have followed up {watch.Nudges} times without a handoff. " +
            $"The next step is on {manager}'s side. You may want to check on {manager}, or give me direction here.";
    }
}

public sealed partial class SpecialistAgent
{
    /// <summary>
    /// Runs on attention reviews while the Producer has no visible project. Instead of silently
    /// idling, it follows up with its manager and, if the handoff still does not arrive, tells the owner.
    /// </summary>
    internal static async Task FollowUpAwaitedHandoffAsync(DateTimeOffset now, AgentRuntimeContext context,
        CancellationToken token)
    {
        AgentOperatingState<ProducerHandoffWatch>? existing;
        try { existing = await context.Platform.ReadOperatingStateAsync<ProducerHandoffWatch>(HandoffFollowUpPolicy.StateKey, token); }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound) { existing = null; }
        var store = new RevisionSafeProjectState(context.Platform);
        var watch = existing?.Payload;
        if (!HandoffFollowUpPolicy.IsValid(watch))
        {
            // Producers onboarded before 2.15.0 have no watch: start the clock now.
            if (!Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) || manager == Guid.Empty) return;
            var started = new ProducerHandoffWatch(manager, context.Identity?.ManagerDisplayName, null, null, now, 0, null, null);
            await store.MergeAsync<ProducerHandoffWatch>(HandoffFollowUpPolicy.StateKey, HandoffFollowUpPolicy.SchemaId, 1,
                current => HandoffFollowUpPolicy.IsValid(current) ? current! : started,
                new Dictionary<string, string>(), $"{HandoffFollowUpPolicy.StateKey}:start", token);
            return;
        }

        switch (HandoffFollowUpPolicy.Decide(watch!, now))
        {
            case HandoffFollowUpAction.NudgeManager:
                var nudge = watch.Nudges + 1;
                // A direct message from the Producer is also the Director's kickoff wake, so the nudge
                // re-triggers the idempotent documentation handoff on the manager's side.
                await context.Platform.Communication.SendDirectMessageAsync(watch.ManagerId,
                    HandoffFollowUpPolicy.NudgeMessage(watch),
                    $"producer-handoff-nudge:{watch.ManagerId:N}:{watch.WaitingSince.UtcTicks}:{nudge}", token);
                await store.MergeAsync<ProducerHandoffWatch>(HandoffFollowUpPolicy.StateKey, HandoffFollowUpPolicy.SchemaId, 1,
                    current => (current ?? watch) with { Nudges = Math.Max((current ?? watch).Nudges, nudge), LastNudgeAt = now },
                    new Dictionary<string, string>(), $"{HandoffFollowUpPolicy.StateKey}:nudge:{nudge}", token);
                break;
            case HandoffFollowUpAction.EscalateToOwner:
                await context.Platform.Communication.SendMessageAsync(watch.OwnerConversationId!.Value,
                    HandoffFollowUpPolicy.EscalationMessage(watch),
                    $"producer-handoff-escalation:{watch.WaitingSince.UtcTicks}", token);
                await store.MergeAsync<ProducerHandoffWatch>(HandoffFollowUpPolicy.StateKey, HandoffFollowUpPolicy.SchemaId, 1,
                    current => (current ?? watch) with { EscalatedAt = now },
                    new Dictionary<string, string>(), $"{HandoffFollowUpPolicy.StateKey}:escalated", token);
                break;
        }
    }
}
