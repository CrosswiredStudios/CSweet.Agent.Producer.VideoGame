using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Fact]
    public void ConstraintsAndDecisionsOwnObligationsWithoutDuplicatingEpicCriteria()
    {
        var f = new Fixture(adaptive: true);
        var source = new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "digest", "Director", "Fixed camera; procedural assets.");
        var catalog = SpecialistAgent.OwnershipCatalog(f.Outline.DeliveryItems, ["Use a fixed camera."], ["Approved decision: use procedural assets."]);
        var constraint = catalog.Select((e,i) => (e,i)).Single(x => x.e.Kind == "Constraint");
        var decision = catalog.Select((e,i) => (e,i)).Single(x => x.e.Kind == "Decision");
        var review = SpecialistAgent.BindOwnership(new([
            new("Fixed camera", [0], [constraint.i]), new("Procedural assets", [0], [decision.i])], []), [source], f.Outline.DeliveryItems, suppliedCatalog: catalog);
        Assert.Empty(review.Gaps); Assert.All(review.Commitments, c => Assert.Equal(2, c.Owners.Single().ScopeKeys.Count));
        Assert.Equal("Constraint", review.Commitments[0].Owners.Single().Evidence!.Kind);
        Assert.DoesNotContain(f.Outline.DeliveryItems.SelectMany(e => e.AcceptanceCriteria), c => c == "Use a fixed camera.");
        Assert.Equal(constraint.e.Text, review.Commitments[0].Owners.Single().Criterion);
    }

    [Fact]
    public async Task OneMissingResponsibilityPublishesTheRoadmapAndRefinesIndependentScopeWithoutResolvingTheFinding()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Conditional FPS escalation owner", "Name the planning responsibility.", ["final"])]);
        var result = await f.Advance(null, ownership);
        Assert.Null(result.Blocker); Assert.False(result.Complete); Assert.Equal(2, f.Items.Count);
        Assert.All(f.Items, i => Assert.Equal("Epic", i.Kind));
        Assert.Equal("prototype.story-1", result.Progress.PendingStoryKey); Assert.Single(f.Starts);
        Assert.Single(ownership.Gaps); Assert.Empty(ownership.Coverage.Single().ProposalKeys);
        var replay = await f.Advance(result.Progress, ownership);
        Assert.Null(replay.Blocker); Assert.False(replay.Complete); Assert.Single(f.Starts);
    }

    [Fact]
    public async Task FindingHoldsConsumersAndCannotConcludeTheProjectEvenWhenNoEligibleScopeRemains()
    {
        var f = new Fixture(adaptive: true);
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Core behavior needs an owner", "Missing responsibility", ["prototype"])]);
        var result = await f.Advance(null, ownership);
        Assert.Null(result.Blocker); Assert.False(result.Complete); Assert.NotNull(result.Waiting);
        Assert.Empty(f.Starts); Assert.Equal(2, f.Items.Count);
        Assert.Equal(new[] { "final", "prototype" }, SpecialistAgent.OwnershipHolds(f.Outline.DeliveryItems, ownership).Order().ToArray());
    }

    [Fact]
    public async Task ANewFindingRetainsAffectedDraftAndAllowsAnIndependentIncrementToBeSelected()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var first = await f.Advance(null);
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Core review", "Unresolved", ["prototype"])]);
        var next = await f.Advance(first.Progress, ownership);
        Assert.Null(next.Blocker); Assert.Equal("final.story-1", next.Progress.PendingStoryKey);
        Assert.Equal(first.Progress.RefinementSessionId, next.Progress.DraftSessionId);
        Assert.Equal("prototype.story-1", next.Progress.DraftStoryKey);
        Assert.All(f.Items, i => Assert.Equal("Epic", i.Kind));
    }

    [Fact]
    public void HypotheticalObservationFailureIsReconciliationAndCannotSupplyConflictEvidence()
    {
        var f = new Fixture(adaptive: true);
        const string condition = "If lightweight FPS observation is impossible without violating the no-profiling directive, escalate the conflict.";
        var sources = new[] { new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "sha", "Director", condition) };
        var catalog = SpecialistAgent.OwnershipCatalog(f.Outline.DeliveryItems, [condition], []);
        var index = catalog.Select((e,i) => (e,i)).Single(x => x.e.Kind == "Constraint").i;
        var alreadyOwned = SpecialistAgent.BindOwnership(new([new(condition, [0], [index])], []), sources, f.Outline.DeliveryItems, suppliedCatalog: catalog);
        Assert.Empty(alreadyOwned.Gaps);
        // Minimized incident reply: a missing criterion was mistaken for an observed conflict.
        var incident = System.Text.Json.JsonSerializer.Deserialize<SpecialistAgent.OwnershipDraft>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "conditional-fps-ownership-gap.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var gap = Assert.Single(incident.Gaps);
        var missing = SpecialistAgent.BindOwnership(new([], [gap]), sources, f.Outline.DeliveryItems, suppliedCatalog: catalog);
        Assert.Empty(missing.Gaps[0].ConflictEvidence);
        var finding = SpecialistAgent.OwnershipFinding(f.Cycle, f.Evidence.OutlineDigest, missing.Gaps[0], f.Outline.DeliveryItems, "Victor Lin");
        Assert.False(finding.UserDecisionRequired); Assert.Contains("Planning reconciliation", finding.StatusSummary);
        Assert.DoesNotContain("measurement failed", finding.StatusSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.BindOwnership(new([], [gap with { ConflictEvidenceIndexes = [index] }]), sources, f.Outline.DeliveryItems, suppliedCatalog: catalog));
    }

    [Fact]
    public void AnActualRequirementConflictRetainsExactSupportingRequirementsForManagerJudgment()
    {
        var f = new Fixture(adaptive: true);
        var sources = new[] { new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "sha", "Director", "Use only provided audio; provided audio is forbidden.") };
        var catalog = SpecialistAgent.OwnershipCatalog(f.Outline.DeliveryItems, ["Use only provided audio."], ["Provided audio is forbidden."]);
        var indexes = catalog.Select((e,i) => (e,i)).Where(x => x.e.Kind is "Constraint" or "Decision").Select(x => x.i).ToArray();
        var gap = new SpecialistAgent.OwnershipGap("Resolve incompatible audio requirements",0,"The two approved rules cannot both hold.")
            { AffectedEpicIndexes = [1], ConflictEvidenceIndexes = indexes };
        var review = SpecialistAgent.BindOwnership(new([], [gap]), sources, f.Outline.DeliveryItems, suppliedCatalog: catalog);
        Assert.Equal(2, review.Gaps.Single().ConflictEvidence.Count);
        Assert.Equal("final", Assert.Single(review.Gaps.Single().AffectedProposalKeys));
        Assert.False(SpecialistAgent.OwnershipFinding(f.Cycle,f.Evidence.OutlineDigest,review.Gaps.Single(),f.Outline.DeliveryItems,"Marcus Vale").UserDecisionRequired);
    }
}
