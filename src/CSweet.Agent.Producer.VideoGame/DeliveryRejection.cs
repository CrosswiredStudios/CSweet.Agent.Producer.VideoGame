using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

internal sealed record ProducerDeliveryRejectionEscalation(Guid BoardId, string Reason, Guid ManagerId, DateTimeOffset EscalatedAt);

public sealed partial class SpecialistAgent
{
    /// <summary>Starts a staffing gap that came from a platform rejection of the delivery plan itself.</summary>
    internal const string PlatformRejectionGapPrefix = "The platform rejects my delivery plan, so retrying it unchanged can't help. ";

    /// <summary>
    /// How long to wait before rechecking a rejected plan. Super Awesome Games (2026-11-10): two commitments
    /// re-ran the same rejected configuration every 7–10 minutes, about 1,000 capability calls in 40 minutes.
    /// The recheck still happens, so a fixed agent or platform recovers without anyone intervening.
    /// </summary>
    internal static readonly TimeSpan RejectedDeliveryRecheck = TimeSpan.FromHours(1);

    internal static TimeSpan StaffingRecheckDelay(string? gap, TimeSpan normal) =>
        gap?.StartsWith(PlatformRejectionGapPrefix, StringComparison.Ordinal) == true ? RejectedDeliveryRecheck : normal;

    /// <summary>
    /// Raised once per board and rejection reason. Follows the delivery-escalation convention (an ask and a
    /// reply command), so a non-deciding manager such as the Video Game Director passes it up the chain.
    /// </summary>
    internal static (string Key, string Content) DeliveryRejectionEscalation(string? projectName, Guid boardId, string reason)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "This project" : projectName.Trim();
        var shown = reason.Length <= 500 ? reason : reason[..497] + "...";
        var content = $"{project} delivery is stuck: the platform rejects my delivery plan, so the planned sprint can't start. " +
            "Nothing I retry can fix this, so it needs your attention.\n\n" +
            $"Platform reason: {shown}\n\n" +
            "This usually means a C-Sweet defect rather than a planning decision; please pass it to whoever maintains C-Sweet. " +
            $"I recheck on my own every {RejectedDeliveryRecheck.TotalMinutes:0} minutes; once it's fixed you can reply " +
            $"`{StaffingRetryCommand}: <what changed>` to have me recheck now.";
        return ($"{DecisionEscalationPrefix}{boardId:N}:rejected:{AcceptanceDigest(reason)[..16]}", content);
    }

    internal static async Task EscalateDeliveryRejectionAsync(WorkBoardSummary board, string reason,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) || manager == Guid.Empty ||
            manager.ToString() == context.Identity?.EmployeeId) return;
        try
        {
            string? projectName = null;
            if (board.WorkstreamId is { } project)
            {
                try { projectName = (await context.Platform.ReadWorkstreamAsync(new(project), token)).Name; }
                catch (PlatformCapabilityException) { }
            }
            var escalation = DeliveryRejectionEscalation(projectName, board.Id, reason);
            AgentOperatingState<ProducerDeliveryRejectionEscalation>? sent;
            try { sent = await context.Platform.ReadOperatingStateAsync<ProducerDeliveryRejectionEscalation>(escalation.Key, token); }
            catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound) { sent = null; }
            if (sent is not null) return;
            await context.Platform.Communication.SendDirectMessageAsync(manager, escalation.Content, escalation.Key, token);
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(escalation.Key,
                "video-game.producer-delivery-rejection-escalation.v1", 1, "Active", new Dictionary<string, string>(), [], escalation.Key, [],
                Guid.NewGuid(), JsonSerializer.SerializeToElement(new ProducerDeliveryRejectionEscalation(board.Id, reason, manager,
                    DateTimeOffset.UtcNow)), null, escalation.Key), token);
        }
        catch (PlatformCapabilityException)
        {
            // Advisory: the commitment still waits with the reason and raises it again on the next recheck.
        }
    }
}
