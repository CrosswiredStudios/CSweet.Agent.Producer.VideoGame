using System.Reflection;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// Enters through the Producer's real planning commitment (HandlePersonalTodoAsync), with the platform scripted.
/// QFN Games Brickstorm, November 17: the Technical Director's roadmap generation failed, the Producer blocked
/// permanently, and asking again would have returned the same failed session.
/// </summary>
public sealed class PlanningCommitmentJourneyTests
{
    private static T Read<T>(PersonalTodoResult result, string property) =>
        (T)typeof(PersonalTodoResult).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;

    [Fact]
    public async Task A_failed_roadmap_generation_is_retried_later_with_a_fresh_request()
    {
        var fixture = new BrickstormFixture();
        var requests = fixture.Requests;
        var sprints = fixture.Sprints;
        var context = fixture.Runtime.CreateContext(fixture.BusinessId, identity: fixture.Identity);
        var commitment = fixture.Card with { Status = PersonalTodoStatuses.Running };
        var agent = new SpecialistAgent();

        var first = await agent.HandlePersonalTodoAsync(commitment, context, default);
        // A failed generation is a retry with the specialist's exact diagnostic, never a permanent block.
        Assert.Contains("2 commitment(s) do not cite", Read<string>(first, "RetryFailure"));
        Assert.Single(requests);

        var second = await agent.HandlePersonalTodoAsync(commitment, context, default);
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].IdempotencyKey, requests[1].IdempotencyKey); // A fresh generation, not the failed session again.
        Assert.NotNull(Read<DateTimeOffset?>(second, "NextReviewAt"));
        Assert.Contains("Waiting for both role authorities", Read<string>(second, "Content"));
        Assert.Single(sprints); // The provisional sprint was not duplicated by the retry.
    }
}
