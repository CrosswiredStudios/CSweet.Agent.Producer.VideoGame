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
        Assert.Null(TicketVerification.FromProposal(null));
        Assert.Null(TicketVerification.FromProposal("unexpected"));
    }
}
