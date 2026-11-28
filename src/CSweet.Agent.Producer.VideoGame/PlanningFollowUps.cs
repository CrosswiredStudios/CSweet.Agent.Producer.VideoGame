using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record PlanningFollowUp(string Question, string Check, IReadOnlyList<string> ScopeKeys, IReadOnlyList<string> Evidence);
    private sealed record FollowUpObservation(Guid ItemId, Guid ExecutionId, long Revision, string Text);
    private sealed record FollowUpJudgment(string Verdict, int? EvidenceIndex, string Reason);
    private const string PlanningFollowUpPrefix = "planning-check:";

    private static bool IsPlanningFollowUp(WorkIssueCaseResponse issue) =>
        issue.ObligationKey.StartsWith(PlanningFollowUpPrefix, StringComparison.Ordinal);

    internal static IReadOnlyList<WorkIssueUpdate> PlanFollowUpTracking(IReadOnlyList<PlanningFollowUp> followUps,
        GameTechnicalDeliveryProposalV1 proposal, WorkApprovedSource source, WorkBoardDetail board, WorkIssueDirectory directory)
    {
        var known = proposal.DeliveryItems.Select(item => item.ProposalKey).ToHashSet(StringComparer.Ordinal);
        if (followUps.Any(f => f is null || string.IsNullOrWhiteSpace(f.Question) || string.IsNullOrWhiteSpace(f.Check) ||
            f.ScopeKeys is null || f.ScopeKeys.Any(key => !known.Contains(key)) ||
            f.Evidence is null || f.Evidence.Count == 0 || f.Evidence.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("A planning follow-up must carry its checked work and cited basis from the accepted proposal.");
        var current = directory.Issues.Where(i => IsPlanningFollowUp(i) && i.Source == source)
            .GroupBy(i => i.ObligationKey).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.Episode).First());
        var tracked = followUps.GroupBy(f => PlanningFollowUpPrefix + ProducerPolicyFingerprint.Digest(
            JsonSerializer.Serialize(new { source, question = f.Question.Trim() }))).ToDictionary(g => g.Key, g => g.First());
        var updates = new List<WorkIssueUpdate>();
        foreach (var key in tracked.Keys.Union(current.Keys))
        {
            var previous = current.GetValueOrDefault(key);
            if (previous is { Status: not "Open" }) continue;
            var check = tracked.GetValueOrDefault(key);
            var scope = check?.ScopeKeys ?? previous!.ScopeKeys;
            var detail = check is null ? previous!.Detail : $"Evidence to collect: {check.Check}\n\nReview basis: {string.Join("\n", check.Evidence)}";
            var links = EvidenceWork(board, scope).Select(item => new WorkIssueLink("WorkItem", item.Id)).ToArray();
            if (previous is not null && previous.AccountableEmployeeId == board.Board.ManagerOrganizationUserId &&
                previous.Detail == detail && scope.Order().SequenceEqual(previous.ScopeKeys.Order()) &&
                links.All(previous.Links.Contains)) continue;
            var phase = previous?.Phase == "AwaitingDecision" ? previous.Phase :
                links.Length > 0 || previous?.Links.Any(l => l.Kind == "WorkItem") == true ? "AwaitingEvidence" : "Working";
            var nextAction = previous?.Phase == "AwaitingDecision" || previous?.NextAction == AdditionalEvidenceAction
                ? previous.NextAction : phase == "Working" ? "Identify or commission the evidence-gathering work."
                : "Review the recorded results from the linked work.";
            var statement = previous?.Statement ?? check!.Question;
            updates.Add(new(key, statement, detail, scope)
            {
                IssueId = previous?.Id, ExpectedRevision = previous?.Revision, Phase = phase, Links = links,
                BriefTitle = "Planning check", BriefSummary = statement, NextAction = nextAction
            });
        }
        return updates;
    }

    private static IReadOnlyList<WorkItem> EvidenceWork(WorkBoardDetail board, IReadOnlyList<string> scope, IReadOnlySet<Guid>? linked = null)
    {
        var live = board.Items.Where(i => i.Status is not ("Cancelled" or "Archived")).ToArray();
        var selected = live.Where(i => linked?.Contains(i.Id) == true || i.ProposalProvenance is { } p && scope.Contains(p.ProposalItemKey))
            .Select(i => i.Id).ToHashSet();
        var children = live.ToLookup(i => i.ParentItemId);
        var pending = new Queue<Guid>(selected);
        while (pending.TryDequeue(out var parent))
            foreach (var child in children[parent])
                if (selected.Add(child.Id)) pending.Enqueue(child.Id);
        return live.Where(i => selected.Contains(i.Id)).ToArray();
    }

    private async Task<bool> ReconcilePlanningFollowUpsAsync(GameProductionPlanningCycleV1 cycle, ProducerAcceptedHandoff handoff,
        GameTechnicalDeliveryProposalV1 proposal, IReadOnlyList<PlanningFollowUp> followUps,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var source = new WorkApprovedSource(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest);
        var directory = await context.Platform.Issues.ReadAllAsync(cycle.BoardId, true, ct);
        if (followUps.Count == 0 && !directory.Issues.Any(i => IsPlanningFollowUp(i) && i.Source == source && i.Status == "Open"))
            return false;
        var board = await context.Platform.Work.ReadBoardAsync(cycle.BoardId, ct);
        var changes = PlanFollowUpTracking(followUps, proposal, source, board, directory);
        if (changes.Count > 0)
        {
            await SaveFollowUpsAsync(cycle.BoardId, source, changes, context, ct);
            directory = await context.Platform.Issues.ReadAllAsync(cycle.BoardId, true, ct);
        }
        var open = directory.Issues.Where(i => IsPlanningFollowUp(i) && i.Source == source && i.Status == "Open").ToArray();
        if (open.Length == 0) return false;
        var evidenceWork = open.ToDictionary(i => i.Id, i => EvidenceWork(board, i.ScopeKeys,
            i.Links.Where(l => l.Kind == "WorkItem").Select(l => l.Id).ToHashSet()));
        var results = await ReadFollowUpObservationsAsync(cycle, evidenceWork.Values.SelectMany(x => x).DistinctBy(w => w.Id).ToArray(), context, ct);
        var linkedDecisions = open.Any(i => i.Links.Any(l => l.Kind == "Decision"))
            ? await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: cycle.WorkstreamId), ct) : [];
        var stillOpen = 0;
        foreach (var issue in open)
        {
            var work = evidenceWork[issue.Id];
            var observations = results.Where(x => work.Any(w => w.Id == x.ItemId)).ToArray();
            var judgment = observations.Length == 0 ? null : await AssessFollowUpAsync(issue, observations, context, ct);
            if (judgment?.Verdict == "satisfied" && issue.Links.Where(l => l.Kind == "Decision")
                .All(l => IsSettledFollowUpDecision(l.Id, linkedDecisions)))
            {
                var observed = observations[judgment.EvidenceIndex!.Value];
                await SaveFollowUpsAsync(cycle.BoardId, source, [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) {
                    IssueId = issue.Id, ExpectedRevision = issue.Revision, Phase = "AwaitingReview", Status = "Resolved",
                    NextAction = "The recorded result answers the planning check.",
                    ResolutionEvidence = JsonSerializer.Serialize(new { observed, judgment.Reason })
                }], context, ct);
                continue;
            }
            stillOpen++;
            if (judgment?.Verdict == "conflict" && !issue.Links.Any(l => l.Kind == "Decision"))
            {
                var observed = observations[judgment.EvidenceIndex!.Value];
                var question = $"{issue.Statement}\nRecorded execution evidence: {observed.Text}\nCurrent conflict: {judgment.Reason}";
                var decisions = await EnsurePlanningDecisionsAsync(cycle.WorkstreamId, cycle.BoardId, [question], handoff, context, ct);
                if (CurrentPlanningDecisions([question], decisions, cycle.WorkstreamId, handoff.RevisionDigest).FirstOrDefault() is { } decision)
                    await SaveFollowUpsAsync(cycle.BoardId, source, [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) {
                        IssueId = issue.Id, ExpectedRevision = issue.Revision, Phase = "AwaitingDecision",
                        NextAction = "Incorporate the accountable manager's direction on the observed conflict.",
                        Links = [new("Decision", decision.Id)]
                    }], context, ct);
            }
            else if (issue.Phase != "AwaitingDecision" && issue.NextAction != AdditionalEvidenceAction &&
                (issue.ScopeKeys.Count == 0 || work.Count > 0 && work.All(w => w.Status is "Done" or "Completed")))
            {
                // Use the normal reviewed publication flow to add only the missing evidence work.
                await RefineRemainingApprovedWorkAsync(cycle, handoff, issue.ObligationKey,
                    $"Plan the missing evidence for this open check, within the approved scope: {issue.Statement}\n{issue.Detail}",
                    context, ct, async itemIds =>
                    {
                        await SaveFollowUpsAsync(cycle.BoardId, source, [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) {
                            IssueId = issue.Id, ExpectedRevision = issue.Revision, Phase = "AwaitingEvidence",
                            NextAction = AdditionalEvidenceAction, Links = itemIds.Select(id => new WorkIssueLink("WorkItem", id)).ToArray()
                        }], context, ct);
                    });
            }
        }
        return stillOpen > 0;
    }

    private const string AdditionalEvidenceAction = "Review the results of the added evidence-gathering work.";

    private static async Task<IReadOnlyList<FollowUpObservation>> ReadFollowUpObservationsAsync(GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<WorkItem> work, AgentRuntimeContext context, CancellationToken ct)
    {
        if (work.Count == 0) return [];
        var selected = work.ToDictionary(w => w.Id);
        var results = new List<FollowUpObservation>();
        foreach (var plan in await ReadProjectDeliveryHistoryAsync(cycle.WorkstreamId, context, ct))
        foreach (var execution in plan.Executions.Where(e => e.BoardId == cycle.BoardId && e.ItemId is Guid id &&
            selected.TryGetValue(id, out var item) && plan.Scopes.Any(s => s.ItemId == id && s.PlanningRevision == item.PlanningRevision)))
            AddObservations(execution.ItemId!.Value, execution.Id, execution.Revision, execution.Stages);
        foreach (var sprint in work.Where(w => w.SprintId.HasValue).GroupBy(w => w.SprintId!.Value))
        {
            var current = await context.Platform.Work.ReadOrchestrationAsync(new(cycle.BoardId, sprint.Key), ct);
            if (current is null) continue;
            var currentScope = sprint.Select(w => w.Id).ToHashSet();
            foreach (var item in current.Items.Where(i => currentScope.Contains(i.WorkItemId)))
                AddObservations(item.WorkItemId, item.Id, current.Revision, item.Stages);
        }
        return results.Distinct().OrderBy(x => x.ExecutionId).ThenBy(x => x.Text, StringComparer.Ordinal).ToArray();

        void AddObservations(Guid item, Guid execution, long revision, IReadOnlyList<WorkStageExecutionResponse> stages)
        {
            foreach (var stage in stages.Where(s => s.Status is "Completed" or "Failed" or "Blocked"))
            {
                var summary = stage.LatestOutcome?.Summary ?? stage.LastSummary;
                if (!string.IsNullOrWhiteSpace(summary)) results.Add(new(item, execution, revision, summary));
            }
        }
    }

    private async Task<FollowUpJudgment> AssessFollowUpAsync(WorkIssueCaseResponse issue, IReadOnlyList<FollowUpObservation> observations,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var key = AgentOperatingStateKey.Create("planning-check-evidence:v1", new { issue.Id, issue.Source, issue.Detail, observations });
        return await DurableWorkHandoff.GetOrCreateAsync(key, "planning.check-evidence.v1", async () =>
        {
            using var client = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                throw new InvalidOperationException("Configure the Producer planning provider."), Settings.GetString("llmModel")));
            return await AgentModelJson.ReadAsync(client, [new(ChatRole.System, """
                Judge this planning check from the linked work's recorded outcomes. Use satisfied only when an outcome
                supplies the requested evidence. Use conflict only for an observed incompatibility with the accepted
                basis that needs a decision now. Use insufficient when the evidence is missing, hypothetical, or inconclusive.
                Return verdict, reason and one zero-based evidenceIndex for satisfied or conflict. Use null for insufficient.
                Task status, a promise to check, and proposed changes are not observations.
                """), new(ChatRole.User, JsonSerializer.Serialize(new { issue.Statement, issue.Detail,
                    observations = observations.Select((x, index) => new { index, x.Text }) }))],
                new AgentModelJsonRequest<FollowUpJudgment>("planning follow-up evidence") {
                    CorrectionLimit = 1, Options = ResponseOptions(),
                    Normalize = value => !string.IsNullOrWhiteSpace(value.Reason) &&
                        (value.Verdict == "insufficient" && value.EvidenceIndex is null ||
                            value.Verdict is "satisfied" or "conflict" && value.EvidenceIndex is int index && index >= 0 && index < observations.Count)
                        ? value : throw new AgentModelJsonException("A conclusion must cite one of the supplied observations.")
                }, ct);
        }, context, ct);
    }

    private static bool IsSettledFollowUpDecision(Guid id, IReadOnlyList<DecisionRecord> decisions)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(id))
        {
            var decision = decisions.FirstOrDefault(d => d.Id == id);
            if (decision is { Status: DecisionStatuses.Superseded, SupersededByDecisionId: Guid successor })
                id = successor;
            else return decision is { Status: DecisionStatuses.Decided or "Cancelled" };
        }
        return false;
    }

    private static Task SaveFollowUpsAsync(Guid board, WorkApprovedSource source, IReadOnlyList<WorkIssueUpdate> updates,
        AgentRuntimeContext context, CancellationToken ct) =>
        context.Platform.Issues.ReconcileAsync(new(board, source, updates,
            AgentOperatingStateKey.Create("track-planning-check", new { board, source, updates })), ct);
}
