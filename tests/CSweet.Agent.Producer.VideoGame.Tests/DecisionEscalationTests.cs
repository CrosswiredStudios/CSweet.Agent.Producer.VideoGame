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
}
