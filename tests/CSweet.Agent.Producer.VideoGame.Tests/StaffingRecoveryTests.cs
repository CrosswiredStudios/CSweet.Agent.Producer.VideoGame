using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StaffingRecoveryTests
{
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