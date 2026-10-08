using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

internal sealed record ProducerAccessEscalation(Guid BoardId, string Capability, Guid ManagerId, DateTimeOffset EscalatedAt);

public sealed partial class SpecialistAgent
{
    internal const string StaffingRetryCommand = "Retry staffing";

    /// <summary>
    /// A platform refusal is an authority problem: no retry of mine can clear it, and letting it escape as an
    /// unhandled failure only shows "execution stopped unexpectedly" while the whole project waits unseen.
    /// </summary>
    internal static bool IsAccessRefusal(PlatformCapabilityException exception) =>
        exception.Code == PlatformCapabilityErrorCode.Denied;

    /// <summary>The platform's own refusal sentence, without the transport envelope, bounded for a message.</summary>
    internal static string RefusalReason(PlatformCapabilityException exception)
    {
        var text = exception.Message?.Trim() ?? "";
        if (text.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    text = message.GetString()?.Trim() ?? "";
            }
            catch (JsonException) { }
        }
        return text.Length <= 500 ? text : text[..497] + "...";
    }

    /// <summary>
    /// Raised once per board and refused capability. The message follows the delivery-escalation convention
    /// (an ask and a reply command), so a non-deciding manager such as the Creative Director passes it up the chain.
    /// </summary>
    internal static (string Key, string Content) DeliveryAccessEscalation(string? projectName, Guid boardId, string capability, string reason)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "This project" : projectName.Trim();
        var content = $"{project} delivery can't start: the platform denied me `{capability}`, so I can't staff, dispatch or " +
            "recover its tickets. Nothing I retry can fix this, so it needs your decision.\n\n" +
            (string.IsNullOrWhiteSpace(reason) ? "" : $"Platform reason: {reason}\n\n") +
            $"As the project's accountable manager I need to be one of its members: add me under Projects → {project} → Manage members, " +
            "which gives me its delivery access, or tell me who should run delivery instead. " +
            $"I recheck on my own every {UnhandledBlockerGrace.TotalMinutes:0} minutes; once it's fixed you can reply " +
            $"`{StaffingRetryCommand}: <what changed>` to have me recheck now.";
        return ($"{DecisionEscalationPrefix}{boardId:N}:access:{AcceptanceDigest(capability)[..16]}", content);
    }

    private static async Task EscalateDeliveryAccessAsync(WorkBoardSummary board, string? projectName,
        PlatformCapabilityException refusal, AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) || manager == Guid.Empty ||
            manager.ToString() == context.Identity?.EmployeeId) return;
        var capability = string.IsNullOrWhiteSpace(refusal.Capability) ? "a project capability" : refusal.Capability;
        var escalation = DeliveryAccessEscalation(projectName, board.Id, capability, RefusalReason(refusal));
        try
        {
            AgentOperatingState<ProducerAccessEscalation>? sent;
            try { sent = await context.Platform.ReadOperatingStateAsync<ProducerAccessEscalation>(escalation.Key, token); }
            catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound) { sent = null; }
            if (sent is not null) return;
            await context.Platform.Communication.SendDirectMessageAsync(manager, escalation.Content, escalation.Key, token);
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(escalation.Key,
                "video-game.producer-access-escalation.v1", 1, "Active", new Dictionary<string, string>(), [], escalation.Key, [],
                Guid.NewGuid(), JsonSerializer.SerializeToElement(new ProducerAccessEscalation(board.Id, capability, manager,
                    DateTimeOffset.UtcNow)), null, escalation.Key), token);
        }
        catch (PlatformCapabilityException)
        {
            // Advisory: the recovery commitment stays waiting with the reason and raises it again on the next review.
        }
    }

    // Only the current authenticated message authorizes a recheck, never history or model output.
    internal static bool TryReadStaffingRetry(string message, out string reason)
    {
        var match = Regex.Match(message.Trim(), @"\ARetry staffing:\s*(.{1,1000})\z",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        reason = match.Success ? match.Groups[1].Value.Trim() : "";
        return match.Success && reason.Length > 0;
    }

    /// <summary>Wakes my waiting or blocked delivery-recovery commitments so they recheck current access now.</summary>
    private static async Task<string> RetryStaffingAsync(Guid turnId, AgentRuntimeContext context, CancellationToken token)
    {
        var pending = (await context.Platform.PersonalTodo.ListAsync(token)).Boards.SelectMany(b => b.Items)
            .Where(t => t.ArchivedAt is null && t.CorrelationId?.StartsWith(WorkflowRecoveryPrefix, StringComparison.Ordinal) == true &&
                (t.Status is PersonalTodoStatuses.Backlog or PersonalTodoStatuses.Blocked ||
                 (t.Status == PersonalTodoStatuses.Running && t.Wait is not null)))
            .ToArray();
        if (pending.Length == 0)
            return "I have no delivery staffing waiting to be rechecked. Current assignments continue through the project board.";
        foreach (var item in pending)
        {
            try
            {
                await context.Platform.PersonalTodo.RequeueAsync(new RequeuePersonalTodoItemRequest(item.Id, item.Revision,
                    $"producer-staffing-retry:{turnId:N}:{item.Id:N}"), token);
            }
            catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.Conflict)
            {
                // It changed meanwhile and will be rechecked from its current state.
            }
        }
        return $"Rechecking delivery staffing now ({pending.Length} board{(pending.Length == 1 ? "" : "s")}). " +
            "If access is still missing I'll say so again; nothing else is needed from you.";
    }
}
