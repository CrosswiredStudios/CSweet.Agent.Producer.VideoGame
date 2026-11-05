using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DeliveryRecoveryTests
{
    [Fact]
    public void Escalation_carries_the_canonical_project_and_ticket_instead_of_an_unscoped_direct_message()
    {
        var org = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid();
        var board = new WorkBoardSummary(Guid.NewGuid(), "Sprint", "", false, false, 1, []) {
            WorkstreamId = project, TeamId = team };
        var ticket = Guid.NewGuid(); var execution = Guid.NewGuid();
        var scope = SpecialistAgent.DeliveryEscalationContext(board, ticket, execution, org.ToString())!;
        Assert.Equal(org, scope.OrganizationId); Assert.Equal(project, scope.WorkstreamId);
        Assert.Equal(team, scope.TeamId); Assert.Equal(board.Id, scope.BoardId);
        Assert.Equal(ticket, scope.WorkItemId); Assert.Equal(execution, scope.CorrelationId);
        Assert.Null(SpecialistAgent.DeliveryEscalationContext(board with { WorkstreamId = null }, ticket, execution, org.ToString()));
    }
    [Fact]
    public void Legacy_mcp_500_waits_for_platform_verified_executor_update_instead_of_spending_a_retry_on_old_code()
    {
        var stage=Blocked(); stage=stage with {LatestOutcome=stage.LatestOutcome! with {Summary="The MCP service rejected the request: Internal Server Error"}};
        var board=Guid.NewGuid();var execution=Guid.NewGuid();
        Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(board,execution,stage));
        Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(board,execution,stage with {AttemptCount=2}));
        Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(board,execution,stage with {StageKey="quality"}));
        Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(board,execution,stage with {LatestOutcome=stage.LatestOutcome with {Summary="The MCP service rejected the request: Forbidden"}}));
    }
    private static WorkStageExecutionResponse Blocked() => new(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0,
        "Blocked", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid(), null, 1, "blocked", "Current blocker", "Stale infrastructure error", null, DateTimeOffset.UtcNow)
    {
        AssignmentRevision = 7, MaximumAttempts = 3,
        LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Blocked, "blocked",
            "The deliverable is missing required section 'Toolchain Feasibility'.", JsonSerializer.SerializeToElement(new {}), [], [])
    };

    [Fact]
    public void RetryUsesAuthoritativeOutcomeAndAssignmentAndStableKeyAcrossAttempts()
    {
        var stage = Blocked(); var board = Guid.NewGuid(); var execution = Guid.NewGuid();
        var first = SpecialistAgent.CorrectableDeliveryRetry(board, execution, stage)!;
        var second = SpecialistAgent.CorrectableDeliveryRetry(board, execution, stage with { AttemptCount = 2 })!;
        Assert.Equal(stage.AssignmentRevision, first.ExpectedAssignmentRevision);
        Assert.Equal(stage.Id, first.StageExecutionId);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.True(first.IdempotencyKey.Length <= 128);
    }

    [Theory]
    [InlineData("The deliverable contains unresolved placeholder text: todo.")]
    [InlineData("The durable deliverable is too short to be substantive.")]
    public void SelfValidationFailuresAreRetried(string reason)
    {
        var stage = Blocked();
        Assert.NotNull(SpecialistAgent.CorrectableDeliveryRetry(Guid.NewGuid(), Guid.NewGuid(), stage with
            { LatestOutcome = stage.LatestOutcome! with { Summary = reason } }));
    }

    [Theory]
    [InlineData("No measured performance evidence is available.")]
    [InlineData("Access denied.")]
    [InlineData("Independent QA failed.")]
    public void RealDeliveryBlockersAreNotBlindlyRetried(string reason)
    {
        var stage = Blocked();
        Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(Guid.NewGuid(), Guid.NewGuid(), stage with
            { LatestOutcome = stage.LatestOutcome! with { Summary = reason } }));
    }

    [Fact]
    public void RetryRequiresBlockedAssignedStageAndRemainingBudget()
    {
        var stage = Blocked();
        foreach (var ineligible in new[] { stage with { Status = "Running" }, stage with { AgentInstallationId = null },
            stage with { AttemptCount = 3 }, stage with { AssignmentRevision = 0 }, stage with { StageKey = "quality" } })
            Assert.Null(SpecialistAgent.CorrectableDeliveryRetry(Guid.NewGuid(), Guid.NewGuid(), ineligible));
    }
}
