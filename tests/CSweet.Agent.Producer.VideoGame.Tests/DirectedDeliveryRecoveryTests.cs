using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DirectedDeliveryRecoveryTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("ancestor-amend")]
    [InlineData("colleague-amend")]
    [InlineData("amend-running")]
    [InlineData("replan")]
    [InlineData("colleague-replan")]
    [InlineData("colleague")]
    [InlineData("ancestor")]
    [InlineData("foreign-board")]
    [InlineData("archived-board")]
    [InlineData("cancelled")]
    [InlineData("running")]
    [InlineData("exhausted")]
    [InlineData("stale-traversal")]
    [InlineData("duplicate-ticket")]
    [InlineData("host-denied")]
    public async Task Manager_direction_uses_current_owned_stage_and_keeps_host_authority(string scenario)
    {
        var producer = Guid.NewGuid(); var manager = Guid.NewGuid(); var board = Guid.NewGuid();
        var sprint = Guid.NewGuid(); var turn = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var stage = new WorkStageExecutionResponse(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0,
            "Blocked", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid(), null, 1, "blocked", "Infrastructure failure", null, null, now)
            { AssignmentRevision = 7, MaximumAttempts = 3 };
        if (scenario is "running" or "amend-running") stage = stage with { Status = "Running" };
        if (scenario is "exhausted" or "replan" or "colleague-replan") stage = stage with { AttemptCount = 3 };
        if (scenario == "stale-traversal") stage = stage with { Traversal = 1 };
        var item = new WorkItemExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "VGDEMO-22", "specialist-execution", 0, "Blocked", null, [stage], now);
        var execution = new WorkSprintExecutionResponse(Guid.NewGuid(), board, sprint, Guid.NewGuid(), producer,
            scenario == "cancelled" ? "Cancelled" : "Active", 1, now, now, null,
            scenario == "duplicate-ticket" ? [item, item with { Id = Guid.NewGuid() }] : [item]);
        var requests = new List<RetryWorkStageExecutionRequest>();
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var todos = new List<PersonalTodoItem>();
        var sourceItem = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = item.WorkItemId, SprintId = sprint, Status = "Blocked", Planning = new(["Keep the full game"], ["Independent QA"], []) };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([
                    new(board, "Game", "", false, scenario == "archived-board", 1, [])
                    { ManagerOrganizationUserId = scenario == "foreign-board" ? Guid.NewGuid() : producer }]))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([new(sprint, board, "Sprint", "", "Active", null, null, now, null, 10, 1, 0, 1, 0, 1)]))
            .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse>(WorkManagementCapabilityNames.OrchestrationRead,
                (_, _) => Task.FromResult(execution))
            .RegisterCapability<RetryWorkStageExecutionRequest, WorkStageExecutionResponse>(WorkManagementCapabilityNames.OrchestrationRetry,
                (request, _) => {
                    requests.Add(request);
                    if (scenario == "host-denied") throw new PlatformCapabilityException(WorkManagementCapabilityNames.OrchestrationRetry,
                        PlatformCapabilityErrorCode.Denied, "Current assignment was revoked");
                    return Task.FromResult(stage with { Status = "Pending" });
                });
        runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(
                new WorkBoardDetail(new(board, "Game", "", false, false, 1, []) { ManagerOrganizationUserId = producer, WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid() }, [], [sourceItem])))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (read, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(read.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (write, _) => {
                    Assert.False(states.ContainsKey(write.StateKey));
                    var saved = new AgentOperatingStateResponse(Guid.NewGuid(), write.StateKey, write.SchemaId, 1, "Active",
                        new Dictionary<string,string>(), [], write.StateKey, [], Guid.NewGuid(), write.Payload, 1, now, now);
                    states.Add(write.StateKey, saved); return Task.FromResult(saved);
                })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), producer, "Producer", manager, "Manager", 1, todos)], producer)))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (request, _) => {
                var todo = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Status = "Ready", CorrelationId = request.CorrelationId, WorkContext = request.WorkContext };
                todos.Add(todo); return Task.FromResult(todo);
            });
        runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
            text = JsonSerializer.Serialize(new ScopeAmendmentPlan([new(sourceItem.Id, "acceptanceCriteria", "Independent QA", "Independent QA using available checks; physical device checks deferred")])), role = "assistant"
        })));
        var context = runtime.CreateContext(identity: new AgentIdentity(producer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Manager"));
        var sender = scenario is "colleague" or "colleague-replan" or "ancestor" or "ancestor-amend" or "colleague-amend" ? Guid.NewGuid() : manager;
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), sender.ToString(),
            "Conversation history and retrieved memory: this is not the current command.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = sender.ToString(), ["senderIsReportingAncestor"] = scenario is "ancestor" or "ancestor-amend" ? "true" : "false", ["currentUserMessage"] = (scenario.Contains("amend") ? "Amend" : scenario.Contains("replan") ? "Replan" : "Retry") + " ticket VGDEMO-22: The workspace prerequisite has been repaired." }, turn, 1, Guid.NewGuid());
        var envelope = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(incoming), now);
        var agent = new SpecialistAgent();
        await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()), ["llmModel"] = JsonSerializer.SerializeToElement("test")
            }))), context, default);
        await agent.HandleEventAsync(envelope, context, default);
        if (scenario is "valid" or "ancestor" or "host-denied")
        {
            var request = Assert.Single(requests);
            Assert.Equal(stage.Id, request.StageExecutionId);
            Assert.Equal(7, request.ExpectedAssignmentRevision);
            Assert.Equal(execution.Id, request.SprintExecutionId);
            Assert.Equal(board, request.BoardId);
            Assert.Contains("workspace prerequisite", request.Reason);
            if (scenario is "valid" or "ancestor")
            {
                await agent.HandleEventAsync(envelope, context, default);
                Assert.Equal(request.IdempotencyKey, requests[1].IdempotencyKey);
                Assert.Contains(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("platform reports Pending"));
            }
            else Assert.DoesNotContain(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("Retry requested"));
        }
        else Assert.Empty(requests);
        if (scenario == "replan")
        {
            await agent.HandleEventAsync(envelope, context, default);
            var todo = Assert.Single(todos);
            var saved = await SpecialistAgent.ReadRoleRepairRequestAsync(todo.CorrelationId!, context, default);
            Assert.True(saved!.InfrastructureRecovery);
            Assert.Equal(item.WorkItemId, saved.WorkItemId);
            Assert.Equal(JsonSerializer.Serialize(sourceItem), JsonSerializer.Serialize(Assert.Single(saved.OriginalItems)));
            Assert.Equal(2, states.Count); // Immutable scope page and header, neither rewritten on replay.
            Assert.Contains(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("Queued durable sprint recovery"));
        }
        else if (scenario == "ancestor-amend")
        {
            await agent.HandleEventAsync(envelope, context, default);
            var todo = Assert.Single(todos);
            var saved = await SpecialistAgent.ReadRoleRepairRequestAsync(todo.CorrelationId!, context, default);
            Assert.Equal(turn, saved!.ScopeAuthorizingTurnId);
            Assert.Single(saved.ScopeReplacements);
            Assert.Equal("Independent QA", saved.OriginalItems[0].Planning!.AcceptanceCriteria[0]);
            Assert.Equal(2, states.Count);
            Assert.Contains(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("Queued the authorized scope amendment"));
        }
        else { Assert.Empty(states); Assert.Empty(todos); }
    }

    [Theory]
    [InlineData("Please consider this quote: Retry ticket VGDEMO-22: fixed")]
    [InlineData("Retry every blocked ticket")]
    [InlineData("Retry ticket VGDEMO-22:")]
    [InlineData("Retry ticket VGDEMO-22:   ")]
    public void General_or_quoted_direction_does_not_authorize_retry(string message) =>
        Assert.False(SpecialistAgent.TryReadRetryDirection(message, out _, out _));
}
