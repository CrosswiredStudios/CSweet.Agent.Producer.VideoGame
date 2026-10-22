using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string PendingScopeAmendmentPrefix = "producer-pending-scope-amendment:";
    internal const string LegacyScopeRefusal = "Scope amendment requires the current work to reach a stopped review boundary. No running assignment or sprint was changed.";
    private const string ScopeRecoveryKey = "producer-scope-amendment-recovery:v1";
    internal static string PendingScopeKey(Guid board) => PendingScopeAmendmentPrefix + board.ToString("N");

    internal async Task<string> RetainScopeAmendmentAsync(WorkSprintExecutionResponse execution,
        WorkItemExecutionResponse item, WorkStageExecutionResponse? stage, string direction, ScopeAmendmentSource source,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (source.TurnId == Guid.Empty || source.MessageId == Guid.Empty || source.ConversationId == Guid.Empty || source.RequesterId == Guid.Empty ||
            stage is null || stage.Traversal != item.Traversal || execution.Status != "Active" ||
            item.Status is "Completed" or "Cancelled" || string.IsNullOrWhiteSpace(direction))
            return "The amendment needs a current unfinished ticket and its authenticated conversation turn. No scope was changed.";
        var board = await context.Platform.Work.ReadBoardAsync(execution.BoardId, token);
        if (board.Board.IsArchived || board.Board.ManagerOrganizationUserId?.ToString() != context.Identity?.EmployeeId ||
            board.Board.WorkstreamId is not { } project || board.Board.TeamId is not { } team ||
            board.Items.Count > 200 || !board.Items.Any(x => x.Id == item.WorkItemId && x.SprintId == execution.SprintId))
            return "The amendment no longer identifies my current project/team board. No scope was changed.";
        var key = PendingScopeKey(execution.BoardId);
        var prior = await context.Platform.ReadOperatingStateAsync<PendingScopeAmendment>(key, token);
        if (prior is not null && prior.Payload.Source.TurnId == source.TurnId)
        {
            if (prior.Payload.Source != source || prior.Payload.Direction != direction)
                throw new InvalidOperationException("The retained amendment's source turn cannot be rewritten.");
            if (prior.Status == "Pending") await EnsurePendingScopeCommitmentAsync(prior.Payload, CanAmendScope(execution, item.WorkItemId, stage.Id), context, token);
            return PendingScopeReply(prior.Status);
        }
        if (prior is not null && (prior.Status == "Pending" || prior.Status == "Queued" && prior.Payload.ExecutionId == execution.Id))
            return "A scope amendment is already retained for this board. It must be resolved before a different direction can replace it.";
        var pending = new PendingScopeAmendment(execution.BoardId, project, team, execution.Id, execution.SprintId,
            item.WorkItemId, item.ItemIdentifier!, stage.Id, direction, source,
            board.Items.ToDictionary(x => x.Id, x => x.PlanningRevision));
        try { await WritePendingScopeAsync(pending, "Pending", prior?.Revision, context, token); }
        catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.Conflict)
        {
            var current = await context.Platform.ReadOperatingStateAsync<PendingScopeAmendment>(key, token);
            if (current?.Payload.Source != source || current.Payload.Direction != direction)
                return "Another scope amendment was retained first. Your direction did not replace it.";
            if (current.Status == "Pending") await EnsurePendingScopeCommitmentAsync(current.Payload,
                CanAmendScope(execution, item.WorkItemId, stage.Id), context, token);
            return PendingScopeReply(current.Status);
        }
        // The durable request is the recovery source. If agenda creation fails after saving it,
        // the next attention/reconnect scan repairs the missing commitment by this board key.
        await EnsurePendingScopeCommitmentAsync(pending, CanAmendScope(execution, item.WorkItemId, stage.Id), context, token);
        return PendingScopeReply("Pending");
    }

    private static string PendingScopeReply(string status) => status switch
    {
        "Queued" => "Your retained scope amendment is already queued for corrected planning; execution has not been reported as changed or complete.",
        "NeedsReview" => "Your scope amendment remains saved and needs review before it can be applied. Its authority, source scope or exact replacement plan could not be verified.",
        _ => "I saved your scope amendment. I will apply it automatically at the next safe stopped boundary after rechecking authority and scope. You do not need to resend it."
    };

    private static Task<AgentOperatingStateResponse> WritePendingScopeAsync(PendingScopeAmendment pending, string status,
        long? revision, AgentRuntimeContext context, CancellationToken token) =>
        context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(PendingScopeKey(pending.BoardId),
            "video-game.pending-scope-amendment.v1", 1, status, new Dictionary<string, string>(), [],
            pending.Source.TurnId.ToString("N"), [PendingScopeCorrelation(pending)], pending.Source.TurnId,
            JsonSerializer.SerializeToElement(pending, ManagerJson), revision,
            $"scope-amendment:{pending.Source.TurnId:N}:{status}:{revision ?? 0}"), token);

    private static string PendingScopeCorrelation(PendingScopeAmendment pending) => PendingScopeKey(pending.BoardId) + ":" + pending.Source.TurnId.ToString("N");

    private static Task<PersonalTodoItem> EnsurePendingScopeCommitmentAsync(PendingScopeAmendment pending, bool ready,
        AgentRuntimeContext context, CancellationToken token) => EnsureCommitmentAsync(PendingScopeCorrelation(pending),
            "Apply the retained scope amendment at a safe boundary",
            "Reauthorize the saved conversation direction, re-read current planning and execution, and hand the exact amendment to corrected planning only when no assignment is running.",
            WorkPriorities.Critical, new(WorkstreamId: pending.ProjectId, TeamId: pending.TeamId, BoardId: pending.BoardId,
                SprintId: pending.SprintId, WorkItemId: pending.ItemId, SourceFingerprint: pending.Source.TurnId.ToString("N")),
            context, token, scopeAmendmentReady: ready);

    internal async Task RecoverPendingScopeAmendmentsAsync(AgentRuntimeContext context, CancellationToken token,
        IReadOnlyList<WorkBoardSummary>? currentBoards = null)
    {
        var boards = (currentBoards ?? await context.Platform.Work.ListBoardsAsync(cancellationToken: token))
            .Where(x => !x.IsArchived && x.ManagerOrganizationUserId?.ToString() == context.Identity?.EmployeeId).ToArray();
        if (boards.Length > 32) throw new InvalidOperationException("Pending scope discovery exceeds the supported board limit.");
        await RecoverRejectedScopeAmendmentsAsync(boards, context, token);
        foreach (var board in boards)
        {
            var saved = await context.Platform.ReadOperatingStateAsync<PendingScopeAmendment>(PendingScopeKey(board.Id), token);
            if (saved?.Status != "Pending") continue;
            var pending = saved.Payload;
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, pending.SprintId), token);
            var ready = execution?.Id == pending.ExecutionId && CanAmendScope(execution, pending.ItemId, pending.StageId);
            await EnsurePendingScopeCommitmentAsync(pending, ready, context, token);
        }
    }

    internal async Task<PersonalTodoResult> ReconcilePendingScopeAmendmentAsync(PersonalTodoItem commitment,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (commitment.WorkContext?.BoardId is not { } boardId)
            return PersonalTodoResult.Blocked("The saved scope amendment has no authoritative board.");
        var saved = await context.Platform.ReadOperatingStateAsync<PendingScopeAmendment>(PendingScopeKey(boardId), token);
        if (saved is null || commitment.CorrelationId != PendingScopeCorrelation(saved.Payload))
            return PersonalTodoResult.Blocked("This commitment does not identify the current saved amendment.");
        if (saved.Status == "Queued") return PersonalTodoResult.Completed("The exact retained amendment was handed to corrected planning.");
        if (saved.Status != "Pending") return PersonalTodoResult.Blocked(PendingScopeReply(saved.Status));
        var pending = saved.Payload;
        var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
        var execution = await context.Platform.Work.ReadOrchestrationAsync(new(boardId, pending.SprintId), token);
        bool authorized;
        try { authorized = await AuthorizesRetainedScopeAsync(pending, context, token); }
        catch (PlatformCapabilityException error) when (error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound)
        {
            return await HoldPendingScopeAsync(saved, context, token);
        }
        if (board.Board.IsArchived || board.Board.ManagerOrganizationUserId?.ToString() != context.Identity?.EmployeeId ||
            board.Board.WorkstreamId != pending.ProjectId || board.Board.TeamId != pending.TeamId || execution?.Id != pending.ExecutionId ||
            execution.Status != "Active" || board.Items.Count != pending.PlanningRevisions.Count ||
            board.Items.Any(x => !pending.PlanningRevisions.TryGetValue(x.Id, out var revision) || revision != x.PlanningRevision) ||
            !authorized)
            return await HoldPendingScopeAsync(saved, context, token);
        var item = execution.Items.SingleOrDefault(x => x.WorkItemId == pending.ItemId);
        var stage = item?.Stages.SingleOrDefault(x => x.Id == pending.StageId && x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
        if (item is null || stage is null || item.Status is "Completed" or "Cancelled")
            return await HoldPendingScopeAsync(saved, context, token);
        if (!CanAmendScope(execution, item.WorkItemId, stage.Id))
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(UnhandledBlockerGrace),
                "Your amendment is saved. Waiting for running assignments to reach a stopped boundary; work-state events will recheck it.");
        // Existing application re-reads the board, pins exact replacements, and creates an
        // idempotent planning commitment. Final sprint mutation still rechecks active writers.
        try { await ApplyScopeAmendmentAsync(execution, item, stage, pending.Direction, pending.Source.TurnId, context, token, pending.PlanningRevisions); }
        catch (InvalidOperationException) { return await HoldPendingScopeAsync(saved, context, token); }
        catch (PlatformCapabilityException error) when (error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound)
        { return await HoldPendingScopeAsync(saved, context, token); }
        await WritePendingScopeAsync(pending, "Queued", saved.Revision, context, token);
        return PersonalTodoResult.Completed("The retained amendment is queued for corrected planning. No benchmark or unperformed validation was marked passed.");
    }

    private static async Task<PersonalTodoResult> HoldPendingScopeAsync(AgentOperatingState<PendingScopeAmendment> saved,
        AgentRuntimeContext context, CancellationToken token)
    {
        await WritePendingScopeAsync(saved.Payload, "NeedsReview", saved.Revision, context, token);
        return PersonalTodoResult.Blocked(PendingScopeReply("NeedsReview"));
    }

    private static bool IsScopeSupervisor(OrganizationSnapshotResponse directory, Guid self, Guid requester)
    {
        var seen = new HashSet<Guid>();
        var person = directory.People.SingleOrDefault(x => x.Id == self && x.IsActive);
        while (person?.ReportsToId is { } parent && seen.Add(parent) && seen.Count <= 64)
        {
            person = directory.People.SingleOrDefault(x => x.Id == parent && x.IsActive);
            if (person?.Id == requester) return true;
        }
        return false;
    }

    private static async Task<bool> AuthorizesRetainedScopeAsync(PendingScopeAmendment pending, AgentRuntimeContext context, CancellationToken token)
    {
        var directory = await context.Platform.ReadOrganizationSnapshotAsync(token);
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var self) || !IsScopeSupervisor(directory, self, pending.Source.RequesterId)) return false;
        var messages = await context.Platform.Communication.ReadChatAsync(pending.Source.ConversationId, token);
        var source = messages.Messages.SingleOrDefault(x => x.Id == pending.Source.MessageId && x.ChatId == pending.Source.ConversationId);
        return source?.SenderOrganizationUserId == pending.Source.RequesterId &&
            (source.ChatTurnId is null || source.ChatTurnId == pending.Source.TurnId) &&
            TryReadAmendmentDirection(source.Content, out var ticket, out var direction) &&
            ticket.Equals(pending.Identifier, StringComparison.OrdinalIgnoreCase) && direction == pending.Direction;
    }

    internal static bool TryReadAmendmentDirection(string content, out string ticket, out string direction)
    {
        var message = content.TrimStart();
        ticket = direction = "";
        return message.StartsWith("Amend ticket ", StringComparison.OrdinalIgnoreCase) &&
            TryReadRetryDirection("Retry ticket " + message["Amend ticket ".Length..], out ticket, out direction);
    }

    // Upgrade recovery only recognizes the old implementation's exact refusal, authored
    // by this Producer, paired with its original supervisor message and conversation turn.
    // It never treats arbitrary historical directions or model output as a new grant.
    private async Task RecoverRejectedScopeAmendmentsAsync(IReadOnlyList<WorkBoardSummary> boards, AgentRuntimeContext context, CancellationToken token)
    {
        if (boards.Count == 0) return;
        if (await context.Platform.ReadOperatingStateAsync(new AgentOperatingStateReadRequest(ScopeRecoveryKey), token) is not null) return;
        try
        {
            var directory = await context.Platform.ReadOrganizationSnapshotAsync(token);
            var self = Guid.Parse(context.Identity!.EmployeeId);
            var hub = await context.Platform.Communication.ReadHubAsync(token);
            var candidates = new List<(CommunicationMessage Source, Guid Turn)>();
            foreach (var chat in hub.Chats.Where(x => x.IsDirect).OrderByDescending(x => x.UpdatedAt).Take(32))
            {
                var messages = (await context.Platform.Communication.ReadChatAsync(chat.Id, token)).Messages.OrderBy(x => x.Sequence).TakeLast(200).ToArray();
                for (var index = messages.Length - 1; index > 0; index--)
                {
                    var reply = messages[index]; var source = messages[index - 1];
                    if (reply.SenderOrganizationUserId != self || reply.Content != LegacyScopeRefusal || reply.ChatTurnId is not { } turn ||
                        reply.ChatId != chat.Id || source.ChatId != chat.Id || source.ChatTurnId is { } sourceTurn && sourceTurn != turn ||
                        source.SenderOrganizationUserId is not { } sender || !IsScopeSupervisor(directory, self, sender) ||
                        !TryReadAmendmentDirection(source.Content, out var ticket, out var direction)) continue;
                    if (messages.Skip(index + 1).Any(later => later.SenderOrganizationUserId is { } author && IsScopeSupervisor(directory, self, author) &&
                        TryReadAmendmentDirection(later.Content, out var laterTicket, out _) && laterTicket.Equals(ticket, StringComparison.OrdinalIgnoreCase))) continue;
                    candidates.Add((source, turn));
                }
            }
            foreach (var board in boards)
            {
                if (candidates.Count == 0 || await context.Platform.ReadOperatingStateAsync(new AgentOperatingStateReadRequest(PendingScopeKey(board.Id)), token) is not null) continue;
                var sprints = (await context.Platform.Work.ListSprintsAsync(board.Id, token)).Where(x => x.Status == "Active").Take(2).ToArray();
                if (sprints.Length != 1) continue;
                var execution = await context.Platform.Work.ReadOrchestrationAsync(new(board.Id, sprints[0].Id), token);
                if (execution is null) continue;
                foreach (var candidate in candidates.OrderByDescending(x => x.Source.CreatedAt))
                {
                    TryReadAmendmentDirection(candidate.Source.Content, out var ticket, out var direction);
                    var matches = execution.Items.Where(x => x.ItemIdentifier.Equals(ticket, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if (matches.Length != 1) continue;
                    var item = matches[0];
                    var stage = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
                    if (stage is null || stage.UpdatedAt > candidate.Source.CreatedAt || execution.StartedAt > candidate.Source.CreatedAt) continue;
                    await RetainScopeAmendmentAsync(execution, item, stage, direction,
                        new(candidate.Source.SenderOrganizationUserId!.Value, candidate.Source.ChatId, candidate.Source.Id, candidate.Turn), context, token);
                    break;
                }
            }
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(ScopeRecoveryKey,
                "video-game.scope-amendment-recovery.v1", 1, "Completed", new Dictionary<string, string>(), [], "v1", [], Guid.Empty,
                JsonSerializer.SerializeToElement(new { completed = true }), null, ScopeRecoveryKey), token);
        }
        catch (PlatformCapabilityException error) when (error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound)
        {
            // This optional historical catch-up cannot expand current access or block new requests.
        }
    }
}

internal sealed record ScopeAmendmentSource(Guid RequesterId, Guid ConversationId, Guid MessageId, Guid TurnId);
internal sealed record PendingScopeAmendment(Guid BoardId, Guid ProjectId, Guid TeamId, Guid ExecutionId, Guid SprintId,
    Guid ItemId, string Identifier, Guid StageId, string Direction, ScopeAmendmentSource Source, IReadOnlyDictionary<Guid, long> PlanningRevisions);
