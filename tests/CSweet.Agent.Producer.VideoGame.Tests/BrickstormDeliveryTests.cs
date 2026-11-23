using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class BrickstormDeliveryTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("Cancelled")]
    [InlineData("Blocked")]
    public async Task One_stopped_planning_session_carries_its_reason_into_Gabriels_ticket(string status)
    {
        const string reason = "A target-device measurement is missing.";
        var fixture = new BrickstormFixture(failure: reason, stoppedStatus: status);
        var queue = new PlanningCard(fixture.Card, fixture.Runtime);
        var wake = new PersonalTodoAvailableEvent(fixture.Card.OwnerOrganizationUserId, fixture.Card.BoardId, fixture.Card.Id);
        var eventId = Guid.NewGuid();
        for (var i = 0; i < 2; ++i)
        {
            var host = new AgentTestHost(new SpecialistAgent(), fixture.Runtime, fixture.Identity, fixture.BusinessId);
            var result = await host.DeliverEventAsync(PersonalTodoEvents.Available, wake, eventId);
            Assert.True(result.Succeeded, result.Error);
        }
        var stopped = Assert.Single(fixture.AcceptedSessions).Value;
        Assert.Equal(PersonalTodoStatuses.Blocked, queue.Card.Status);
        Assert.Equal(2, queue.Card.BlockReason!.Split(stopped.Id.ToString("D")).Length);
        Assert.Contains(reason, queue.Card.BlockReason);
        Assert.DoesNotContain("conflict", queue.Card.BlockReason);
    }
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Roadmap_failure_is_deferred_and_one_fresh_session_survives_restart_and_redelivery(bool loseFirstRetryResponse)
    {
        var fixture = new BrickstormFixture(loseFirstRetryResponse);
        var queue = new PlanningCard(fixture.Card, fixture.Runtime);
        var wake = new PersonalTodoAvailableEvent(fixture.Card.OwnerOrganizationUserId, fixture.Card.BoardId, fixture.Card.Id);
        AgentTestHost StartProducer() => new(new SpecialistAgent(), fixture.Runtime, fixture.Identity, fixture.BusinessId);

        var firstEvent = Guid.NewGuid();
        var first = await StartProducer().DeliverEventAsync(PersonalTodoEvents.Available, wake, firstEvent);
        Assert.True(first.Succeeded);
        Assert.True(queue.Card.Wait is not null, $"Expected a scheduled retry. Actual card: {queue.Card.Status} {queue.Card.BlockReason}");
        Assert.Contains(BrickstormFixture.SpecialistFailure, queue.Card.Wait.Reason);
        var due = queue.Card.Wait.NextReviewAt;
        var original = Assert.Single(fixture.AcceptedSessions);
        Assert.Equal("Blocked", original.Value.Status);

        var restarted = StartProducer();
        Assert.False(queue.MakeReadyIfDue(due.AddTicks(-1)));
        await restarted.DeliverEventAsync(PersonalTodoEvents.Available, wake, firstEvent);
        Assert.Single(fixture.Requests);
        Assert.True(queue.MakeReadyIfDue(due));

        var dueEvent = Guid.NewGuid();
        await restarted.DeliverEventAsync(PersonalTodoEvents.Available, wake, dueEvent);
        var retry = Assert.Single(fixture.AcceptedSessions, pair => pair.Key != original.Key);
        Assert.Equal("Active", retry.Value.Status);
        Assert.NotEqual(original.Value.Id, retry.Value.Id);
        var retryRequest = fixture.Requests[1];
        Assert.Equal(retry.Key, retryRequest.IdempotencyKey);
        Assert.Equal(original.Value.Target.OrganizationUserId, retryRequest.TargetOrganizationUserId);
        Assert.Equal(fixture.Card.WorkContext!.BoardId, retryRequest.BoardId);
        if (loseFirstRetryResponse)
        {
            Assert.Contains("Coordination response lost", queue.Card.Wait?.Reason);
            Assert.True(queue.MakeReadyIfDue(queue.Card.Wait!.NextReviewAt));
            restarted = StartProducer();
            dueEvent = Guid.NewGuid();
            await restarted.DeliverEventAsync(PersonalTodoEvents.Available, wake, dueEvent);
            Assert.Equal(retryRequest.IdempotencyKey, fixture.Requests[2].IdempotencyKey);
        }
        Assert.Contains("Waiting for both role authorities", queue.Card.Wait?.Reason);

        var revisionAfterRetry = queue.Card.Revision;
        await restarted.DeliverEventAsync(PersonalTodoEvents.Available, wake, dueEvent);
        Assert.Equal(revisionAfterRetry, queue.Card.Revision);
        Assert.Equal(loseFirstRetryResponse ? 3 : 2, fixture.Requests.Count);
        Assert.Equal(2, fixture.AcceptedSessions.Count);
        Assert.Single(fixture.Sprints);
    }

    // Script just the platform transitions used here: a deferred card becomes eligible at its due time.
    // Claim ownership and expected revisions fence every mutation, including duplicate event deliveries.
    private sealed class PlanningCard
    {
        public PersonalTodoItem Card { get; private set; }
        private Guid? _claim;

        public PlanningCard(PersonalTodoItem seed, AgentTestRuntime runtime)
        {
            Card = seed;
            runtime.RegisterCapability<BlockPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Block, (request, _) =>
            {
                CheckClaim(request.ItemId, request.EventId, request.ExpectedRevision);
                _claim = null;
                Card = Card with { Revision = Card.Revision + 1, Status = PersonalTodoStatuses.Blocked, BlockReason = request.Reason };
                return Task.FromResult(Card);
            });
            runtime.RegisterCapability<ClaimPersonalTodoItemRequest, PersonalTodoClaim>(
                PersonalTodoCapabilities.Claim, (request, _) => Task.FromResult(Claim(request)));
            runtime.RegisterCapability<DeferPersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Defer, (request, _) =>
                {
                    CheckClaim(request.ItemId, request.EventId, request.ExpectedRevision);
                    _claim = null;
                    Card = Card with { Revision = Card.Revision + 1, Wait = new(request.NextReviewAt, request.Reason, request.WaitingOnOrganizationUserId)
                        { Dependency = request.Dependency } };
                    return Task.FromResult(Card);
                });
            runtime.RegisterCapability<BlockPersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Block, (request, _) =>
                {
                    CheckClaim(request.ItemId, request.EventId, request.ExpectedRevision);
                    _claim = null;
                    Card = Card with { Revision = Card.Revision + 1, Status = PersonalTodoStatuses.Blocked, BlockReason = request.Reason };
                    return Task.FromResult(Card);
                });
        }

        private PersonalTodoClaim Claim(ClaimPersonalTodoItemRequest request)
        {
            if (request.ItemId.HasValue && request.ItemId != Card.Id) return new(null);
            if (_claim == request.EventId) return new(Card);
            if (Card.Status != PersonalTodoStatuses.Ready ||
                (request.ExpectedRevision.HasValue && request.ExpectedRevision != Card.Revision)) return new(null);
            _claim = request.EventId;
            Card = Card with { Revision = Card.Revision + 1, Status = PersonalTodoStatuses.Running };
            return new(Card);
        }

        private void CheckClaim(Guid itemId, Guid eventId, long expectedRevision)
        {
            Assert.Equal(Card.Id, itemId);
            Assert.Equal(_claim, eventId);
            Assert.Equal(Card.Revision, expectedRevision);
        }

        public bool MakeReadyIfDue(DateTimeOffset now)
        {
            if (Card.Wait is null || now < Card.Wait.NextReviewAt) return false;
            Card = Card with { Revision = Card.Revision + 1, Status = PersonalTodoStatuses.Ready, Wait = null };
            return true;
        }
    }
}
