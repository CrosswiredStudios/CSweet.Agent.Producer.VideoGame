using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class AdaptiveApprovalTests
{
    [Fact]
    public async Task Replayed_review_reuses_the_pending_hire_and_reads_its_authorized_result()
    {
        var project = Guid.NewGuid();
        var team = Guid.NewGuid();
        var producer = Guid.NewGuid();
        var manager = Guid.NewGuid();
        var cycle = new GameProductionPlanningCycleV1(project, team, 1, Guid.NewGuid(), "profile", Guid.NewGuid(), 1,
            "package", "production", "prototype", "scope");
        var handoff = new ProducerAcceptedHandoff(project, Guid.NewGuid(), Guid.NewGuid(), "brief", "handoff",
            Guid.NewGuid(), cycle.ApprovedPackageId, 1, DateTimeOffset.UtcNow);
        var roster = new AgentTeamContext(team.ToString(), "game", "Game", 1, producer.ToString(), "Producer", [], [], 0, false);
        var identity = new AgentIdentity(producer.ToString(), "Gabriel", null, null, null, [], null, manager.ToString(), "Marcus");
        var hire = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Id = Guid.NewGuid(), WorkstreamId = project, TeamId = team, RequesterOrganizationUserId = producer, Status = "Pending",
            Roles = [new ResourceChangeRole("game-designer", "studio", "Game Designer", "Tune interacting power-ups", 1, 1,
                "next sprint", ["work.execution.run"], false, producer, null)] };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var unscopedReads = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                    new Dictionary<string,string>(), [], "", [], Guid.NewGuid(), r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                (r, _) => { if (r.RequestId == null) unscopedReads++; else Assert.Equal(hire.Id, r.RequestId);
                    return Task.FromResult(new ResourceChangeReadResponse([hire])); });
        var decision = new SpecialistAgent.ReviewChoice("Repair collision", 0, true, true, "QA retest", "No new build", [
            new("resequence", "Address collision before scoring", null, [0]), new("hire", "Tune interacting power-ups", 0, [0])]);
        var receipt = new SpecialistAgent.ReviewReceipt("review", "repair", decision,
            [new("brief", handoff.ArtifactId, "brief", "approved rules")], ["game-designer"], DateTimeOffset.UtcNow, "evidence");
        await SpecialistAgent.ApplyReviewEffectsAsync(receipt, cycle, roster, handoff, runtime.CreateContext(identity: identity), default);
        var restarted = runtime.CreateContext(identity: identity);
        var pending = await SpecialistAgent.RefreshReviewProposalsAsync(project, [], restarted, default);
        Assert.Equal("Pending", Assert.Single(pending).Revision);
        await SpecialistAgent.ApplyReviewEffectsAsync(receipt, cycle, roster, handoff, restarted, default);
        hire = hire with { Status = "Approved", DecidedByOrganizationUserId = manager };
        var approved = Assert.Single(await SpecialistAgent.RefreshReviewProposalsAsync(project, [], restarted, default));
        Assert.Equal(hire.Id, approved.Id);
        Assert.Equal("Approved", approved.Revision);
        Assert.Equal(1, unscopedReads);
    }
}
