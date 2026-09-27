using System.Text.Json;
using CSweet.WorkManagement.Contracts;
namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class SprintRecoveryTests
{
    [Theory]
    [InlineData("exhausted", true)]
    [InlineData("remaining", false)]
    [InlineData("cancelled", false)]
    [InlineData("running", false)]
    [InlineData("approval", false)]
    [InlineData("foreign-stage", false)]
    [InlineData("stale-traversal", false)]
    [InlineData("unassigned", false)]
    public void Recovery_requires_exact_exhausted_assignment_and_no_active_work(string condition, bool expected)
    {
        var itemId = Guid.NewGuid(); var stageId = Guid.NewGuid();
        var stage = JsonSerializer.Deserialize<WorkStageExecutionResponse>("{}")! with { Id = stageId,
            StageKey = "specialist-execution", Status = "Blocked", PrincipalKind = "AgentInstallation",
            AgentInstallationId = Guid.NewGuid(), AssignmentRevision = 1, AttemptCount = 3, MaximumAttempts = 3 };
        stage = condition switch { "remaining" => stage with { AttemptCount = 2 }, "stale-traversal" => stage with { Traversal = 1 },
            "unassigned" => stage with { AgentInstallationId = null }, _ => stage };
        var item = JsonSerializer.Deserialize<WorkItemExecutionResponse>("{}")! with { WorkItemId = itemId,
            CurrentStageKey = stage.StageKey, Status = "Blocked", Stages = [stage] };
        var execution = JsonSerializer.Deserialize<WorkSprintExecutionResponse>("{}")! with { Status = "Active", Items = [item] };
        if (condition == "cancelled") execution = execution with { Status = "Cancelled" };
        if (condition is "running" or "approval") execution = execution with { Items = [item, item with { WorkItemId = Guid.NewGuid(),
            Status = condition == "running" ? "Running" : "WaitingForApproval", Stages = [] }] };
        Assert.Equal(expected, SpecialistAgent.CanRecoverExhaustedSprint(execution, itemId, condition == "foreign-stage" ? Guid.NewGuid() : stageId));
    }
}
