using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using TicketConversations;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DiscussionRecoveryTests
{
    [Theory]
    [InlineData("answered", true)]
    [InlineData("wrong-author", false)]
    [InlineData("stale-question", false)]
    [InlineData("unanswered", false)]
    [InlineData("exhausted", false)]
    [InlineData("running", false)]
    [InlineData("unrelated-blocker", false)]
    [InlineData("revision-continue", true)]
    [InlineData("revision-replace", false)]
    [InlineData("revision-clarify", false)]
    [InlineData("revision-unstructured", false)]
    [InlineData("revision-conflicting", false)]
    public void OnlyAnExactAnsweredWaitAllowsBoundedResume(string scenario, bool expected)
    {
        var board = Guid.NewGuid(); var item = Guid.NewGuid(); var execution = Guid.NewGuid(); var author = Guid.NewGuid();
        var wait = new Discussion.ResponseWait(board, item, Guid.NewGuid(), 1, Guid.NewGuid());
        var stage = new WorkStageExecutionResponse(Guid.NewGuid(), "development", "AgentExecution", 1,
            scenario == "running" ? "Running" : "Blocked", "AgentInstallation", Guid.NewGuid(), author, null,
            scenario == "exhausted" ? 3 : 1, "blocked", "Waiting", null, null, DateTimeOffset.UtcNow)
        {
            AssignmentRevision = 7, MaximumAttempts = 3,
            LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Blocked, "blocked", "Waiting",
                JsonSerializer.SerializeToElement(wait), [], scenario == "unrelated-blocker" ? [] : [Discussion.Waiting])
        };
        var question = new WorkItemComment(wait.CommentId, item, "AgentInstallation", author, "Daniel", "@Victor: clarify?",
            scenario == "stale-question" ? 2 : 1, DateTimeOffset.UtcNow, null) { Kind = "discussion.request" };
        if (scenario.StartsWith("revision-")) question = question with { Body = "@Victor: " + TaskRevisionComments.RequestMarker + " The input API changed." };
        var reply = new WorkItemComment(Guid.NewGuid(), item, "AgentInstallation",
            scenario == "wrong-author" ? Guid.NewGuid() : wait.RespondingInstallationId, "Victor", "Explanation", 1, DateTimeOffset.UtcNow, null)
            { Kind = "discussion.reply", CausationId = Discussion.Correlation(wait.CommentId, wait.CommentRevision) };
        if (scenario.StartsWith("revision-")) reply = reply with { Body = scenario switch {
            "revision-unstructured" => "Please continue; we can sort out the details later.",
            "revision-conflicting" => TaskRevisionComments.ContinueMarker + "\n" + TaskRevisionComments.ReplaceMarker,
            _ => $"[task-revision:v1:{scenario[9..]}]\nConcrete correction"
        } };
        var comments = scenario == "unanswered" ? new[] { question } : new[] { question, reply };
        var request = SpecialistAgent.AnsweredDiscussionRetry(board, execution, item, stage, comments);
        Assert.Equal(expected, request is not null);
        var executionItem = new WorkItemExecutionResponse(Guid.NewGuid(), item, "GAME-12", stage.StageKey,
            stage.Traversal, "Blocked", "Waiting", [stage], DateTimeOffset.UtcNow);
        var escalation = SpecialistAgent.ReplacementDiscussionEscalation(board, executionItem, stage, comments);
        Assert.Equal(scenario == "revision-replace", escalation is not null);
        if (escalation is { } replacement)
        {
            Assert.Contains("superseded/cancelled, never completed", replacement.Content);
            Assert.Contains("redirect dependencies", replacement.Content);
            Assert.Equal(replacement.Key, SpecialistAgent.ReplacementDiscussionEscalation(board, executionItem, stage, comments)!.Value.Key);
        }
        if (request is not null)
        {
            Assert.Equal(7, request.ExpectedAssignmentRevision);
            Assert.True(request.IdempotencyKey.Length <= 128);
            Assert.Equal(request.IdempotencyKey, SpecialistAgent.AnsweredDiscussionRetry(board, execution, item, stage with { AttemptCount = 2 }, comments)!.IdempotencyKey);
        }
    }
}
