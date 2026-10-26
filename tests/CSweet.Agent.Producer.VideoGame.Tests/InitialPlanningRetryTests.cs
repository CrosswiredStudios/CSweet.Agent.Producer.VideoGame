using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class InitialPlanningRetryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportingAncestorCanWakeTheSameBlockedInitialPlanningWithoutSkippingApprovals(bool authorized)
    {
        var producer = Guid.NewGuid(); var manager = Guid.NewGuid(); var sender = Guid.NewGuid();
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with {
            Id = Guid.NewGuid(), Revision = 7, Status = PersonalTodoStatuses.Blocked,
            OwnerOrganizationUserId = producer, CorrelationId = "producer-planning:approved-project:exact-brief" };
        var requeues = new List<Guid>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), producer, "Producer", manager, "Director", 1, [item])], producer)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue,
                (request, _) => { requeues.Add(request.ItemId); Assert.Equal(item.Revision, request.ExpectedRevision);
                    return Task.FromResult(item with { Status = PersonalTodoStatuses.Ready }); });
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), sender.ToString(),
            "Retry staffing: PRISM BREAK technical staff are hired; resume the retained planning commitment.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = sender.ToString(),
                ["senderIsReportingAncestor"] = authorized ? "true" : "false" }, Guid.NewGuid(), 1, Guid.NewGuid());
        await new SpecialistAgent().HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(incoming, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow),
            runtime.CreateContext(identity: new(producer.ToString(), "Producer", null, null, null, [], null, manager.ToString(), "Director")), default);
        Assert.Equal(authorized ? new[] { item.Id } : [], requeues);
    }
}
