using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningDirectionRecoveryTests
{
    [Fact]
    public async Task RetainedExactCompletedProposalIsReusedWithoutStartingAnotherSession()
    {
        var target = Guid.NewGuid(); var board = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, board, "profile", Guid.NewGuid(),
            1, "package", "concept", "vision", new string('a', 64));
        var member = new AgentTeammate(target.ToString(), "Victor", "Agent", null, null, "Teammate", "Online") { AgentInstallationId = Guid.NewGuid() };
        var person = new AgentCoordinationParticipant(target, member.AgentInstallationId.Value, "Victor", "Technical Director");
        var type = "video-game.production.technical-delivery-proposal.v1";
        var artifact = new AgentCoordinationArtifact(type, "1.0", "plan", 1, true,
            JsonSerializer.SerializeToElement(new GameTechnicalDeliveryProposalV1(cycle, [], [], [], [], "digest")), "digest");
        var retained = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            person, person, "Planning", "Plan", [], "Completed", 2, 2, null, true, "Complete", now, now,
            [new(Guid.NewGuid(), 1, target, "complete", "Accepted proposal", now, artifact)]) { BoardSource = new(board) };
        var runtime = new AgentTestRuntime().RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(
            CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(retained));
        var result = await SpecialistAgent.EnsurePlanningSessionAsync(member, board, cycle, "Planning", "Plan", type,
            [], [], runtime.CreateContext(), default, existingSessionId: retained.Id);
        Assert.Equal(retained.Id, result.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    public async Task Direction_recovery_is_distinct_from_legacy_but_stable_across_retries(int contextLength)
    {
        var additionalContext = contextLength == 0 ? null : new string('x', contextLength);
        var target = Guid.NewGuid();
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            "profile", Guid.NewGuid(), 1, "package", "concept", "vision", new string('a', 64));
        var member = new AgentTeammate(target.ToString(), "Victor", "Agent", null, null, "Teammate", "Online")
            { AgentInstallationId = Guid.NewGuid() };
        var requests = new List<StartBoardCoordinationRequest>();
        var participant = new AgentCoordinationParticipant(target, member.AgentInstallationId.Value, "Victor", "Technical Director");
        var runtime = new AgentTestRuntime().RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(
            CommunicationCapabilities.CoordinationStartBoard, (request, _) => {
                requests.Add(request);
                return Task.FromResult(new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
                    participant, participant, "Planning", "Plan", [], "Active", 1, 1, target, false, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
            });
        string[] directions = ["Proceed with delegated engine research. Do not reopen the resolved version question."];
        for (var i = 0; i < 2; i++)
            await SpecialistAgent.EnsurePlanningSessionAsync(member, cycle.BoardId, cycle, "Planning", "Plan delivery",
                "video-game.production.technical-delivery-proposal.v1", [], directions, runtime.CreateContext(), default, additionalContext);
        var legacy = "producer-planning-session:direction:" + ProducerPolicyFingerprint.Digest(
            JsonSerializer.Serialize(new { cycle.PlanningFingerprint, targetUserId = target, managerDirections = directions }));
        Assert.Equal(legacy + ":directions-v2", requests[0].IdempotencyKey);
        Assert.Equal(requests[0].IdempotencyKey, requests[1].IdempotencyKey);
        Assert.Contains(directions[0], requests[0].InitialMessage);
        Assert.Equal("Plan delivery", requests[0].Objective);
        if (additionalContext is not null) Assert.Contains(additionalContext, requests[0].InitialMessage);
        Assert.InRange(requests[0].IdempotencyKey.Length, 1, 160);
        await SpecialistAgent.EnsurePlanningSessionAsync(member, cycle.BoardId, cycle, "Planning", "Plan delivery",
            "video-game.production.technical-delivery-proposal.v1", [], [], runtime.CreateContext(), default);
        Assert.Equal($"producer-planning-session:{cycle.PlanningFingerprint}:{target:N}", requests[2].IdempotencyKey);
    }
}
