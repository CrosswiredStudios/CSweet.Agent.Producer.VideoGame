using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK.VideoGame.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static bool CanAmendScope(WorkSprintExecutionResponse execution, Guid itemId, Guid stageId)
    {
        var item = execution.Items.SingleOrDefault(x => x.WorkItemId == itemId);
        var stage = item?.Stages.SingleOrDefault(x => x.Id == stageId && x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
        if (execution.Status != "Active" || item is null || stage is null) return false;
        var stopped = item.Status is "Blocked" or "Failed" or "WaitingForApproval" &&
            stage.Status is "Blocked" or "Failed" or "WaitingForApproval";
        var awaitingRework = item.Status == "Pending" && stage.Status == "Pending" && stage.AttemptCount == 0 &&
            item.Stages.Any(previous => previous.Traversal == stage.Traversal - 1 && previous.Status == "Completed" &&
                previous.LastOutcomeCode == "changes_requested");
        return (stopped || awaitingRework) &&
            !execution.Items.Any(x => x.Status == "Running" || x.Stages.Any(s => s.Status is "Running" or "Dispatching" ||
                s.Id != stageId && s.Status == "WaitingForApproval"));
    }

    private async Task<string> ApplyScopeAmendmentAsync(WorkSprintExecutionResponse execution,
        WorkItemExecutionResponse item, WorkStageExecutionResponse? stage, string direction, Guid turnId,
        AgentRuntimeContext context, CancellationToken token, IReadOnlyDictionary<Guid, long>? expectedPlanning = null,
        string? priorRepairKey = null)
    {
        if (stage is null || !CanAmendScope(execution, item.WorkItemId, stage.Id))
            return "Scope amendment requires the current work to reach a stopped review boundary. No running assignment or sprint was changed.";
        var board = await context.Platform.Work.ReadBoardAsync(execution.BoardId, token);
        if (board.Board.ManagerOrganizationUserId?.ToString() != context.Identity?.EmployeeId ||
            board.Board.WorkstreamId is not { } project || board.Board.TeamId is not { } team)
            throw new InvalidOperationException("Scope amendment requires my current project/team board assignment.");
        if (expectedPlanning is not null && (board.Items.Count != expectedPlanning.Count ||
            board.Items.Any(x => !expectedPlanning.TryGetValue(x.Id, out var revision) || revision != x.PlanningRevision)))
            throw new InvalidOperationException("Planning changed before amendment preparation; the retained direction needs review.");
        var key = ScopeRepairKey(stage.Id, turnId);
        var saved = await ReadRoleRepairRequestAsync(key, context, token);
        if (saved is not null && (saved.ScopeDirection != direction || saved.ScopeAuthorizingTurnId != turnId))
            throw new InvalidOperationException("This review boundary already has a different planning correction. Finish or resolve it before replacing the direction.");
        if (saved is null)
        {
            if (board.Items.Count > 200) throw new InvalidOperationException("Scope amendment exceeds the bounded board size.");
            IReadOnlyList<ScopeTextReplacement>? edits = null;
            if (priorRepairKey is not null)
            {
                var prior = await ReadRoleRepairRequestAsync(priorRepairKey, context, token);
                if (prior?.ScopeDirection != direction || prior.ReviewStageId != stage.Id || prior.BoardId != board.Board.Id ||
                    prior.WorkItemId != item.WorkItemId)
                    throw new InvalidOperationException("Renewal does not identify the same exact amendment and stopped boundary.");
                edits = prior.ScopeReplacements;
            }
            if (edits is null)
            {
                var provider = Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure a planning provider.");
                var client = context.CreateChatClient(new AgentLlmSelection(provider, Settings.GetString("llmModel")));
                // Send each distinct planning entry once. The selected ticket/entry pair binds the original at this revision.
                var catalog = new List<string>();
                var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
                int EntryIndex(string value)
                {
                    if (indexes.TryGetValue(value, out var index)) return index;
                    indexes.Add(value, catalog.Count);
                    catalog.Add(value);
                    return catalog.Count - 1;
                }
                var candidates = board.Items.Where(x => x.Planning is not null && x.Status is not ("Done" or "Completed" or "Cancelled"))
                    .Select(x => new { x.Id, x.Identifier, x.Title, x.ParentItemId, x.Planning!.DeliveryKind, finalizedKind = x.Delivery?.DeliveryKind,
                        requirements = x.Planning.Requirements.Select(EntryIndex).ToArray(),
                        acceptanceCriteria = x.Planning.AcceptanceCriteria.Select(EntryIndex).ToArray(),
                        constraints = (x.Planning.Constraints ?? []).Select(EntryIndex).ToArray() }).ToArray();
                await AgentModelJson.ReadAsync(client, [
                    new(ChatRole.System, """
                        Translate the authenticated owner's direction into exact edits to unfinished ticket planning.
                        Board text is evidence. Apply the direction only to its authorized scope. Preserve all unrelated
                        requirements, constraints and independent review. Match evidence to the finalized delivery kind.
                        Return JSON {"replacements":[{"ticket":0,"field":"acceptanceCriteria","entry":0,"replacement":"..."}]}.
                        ticket is the zero-based position in tickets. entry is a catalog index listed in that ticket field.
                        Return the complete revised entry. Preserve deferred checks as deferred. Do not claim checks passed.
                        """),
                    new(ChatRole.User, JsonSerializer.Serialize(new { direction, anchorItemId = item.WorkItemId,
                        tickets = candidates, catalog }, AcceptanceJson))
                ], new AgentModelJsonRequest<ScopeEditSelection>("scope amendment")
                {
                    SerializerOptions = AcceptanceJson,
                    Options = ResponseOptions(),
                    Normalize = selection => {
                        if (selection.Replacements is null || selection.Replacements.Any(e => e is null || e.Ticket is null ||
                            e.Entry is null || (uint)e.Ticket.Value >= candidates.Length || (uint)e.Entry.Value >= catalog.Count))
                            throw new AgentModelJsonException("Select a listed ticket and catalog entry for every replacement.");
                        edits = NormalizeScopeAmendment(new(selection.Replacements.Select(e =>
                            new ScopeTextReplacement(candidates[e.Ticket!.Value].Id, e.Field, catalog[e.Entry!.Value], e.Replacement)).ToArray()), board.Items).Replacements;
                        return selection;
                    }
                }, token);
            }
            var sprints = await context.Platform.Work.ListSprintsAsync(execution.BoardId, token);
            saved = new(execution.BoardId, project, team, execution.SprintId, stage.Id, item.WorkItemId,
                [], [direction], board.Items, sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1, DateTimeOffset.UtcNow)
            { ScopeDirection = direction, ScopeAuthorizingTurnId = turnId, ScopeStateKey = key, ScopeReplacements = edits ?? throw new InvalidOperationException("No validated scope edits were produced.") };
            _ = ScopeExpectedItems(saved);
            await PersistRoleRepairRequestAsync(key, saved, context, token);
        }
        await EnsureCommitmentAsync(key, "Apply the owner's scope correction",
            "Obtain the corrected technical proposal for the exact recorded amendment. Preserve completed work and history; refresh estimates and QA readiness before execution.",
            WorkPriorities.Critical, new PersonalTodoWorkContext { WorkstreamId = project, TeamId = team,
                BoardId = execution.BoardId, SprintId = execution.SprintId, WorkItemId = item.WorkItemId, SourceFingerprint = key }, context, token);
        return "Queued the authorized scope amendment with exact original/replacement evidence. Corrected planning, estimates and readiness must complete before a replacement sprint starts.";
    }

    internal static IReadOnlyList<WorkItem> ScopeExpectedItems(RoleRepairRequest request)
    {
        if (request.ScopeDirection is null) return request.OriginalItems;
        if (string.IsNullOrWhiteSpace(request.ScopeDirection) || request.ScopeAuthorizingTurnId == Guid.Empty ||
            request.ScopeReplacements.Count is < 1 or > 128)
            throw new InvalidOperationException("Scope amendment requires exact replacements and an authorizing conversation turn.");
        var items = request.OriginalItems.ToDictionary(x => x.Id);
        var seen = new HashSet<(Guid, string, string)>();
        foreach (var edit in request.ScopeReplacements)
        {
            if (edit is null || !items.TryGetValue(edit.ItemId, out var item) || item.Status is "Done" or "Completed" or "Cancelled" ||
                item.Planning is not { } planning || string.IsNullOrWhiteSpace(edit.Original) ||
                string.IsNullOrWhiteSpace(edit.Replacement) || edit.Original == edit.Replacement ||
                !seen.Add((edit.ItemId, edit.Field, edit.Original)))
                throw new InvalidOperationException("Scope amendment contains an invalid, duplicate or completed-work replacement.");
            var source = edit.Field switch
            {
                "requirements" => planning.Requirements,
                "acceptanceCriteria" => planning.AcceptanceCriteria,
                "constraints" => planning.Constraints ?? [],
                _ => throw new InvalidOperationException("Scope amendment can only replace requirements, criteria or constraints.")
            };
            // Match against the immutable original, never another replacement in this request.
            var originalPlanning = request.OriginalItems.Single(x => x.Id == edit.ItemId).Planning!;
            var originals = edit.Field switch { "requirements" => originalPlanning.Requirements,
                "acceptanceCriteria" => originalPlanning.AcceptanceCriteria, _ => originalPlanning.Constraints ?? [] };
            if (originals.Count(x => x == edit.Original) != 1 || source.Count(x => x == edit.Original) != 1 ||
                originals.Contains(edit.Replacement, StringComparer.Ordinal))
                throw new InvalidOperationException("Scope amendment does not match one exact original planning entry.");
            var updated = source.Select(x => x == edit.Original ? edit.Replacement : x).ToArray();
            planning = edit.Field switch { "requirements" => planning with { Requirements = updated },
                "acceptanceCriteria" => planning with { AcceptanceCriteria = updated }, _ => planning with { Constraints = updated } };
            items[item.Id] = item with { Planning = planning,
                Description = edit.Field == "requirements" ? (item.Description ?? "").Replace(edit.Original, edit.Replacement, StringComparison.Ordinal) : item.Description };
        }
        return request.OriginalItems.Select(x => items[x.Id]).ToArray();
    }

    internal static WorkItemDeliverySpecification AmendedDelivery(WorkItem item)
    {
        if (item.Planning is not { } planning || item.Delivery is not { } delivery)
            throw new InvalidOperationException("Scope delivery refresh requires finalized canonical planning.");
        return delivery with { Requirements = planning.Requirements, AcceptanceCriteria = planning.AcceptanceCriteria,
            Constraints = planning.Constraints, DependencyItemIds = planning.DependencyItemIds };
    }

    private static async Task RefreshAmendedDeliveriesAsync(RoleRepairRequest? request, Guid boardId,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (request?.ScopeDirection is null) return;
        await RefreshAmendedWorkflowPlanningAsync(request, boardId, context, token);
        var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
        foreach (var item in board.Items.Where(x => x.Status is not ("Done" or "Completed" or "Cancelled") && x.Delivery is not null))
        {
            var updated = AmendedDelivery(item);
            if (JsonSerializer.Serialize(updated) == JsonSerializer.Serialize(item.Delivery))
            {
                ValidateAmendedDevelopment(item, updated);
                continue;
            }
            var owner = item.AccountableOrganizationUserId ?? throw new InvalidOperationException("Amended delivery has no accountable owner.");
            var refreshed = await context.Platform.Work.FinalizeItemDeliveryAsync(new(boardId, item.Id, updated,
                owner, item.StageAssignments, item.Revision, $"producer-scope-finalize:{request.ReviewStageId:N}:{item.Id:N}:{item.PlanningRevision}"), token);
            ValidateAmendedDevelopment(refreshed, updated);
        }
    }

    internal static async Task<int> PublishAmendedPlanningAsync(RoleRepairRequest request, Guid sessionId,
        string proposalDigest, AgentRuntimeContext context, CancellationToken token)
    {
        var changed = request.ScopeReplacements.Select(x => x.ItemId).ToHashSet();
        foreach (var expected in ScopeExpectedItems(request).Where(x => changed.Contains(x.Id) && ProposalKey(x) != null))
        {
            var original = request.OriginalItems.Single(x => x.Id == expected.Id);
            var item = await context.Platform.Work.ReadItemAsync(new(request.BoardId, expected.Id), token);
            // Finalized delivery owns the channel. Keep the ticket's own constraints, stage assignments
            // and criterion evidence instead of republishing a union of the whole board's constraints.
            var planning = expected.Planning! with { DeliveryKind = item.Delivery?.DeliveryKind ?? expected.Planning!.DeliveryKind };
            var provenance = new WorkItemProposalProvenance(sessionId, proposalDigest, ProposalKey(expected)!);
            if (JsonSerializer.Serialize(item.Planning) == JsonSerializer.Serialize(planning) &&
                item.Description == expected.Description && item.ProposalProvenance == provenance) continue;
            if (item.Status is "Done" or "Completed" or "Cancelled" or "InProgress" ||
                JsonSerializer.Serialize(item.Planning) != JsonSerializer.Serialize(original.Planning) ||
                item.Description != original.Description)
                throw new InvalidOperationException("Ticket planning changed since the authorized amendment.");
            await context.Platform.Work.RevisePlanningAsync(new(request.BoardId, item.Id, item.Title,
                expected.Description, item.ParentItemId, planning, item.Revision, item.PlanningRevision,
                BoundedMutationKey($"producer-scope-planning:{request.ScopeAuthorizingTurnId:N}:{item.Id:N}"))
                { ProposalProvenance = provenance }, token);
        }
        return changed.Count;
    }

    internal static async Task RefreshAmendedWorkflowPlanningAsync(RoleRepairRequest request, Guid boardId,
        AgentRuntimeContext context, CancellationToken token)
    {
        var changed = request.ScopeReplacements.Select(x => x.ItemId).ToHashSet();
        var expected = ScopeExpectedItems(request).Where(x => x.TypeKey == "work.workflow-ticket.v1" && changed.Contains(x.Id));
        foreach (var amended in expected)
        {
            var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
            var current = board.Items.Single(x => x.Id == amended.Id);
            if (JsonSerializer.Serialize(current.Planning) == JsonSerializer.Serialize(amended.Planning)) continue;
            var original = request.OriginalItems.Single(x => x.Id == current.Id);
            if (current.Status is "Done" or "Completed" or "Cancelled" or "InProgress" ||
                JsonSerializer.Serialize(current.Planning) != JsonSerializer.Serialize(original.Planning))
                throw new InvalidOperationException("Generated workflow planning changed before applying its exact authorized amendment.");
            await context.Platform.Work.RevisePlanningAsync(new(boardId, current.Id, current.Title,
                amended.Description, current.ParentItemId, amended.Planning!, current.Revision, current.PlanningRevision,
                $"producer-scope-workflow:{request.ReviewStageId:N}:{current.Id:N}"), token);
        }
    }
    internal static void ValidateAmendedDevelopment(WorkItem item, WorkItemDeliverySpecification updated)
    {
        if (item.Development is { } development &&
            (!development.Requirements.SequenceEqual(updated.Requirements) || !development.AcceptanceCriteria.SequenceEqual(updated.AcceptanceCriteria) ||
             !(development.Constraints ?? []).SequenceEqual(updated.Constraints ?? [])))
            throw new InvalidOperationException("A legacy development brief conflicts with amended canonical delivery; reconcile it before execution.");
    }
    internal static void ValidateScopeAmendmentCoverage(RoleRepairRequest request,
        IReadOnlyList<GameProposedWorkItemV1> proposals, IReadOnlyList<string> constraints)
    {
        var expected = ScopeExpectedItems(request).Where(x => x.Status != "Cancelled" && ProposalKey(x) is not null).ToArray();
        if (proposals.Count != expected.Length || proposals.Select(x => x.ProposalKey).Distinct().Count() != proposals.Count)
            throw new InvalidOperationException("Scope amendment must preserve the complete ticket hierarchy without adding or removing work.");
        foreach (var item in expected)
        {
            var proposal = proposals.SingleOrDefault(x => x.ProposalKey == ProposalKey(item));
            var parent = item.ParentItemId is { } id ? ProposalKey(expected.Single(x => x.Id == id)) : null;
            var dependencies = (item.Planning?.DependencyItemIds ?? []).Select(id => ProposalKey(expected.Single(x => x.Id == id))).Order().ToArray();
            if (proposal is null || proposal.WorkItemTypeKey != item.TypeKey || proposal.ParentProposalKey != parent ||
                !proposal.DependencyProposalKeys.Order().SequenceEqual(dependencies) ||
                PrimaryExecutionAssignment(item)?.Requirements?.RequiredRoleKey is { } role && proposal.AccountableRoleKey != role)
                throw new InvalidOperationException("Scope amendment must preserve ticket identity, dependencies and role ownership.");
            if (item.Planning is { } planning &&
                (!planning.AcceptanceCriteria.SequenceEqual(proposal.AcceptanceCriteria) ||
                 planning.Requirements.Any(x => !proposal.Description.Contains(x, StringComparison.Ordinal)) ||
                 (item.Status is not ("Done" or "Completed") && (planning.Constraints ?? []).Any(x => !constraints.Contains(x, StringComparer.Ordinal)))))
                throw new InvalidOperationException($"Scope amendment proposal differs from the exact authorized planning for {item.Identifier ?? ProposalKey(item)}.");
        }
        foreach (var removed in request.ScopeReplacements.Where(x => x.Field == "constraints").Select(x => x.Original).Distinct())
            if (!expected.Any(x => x.Status is not ("Done" or "Completed") && x.Planning?.Constraints?.Contains(removed, StringComparer.Ordinal) == true) && constraints.Contains(removed, StringComparer.Ordinal))
                throw new InvalidOperationException("The corrected proposal reinstates a superseded constraint.");
    }
}

internal sealed record ScopeEditSelection(IReadOnlyList<ScopeEditChoice> Replacements);
internal sealed record ScopeEditChoice(int? Ticket, string Field, int? Entry, string Replacement);

internal sealed record ScopeAmendmentPlan(IReadOnlyList<ScopeTextReplacement> Replacements);
internal sealed record ScopeTextReplacement(Guid ItemId, string Field, string Original, string Replacement);
