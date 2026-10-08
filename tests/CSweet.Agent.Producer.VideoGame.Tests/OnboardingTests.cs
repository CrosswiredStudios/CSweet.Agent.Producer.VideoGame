using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class OnboardingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnboardingContactsOnlyManagerThenPersistsBeforeAcknowledging(bool managerIsOwner)
    {
        var h = new Harness(managerIsOwner);
        await h.Deliver();
        Assert.Single(h.Messages);
        Assert.Contains(h.Messages.Values, x => x.Contains("short brief"));
        Assert.Equal(h.ManagerId, Assert.Single(h.Recipients));
        Assert.Equal(h.ManagerChat, Assert.Single(h.MessageChats));
        Assert.NotNull(h.State);
        Assert.NotNull(h.WatchState);
        Assert.Equal(1, h.Acknowledgements);
        // A new delivery work ID retains the source event ID and does not introduce twice.
        await h.Deliver();
        Assert.Equal(1, h.SendCalls);
        Assert.Equal(2, h.Acknowledgements);
    }

    [Fact]
    public async Task InterruptedIntroductionRetriesWithStableMessageKeys()
    {
        var h = new Harness { FailStateOnce = true };
        await Assert.ThrowsAnyAsync<Exception>(h.Deliver);
        Assert.Null(h.State);
        Assert.Equal(0, h.Acknowledgements);
        await h.Deliver();
        Assert.Single(h.Messages);
        Assert.All(h.MessageChats, chat => Assert.Equal(h.ManagerChat, chat));
        Assert.Equal(1, h.Acknowledgements);
    }

    [Fact]
    public async Task AnotherEmployeesOnboardingIsIgnored()
    {
        var h = new Harness();
        await h.Deliver(Guid.NewGuid());
        Assert.Empty(h.Messages);
        Assert.Null(h.State);
        Assert.Equal(0, h.Acknowledgements);
    }

    private sealed class Harness
    {
        private readonly Guid organization = Guid.NewGuid(), producer = Guid.NewGuid(), director = Guid.NewGuid();
        private readonly Guid conversation = Guid.NewGuid(), managerChat = Guid.NewGuid(), eventId = Guid.NewGuid();
        private readonly Guid owner;
        private readonly AgentRuntimeContext context;
        public readonly Dictionary<string, string> Messages = [];
        public AgentOperatingStateResponse? State;
        public AgentOperatingStateResponse? WatchState;
        public readonly List<Guid> Recipients = [];
        public readonly List<Guid> MessageChats = [];
        public Guid ManagerId => director;
        public Guid ManagerChat => managerChat;
        public int SendCalls, Acknowledgements;
        public bool FailStateOnce;

        public Harness(bool managerIsOwner = false)
        {
            owner = managerIsOwner ? director : Guid.NewGuid();
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey == HandoffFollowUpPolicy.StateKey ? WatchState : State)))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => {
                        Assert.Single(Messages);
                        if (r.StateKey != HandoffFollowUpPolicy.StateKey && FailStateOnce)
                        { FailStateOnce = false; throw new InvalidOperationException("Temporary interruption"); }
                        var state = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                            new Dictionary<string, string>(), [], r.StateKey, [], eventId, r.Payload,
                            r.ExpectedRevision.GetValueOrDefault() + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                        if (r.StateKey == HandoffFollowUpPolicy.StateKey) WatchState = state;
                        else { Assert.NotNull(WatchState); State = state; }
                        return Task.FromResult(state);
                    })
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.read.v1", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { chats = Array.Empty<object>() })))
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (r, _) => {
                    Recipients.Add(Assert.Single(r.GetProperty("participantOrganizationUserIds").EnumerateArray()).GetGuid());
                    return Task.FromResult(JsonSerializer.SerializeToElement(new {
                    succeeded = true, message = "Created", chat = new { id = managerChat, isDirect = true, isPrivate = true,
                        participants = new[] { new { organizationUserId = director, employeeType = "Agent", displayName = "Director", role = "Member" } } }
                })); })
                .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) => {
                    SendCalls++;
                    var chat = r.GetProperty("chatId").GetGuid();
                    MessageChats.Add(chat);
                    var content = r.GetProperty("content").GetString()!;
                    Messages[r.GetProperty("idempotencyKey").GetString()!] = content;
                    return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, producer, "Producer", "Agent", content,
                        DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
                })
                .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(AgentLifecycleCapabilities.CompleteOnboarding,
                    (r, _) => { Assert.Equal(eventId, r.EventId); Assert.NotNull(State); Acknowledgements++; return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow)); });
            context = runtime.CreateContext(organization.ToString(), identity: new AgentIdentity(producer.ToString(), "Producer", null,
                "Producer", null, [], null, director.ToString(), managerIsOwner ? "CEO" : "Creative Director"));
        }
        public Task Deliver() => Deliver(producer);
        public Task Deliver(Guid employee) => new SpecialistAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), eventId,
            AgentLifecycleEvents.Onboarded, JsonSerializer.SerializeToElement(new AgentOnboardedEvent(organization, employee,
                owner, conversation, DateTimeOffset.UtcNow)), DateTimeOffset.UtcNow), context, default);
    }
}
