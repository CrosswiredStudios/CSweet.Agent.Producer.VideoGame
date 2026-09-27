using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ScopeAmendmentTests
{
    private static (RoleRepairRequest Request, GameProposedWorkItemV1[] Proposals) Fixture()
    {
        var (request, _) = RoleReplanningTests.Fixture();
        var source = request.OriginalItems[0] with { Description = "Plan and implement the foundation",
            Planning = request.OriginalItems[0].Planning! with { AcceptanceCriteria = ["Working prototype", "Measure on physical Android"] } };
        request = request with { OriginalItems = [source, request.OriginalItems[1]], ScopeDirection = "Defer physical-device measurement; retain available checks.",
            ScopeAuthorizingTurnId = Guid.NewGuid(), ScopeReplacements = [new(source.Id, "acceptanceCriteria", "Measure on physical Android", "Physical-device measurement deferred; report available checks and limitations")] };
        GameProposedWorkItemV1[] proposals = [
            new("plan", "task", "Foundation", "Plan and implement the foundation",
                ["Working prototype", "Physical-device measurement deferred; report available checks and limitations"], "game-technical-director", [], [], [], []),
            new("game", "task", "Game", "Complete game", ["Playable game"], "game-engineer", [], [], [], ["plan"])];
        return (request, proposals);
    }

    [Fact]
    public void ExactAmendmentPreservesOriginalSnapshotAndUnrelatedRequirements()
    {
        var (request, proposals) = Fixture();
        var revised = SpecialistAgent.ScopeExpectedItems(request);
        Assert.Equal("Measure on physical Android", request.OriginalItems[0].Planning!.AcceptanceCriteria[1]);
        Assert.Equal("Working prototype", revised[0].Planning!.AcceptanceCriteria[0]);
        Assert.Contains("deferred", revised[0].Planning!.AcceptanceCriteria[1]);
        SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, ["60 fps"]);
    }

    [Theory]
    [InlineData("unknown-item")]
    [InlineData("wrong-text")]
    [InlineData("empty-replacement")]
    [InlineData("wrong-field")]
    [InlineData("completed")]
    [InlineData("duplicate")]
    [InlineData("missing-authority")]
    public void InvalidReplacementCannotReachPlanning(string kind)
    {
        var (request, _) = Fixture();
        var edit = request.ScopeReplacements[0];
        request = kind switch
        {
            "unknown-item" => request with { ScopeReplacements = [edit with { ItemId = Guid.NewGuid() }] },
            "wrong-text" => request with { ScopeReplacements = [edit with { Original = "Invented criterion" }] },
            "empty-replacement" => request with { ScopeReplacements = [edit with { Replacement = " " }] },
            "wrong-field" => request with { ScopeReplacements = [edit with { Field = "capabilities" }] },
            "completed" => request with { OriginalItems = [request.OriginalItems[0] with { Status = "Completed" }, request.OriginalItems[1]] },
            "duplicate" => request with { ScopeReplacements = [edit, edit] },
            _ => request with { ScopeAuthorizingTurnId = Guid.Empty }
        };
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ScopeExpectedItems(request));
    }

    [Theory]
    [InlineData("drop-criterion")]
    [InlineData("wrong-owner")]
    [InlineData("drop-dependency")]
    [InlineData("add-ticket")]
    [InlineData("unrelated-change")]
    public void TechnicalProposalCannotExpandTheAuthorizedAmendment(string kind)
    {
        var (request, proposals) = Fixture();
        switch (kind)
        {
            case "drop-criterion": proposals[0] = proposals[0] with { AcceptanceCriteria = [proposals[0].AcceptanceCriteria[1]] }; break;
            case "wrong-owner": proposals[0] = proposals[0] with { AccountableRoleKey = "game-engineer" }; break;
            case "drop-dependency": proposals[1] = proposals[1] with { DependencyProposalKeys = [] }; break;
            case "add-ticket": proposals = [..proposals, proposals[0] with { ProposalKey = "extra" }]; break;
            case "unrelated-change": proposals[1] = proposals[1] with { AcceptanceCriteria = ["Less work"] }; break;
        }
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, ["60 fps"]));
    }

    [Fact]
    public void SupersededGlobalConstraintCannotReturnThroughPlanning()
    {
        var (request, proposals) = Fixture();
        request = request with { ScopeReplacements = [..request.ScopeReplacements,
            new(request.WorkItemId, "constraints", "60 fps", "Measure on available infrastructure; physical captures deferred")] };
        var retained = SpecialistAgent.RetainedRoleRepairConstraints(request);
        Assert.DoesNotContain("60 fps", retained);
        SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, retained);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, [..retained, "60 fps"]));
    }

    [Fact]
    public void SerializedAmendmentRetainsAuthorityAndExactReplacements()
    {
        var (request, _) = Fixture();
        var replay = JsonSerializer.Deserialize<RoleRepairRequest>(JsonSerializer.Serialize(request))!;
        Assert.Equal(request.ScopeAuthorizingTurnId, replay.ScopeAuthorizingTurnId);
        Assert.Equal(request.ScopeDirection, replay.ScopeDirection);
        Assert.Equal(request.ScopeReplacements[0], replay.ScopeReplacements[0]);
        Assert.Equal(SpecialistAgent.ScopeExpectedItems(request)[0].Planning!.AcceptanceCriteria,
            SpecialistAgent.ScopeExpectedItems(replay)[0].Planning!.AcceptanceCriteria);
    }
    [Fact]
    public void DeliveryRefreshCarriesNewCriteriaAndRetainsRepositoryAndDeliveryControls()
    {
        var (request, _) = Fixture();
        var original = request.OriginalItems[0];
        var repository = Guid.NewGuid();
        var qualityGate = Guid.NewGuid();
        original = original with { Delivery = new(repository, original.Planning!.Requirements, original.Planning.AcceptanceCriteria, original.Planning.Constraints)
            { BaseBranch = "main", QualityGateColumnId = qualityGate } };
        request = request with { OriginalItems = [original, request.OriginalItems[1]] };
        var updated = SpecialistAgent.AmendedDelivery(SpecialistAgent.ScopeExpectedItems(request)[0]);
        Assert.Contains("deferred", updated.AcceptanceCriteria[1]);
        Assert.Equal(repository, updated.RepositoryId);
        Assert.Equal("main", updated.BaseBranch);
        Assert.Equal(qualityGate, updated.QualityGateColumnId);
        Assert.Equal(original.Planning!.Requirements, updated.Requirements);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Dispatching")]
    [InlineData("WaitingForApproval")]
    public void AnotherActiveOperationPreventsScopeCancellation(string otherStatus)
    {
        var (request, _) = Fixture();
        var stage = JsonSerializer.Deserialize<WorkStageExecutionResponse>("{}")! with {
            Id = request.ReviewStageId, StageKey = "technical-review", Status = "Blocked", Traversal = 2 };
        var item = JsonSerializer.Deserialize<WorkItemExecutionResponse>("{}")! with {
            WorkItemId = request.WorkItemId, Status = "Blocked", CurrentStageKey = stage.StageKey, Traversal = 2, Stages = [stage] };
        var execution = JsonSerializer.Deserialize<WorkSprintExecutionResponse>("{}")! with { Status = "Active", Items = [item] };
        Assert.True(SpecialistAgent.CanAmendScope(execution, request.WorkItemId, stage.Id));
        execution = execution with { Items = [item, item with { WorkItemId = Guid.NewGuid(), Stages = [stage with { Id = Guid.NewGuid(), Status = otherStatus }] }] };
        Assert.False(SpecialistAgent.CanAmendScope(execution, request.WorkItemId, stage.Id));
    }    [Fact]
    public void StaleLegacyBriefCannotReintroduceOldCriteriaOnReplay()
    {
        var (request, _) = Fixture();
        var original = request.OriginalItems[0];
        var repository = Guid.NewGuid();
        original = original with { Delivery = new(repository, original.Planning!.Requirements, original.Planning.AcceptanceCriteria, original.Planning.Constraints),
            Development = new(repository, "profile", original.Planning.Requirements, original.Planning.AcceptanceCriteria, original.Planning.Constraints) };
        request = request with { OriginalItems = [original, request.OriginalItems[1]] };
        var item = SpecialistAgent.ScopeExpectedItems(request)[0];
        var updated = SpecialistAgent.AmendedDelivery(item);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateAmendedDevelopment(item with { Delivery = updated }, updated));
        SpecialistAgent.ValidateAmendedDevelopment(item with { Development = item.Development! with { AcceptanceCriteria = updated.AcceptanceCriteria } }, updated);
    }}