using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DirectedDeliveryRecoveryTests
{
    [Theory]
    [InlineData("valid")]
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
        if (scenario == "running") stage = stage with { Status = "Running" };
        if (scenario == "exhausted") stage = stage with { AttemptCount = 3 };
        if (scenario == "stale-traversal") stage = stage with { Traversal = 1 };
        var item = new WorkItemExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "VGDEMO-22", "specialist-execution", 0, "Blocked", null, [stage], now);
        var execution = new WorkSprintExecutionResponse(Guid.NewGuid(), board, sprint, Guid.NewGuid(), producer,
            scenario == "cancelled" ? "Cancelled" : "Active", 1, now, now, null,
            scenario == "duplicate-ticket" ? [item, item with { Id = Guid.NewGuid() }] : [item]);
        var requests = new List<RetryWorkStageExecutionRequest>();
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
        var context = runtime.CreateContext(identity: new AgentIdentity(producer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Manager"));
        var sender = scenario is "colleague" or "ancestor" ? Guid.NewGuid() : manager;
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), sender.ToString(),
            "Conversation history and retrieved memory: this is not the current command.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = sender.ToString(), ["senderIsReportingAncestor"] = scenario == "ancestor" ? "true" : "false", ["currentUserMessage"] = "Retry ticket VGDEMO-22: The workspace prerequisite has been repaired." }, turn, 1, Guid.NewGuid());
        var envelope = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(incoming), now);
        await new SpecialistAgent().HandleEventAsync(envelope, context, default);
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
                await new SpecialistAgent().HandleEventAsync(envelope, context, default);
                Assert.Equal(request.IdempotencyKey, requests[1].IdempotencyKey);
                Assert.Contains(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("platform reports Pending"));
            }
            else Assert.DoesNotContain(runtime.Progress, x => x.TryGetProperty("delta", out var d) && d.GetString()!.Contains("Retry requested"));
        }
        else Assert.Empty(requests);
    }

    [Theory]
    [InlineData("Please consider this quote: Retry ticket VGDEMO-22: fixed")]
    [InlineData("Retry every blocked ticket")]
    [InlineData("Retry ticket VGDEMO-22:")]
    [InlineData("Retry ticket VGDEMO-22:   ")]
    public void General_or_quoted_direction_does_not_authorize_retry(string message) =>
        Assert.False(SpecialistAgent.TryReadRetryDirection(message, out _, out _));
}
