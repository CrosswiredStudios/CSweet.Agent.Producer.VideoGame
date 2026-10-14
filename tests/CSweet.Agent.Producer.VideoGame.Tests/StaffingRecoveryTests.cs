using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StaffingRecoveryTests
{
    [Theory]
    [InlineData("fresh", 2)]
    [InlineData("old", 1)]
    public async Task Stale_metrics_get_one_correction_with_approved_baseline_and_stable_replay(string revision, int expected)
    {
        var rejected = Guid.NewGuid();
        var proposal = JsonSerializer.Deserialize<ResourceChangeProposalRequest>("{}")! with
        {
            IdempotencyKey = "producer-coverage:original", TeamId = Guid.NewGuid(), WorkstreamId = Guid.NewGuid(),
            SupersedesRequestId = Guid.NewGuid(),
            Roles = [new("game-artist", "Game", "Artist", "Produce assets", 1, 3, "Next increment", ["work.execution.run"], false, null, null)],
            Evidence = [new("scope-capability-gap", "old", "Named scope"), new("accepted-input", "unchanged", "Accepted brief")]
        };
        var response = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with
        {
            Id = rejected, Status = "RevisionRequested",
            DecisionComment = "Refresh the proposal with the current trusted flow-metrics source revision."
        };
        var calls = new List<ResourceChangeProposalRequest>();
        Task<ResourceChangeRequestResponse> Submit(ResourceChangeProposalRequest request, CancellationToken token)
        { calls.Add(request); return Task.FromResult(response); }
        for (var replay = 0; replay < 2; replay++)
        {
            await SpecialistAgent.SubmitCoverageAsync(proposal, Guid.NewGuid(), Submit, default, _ => Task.FromResult(revision));
            Assert.Equal(expected * (replay + 1), calls.Count);
        }
        if (expected == 2)
        {
            Assert.Equal(JsonSerializer.Serialize(calls[1]), JsonSerializer.Serialize(calls[3]));
            Assert.Equal(proposal.SupersedesRequestId, calls[1].SupersedesRequestId);
            Assert.Equal(proposal.Roles, calls[1].Roles);
            Assert.Equal("fresh", calls[1].Evidence[0].SourceRevision);
            Assert.Equal(proposal.Evidence[1], calls[1].Evidence[1]);
            Assert.InRange(calls[1].IdempotencyKey.Length, 1, 160);
        }
    }

    [Theory]
    [InlineData("legacy", true, 2)]
    [InlineData("legacy", false, 1)]
    [InlineData("semantic", true, 1)]
    [InlineData("pending", true, 1)]
    [InlineData("approved", true, 1)]
    public async Task Only_legacy_board_rejection_gets_one_stable_resubmission(string scenario, bool hasBoard, int expected)
    {
        var proposal = JsonSerializer.Deserialize<ResourceChangeProposalRequest>("{}")! with
            { IdempotencyKey = "producer-coverage:" + new string('a', 64), TeamId = Guid.NewGuid(), WorkstreamId = Guid.NewGuid() };
        var calls = new List<ResourceChangeProposalRequest>();
        var response = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Status = scenario == "pending" ? "Pending" : scenario == "approved" ? "Approved" : "RevisionRequested",
            DecisionComment = scenario == "legacy" ? "Initial staffing requires the Producer's confident review and the Director's exact accepted production brief." : "Revise the requested headcount." };
        Task<ResourceChangeRequestResponse> Submit(ResourceChangeProposalRequest request, CancellationToken token)
        {
            calls.Add(request);
            return Task.FromResult(response);
        }
        await SpecialistAgent.SubmitCoverageAsync(proposal, hasBoard ? Guid.NewGuid() : null, Submit, default);
        Assert.Equal(expected, calls.Count);
        Assert.Equal(proposal, calls[0]);
        if (expected == 2)
        {
            Assert.Equal(proposal with { IdempotencyKey = proposal.IdempotencyKey + ":board-review-v1" }, calls[1]);
            Assert.InRange(calls[1].IdempotencyKey.Length, 1, 160);
            var retryKey = calls[1].IdempotencyKey;
            calls.Clear();
            await SpecialistAgent.SubmitCoverageAsync(proposal, Guid.NewGuid(), Submit, default);
            Assert.Equal(retryKey, calls[1].IdempotencyKey);
        }
    }
}
