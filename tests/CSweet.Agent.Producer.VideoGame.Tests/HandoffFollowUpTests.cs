using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class HandoffFollowUpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 7, 0, TimeSpan.Zero);

    [Fact]
    public void AgentManagerIsNudgedTwiceThenTheOwnerIsToldOnce()
    {
        var manager = Guid.NewGuid();
        var watch = new ProducerHandoffWatch(manager, "Naomi", Guid.NewGuid(), Guid.NewGuid(), Start, 0, null, null);
        Assert.Equal(HandoffFollowUpAction.None, HandoffFollowUpPolicy.Decide(watch, Start.AddMinutes(29)));
        Assert.Equal(HandoffFollowUpAction.NudgeManager, HandoffFollowUpPolicy.Decide(watch, Start.AddMinutes(30)));
        watch = watch with { Nudges = 1, LastNudgeAt = Start.AddMinutes(30) };
        Assert.Equal(HandoffFollowUpAction.None, HandoffFollowUpPolicy.Decide(watch, Start.AddMinutes(45)));
        Assert.Equal(HandoffFollowUpAction.NudgeManager, HandoffFollowUpPolicy.Decide(watch, Start.AddMinutes(60)));
        watch = watch with { Nudges = 2, LastNudgeAt = Start.AddMinutes(60) };
        Assert.Equal(HandoffFollowUpAction.EscalateToOwner, HandoffFollowUpPolicy.Decide(watch, Start.AddMinutes(90)));
        watch = watch with { EscalatedAt = Start.AddMinutes(90) };
        Assert.Equal(HandoffFollowUpAction.None, HandoffFollowUpPolicy.Decide(watch, Start.AddDays(1)));
    }

    [Fact]
    public void ForeignOrEmptyPayloadsAreNotTreatedAsAWatch()
    {
        Assert.False(HandoffFollowUpPolicy.IsValid(null));
        Assert.False(HandoffFollowUpPolicy.IsValid(new ProducerHandoffWatch(Guid.Empty, null, null, null, Start, 0, null, null)));
        Assert.False(HandoffFollowUpPolicy.IsValid(new ProducerHandoffWatch(Guid.NewGuid(), null, null, null, default, 0, null, null)));
        Assert.True(HandoffFollowUpPolicy.IsValid(new ProducerHandoffWatch(Guid.NewGuid(), null, null, null, Start, 0, null, null)));
    }

    [Fact]
    public void OwnerManagerGetsOneReminderAndNoEscalation()
    {
        var owner = Guid.NewGuid();
        var watch = new ProducerHandoffWatch(owner, "Matt", owner, Guid.NewGuid(), Start, 0, null, null);
        Assert.Equal(HandoffFollowUpAction.NudgeManager, HandoffFollowUpPolicy.Decide(watch, Start.AddHours(1)));
        watch = watch with { Nudges = 1, LastNudgeAt = Start.AddHours(1) };
        Assert.Equal(HandoffFollowUpAction.None, HandoffFollowUpPolicy.Decide(watch, Start.AddDays(1)));
    }

    [Fact]
    public void LegacyWatchWithoutOwnerConversationNeverEscalates()
    {
        var watch = new ProducerHandoffWatch(Guid.NewGuid(), null, null, null, Start, 2, Start, null);
        Assert.Equal(HandoffFollowUpAction.None, HandoffFollowUpPolicy.Decide(watch, Start.AddDays(1)));
    }

    [Fact]
    public async Task ReviewsWithoutAProjectFollowUpThenEscalateIdempotently()
    {
        var h = new Harness();
        await SpecialistAgent.FollowUpAwaitedHandoffAsync(Start, h.Context, default);
        var watch = h.Watch!;
        Assert.Equal(0, watch.Nudges);
        Assert.Null(watch.OwnerConversationId);
        Assert.Empty(h.Messages);

        // Simulate a Producer onboarded by 2.15.0, which records the owner conversation.
        h.SetWatch(watch with { OwnerId = Guid.NewGuid(), OwnerConversationId = h.OwnerConversation });
        foreach (var minutes in new[] { 10, 30, 40, 60, 90, 95, 200 })
            await SpecialistAgent.FollowUpAwaitedHandoffAsync(Start.AddMinutes(minutes), h.Context, default);

        Assert.Equal(2, h.Messages.Keys.Count(x => x.StartsWith("producer-handoff-nudge:", StringComparison.Ordinal)));
        var escalation = Assert.Single(h.Messages, x => x.Key.StartsWith("producer-handoff-escalation:", StringComparison.Ordinal));
        Assert.Contains("Creative Director", escalation.Value);
        Assert.Equal(h.OwnerConversation, h.EscalationChat);
        Assert.NotNull(h.Watch!.EscalatedAt);
    }

    private sealed class Harness
    {
        private readonly Guid producer = Guid.NewGuid(), director = Guid.NewGuid(), managerChat = Guid.NewGuid();
        private AgentOperatingStateResponse? state;
        public readonly Guid OwnerConversation = Guid.NewGuid();
        public readonly Dictionary<string, string> Messages = [];
        public Guid? EscalationChat;
        public AgentRuntimeContext Context { get; }
        public ProducerHandoffWatch? Watch => state?.Payload.Deserialize<ProducerHandoffWatch>();

        public void SetWatch(ProducerHandoffWatch watch) => state = state! with
        {
            Payload = JsonSerializer.SerializeToElement(watch), Revision = state.Revision + 1
        };

        public Harness()
        {
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey == HandoffFollowUpPolicy.StateKey ? state : null)))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) =>
                    {
                        Assert.Equal(HandoffFollowUpPolicy.StateKey, r.StateKey);
                        Assert.Equal(state?.Revision, r.ExpectedRevision);
                        state = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions,
                            r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId,
                            r.Payload, (state?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                        return Task.FromResult(state);
                    })
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.read.v1", (_, _) =>
                    Task.FromResult(JsonSerializer.SerializeToElement(new { chats = Array.Empty<object>() })))
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (_, _) =>
                    Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        succeeded = true, message = "Created", chat = new { id = managerChat, isDirect = true, isPrivate = true,
                            participants = new[] { new { organizationUserId = director, employeeType = "Agent", displayName = "Director", role = "Member" } } }
                    })))
                .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) =>
                {
                    var chat = r.GetProperty("chatId").GetGuid();
                    var key = r.GetProperty("idempotencyKey").GetString()!;
                    var content = r.GetProperty("content").GetString()!;
                    Messages[key] = content;
                    if (key.StartsWith("producer-handoff-escalation:", StringComparison.Ordinal)) EscalationChat = chat;
                    return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, producer, "Producer", "Agent", content,
                        DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
                });
            Context = runtime.CreateContext(Guid.NewGuid().ToString(), identity: new AgentIdentity(producer.ToString(), "Producer", null,
                "Producer", null, [], null, director.ToString(), "Creative Director"));
        }
    }
}
