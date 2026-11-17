using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Fact]
    public async Task ExplicitFutureOutcomeRemainsOpenWhileTheIndependentPocIsPublishedAndRefined()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var source = new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "digest", "Director", "Technical PoC followed by ten designed levels.");
        var review = SpecialistAgent.BindOwnership(new([], [new("Ten designed levels", 0,
            "Later level content needs an owner; it is not a prerequisite of the independent engine smoke experiment.") { Impact = "FutureWork" }]),
            [source], f.Outline.DeliveryItems);
        Assert.Equal("FutureWork", Assert.Single(review.Gaps).Impact);
        Assert.Empty(review.Gaps[0].AffectedProposalKeys);
        var first = await f.Advance(null, review);
        Assert.Equal(2, f.Items.Count); Assert.Single(f.Starts); Assert.False(first.Complete);
        var replay = await f.Advance(first.Progress, review);
        Assert.Single(f.Starts); Assert.Equal(2, f.Items.Count); Assert.False(replay.Complete);
        Assert.Single(review.Gaps); // Publication/refinement is not final acceptance or an issue resolution.
    }

    [Theory]
    [InlineData("Scoped")]
    [InlineData("Unassessed")]
    public async Task AnOrdinaryGapIsTrackedBesideThePlanAndDoesNotHoldWork(string impact)
    {
        var f = new Fixture(adaptive: true);
        var source = new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "digest", "Director", "The core must be feasible.");
        var review = SpecialistAgent.BindOwnership(new([], [new("Core feasibility", 0, "The missing capability affects the actual experiment.") {
            Impact = impact, AffectedEpicIndexes = impact == "Scoped" ? [0] : [] }]), [source], f.Outline.DeliveryItems);
        var result = await f.Advance(null, review);
        // The ownership review is advisory: the first Story is still requested. The gap must close before
        // final acceptance, and only a conflict between binding requirements holds scope.
        Assert.Single(f.Starts); Assert.False(result.Complete); Assert.Null(result.Blocker);
        Assert.Single(review.Gaps); Assert.Empty(SpecialistAgent.OwnershipHolds(f.Outline.DeliveryItems, review));
    }

    [Fact]
    public async Task AFullyHeldRoadmapWaitsOnItsReconciliationInsteadOfAskingForAnImpossibleSelection()
    {
        // Fresh Prism Break, November 17: a gap on the first Epic held every candidate, the review was still
        // asked to select, and three rejected replies blocked planning as a technical failure.
        var f = new Fixture(adaptive: true);
        var source = new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "digest", "Director", "The core must be feasible.");
        var review = new SpecialistAgent.OwnershipReview([], [new("Core feasibility", "Two binding requirements cannot both hold.",
            f.Outline.DeliveryItems.Select(i => i.ProposalKey).ToArray()) { BriefTitle = "Core feasibility is unresolved",
            ConflictEvidence = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["prototype"]), new("two", "Decision", "Provided audio is forbidden.", ["prototype"]) } }]);
        Assert.NotNull(source);
        var asked = 0;
        var result = await SpecialistAgent.AdvanceProgressivePlanningAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Member, f.Session, f.Artifact,
            f.Outline, null, f.Runtime.CreateContext(), default, (_, _, _, _, _, _) => { asked++; throw new InvalidOperationException("No selection is possible."); }, review);
        Assert.Equal(0, asked); Assert.Null(result.Blocker); Assert.False(result.Complete); Assert.Empty(f.Starts);
        Assert.Contains("Core feasibility is unresolved", result.Waiting);
    }

    [Fact]
    public void ARejectedReviewReplyNamesItsOwnCorrection()
    {
        SpecialistAgent.ReviewFact[] facts = [new("brief", Guid.NewGuid(), "approved", "Approved scope")];
        var select = new SpecialistAgent.ReviewChoice("Start the first increment", 0, true, true, "After the spike", "Planning only",
            [new("proceed", "The draft is current", null, [0])]) { Disposition = "select" };
        string Rejected(SpecialistAgent.ReviewChoice choice, int candidates) =>
            Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.ValidateReview(choice, candidates, [], facts)).Message;
        SpecialistAgent.ValidateReview(select, 1, [], facts);
        Assert.Contains("No candidate is eligible", Rejected(select, 0));
        Assert.Contains("zero-based integer index", Rejected(select with { NextCandidate = null }, 1));
        Assert.Contains("0..0", Rejected(select with { NextCandidate = 3 }, 1));
        Assert.Contains("evidenceIndexes within 0..0", Rejected(select with { Actions = [new("proceed", "Cited", null, [4])] }, 1));
        Assert.Contains("null capability", Rejected(select with { Actions = [new("proceed", "Cited", "Engineering", [0])] }, 1));
        SpecialistAgent.ValidateReview(select with { Disposition = "wait", NextCandidate = null }, 0, [], facts);
    }

    [Fact]
    public void FutureClassificationCannotAlsoClaimAnAffectedCurrentEpic()
    {
        var f = new Fixture(adaptive: true);
        var source = new GameOutcomeSource("approved", Guid.NewGuid(), Guid.NewGuid(), "digest", "Director", "Preserve scope.");
        Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.BindOwnership(new([], [new("Future content", 0, "Contradictory impact") {
            Impact = "FutureWork", AffectedEpicIndexes = [0] }]), [source], f.Outline.DeliveryItems));
    }

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
    public async Task ABindingConflictHoldsConsumersAndCannotConcludeTheProjectEvenWhenNoEligibleScopeRemains()
    {
        var f = new Fixture(adaptive: true);
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Core behavior has incompatible requirements", "Both cannot hold", ["prototype"]) {
            ConflictEvidence = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["prototype"]), new("two", "Decision", "Provided audio is forbidden.", ["prototype"]) } }]);
        var result = await f.Advance(null, ownership);
        Assert.Null(result.Blocker); Assert.False(result.Complete); Assert.NotNull(result.Waiting);
        Assert.Empty(f.Starts); Assert.Equal(2, f.Items.Count);
        Assert.Equal(new[] { "final", "prototype" }, SpecialistAgent.OwnershipHolds(f.Outline.DeliveryItems, ownership).Order().ToArray());
    }

    [Fact]
    public async Task ANewBindingConflictRetainsAffectedDraftAndAllowsAnIndependentIncrementToBeSelected()
    {
        var f = new Fixture(adaptive: true, independent: true);
        var first = await f.Advance(null);
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Core review", "Unresolved", ["prototype"]) {
            ConflictEvidence = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["prototype"]), new("two", "Decision", "Provided audio is forbidden.", ["prototype"]) } }]);
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
