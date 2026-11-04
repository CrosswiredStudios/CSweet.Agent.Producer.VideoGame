using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningGateScopeTests
{
    [Fact]
    public void Final_style_blocks_final_art_descendants_and_consumers_but_not_bootstrap_or_style_evidence()
    {
        var f = new Fixture();
        var scoped = SpecialistAgent.ScopePlanningGates(f.Cycle, f.Items, [f.Decision], "accepted");
        Assert.NotNull(scoped.DecisionIndependentProposalKeys);
        Assert.True(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Bootstrap, scoped));
        Assert.True(SpecialistAgent.CanPrepareWithPlanningDecisions(f.StyleEvidence, scoped));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.FinalArt, scoped));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Consumer, scoped));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.NestedConsumer, scoped));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Bootstrap with { ProposalProvenance = null }, scoped));
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Bootstrap with {
            ProposalProvenance = f.Bootstrap.ProposalProvenance! with { ArtifactDigest = "foreign" } }, scoped));
        Assert.Equal(f.Cycle.OutstandingAuthorityQuestions, scoped.OutstandingAuthorityQuestions);
        Assert.DoesNotContain("decisionIndependentProposalKeys", JsonSerializer.Serialize(scoped, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var changed = f.Items.Select(item => item.Id == f.Bootstrap.Id ? item with {
            Planning = item.Planning! with { DependencyItemIds = [f.FinalArt.Id] } } : item).ToArray();
        var rechecked = SpecialistAgent.ScopePlanningGates(scoped, changed, [f.Decision], "accepted");
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Bootstrap, rechecked));
    }

    [Theory]
    [InlineData("foreign-project")][InlineData("stale-brief")][InlineData("superseded")]
    [InlineData("pending")][InlineData("material-scope")][InlineData("duplicate-decision")]
    [InlineData("missing-scope")][InlineData("duplicate-key")][InlineData("unknown-question")]
    [InlineData("foreign-proposal")]
    public void Missing_or_ambiguous_current_authority_keeps_the_conservative_gate(string scenario)
    {
        var f = new Fixture(); var cycle = f.Cycle; var decision = f.Decision;
        IReadOnlyList<WorkItem> items = f.Items;
        if (scenario == "foreign-project") decision = decision with { WorkstreamId = Guid.NewGuid() };
        if (scenario == "stale-brief") decision = decision with { Evidence = [] };
        if (scenario == "superseded") decision = decision with { SupersededByDecisionId = Guid.NewGuid() };
        if (scenario == "pending") decision = decision with { Status = DecisionStatuses.Pending };
        if (scenario == "material-scope") decision = decision with { AuthorityRuleKey = "material-strategy-change" };
        if (scenario == "missing-scope") items = items.Where(item => item.ProposalProvenance!.ProposalItemKey != SpecialistAgent.FinalVisualStyleScopes[0]).ToArray();
        if (scenario == "duplicate-key") items = [.. items, f.Bootstrap with { Id = Guid.NewGuid() }];
        if (scenario == "unknown-question") cycle = cycle with { OutstandingAuthorityQuestions = [SpecialistAgent.FinalVisualStyleQuestion, "New engine approval"] };
        if (scenario == "foreign-proposal") cycle = cycle with { TechnicalProposalDigest = "foreign" };
        var decisions = scenario == "duplicate-decision" ? new[] { decision, decision with { Id = Guid.NewGuid() } } : [decision];
        var scoped = SpecialistAgent.ScopePlanningGates(cycle, items, decisions, "accepted");
        Assert.Null(scoped.DecisionIndependentProposalKeys);
        Assert.False(SpecialistAgent.CanPrepareWithPlanningDecisions(f.Bootstrap, scoped));
    }

    internal sealed class Fixture
    {
        internal ProducerPlanningCycleState Cycle;
        internal DecisionRecord Decision;
        internal List<WorkItem> Items = [];
        internal WorkItem Bootstrap, StyleEvidence, FinalArt, Consumer, NestedConsumer;
        internal Fixture()
        {
            Cycle = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cycle", null, null, null, null, "technical", "reconciled", DateTimeOffset.UtcNow) {
                OutstandingAuthorityQuestions = [SpecialistAgent.FinalVisualStyleQuestion]
            };
            Decision = new(Guid.NewGuid(), Cycle.WorkstreamId, "video-game.management-direction.v1", SpecialistAgent.FinalVisualStyleQuestion,
                "work-planning", [], "provide-direction", "request-more-evidence", DecisionStatuses.Decided,
                "Final art waits for style boards; prototype preparation is unaffected.",
                [new("artifact", Guid.NewGuid(), Guid.NewGuid(), "accepted", "video-game.production-plan.v1", "Accepted")],
                null, null, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            WorkItem Add(string key, bool container = false, Guid? parent = null, params Guid[] dependencies)
            {
                var item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = Guid.NewGuid(), ParentItemId = parent,
                    TypeKey = "video-game.task.v1", Status = "Ready", ExecutionMode = container ? WorkItemExecutionModes.Container : WorkItemExecutionModes.Executable,
                    ProposalProvenance = new(Guid.NewGuid(), "technical", key), Planning = new(["Requirement"], ["Criterion"], []) { DependencyItemIds = dependencies } };
                Items.Add(item); return item;
            }
            var roots = SpecialistAgent.FinalVisualStyleScopes.Select(key => Add(key, true)).ToArray();
            Bootstrap = Add("bootstrap"); StyleEvidence = Add("style-options");
            FinalArt = Add("final-art-task", parent: roots[0].Id);
            Consumer = Add("consumer", dependencies: [FinalArt.Id]);
            NestedConsumer = Add("nested-consumer", parent: Consumer.Id);
        }
    }
}
