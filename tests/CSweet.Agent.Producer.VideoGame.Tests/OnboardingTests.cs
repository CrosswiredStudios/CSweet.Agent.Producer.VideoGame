using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class OnboardingTests
{
    [Fact]
    public async Task OnboardingContactsDirectorAndOwnerThenPersistsBeforeAcknowledging()
    {
        var h = new Harness();
        await h.Deliver();
        Assert.Equal(2, h.Messages.Count);
        Assert.Contains(h.Messages.Values, x => x.Contains("accepted pitch"));
        Assert.Contains(h.Messages.Values, x => x.Contains("recommend staffing"));
        Assert.NotNull(h.State);
        Assert.Equal(1, h.Acknowledgements);
        // A new delivery work ID retains the source event ID and does not introduce twice.
        await h.Deliver();
        Assert.Equal(2, h.SendCalls);
        Assert.Equal(2, h.Acknowledgements);
    }

    [Fact]
    public async Task InterruptedIntroductionRetriesWithStableMessageKeys()
    {
        var h = new Harness { FailOwnerOnce = true };
        await Assert.ThrowsAnyAsync<Exception>(h.Deliver);
        Assert.Null(h.State);
        Assert.Equal(0, h.Acknowledgements);
        await h.Deliver();
        Assert.Equal(2, h.Messages.Count);
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
        private readonly AgentRuntimeContext context;
        public readonly Dictionary<string, string> Messages = [];
        public AgentOperatingStateResponse? State;
        public int SendCalls, Acknowledgements;
        public bool FailOwnerOnce;

        public Harness()
        {
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(State)))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => { Assert.Equal(2, Messages.Count); State = new(Guid.NewGuid(), r.StateKey, "video-game.producer-onboarding.v1", 1, "Active",
                        new Dictionary<string, string>(), [], r.StateKey, [], eventId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); return Task.FromResult(State); })
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.read.v1", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { chats = Array.Empty<object>() })))
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (r, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
                    succeeded = true, message = "Created", chat = new { id = managerChat, isDirect = true, isPrivate = true,
                        participants = new[] { new { organizationUserId = director, employeeType = "Agent", displayName = "Director", role = "Member" } } }
                })))
                .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) => {
                    SendCalls++;
                    var chat = r.GetProperty("chatId").GetGuid();
                    if (chat == conversation && FailOwnerOnce) { FailOwnerOnce = false; throw new InvalidOperationException("Temporary interruption"); }
                    var content = r.GetProperty("content").GetString()!;
                    Messages[r.GetProperty("idempotencyKey").GetString()!] = content;
                    return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, producer, "Producer", "Agent", content,
                        DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
                })
                .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(AgentLifecycleCapabilities.CompleteOnboarding,
                    (r, _) => { Assert.Equal(eventId, r.EventId); Assert.NotNull(State); Acknowledgements++; return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow)); });
            context = runtime.CreateContext(organization.ToString(), identity: new AgentIdentity(producer.ToString(), "Producer", null,
                "Producer", null, [], null, director.ToString(), "Creative Director"));
        }
        public Task Deliver() => Deliver(producer);
        public Task Deliver(Guid employee) => new SpecialistAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), eventId,
            AgentLifecycleEvents.Onboarded, JsonSerializer.SerializeToElement(new AgentOnboardedEvent(organization, employee,
                Guid.NewGuid(), conversation, DateTimeOffset.UtcNow)), DateTimeOffset.UtcNow), context, default);
    }
}