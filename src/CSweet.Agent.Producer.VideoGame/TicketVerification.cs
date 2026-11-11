using CrosswiredStudios.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

/// <summary>
/// Records a plan's per-task browser choice on the ticket, where the engineer binds it, the technical reviewer checks it
/// and QA reproduces it. Super Awesome Games (2026-11-10): without one shared declaration each role read different text.
/// </summary>
internal static class TicketVerification
{
    /// <summary>Null (a proposal from before the field existed) stays undeclared so agents keep their previous rules.</summary>
    internal static IReadOnlyList<WorkItemVerificationRequirement>? FromProposal(string? browserVerification) => browserVerification switch
    {
        GameBrowserVerification.Canvas => [new(WorkItemVerificationKinds.BrowserSmoke) { RequireCanvas = true }],
        GameBrowserVerification.Page => [new(WorkItemVerificationKinds.BrowserSmoke)],
        GameBrowserVerification.None => [],
        _ => null
    };
}
