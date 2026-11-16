using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StoppedWorkTests
{
    [Fact]
    public void CurrentCompletedReviewSurvivesAStagnationStopAndHasOneDependencyAcrossRecoveryPaths()
    {
        var stage = JsonSerializer.Deserialize<WorkStageExecutionResponse>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "shardfall-stalled-review.json")), JsonSerializerOptions.Web)!;
        var board = Guid.NewGuid(); var item = Guid.NewGuid();
        var recovery = SpecialistAgent.StoppedWorkDependency(board, item, stage);
        var planning = SpecialistAgent.StoppedWorkDependency(board, item, stage);
        Assert.Equal(recovery.Root, planning.Root);
        Assert.Equal(recovery.Facts.GetRawText(), planning.Facts.GetRawText());
        Assert.Equal("changes_requested", recovery.Facts.GetProperty("lastOutcomeCode").GetString());
        Assert.Contains("criteria", recovery.Facts.GetProperty("latestOutcome").GetProperty("output").GetRawText());
        Assert.DoesNotContain("comment conflict", recovery.Facts.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
