using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ScopeHandoffTests
{
    private static RoleRepairRequest LargeAmendment()
    {
        var (request, _) = RoleReplanningTests.Fixture();
        var items = Enumerable.Range(0, 3).Select(i => request.OriginalItems[0] with
        {
            Id = Guid.NewGuid(), Description = $"{i}: " + new string('a', 3500) + " ≥ 60 fps",
            ProposalProvenance = request.OriginalItems[0].ProposalProvenance! with { ProposalItemKey = $"task-{i}" },
            Planning = new([$"{i}: " + new string('a', 3500) + " ≥ 60 fps"], ["Keep available validation"], [])
        }).ToArray();
        return request with
        {
            WorkItemId = items[0].Id, OriginalItems = items,
            ScopeDirection = "Defer physical device measurements, retain all available validation.",
            ScopeAuthorizingTurnId = Guid.NewGuid(),
            ScopeReplacements = items.Select(x => new ScopeTextReplacement(x.Id, "requirements",
                x.Planning!.Requirements[0], x.Planning.Requirements[0] + "; mobile measurement deferred, never passed.")).ToArray()
        };
    }

    [Fact]
    public async Task LargeAmendmentAndExistingDirectionsReachCoordinationWithoutTruncation()
    {
        var request = LargeAmendment();
        var target = Guid.NewGuid();
        var cycle = new GameProductionPlanningCycleV1(request.WorkstreamId, request.TeamId, 1, request.BoardId,
            "profile", Guid.NewGuid(), 1, "package", "production", "game", "amendment");
        var member = new AgentTeammate(target.ToString(), "Victor", "Agent", null, null, "Teammate", "Online")
            { AgentInstallationId = Guid.NewGuid() };
        var calls = new List<StartBoardCoordinationRequest>();
        var runtime = new AgentTestRuntime().RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(
            CommunicationCapabilities.CoordinationStartBoard, (start, _) => {
                calls.Add(start);
                return Task.FromResult(JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with { Status = "Active" });
            });
        string[] directions = [$"Recorded management decision: {new string('d', 17709)}"];
        Assert.True(JsonSerializer.Serialize(request.ScopeReplacements).Length + directions[0].Length > 32768);
        for (var i = 0; i < 2; i++)
            await SpecialistAgent.EnsurePlanningSessionAsync(member, cycle.BoardId, cycle, "Planning", new string('o', 1000),
                "video-game.production.technical-delivery-proposal.v1", [], directions, runtime.CreateContext(), default,
                SpecialistAgent.RoleRepairContext(request));
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey);
        Assert.All(calls, call => {
            Assert.InRange(call.InitialMessage.Length, 1, 32768);
            var complete = call.Artifact!.Payload.GetProperty("coordinationContext").GetString()!;
            Assert.InRange(call.Artifact.Payload.GetRawText().Length, 1, 65536);
            Assert.Contains(directions[0], complete);
            Assert.Contains(SpecialistAgent.RoleRepairContext(request), complete);
            Assert.Equal(cycle, call.Artifact.Payload.Deserialize<GameProductionPlanningCycleV1>());
        });
    }
}