using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ReviewFact(string Kind, Guid Id, string Revision, string Detail);
    internal sealed record ReviewAction(string Kind, string Rationale, int? RoleIndex, IReadOnlyList<int> EvidenceIndexes);
    internal sealed record ReviewChoice(string NextGoal, int? NextCandidate, bool DraftAhead, bool KeepDraft, string RevisitWhen,
        string FeelSummary, IReadOnlyList<ReviewAction> Actions)
    {
        public IReadOnlyList<int>? AdditionalDraftCandidates { get; init; }
    }
    internal sealed record ReviewReceipt(string Digest, string? Story, ReviewChoice Decision,
        IReadOnlyList<ReviewFact> Evidence, IReadOnlyList<string> Roles, DateTimeOffset ReviewedAt, string Basis)
    {
        public IReadOnlyList<OutcomeVerification> Outcomes { get; init; } = [];
    }
    internal sealed record OutcomeVerification(string Source, bool Planned, bool Verified, IReadOnlyList<Guid> AcceptancePlans);
    internal sealed record ReviewEffects(string? Story, bool DraftAhead, bool KeepDraft, string Guidance, string Basis)
    {
        public IReadOnlyList<string> AdditionalDrafts { get; init; } = [];
    }

    // The model can propose, but cannot execute, protected actions. Receipts are read again
    // at the point of use. A pending proposal does not suspend unrelated planning.
    private async Task<ReviewEffects> ReviewAdaptivePlanAsync(GameProductionPlanningCycleV1 cycle, AgentTeamContext roster,
        ProgressivePlanningProgress progress, IReadOnlyList<GameProposedWorkItemV1> choices, WorkBoardDetail board,
        IReadOnlyList<WorkDeliveryPlanResponse> plans, IReadOnlyList<WorkSprint> sprints,
        ProgressiveEvidence roadmap, ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken token, OwnershipReview? ownership = null)
    {
        var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: cycle.WorkstreamId), token);
        var roles = typeof(VideoGameRoleKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Distinct().Order().ToArray();
        var facts = new List<ReviewFact> { new("brief", handoff.ArtifactId, handoff.RevisionDigest, "Accepted production brief") };
        foreach (var item in board.Items.Where(i => i.ProposalProvenance != null).OrderBy(i => i.Id))
            facts.Add(new(item.ExecutionMode == WorkItemExecutionModes.Executable ? "item" : "container", item.Id,
                item.PlanningRevision.ToString(), item.Title + " | " + item.Status));
        foreach (var plan in plans.OrderBy(p => p.Id))
        {
            if (plan.Status == "Completed") facts.Add(new("acceptance", plan.Id, plan.ScopeRevision.ToString(), plan.Name));
            foreach (var f in plan.Findings.OrderBy(f => f.Id)) facts.Add(new("finding", f.Id, f.CandidateDigest, f.Status + " | " + f.Summary));
            foreach (var execution in plan.Executions ?? [])
            {
                foreach (var stage in execution.Stages.Where(s => s.StageKey == "quality" && s.LastSummary != null))
                    facts.Add(new("qa", stage.Id, execution.Candidate?.Digest ?? "", stage.Status + " | Reviewer " + stage.OrganizationUserId + " | " + stage.LastSummary));
                foreach (var candidate in execution.Candidate?.Repositories ?? [])
                    if (candidate.BuildId is Guid id) facts.Add(new("build", id, candidate.CandidateCommitSha, candidate.SourceBranch));
            }
        }
        foreach (var d in decisions.Where(d => d.WorkstreamId == cycle.WorkstreamId && d.Status == DecisionStatuses.Decided &&
            d.SupersededByDecisionId == null).OrderBy(d => d.Id))
            facts.Add(new("direction", d.Id, d.Revision.ToString(), d.Summary + " | " + d.Rationale));
        facts.AddRange(await RefreshReviewProposalsAsync(cycle.WorkstreamId, decisions, context, token));
        facts.AddRange(await ReadDesignAdviceAsync(cycle.WorkstreamId, board, context, token));
        var candidateKeys = choices.Select(c => c.ProposalKey).ToArray();
        var completed = sprints.Where(s => s.Status == "Completed").OrderBy(s => s.Sequence).ThenBy(s => s.Id).ToArray();
        var evidenceDigest = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new {
            cycle.ApprovedPackageDigest, progress.OutlineDigest, candidateKeys, roster.Revision, ownership,
            material = facts.Where(f => f.Kind is "finding" or "direction" or "design" or "proposal" or "qa" or "acceptance" || f.Kind == "item" && f.Detail.EndsWith(" | Blocked", StringComparison.Ordinal)).OrderBy(f => f.Kind).ThenBy(f => f.Id), completed = completed.Select(s => s.Id) }));
        var store = new RevisionSafeProjectState(context.Platform);
        var brief = await context.Platform.Artifacts.GetAsync(handoff.ArtifactId, token);
        var acceptedBrief = brief.Revisions.Single(r => r.Id == handoff.AcceptedRevisionId && r.ContentSha256 == handoff.RevisionDigest && r.Status == "Accepted");
        async Task<ReviewReceipt> Decide(string key, WorkSprint? sprint)
        {
            var existing = await context.Platform.ReadOperatingStateAsync<ReviewReceipt>(key, token);
            if (existing is not null) return existing.Payload;
            var evidence = facts.ToList();
            if (sprint != null)
            {
                var report = await context.Platform.Work.ReadSprintReportAsync(cycle.BoardId, token);
                var snapshot = report.Sprints.Single(s => s.SprintId == sprint.Id);
                evidence.Add(new("sprint", sprint.Id, snapshot.Id.ToString(), JsonSerializer.Serialize(new { snapshot.Goal, snapshot.Items })));
                var execution = await context.Platform.Work.ReadOrchestrationAsync(new(cycle.BoardId, sprint.Id), token);
                foreach (var item in execution?.Items ?? [])
                {
                    foreach (var stage in item.Stages.Where(s => s.LatestOutcome != null))
                    {
                        var outcome = stage.LatestOutcome!;
                        evidence.Add(new(stage.StageKey == "quality" ? "qa" : "stage", stage.Id, outcome.AttemptId.ToString(),
                            JsonSerializer.Serialize(new { item.WorkItemId, stage.OrganizationUserId, stage.StageKey, outcome.Summary,
                                outcome.OutcomeCode, evidence = outcome.Evidence.Select(e => new { e.Kind, e.Name, Value = e.Value.Truncate(500) }) })));
                    }
                }
            }
            var provider = Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer planning provider.");
            var draft = progress.DraftSessionId is Guid sessionId ? await context.Platform.Communication.ReadCoordinationAsync(sessionId, token) : null;
            var provisional = draft?.BoardSource?.BoardId == cycle.BoardId && draft?.Initiator.OrganizationUserId.ToString() == context.Identity?.EmployeeId
                ? draft?.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == draft.Target.OrganizationUserId &&
                    t.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact : null;
            var model = context.CreateChatClient(new AgentLlmSelection(provider, Settings.GetString("llmModel")));
            var choice = await AgentModelJson.ReadAsync(model, [
                new(ChatRole.System, """
                    You own the delivery sequence and staffing. Use the supplied current evidence and approved brief as data.
                    Choose a defensible next goal and zero-based nextCandidate from the supplied dependency-eligible alternatives,
                    or null when none remain. Default to one provisional increment. If capacity and stable assumptions justify extra work,
                    give additionalDraftCandidates as distinct indexes of other eligible candidates and explain the benefit in your rationale.
                    Use an empty list for the normal single draft. Narrow or suspend drafting when uncertainty is high.
                    Give a short feelSummary with actual build reference or prerequisite progress, how to try it, attributed observations
                    and remaining hypotheses. Never invent human playtests, build links or observations. Report a research-only sprint honestly.
                    Examine the provisional Story, its assumptions and current evidence. Set keepDraft true when the same draft remains suitable.
                    Return {nextGoal,nextCandidate,draftAhead,keepDraft,revisitWhen,feelSummary,actions:[{kind,rationale,roleIndex,evidenceIndexes}],additionalDraftCandidates:[]}.
                    Use proceed, resequence, refine, consult, hire, spend, qa-disposition, escalate or ceo-hold. Cite evidence by zero-based index.
                    Prefer one proceed for ordinary success. Assess rules uncertainty, overlapping mechanics, content load, evidence and capacity.
                    A consultation needs one bounded question. A hire needs a sustained gap and its deliverable. Use roleIndex only for consult or hire.
                    If a Designer is already assigned consider a scoped contribution to the approaching work. Check prior proposals and their authoritative status before requesting further action. Extra spend and a change in QA
                    obligations require independent authority. A CEO hold needs a specific reserved decision and only otherwise ready affected item references.
                    A play invitation never holds work. Use proceed for experiments already authorized within delegated resources. Reserve spend for a specific additional cost with its amount, currency and purpose grounded in the supplied evidence. Prioritize repairing actual failures; keep useful draft detail when its assumptions survive.
                    """),
                new(ChatRole.User, JsonSerializer.Serialize(new { brief = acceptedBrief.Content, sprint = sprint?.Goal, candidates = choices.Select(c => new { c.Title,c.Description }),
                    provisionalStory = progress.DraftStoryKey, provisional = provisional?.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()?.DeliveryItems.Where(x => x.ParentProposalKey == progress.DraftStoryKey), evidence, roles, roster = roster.Members.Select(m => new { m.DisplayName, m.DeclaredRoleKeys,m.SpecializationKeys,m.IsAvailable }) }))
            ], new AgentModelJsonRequest<ReviewChoice>("game planning review") {
                Options = ResponseOptions(), Normalize = value =>
                {
                    ValidateReview(value, choices.Count, roles.Length, evidence);
                    if (value.Actions.Any(a => a.Kind == "consult" && (choices.Count == 0 ||
                        SelectRoleMember(roster, roles[a.RoleIndex!.Value]) == null)))
                        throw new AgentModelJsonException("Scope consultations to an approaching Story and an available assigned specialist. Propose a hire only for a sustained gap.");
                    return value;
                }
            }, token);
            await context.ReportProgressAsync(new { adaptivePlanning = "review", cycle.WorkstreamId, cycle.BoardId,
                sprintId = sprint?.Id, candidates = candidateKeys, choice.NextCandidate, choice.DraftAhead, choice.KeepDraft,
                pending = progress.DraftStoryKey, choice.RevisitWhen }, token);
            var used = choice.Actions.SelectMany(a => a.EvidenceIndexes).Distinct().Order().ToArray();
            var compact = used.Select(index => evidence[index]).ToArray();
            var remap = used.Select((index, offset) => (index, offset)).ToDictionary(x => x.index, x => x.offset);
            choice = choice with { Actions = choice.Actions.Select(a => a with {
                EvidenceIndexes = a.EvidenceIndexes.Select(index => remap[index]).ToArray() }).ToArray() };
            var digest = ProducerPolicyFingerprint.Digest(key);
            return (await store.MergeAsync<ReviewReceipt>(key, "game.planning.review.v1", 1, prior => prior ??
                new(digest, choice.NextCandidate is int i ? candidateKeys[i] : null, choice, compact, roles, DateTimeOffset.UtcNow, evidenceDigest) { Outcomes = OutcomeCoverage(ownership?.Coverage ?? roadmap.ScopeCoverage, board.Items, plans, sprint?.CompletedAt) },
                new Dictionary<string,string>(), "review:" + digest, token)).Payload;
        }

        // A missed sprint notification still produces a review of that sprint's frozen report.
        ReviewReceipt? latest = null;
        foreach (var sprint in completed)
        {
            latest = await Decide($"game-sprint-review:{cycle.WorkstreamId:N}:{sprint.Id:N}", sprint);

            await PublishSprintReviewAsync(cycle.WorkstreamId, cycle.BoardId, sprint, latest, context, token);
        }
        var key = "game-plan-assessment:" + evidenceDigest;
        var current = latest?.Basis == evidenceDigest ? latest : await Decide(key, null);
        await ApplyReviewEffectsAsync(current, cycle, roster, handoff, context, token);
        return new(current.Story, current.Decision.DraftAhead, current.Decision.KeepDraft, current.Decision.NextGoal + " " +
            string.Join(" ", current.Decision.Actions.Select(a => a.Rationale)), current.Digest)
        {
            AdditionalDrafts = (current.Decision.AdditionalDraftCandidates ?? []).Select(i => candidateKeys[i]).ToArray()
        };
    }

    private static async Task PublishSprintReviewAsync(Guid projectId, Guid boardId, WorkSprint sprint, ReviewReceipt latest,
        AgentRuntimeContext context, CancellationToken token)
    {
        var outcomes = latest.Outcomes;
        var coverage = $"{outcomes.Count(o => o.Planned)} covered, {outcomes.Count(o => o.Verified)} verified, {outcomes.Count(o => !o.Verified)} outstanding.";
        var cited = latest.Evidence.Select(e => $"{e.Kind} {e.Id:D} ({e.Revision})").ToArray();
        var report = new ManagementStatusReport(sprint.Id, $"{sprint.Name}: {latest.Decision.FeelSummary} Outcomes: {coverage}",
            [], [latest.Decision.NextGoal], [], [], [], [], [latest.Decision.RevisitWhen], 0.7m, latest.ReviewedAt) {
            WorkstreamId = projectId, ReporterRole = "game-producer", Markdown = string.Join(", ", cited), ConversationTopics = ["game-sprint-review"] };
        var reportKey = $"game-review-sent:{projectId:N}:{sprint.Id:N}";
        if (await context.Platform.ReadOperatingStateAsync<ReviewReportSent>(reportKey, token) == null)
        {
            var manager = Guid.Parse(context.Identity!.ManagerEmployeeId!);
            var session = await context.Platform.Communication.StartBoardCoordinationAsync(new(manager, boardId, "Sprint review for CEO reporting",
                "Retain the Producer's sprint evidence and feel summary for the project report.", ["Record the sprint summary."],
                report.Summary, reportKey, new("video-game.sprint.review.v1", "1.0", reportKey, 1, true, JsonSerializer.SerializeToElement(report))), token);
            if (session.Status != "Completed" || session.BoardSource?.BoardId != boardId ||
                session.Initiator.OrganizationUserId.ToString() != context.Identity.EmployeeId || session.Target.OrganizationUserId != manager ||
                !session.Turns.Any(t => t.SpeakerOrganizationUserId == manager && t.Artifact?.Type == "video-game.sprint.review-received.v1" &&
                    t.Artifact.Key == reportKey && t.Artifact.Payload.TryGetProperty("CycleId", out var cycleId) &&
                    cycleId.TryGetGuid(out var reportedSprint) && reportedSprint == sprint.Id)) return;
            await new RevisionSafeProjectState(context.Platform).MergeAsync<ReviewReportSent>(reportKey, "game.review-reported.v1", 1, _ => new(sprint.Id),
                new Dictionary<string,string>(), reportKey, token);
        }
    }

    internal static IReadOnlyList<OutcomeVerification> OutcomeCoverage(IReadOnlyList<ProgressiveCoverage> sources,
        IReadOnlyList<WorkItem> items, IReadOnlyList<WorkDeliveryPlanResponse> plans, DateTimeOffset? cutoff)
    {
        var epics = items.Where(i => i.Kind == "Epic" && i.Status != "Cancelled" && i.ProposalProvenance != null)
            .ToLookup(i => i.ProposalProvenance!.ProposalItemKey);
        var stories = items.Where(i => i.Kind == "Story" && i.Status != "Cancelled" && i.ProposalProvenance != null)
            .ToLookup(i => i.ParentItemId);
        var receipts = plans.Where(p => p.Status == "Completed" && (!cutoff.HasValue || p.UpdatedAt <= cutoff))
            .SelectMany(p => StoryScopeIds(p).Select(id => (Story: id, Plan: p.Id))).ToLookup(p => p.Story);
        return sources.Select(source =>
        {
            var owners = source.ProposalKeys.Distinct().Select(key => epics[key].SingleOrDefault()).ToArray();
            var planned = owners.Length > 0 && owners.All(e => e != null);
            var verified = planned && owners.All(e => stories[e!.Id].Any() && stories[e.Id].All(s => receipts.Contains(s.Id)));
            var evidence = owners.Where(e => e != null).SelectMany(e => stories[e!.Id])
                .SelectMany(s => receipts[s.Id]).Select(r => r.Plan).Distinct().Order().ToArray();
            return new OutcomeVerification(source.SourceKey, planned, verified, evidence);
        }).ToArray();
    }

    private async Task RecoverSprintReviewsAsync(ProducerPlanningCycleState state, ProducerAcceptedHandoff handoff,
        AgentTeamContext roster, AgentRuntimeContext context, CancellationToken token)
    {
        if (!state.AdaptivePlanning || state.Progressive is not { } progress ||
            (progress.LastPublishedSessionId ?? state.TechnicalDirectorSessionId) is not Guid planningId) return;
        var sprints = await context.Platform.Work.ListSprintsAsync(state.BoardId, token);
        foreach (var sprint in sprints.Where(s => s.Status == "Completed").OrderBy(s => s.Sequence))
        {
            var sent = $"game-review-sent:{state.WorkstreamId:N}:{sprint.Id:N}";
            if (await context.Platform.ReadOperatingStateAsync<ReviewReportSent>(sent, token) != null) continue;
            var key = $"game-sprint-review:{state.WorkstreamId:N}:{sprint.Id:N}";
            var saved = await context.Platform.ReadOperatingStateAsync<ReviewReceipt>(key, token);
            if (saved != null)
            {
                await PublishSprintReviewAsync(state.WorkstreamId, state.BoardId, sprint, saved.Payload, context, token);
                continue;
            }
            var session = await context.Platform.Communication.ReadCoordinationAsync(planningId, token);
            if (session.Status != "Completed" || session.BoardSource?.BoardId != state.BoardId ||
                session.Initiator.OrganizationUserId.ToString() != context.Identity?.EmployeeId ||
                session.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == session.Target.OrganizationUserId &&
                    t.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact is not { } artifact ||
                ReadProgressiveEvidence(artifact) is not { Adaptive: true } roadmap || roadmap.OutlineDigest != progress.OutlineDigest) return;
            var plan = artifact.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()!;
            if (plan.Cycle.WorkstreamId != state.WorkstreamId || plan.Cycle.BoardId != state.BoardId ||
                plan.Cycle.ApprovedPackageId != handoff.PlanningPackageId) return;
            var board = await context.Platform.Work.ReadBoardAsync(state.BoardId, token);
            var deliveries = await context.Platform.Work.ReadDeliveryPlansAsync(new(state.WorkstreamId), token);
            var ownership = await context.Platform.ReadOperatingStateAsync<OwnershipReview>(OwnershipKey(plan.Cycle, roadmap), token);
            if (ownership?.Payload is not { } reviewed) return; // Normal planning records the reviewed owners first.
            await ReviewAdaptivePlanAsync(plan.Cycle, roster, progress, SuccessorCandidates(plan.DeliveryItems,
                roadmap.DetailedStoryKeys.ToHashSet()), board, deliveries, sprints, roadmap, handoff, context, token, reviewed);
            return;
        }
    }

    internal static ReviewChoice ValidateReview(ReviewChoice value, int candidateCount, int roleCount, IReadOnlyList<ReviewFact> evidence)
    {
        if (string.IsNullOrWhiteSpace(value.NextGoal) || string.IsNullOrWhiteSpace(value.RevisitWhen) || string.IsNullOrWhiteSpace(value.FeelSummary) ||
            (candidateCount == 0 ? value.NextCandidate.HasValue : value.NextCandidate is not int i || i < 0 || i >= candidateCount) ||
            value.AdditionalDraftCandidates is { Count: > 0 } extras && (!value.DraftAhead || extras.Distinct().Count() != extras.Count ||
                extras.Any(i => i < 0 || i >= candidateCount || i == value.NextCandidate)) ||
            value.Actions is not { Count: > 0 } || value.Actions.Any(a => a == null ||
                a.Kind is not ("proceed" or "resequence" or "refine" or "consult" or "hire" or "spend" or "qa-disposition" or "escalate" or "ceo-hold") ||
                string.IsNullOrWhiteSpace(a.Rationale) || a.EvidenceIndexes is not { Count: > 0 } || a.EvidenceIndexes.Any(i => i < 0 || i >= evidence.Count) ||
                ((a.Kind is "hire" or "consult") ? a.RoleIndex is not int role || role < 0 || role >= roleCount : a.RoleIndex.HasValue) ||
                a.Kind == "ceo-hold" && !a.EvidenceIndexes.Any(i => evidence[i].Kind == "item") ||
                a.Kind == "qa-disposition" && !a.EvidenceIndexes.Any(i => evidence[i].Kind is "qa" or "finding")))
            throw new AgentModelJsonException("Supply an eligible next candidate, supported actions, valid role/evidence indexes and an evidence-backed feel summary.");
        return value;
    }

    internal static async Task ApplyReviewEffectsAsync(ReviewReceipt receipt, GameProductionPlanningCycleV1 cycle,
        AgentTeamContext roster, ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken token)
    {
        for (int i = 0; i < receipt.Decision.Actions.Count; i++)
        {
            var action = receipt.Decision.Actions[i];
            var key = $"review-action:{receipt.Digest}:{i}";
            if (action.Kind is "proceed" or "resequence" or "refine") continue;
            var refs = action.EvidenceIndexes.Select(index => receipt.Evidence[index]).ToArray();
            var journal = new RevisionSafeProjectState(context.Platform);
            var recorded = await context.Platform.ReadOperatingStateAsync<ReviewActionReceipt>(key, token);
            var protectedAction = action.Kind is "hire" or "spend" or "ceo-hold" or "escalate" or "qa-disposition";
            var registryKey = "game-review-proposals:" + cycle.WorkstreamId.ToString("N");
            var role = action.RoleIndex is int index ? receipt.Roles[index] : null;
            var intent = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new { action.Kind, role, action.Rationale,
                evidence = refs.Where(f => f.Kind != "proposal").Select(f => f.Id).Order() }));
            if (recorded != null && !protectedAction) continue;
            var previous = protectedAction ? await context.Platform.ReadOperatingStateAsync<ReviewProposals>(registryKey, token) : null;
            if (previous?.Payload.Actions.Any(p => p.ProposalKey == key || p.Intent == intent) == true) continue;
            var spend = action.Kind == "spend" ? new ApprovalProposalRequest("game-experiment-spend", action.Rationale,
                JsonSerializer.Serialize(new { cycle.WorkstreamId, cycle.ApprovedPackageDigest, proposal = key, evidence = refs }),
                "Financial", key) : null;
            async Task Record(Guid id, string status)
            {
                var bound = new ReviewActionReceipt(action.Kind, id, status, key) { Intent = intent, Spend = spend, Rationale = action.Rationale };
                if (protectedAction)
                    await journal.MergeAsync<ReviewProposals>(registryKey, "game.review-proposals.v1", 1,
                        old => new((old?.Actions ?? []).Append(bound).DistinctBy(r => r.Id).ToArray()),
                        new Dictionary<string,string>(), key + ":register", token);
                await journal.MergeAsync<ReviewActionReceipt>(key, "game.review-action.v1", 1,
                    old => old ?? bound, new Dictionary<string,string>(), key + ":receipt", token);
            }
            if (action.Kind == "hire")
            {
                var staffing = await ProposeCoverageAsync(cycle.WorkstreamId, cycle.BoardId, roster,
                    [new("specialist-execution", receipt.Roles[action.RoleIndex!.Value], ["work.execution.run"], null, true, action.Rationale)],
                    context, token, handoff.RevisionDigest);
                if (staffing != null) await Record(staffing.Id, staffing.Status);
            }
            else if (action.Kind == "consult" && receipt.Story is string story &&
                SelectRoleMember(roster, receipt.Roles[action.RoleIndex!.Value]) is { } member)
            {
                if (receipt.Roles[action.RoleIndex.Value] == VideoGameRoleKeys.GameDesigner)
                {
                    var question = new GameDesignConsultation(cycle.WorkstreamId, cycle.BoardId, handoff.ArtifactId, handoff.AcceptedRevisionId,
                        handoff.RevisionDigest, story, action.Rationale);
                    var artifact = CollaborationActions.WithDocuments(new("video-game.design.consult.v1", "1.0", key, 1, true, JsonSerializer.SerializeToElement(question)),
                        [new(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)]);
                    var session = await context.Platform.Communication.StartBoardCoordinationAsync(new(Guid.Parse(member.EmployeeId),
                        cycle.BoardId, "Design consultation for " + story, action.Rationale, ["Answer the scoped design question."],
                        action.Rationale, key, artifact), token);
                    var watch = new DesignWatch(session.Id, story, Guid.Parse(member.EmployeeId), key);
                    await new RevisionSafeProjectState(context.Platform).MergeAsync<DesignConsultations>("design-consults:" + cycle.WorkstreamId.ToString("N"),
                        "game.design-consults.v1", 1, prior => new((prior?.Requests ?? []).Append(watch).DistinctBy(w => w.SessionId).ToArray()),
                        new Dictionary<string,string>(), key + ":watch", token);
                    await Record(session.Id, session.Status);
                }
                else
                {
                    var specialist = Guid.Parse(member.EmployeeId);
                    var dispatch = await context.Platform.Communication.SendDirectMessageAsync(specialist,
                        $"Contribution requested for {story} on board {cycle.BoardId:D}: {action.Rationale}", key, token);
                    var watch = new DesignWatch(dispatch.ChatId, story, specialist, key) { ChatTurnId = dispatch.RecipientChatTurnId };
                    if (watch.ChatTurnId != null)
                        await journal.MergeAsync<DesignConsultations>("design-consults:" + cycle.WorkstreamId.ToString("N"),
                            "game.design-consults.v1", 1, prior => new((prior?.Requests ?? []).Append(watch).DistinctBy(w => w.ArtifactKey).ToArray()),
                            new Dictionary<string,string>(), key + ":watch", token);
                    await Record(dispatch.MessageId, "Dispatched");
                }
            }
            else if (action.Kind == "spend")
            {
                var proposal = await context.Platform.ProposeApprovalAsync(spend!, token);
                await Record(proposal.ApprovalId, proposal.Status);
            }
            else if (action.Kind is "ceo-hold" or "qa-disposition" or "escalate")
            {
                // A planning decision cannot grant budget, mark a finding resolved or amend acceptance criteria.
                // Existing spending, scope-amendment and finding-disposition flows retain that authority.
                var question = action.Kind + ": " + action.Rationale;
                var request = PlanningDecisionRequests(cycle.WorkstreamId, cycle.BoardId, cycle.PlanningFingerprint, [question], handoff).Single();
                var decision = await context.Platform.RequestDecisionAsync(request with { IdempotencyKey = key,
                    TypeData = JsonSerializer.SerializeToElement(new { adaptiveReview = receipt.Digest, kind = action.Kind,
                        boardId = cycle.BoardId, itemIds = refs.Where(f => f.Kind == "item").Select(f => f.Id), evidence = refs }) }, token);
                await Record(decision.Id, decision.Status);
            }
        }
    }

    internal sealed record ReviewActionReceipt(string Kind, Guid Id, string Status, string ProposalKey)
    {
        public string? Intent { get; init; }
        public string? Rationale { get; init; }
        public ApprovalProposalRequest? Spend { get; init; }
    }
    internal sealed record ReviewProposals(IReadOnlyList<ReviewActionReceipt> Actions);

    internal static async Task<IReadOnlyList<ReviewFact>> RefreshReviewProposalsAsync(Guid project,
        IReadOnlyList<DecisionRecord> decisions, AgentRuntimeContext context, CancellationToken token)
    {
        var key = "game-review-proposals:" + project.ToString("N");
        var registry = await context.Platform.ReadOperatingStateAsync<ReviewProposals>(key, token);
        var facts = new List<ReviewFact>();
        foreach (var action in registry?.Payload.Actions ?? [])
        {
            string? status = action.Status;
            if (status is not ("Approved" or "Rejected" or "Decided" or "Cancelled" or "Superseded"))
            {
                if (action.Kind == "hire")
                {
                    var response = await context.Platform.ReadResourceChangesAsync(new(RequestId: action.Id), token);
                    status = response.Requests.SingleOrDefault(r => r.Id == action.Id && r.WorkstreamId == project)?.Status;
                }
                else if (action.Spend is { } spend)
                {
                    var response = await context.Platform.ProposeApprovalAsync(spend, token);
                    if (response.ApprovalId == action.Id) status = response.Status;
                }
                else status = decisions.SingleOrDefault(d => d.Id == action.Id && d.WorkstreamId == project)?.Status;
            }
            if (status != null && status != action.Status)
            {
                var journal = new RevisionSafeProjectState(context.Platform);
                await journal.MergeAsync<ReviewProposals>(key, "game.review-proposals.v1", 1,
                    old => new((old?.Actions ?? []).Select(p => p.Id == action.Id && p.Status == action.Status ? p with { Status = status } : p).ToArray()),
                    new Dictionary<string,string>(), action.ProposalKey + ":observed:" + status, token);
            }
            facts.Add(new("proposal", action.Id, status ?? "Unavailable", action.Kind + " | " + action.Rationale));
        }
        return facts;
    }
    internal sealed record DesignWatch(Guid SessionId, string Story, Guid SpecialistId, string ArtifactKey)
    {
        public Guid? ChatTurnId { get; init; }
    }
    internal sealed record DesignConsultations(IReadOnlyList<DesignWatch> Requests);
    private static async Task<IReadOnlyList<ReviewFact>> ReadDesignAdviceAsync(Guid project, WorkBoardDetail board, AgentRuntimeContext context, CancellationToken token)
    {
        var registry = await context.Platform.ReadOperatingStateAsync<DesignConsultations>("design-consults:" + project.ToString("N"), token);
        var facts = new List<ReviewFact>();
        foreach (var pending in registry?.Payload.Requests ?? [])
        {
            if (pending.ChatTurnId is Guid turnId)
            {
                var chat = await context.Platform.Communication.ReadChatAsync(pending.SessionId, token);
                var answer = chat.Messages.LastOrDefault(m => m.ChatTurnId == turnId && m.SenderOrganizationUserId == pending.SpecialistId);
                if (answer != null) facts.Add(new("design", answer.Id, turnId.ToString(), $"{answer.SenderDisplayName} on {pending.Story}: {answer.Content}"));
                continue;
            }
            var session = await context.Platform.Communication.ReadCoordinationAsync(pending.SessionId, token);
            if (session.Status != "Completed" || session.BoardSource?.BoardId != board.Board.Id ||
                session.Target.OrganizationUserId != pending.SpecialistId || session.Initiator.OrganizationUserId != board.Board.ManagerOrganizationUserId ||
                session.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == pending.SpecialistId &&
                    t.Artifact?.Type == "video-game.design.advice.v1" && t.Artifact.Key == pending.ArtifactKey)?.Artifact is not { } reply) continue;
            var advice = reply.Payload.Deserialize<GameDesignAdvice>()!;
            var detail = $"{session.Target.DisplayName} on {pending.Story}: {advice.Assessment} Proposed change: {advice.ProposedChange}. Evidence needed: {advice.EvidenceNeeded}";
            facts.Add(new("design", session.Id, reply.Digest, detail));
            var owner = board.Items.FirstOrDefault(t => t.ProposalProvenance?.ProposalItemKey == pending.Story) ??
                board.Items.FirstOrDefault(t => t.Kind == "Epic" && pending.Story.StartsWith(t.ProposalProvenance?.ProposalItemKey + ".story-", StringComparison.Ordinal));
            if (owner != null) await context.Platform.Work.CommentAsync(new(board.Board.Id, owner.Id, detail, pending.ArtifactKey + ":advice"), token);
        }
        return facts;
    }

    private sealed record ReviewReportSent(Guid SprintId);
}
