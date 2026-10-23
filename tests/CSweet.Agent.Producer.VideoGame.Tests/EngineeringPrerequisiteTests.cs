using System.Text.Json;
using System.Text.Json.Nodes;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class EngineeringPrerequisiteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_director_declaration_and_legacy_pin_allow_only_the_bound_prerequisite(bool legacy)
    {
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "profile", Guid.NewGuid(), 1, "approved", "prototype", "playable", "cycle");
        var pin = new GameProposedWorkItemV1("dependency-pin-phaser", VideoGameWorkItemTypeKeys.Task, "Pin", "Commit and verify", ["Integrated and tested"],
            VideoGameRoleKeys.Engineer, [], [], ["work.execution.run"], []);
        var implementation = pin with { ProposalKey = "physics", DependencyProposalKeys = [pin.ProposalKey] };
        var proposal = new GameTechnicalDeliveryProposalV1(cycle, [pin, implementation], [], [], ["Specific Phaser version selection for particle performance"], "proposal");
        var payload = JsonSerializer.SerializeToNode(proposal)!.AsObject();
        if (!legacy) payload["engineeringPrerequisiteKeys"] = JsonSerializer.SerializeToNode(new[] { pin.ProposalKey });
        var artifact = new AgentCoordinationArtifact("video-game.production.technical-delivery-proposal.v1", "1.0", "cycle", 1, true, JsonSerializer.SerializeToElement(payload), "current-artifact");
        var keys = SpecialistAgent.ReadEngineeringPrerequisites(proposal, artifact);
        var state = new ProducerPlanningCycleState(cycle.WorkstreamId, cycle.BoardId, cycle.TeamId, "cycle", null, null, null, null, artifact.Digest, "reconciled", DateTimeOffset.UtcNow) {
            OutstandingAuthorityQuestions = proposal.OpenFeasibilityDecisions, EngineeringPrerequisiteKeys = keys
        };
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with {
            TypeKey = pin.WorkItemTypeKey,
            ProposalProvenance = new(Guid.NewGuid(), artifact.Digest, pin.ProposalKey),
            Planning = new(["Pin"], ["Integrated"], []) {
                DelegationRecommendations = [new("specialist-execution", VideoGameRoleKeys.Engineer, ["work.execution.run"], null, true, "Prerequisite")]
            }
        };
        Assert.True(SpecialistAgent.CanPrepareWithPlanningDecisions(item, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(item with { ProposalProvenance = item.ProposalProvenance with { ProposalItemKey = implementation.ProposalKey } }, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(item with { ProposalProvenance = item.ProposalProvenance with { ArtifactDigest = "stale" } }, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(item with { ProposalProvenance = null }, state));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(item with { Planning = item.Planning with { DelegationRecommendations = [] } }, state));
        Assert.Single(state.OutstandingAuthorityQuestions);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("dependent")]
    [InlineData("duplicate")]
    [InlineData("wrong-role")]
    public void Invalid_prerequisite_declarations_fail_closed(string scenario)
    {
        var task = new GameProposedWorkItemV1("pin", VideoGameWorkItemTypeKeys.Task, "Pin", "Commit", ["Tested"], VideoGameRoleKeys.Engineer, [], [], ["work.execution.run"], []);
        var dependent = task with { ProposalKey = "implementation", DependencyProposalKeys = [task.ProposalKey] };
        var proposal = new GameTechnicalDeliveryProposalV1(null!, [task, dependent], [], [], ["Scope approval"], "proposal");
        if (scenario == "wrong-role") proposal = proposal with { DeliveryItems = [task with { AccountableRoleKey = VideoGameRoleKeys.QualityAssurance }, dependent] };
        var payload = JsonSerializer.SerializeToNode(proposal)!.AsObject();
        payload["engineeringPrerequisiteKeys"] = JsonSerializer.SerializeToNode(scenario switch {
            "foreign" => new[] { "unrelated" }, "dependent" => new[] { "implementation" }, "duplicate" => new[] { "pin", "pin" }, _ => new[] { "pin" }
        });
        var artifact = new AgentCoordinationArtifact("technical", "1.0", "cycle", 1, true, JsonSerializer.SerializeToElement(payload), "digest");
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ReadEngineeringPrerequisites(proposal, artifact));
    }
}
