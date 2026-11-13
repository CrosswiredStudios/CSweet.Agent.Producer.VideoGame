using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class TicketVerificationTests
{
    [Fact]
    public void Canvas_requires_a_browser_smoke_with_a_visible_canvas()
    {
        var requirement = Assert.Single(TicketVerification.FromProposal(GameBrowserVerification.Canvas)!);
        Assert.Equal(WorkItemVerificationKinds.BrowserSmoke, requirement.Kind);
        Assert.True(requirement.RequireCanvas);
        Assert.Empty(WorkItemVerifications.Validate([requirement]));
    }

    [Fact]
    public void Page_requires_a_browser_smoke_without_a_canvas()
    {
        var requirement = Assert.Single(TicketVerification.FromProposal(GameBrowserVerification.Page)!);
        Assert.False(requirement.RequireCanvas);
    }

    [Fact]
    public void None_is_a_declared_empty_list_and_older_proposals_stay_undeclared()
    {
        var none = TicketVerification.FromProposal(GameBrowserVerification.None);
        Assert.NotNull(none);
        Assert.Empty(none);
        Assert.Null(TicketVerification.FromProposal((string?)null));
        Assert.Null(TicketVerification.FromProposal("unexpected"));
    }

    [Fact]
    public void Reordered_bound_criteria_keep_the_same_behavior_and_reject_weakened_wording()
    {
        var task = Proposal(GameBrowserVerification.Canvas, [1]) with {
            AcceptanceCriteria = ["Ball breaks a brick", "Scene renders"],
            CriterionBindings = [new("scene", "loop", "Scene renders", false), new("collision", "loop", "Ball breaks a brick", true)] };
        Assert.Equal([0], TicketVerification.FromProposal(task)!.Single(v => v.Kind == WorkItemVerificationKinds.BrowserInteraction).CriterionIndexes);
        Assert.Throws<InvalidOperationException>(() => TicketVerification.FromProposal(task with { AcceptanceCriteria = ["Paddle renders", "Scene renders"] }));
    }

    private static GameProposedWorkItemV1 Proposal(string verification, IReadOnlyList<int>? interaction) =>
        new("task", VideoGameWorkItemTypeKeys.Task, "Task", "Do it.", ["Scene renders", "Ball breaks a brick"], VideoGameRoleKeys.Engineer,
            [], [], ["work.execution.run"], []) { BrowserVerification = verification, InteractionCriteria = interaction };

    [Fact]
    public void Declared_interaction_criteria_become_a_browser_interaction_requirement()
    {
        var verifications = TicketVerification.FromProposal(Proposal(GameBrowserVerification.Canvas, [1, 5, 1]))!;
        Assert.Equal(2, verifications.Count);
        var interaction = verifications.Single(v => v.Kind == WorkItemVerificationKinds.BrowserInteraction);
        Assert.Equal(new[] { 1 }, interaction.CriterionIndexes); // out-of-range and duplicate indexes are dropped
        var planning = new WorkItemPlanningSpecification(["Task"], ["Scene renders", "Ball breaks a brick"]) { Verifications = verifications };
        Assert.Empty(WorkItemVerifications.ValidatePlanning(planning));

        Assert.Single(TicketVerification.FromProposal(Proposal(GameBrowserVerification.Canvas, null))!);
        Assert.Single(TicketVerification.FromProposal(Proposal(GameBrowserVerification.Canvas, []))!);
        Assert.Empty(TicketVerification.FromProposal(Proposal(GameBrowserVerification.None, [0]))!); // no build to observe
    }
}
