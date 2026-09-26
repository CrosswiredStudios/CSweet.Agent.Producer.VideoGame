using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningDirectionRecoveryTests
{
    [Fact]
    public async Task Direction_recovery_is_distinct_from_legacy_but_stable_across_retries()
    {
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
                "video-game.production.technical-delivery-proposal.v1", [], directions, runtime.CreateContext(), default);
        var legacy = "producer-planning-session:direction:" + ProducerPolicyFingerprint.Digest(
            JsonSerializer.Serialize(new { cycle.PlanningFingerprint, targetUserId = target, managerDirections = directions }));
        Assert.Equal(legacy + ":directions-v2", requests[0].IdempotencyKey);
        Assert.Equal(requests[0].IdempotencyKey, requests[1].IdempotencyKey);
        Assert.Contains(directions[0], requests[0].InitialMessage);
        Assert.InRange(requests[0].IdempotencyKey.Length, 1, 160);
        await SpecialistAgent.EnsurePlanningSessionAsync(member, cycle.BoardId, cycle, "Planning", "Plan delivery",
            "video-game.production.technical-delivery-proposal.v1", [], [], runtime.CreateContext(), default);
        Assert.Equal($"producer-planning-session:{cycle.PlanningFingerprint}:{target:N}", requests[2].IdempotencyKey);
    }
}
