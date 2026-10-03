using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// A blocker only the owner can clear must reach the owner. Reproduces VG4CC32F61E0-22, where a Blocked
/// development stage sat idle because nobody above the developer was asked to decide.
/// </summary>
public sealed class DecisionEscalationTests
{
    private const string Summary = "Decision needed on VG4CC32F61E0-22: no code change can address AC-2 desktop fps. Options: defer it or provide a browser.";

    private static WorkStageExecutionResponse Stage(IReadOnlyList<string> diagnostics, string summary = Summary) =>
        new(Guid.NewGuid(), "specialist-execution", "AgentExecution", 2, "Blocked", "AgentInstallation", Guid.NewGuid(),
            Guid.NewGuid(), null, 1, "blocked", summary, summary, null, DateTimeOffset.UtcNow)
        {
            AssignmentRevision = 1, MaximumAttempts = 3,
            LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Blocked, "blocked", summary,
                JsonSerializer.SerializeToElement(new { }), [], diagnostics)
        };

    private static WorkItemExecutionResponse Item(WorkStageExecutionResponse stage, string status = "Blocked") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "VG4CC32F61E0-22", stage.StageKey, stage.Traversal, status, stage.LastSummary,
            [stage], DateTimeOffset.UtcNow);

    [Fact]
    public void Decision_blockers_reach_the_owner_with_actionable_commands()
    {
        var stage = Stage([SpecialistAgent.DecisionRequiredDiagnostic, "detail"]);
        var escalation = SpecialistAgent.DecisionEscalation(Item(stage), stage)!.Value;
        Assert.StartsWith("VG4CC32F61E0-22 is blocked and needs your decision", escalation.Content);
        Assert.Contains(Summary, escalation.Content);
        Assert.Contains("Amend ticket VG4CC32F61E0-22:", escalation.Content);
        Assert.Contains("Retry ticket VG4CC32F61E0-22:", escalation.Content);
        Assert.Equal($"producer-decision:{stage.Id:N}:1", escalation.Key);
        Assert.True(escalation.Key.Length <= 128);

        // The same blocked attempt escalates once; a later attempt that blocks again is a new request.
        Assert.Equal(escalation.Key, SpecialistAgent.DecisionEscalation(Item(stage), stage)!.Value.Key);
        Assert.NotEqual(escalation.Key, SpecialistAgent.DecisionEscalation(Item(stage), stage with { AttemptCount = 2 })!.Value.Key);
    }

    [Fact]
    public void Ordinary_blockers_and_non_blocked_work_are_not_raised_as_decisions()
    {
        var plain = Stage(["The deliverable is missing required section 'Risks'."]);
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(plain), plain));
        var decision = Stage([SpecialistAgent.DecisionRequiredDiagnostic]);
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(decision, "Running"), decision));
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(decision), decision with { Status = "Running" }));
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(decision), null));
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(decision), decision with { LatestOutcome = null }));
    }

    [Fact]
    public void Exhausted_stages_offer_replanning_instead_of_an_impossible_retry()
    {
        var stage = Stage([SpecialistAgent.DecisionRequiredDiagnostic]) with { AttemptCount = 3 };
        var content = SpecialistAgent.DecisionEscalation(Item(stage), stage)!.Value.Content;
        Assert.DoesNotContain("Retry ticket", content);
        Assert.Contains("Replan ticket VG4CC32F61E0-22:", content);
        Assert.Contains("Amend ticket VG4CC32F61E0-22:", content);
    }

    [Fact]
    public void Long_decision_requests_are_bounded()
    {
        var stage = Stage([SpecialistAgent.DecisionRequiredDiagnostic], new string('x', 6000));
        Assert.True(SpecialistAgent.DecisionEscalation(Item(stage), stage)!.Value.Content.Length < 3200);
    }

    private const string DispatchReason = "Daniel Kim can't start VGF943299B17-6: project.assignment_required: The project must be active and the developer must be an explicit participant on its team. Ask the project manager to update membership.";

    private static WorkStageExecutionResponse NeverStarted(string? error = DispatchReason) =>
        new(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0, "Blocked", "AgentInstallation", Guid.NewGuid(),
            Guid.NewGuid(), null, 0, null, null, error, null, DateTimeOffset.UtcNow) { AssignmentRevision = 1, MaximumAttempts = 3 };

    [Fact]
    public void A_ticket_the_platform_could_not_dispatch_reaches_the_owner_once_with_the_reason()
    {
        var stage = NeverStarted();
        var escalation = SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage)!.Value;
        Assert.StartsWith("VG4CC32F61E0-22 can't start", escalation.Content);
        Assert.Contains(DispatchReason, escalation.Content);
        Assert.Contains("Manage members", escalation.Content);
        Assert.Contains("Retry ticket VG4CC32F61E0-22:", escalation.Content);
        Assert.StartsWith($"producer-decision:{stage.Id:N}:dispatch:", escalation.Key);
        Assert.True(escalation.Key.Length <= 128);
        Assert.Equal(escalation.Key, SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage)!.Value.Key);
        Assert.NotEqual(escalation.Key, SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage with { LastError = "Something else." })!.Value.Key);
    }

    [Fact]
    public void Worker_blockers_and_unblocked_work_are_not_dispatch_blockers()
    {
        var worked = Stage(["detail"]);
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(worked), worked));
        var stage = NeverStarted();
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(stage, "Pending"), stage));
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage with { Status = "Pending" }));
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage with { AttemptCount = 1 }));
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(stage), null));
        var silent = NeverStarted(null);
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(silent) with { BlockedReason = null }, silent));
    }

    private const string NodeMissing = "Implementation remains In Progress because validation failed: - `node -v` exited 127: node: command not found";

    private static WorkStageExecutionResponse Stopped(string status = "Blocked", int attempts = 1, int maximum = 0, double minutesAgo = 60) =>
        new(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0, status, "AgentInstallation", Guid.NewGuid(),
            Guid.NewGuid(), null, attempts, "blocked", NodeMissing, NodeMissing, null, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo))
        { AssignmentRevision = 1, MaximumAttempts = maximum };

    [Fact]
    public void Any_ticket_left_blocked_with_no_recovery_step_reaches_the_owner_once()
    {
        // VGF943299B17-6 sat Blocked for hours: no decision tag, not a format failure, and it had been attempted.
        var stage = Stopped();
        Assert.Null(SpecialistAgent.DecisionEscalation(Item(stage), stage));
        Assert.Null(SpecialistAgent.DispatchBlockerEscalation(Item(stage), stage));
        var escalation = SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(stage), stage, DateTimeOffset.UtcNow)!.Value;
        Assert.StartsWith("VG4CC32F61E0-22 has been blocked since", escalation.Content);
        Assert.Contains(NodeMissing, escalation.Content);
        Assert.Contains("Retry ticket VG4CC32F61E0-22:", escalation.Content);
        Assert.Contains("Amend ticket VG4CC32F61E0-22:", escalation.Content);
        Assert.StartsWith($"producer-decision:{stage.Id:N}:stalled:1:", escalation.Key);
        Assert.True(escalation.Key.Length <= 128);
        Assert.Equal(escalation.Key, SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(stage), stage, DateTimeOffset.UtcNow)!.Value.Key);
        Assert.NotEqual(escalation.Key, SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(stage), stage with { AttemptCount = 2 }, DateTimeOffset.UtcNow)!.Value.Key);
    }

    [Fact]
    public void Failed_and_exhausted_tickets_are_offered_replanning()
    {
        var stage = Stopped("Failed", attempts: 3, maximum: 3);
        var content = SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(stage, "Failed"), stage, DateTimeOffset.UtcNow)!.Value.Content;
        Assert.StartsWith("VG4CC32F61E0-22 has been marked failed since", content);
        Assert.Contains("Replan ticket VG4CC32F61E0-22:", content);
        Assert.DoesNotContain("Retry ticket", content);
    }

    [Fact]
    public void Fresh_or_self_recoverable_stops_get_their_specific_recovery_first()
    {
        var fresh = Stopped(minutesAgo: 5);
        Assert.Null(SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(fresh), fresh, DateTimeOffset.UtcNow));
        var running = Stopped();
        Assert.Null(SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(running, "Running"), running, DateTimeOffset.UtcNow));
        Assert.Null(SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(running), running with { Status = "Running" }, DateTimeOffset.UtcNow));
        Assert.Null(SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(running), null, DateTimeOffset.UtcNow));
        const string format = "The deliverable contains unresolved placeholder text: todo.";
        var correctable = Stopped(maximum: 3) with { LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Blocked, "blocked", format,
            JsonSerializer.SerializeToElement(new { }), [], [format]) };
        Assert.Null(SpecialistAgent.UnhandledBlockerEscalation(Guid.NewGuid(), Guid.NewGuid(), Item(correctable), correctable, DateTimeOffset.UtcNow));
    }
}
