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
    internal sealed record ReviewAction(string Kind, string Rationale, string? Capability, IReadOnlyList<int> EvidenceIndexes);
    internal sealed record StoryReviewDraft(Guid SessionId, string ArtifactDigest, GameProposedWorkItemV1 Story, IReadOnlyList<GameProposedWorkItemV1> Tasks)
    {
        internal static StoryReviewDraft FromProposal(string storyKey, Guid sessionId, string digest, GameTechnicalDeliveryProposalV1 proposal) =>
            new(sessionId, digest, proposal.DeliveryItems.Single(i => i.ProposalKey == storyKey && IsScopeContainer(i)),
                proposal.DeliveryItems.Where(i => i.ParentProposalKey == storyKey && !IsScopeContainer(i)).ToArray());
    }
    internal sealed record ReviewChoice(string NextGoal, int? NextCandidate, bool DraftAhead, bool KeepDraft, string RevisitWhen,
        string FeelSummary, IReadOnlyList<ReviewAction> Actions)
    {
        public IReadOnlyList<int>? AdditionalDraftCandidates { get; init; }
        public string? Disposition { get; init; }
        public IReadOnlyList<int>? VerifiedSources { get; init; }
        public string? DraftRevisionReason { get; init; }
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
        public string? Disposition { get; init; }
    }

    // The model can propose, but cannot execute, protected actions. Receipts are read again
    // at the point of use. A pending proposal does not suspend unrelated planning.
    internal async Task<ReviewEffects> ReviewAdaptivePlanAsync(GameProductionPlanningCycleV1 cycle, AgentTeamContext roster,
        ProgressivePlanningProgress progress, IReadOnlyList<GameProposedWorkItemV1> choices, WorkBoardDetail board,
        IReadOnlyList<WorkDeliveryPlanResponse> plans, IReadOnlyList<WorkSprint> sprints,
        ProgressiveEvidence roadmap, ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken token, OwnershipReview? ownership = null,
        StoryReviewDraft? draft = null, int factRefreshes = 0)
    {
        var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: cycle.WorkstreamId), token);
        var resourceContext = await PlanningResourceContext.ReadAsync(context.Platform, cycle.WorkstreamId, cycle.TeamId, token);
        roster = resourceContext.Roster ?? roster with { Members = [], RoleCoverage = [], TotalMemberCount = 0, HasMore = false };
        var roles = CoverageRoleCatalog(roster);
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
        var currentExecution = new List<object>();
        foreach (var active in sprints.Where(s => s.Status == "Active").OrderBy(s => s.Id))
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(cycle.BoardId, active.Id), token);
            foreach (var task in execution?.Items ?? [])
            {
                var stage = task.Stages.SingleOrDefault(s => s.StageKey == task.CurrentStageKey && s.Traversal == task.Traversal);
                var author = task.Stages.LastOrDefault(s => s.StageKey is "specialist-execution" or "development" && s.LatestOutcome is not null);
                currentExecution.Add(new { task.WorkItemId, task.Status, stage?.Id, stage?.StageKey, stage?.LastOutcomeCode,
                    stage?.LastSummary, stage?.LastError, currentOutcome = stage?.LatestOutcome, delivered = author?.LatestOutcome });
                if (stage is not null) facts.Add(new("current-execution", task.WorkItemId,
                    stage.Id.ToString("N") + ":" + stage.Status, JsonSerializer.Serialize(currentExecution.Last())));
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
            modelContract = "current-execution-evidence.v5", resourceContext.Fingerprint, cycle.ApprovedPackageDigest, progress.OutlineDigest, candidateKeys, roster.Revision, ownership,
            material = facts.OrderBy(f => f.Kind).ThenBy(f => f.Id),
            candidateContent = choices, completed = completed.Select(s => s.Id) }));
        if (draft is null && progress.DraftSessionId is Guid retainedId && progress.DraftStoryKey is string retainedStory)
        {
            var retained = await context.Platform.Communication.ReadCoordinationAsync(retainedId, token);
            if (retained.Status == "Completed" && retained.BoardSource?.BoardId == cycle.BoardId &&
                retained.Initiator.OrganizationUserId.ToString() == context.Identity?.EmployeeId &&
                retained.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == retained.Target.OrganizationUserId &&
                    t.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact is { } artifact &&
                ReadProgressiveEvidence(artifact) is { } detail && detail.OutlineDigest == progress.OutlineDigest &&
                detail.DetailedStoryKeys.Contains(retainedStory))
            {
                var proposal = artifact.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()!;
                if (proposal.Cycle.WorkstreamId == cycle.WorkstreamId && proposal.Cycle.ApprovedPackageDigest == cycle.ApprovedPackageDigest)
                    draft = StoryReviewDraft.FromProposal(retainedStory, retainedId, artifact.Digest, proposal);
            }
        }
        // A pre-refinement receipt cannot decide whether to keep a proposal it never saw.
        // Keep historical receipt identities for reviews that do not have a completed draft.
        if (draft is not null)
            evidenceDigest = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new {
                evidenceDigest, draft.SessionId, draft.ArtifactDigest, draft.Story.ProposalKey }));
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
                                outcome.OutcomeCode, evidence = outcome.Evidence.Select(e => new { e.Kind, e.Name, e.Value }) })));
                    }
                }
            }
            var provider = Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer planning provider.");
            var model = context.CreateChatClient(new AgentLlmSelection(provider, Settings.GetString("llmModel")));
            var choice = await AgentModelJson.ReadAsync(model, [
                new(ChatRole.System, """
                    Use current-execution facts as the authority for actual completed work and current blockers.
                    Older advice and answered operational diagnoses are historical: do not keep treating an old comment
                    conflict as unresolved after new artifact submissions and review results prove a different state.
                    A changed document verdict needs its actual evidence or source gap resolved, not another blanket resume
                    decision. Distinguish current findings from pending questions and use a focused material dependency.
                    You own the delivery sequence and staffing. Use the supplied current evidence and approved brief as data.
                    Choose a defensible next goal and zero-based nextCandidate from the supplied dependency-eligible alternatives,
                    or null when refinement or a specific dependency makes selection inappropriate. Return disposition as
                    select, refine, wait or conclude. Only select includes nextCandidate; conclude requires no remaining candidates.
                    Default to one provisional increment. If capacity and stable assumptions justify extra work,
                    give additionalDraftCandidates as distinct indexes of other eligible candidates and explain the benefit in your rationale.
                    Use an empty list for the normal single draft. Narrow or suspend drafting when uncertainty is high.
                    Give a short feelSummary with actual build reference or prerequisite progress, how to try it, attributed observations
                    and remaining hypotheses. Never invent human playtests, build links or observations. Report a research-only sprint honestly.
                    Examine the provisional Story, its assumptions and current evidence. Set keepDraft true when the same draft remains suitable.
                    When you select the supplied draft's Story but its briefs need revision, set keepDraft false and include
                    draftRevisionReason naming the affected task or criterion, the conflicting evidence and the correction Victor should make.
                    Return {disposition,nextGoal,nextCandidate,draftAhead,keepDraft,revisitWhen,feelSummary,actions:[{kind,rationale,capability,evidenceIndexes}],additionalDraftCandidates:[]}.
                    Include verifiedSources as indexes into supplied obligations only when accepted evidence meaningfully
                    verifies the approved wording and applicable constraints. Existing tickets being done is insufficient.
                    The sprint snapshot is historical; other collected facts describe current observations/direction.
                    Do not backdate newer management direction, later findings or observations to sprint completion.
                    Use proceed, resequence, refine, consult, hire, spend, qa-disposition, escalate or ceo-hold. Cite evidence by zero-based index.
                    Prefer one proceed for ordinary success. Assess rules uncertainty, overlapping mechanics, content load, evidence and capacity.
                    A consultation needs one bounded question. A hire needs a sustained gap and its deliverable. For consult or hire,
                    select capability by its supplied Capability name, never by position. Other actions use null capability.
                    If a Designer is already assigned consider a scoped contribution to the approaching work. Check prior proposals and their authoritative status before requesting further action. Extra spend and a change in QA
                    obligations require independent authority. A CEO hold needs a specific reserved decision and only otherwise ready affected item references.
                    A play invitation never holds work. Use proceed for experiments already authorized within delegated resources. Reserve spend for a specific additional cost with its amount, currency and purpose grounded in the supplied evidence. Prioritize repairing actual failures; keep useful draft detail when its assumptions survive.
                    Select drafts for decomposition and estimation independently of permission to execute them. A brief's statement
                    that it grants no staffing/build/spend authority is not a prohibition or a request for another blanket approval.
                    Current staffing and resources are supplied separately: Pending is a proposal, Approved is approval, fulfilled
                    roster membership is actual staffing, and only Ready resources establish verified capacity. Keep real declined,
                    revoked or explicit owner restrictions. Propose only the missing capability and keep unaffected planning moving.
                    """),
                new(ChatRole.User, JsonSerializer.Serialize(new { brief = acceptedBrief.Content, sprint = sprint?.Goal, candidates = choices.Select(c => new { c.Title,c.Description }),
                    obligations=ownership?.Coverage??roadmap.ScopeCoverage, resourceContext,
                    provisionalStory = draft?.Story.ProposalKey, storyDefinition = draft?.Story, provisional = draft?.Tasks,
                    evidence, roles, roster = roster.Members.Select(m => new { m.DisplayName, m.DeclaredRoleKeys,m.SpecializationKeys,m.IsAvailable }) }))
            ], new AgentModelJsonRequest<ReviewChoice>("game planning review") {
                Options = ResponseOptions(), Normalize = value =>
                {
                    ValidateReview(value, choices.Count, roles, evidence, draft is null ? null : Array.IndexOf(candidateKeys, draft.Story.ProposalKey));
                    var obligations=ownership?.Coverage??roadmap.ScopeCoverage;
                    if(value.VerifiedSources?.Any(i=>i<0||i>=obligations.Count)==true)
                        throw new AgentModelJsonException("Verified source indexes must refer to supplied approved obligations.");
                    return value;
                }
            }, token);
            await context.ReportProgressAsync(new { adaptivePlanning = "review", cycle.WorkstreamId, cycle.BoardId,
                sprintId = sprint?.Id, candidates = candidateKeys, choice.NextCandidate, choice.DraftAhead, choice.KeepDraft,
                pending = draft?.Story.ProposalKey, draftSessionId = draft?.SessionId, proposalDigest = draft?.ArtifactDigest, choice.RevisitWhen }, token);
            var used = choice.Actions.SelectMany(a => a.EvidenceIndexes).Distinct().Order().ToArray();
            var compact = used.Select(index => evidence[index]).ToArray();
            var remap = used.Select((index, offset) => (index, offset)).ToDictionary(x => x.index, x => x.offset);
            choice = choice with { Actions = choice.Actions.Select(a => a with {
                EvidenceIndexes = a.EvidenceIndexes.Select(index => remap[index]).ToArray() }).ToArray() };
            var digest = ProducerPolicyFingerprint.Digest(key);
            return (await store.MergeAsync<ReviewReceipt>(key, "game.planning.review.v1", 1, prior => prior ??
                new(digest, choice.NextCandidate is int i ? candidateKeys[i] : null, choice, compact, roles.Select(r => r.RoleKey).ToArray(), DateTimeOffset.UtcNow, evidenceDigest) {
                    Outcomes = OutcomeCoverage(ownership?.Coverage ?? roadmap.ScopeCoverage, board.Items, plans, sprint?.CompletedAt,
                        (choice.VerifiedSources??[]).Select(i=>(ownership?.Coverage??roadmap.ScopeCoverage)[i].SourceKey).ToHashSet()) },
                new Dictionary<string,string>(), "review:" + digest, token)).Payload;
        }

        // A missed sprint notification still produces a review of that sprint's frozen report.
        ReviewReceipt? latest = null;
        foreach (var sprint in completed)
        {
            latest = await Decide($"game-sprint-review-v2:{cycle.WorkstreamId:N}:{sprint.Id:N}", sprint);

            try { await PublishSprintReviewAsync(cycle.WorkstreamId, cycle.BoardId, sprint, latest, context, token); }
            catch (Exception error) when (error is PlatformCapabilityException or HttpRequestException)
            {
                // The retained review is rediscovered by RecoverSprintReviewsAsync. A report transport
                // failure must not prevent current planning effects or grant a production decision.
                await context.ReportProgressAsync(new { reportPending = sprint.Id, reason = error.Message }, token);
            }
        }
        var key = "game-plan-assessment:" + evidenceDigest;
        var current = latest?.Basis == evidenceDigest ? latest : await Decide(key, null);
        if ((await PlanningResourceContext.ReadAsync(context.Platform, cycle.WorkstreamId, cycle.TeamId, token)).Fingerprint != resourceContext.Fingerprint)
        {
            // A legitimate staffing decision during inference invalidates the judgment, not the draft.
            // Refresh once in this callback. Continued change waits for the normal durable planning wake.
            if (factRefreshes == 0)
                return await ReviewAdaptivePlanAsync(cycle, roster, progress, choices,
                    await context.Platform.Work.ReadBoardAsync(cycle.BoardId, token),
                    await context.Platform.Work.ReadDeliveryPlansAsync(new(cycle.WorkstreamId), token),
                    await context.Platform.Work.ListSprintsAsync(cycle.BoardId, token), roadmap, handoff, context, token, ownership, draft, 1);
            return new(null, false, true, "Current staffing or resources changed again. The Producer will reassess current facts on the next planning review; the existing draft is retained.", evidenceDigest) { Disposition = "wait" };
        }
        await ApplyReviewEffectsAsync(current, cycle, roster, handoff, context, token, new(Settings.GetGuid("llmProviderId")!.Value, Settings.GetString("llmModel")));
        return new(current.Story, current.Decision.DraftAhead, current.Decision.KeepDraft, current.Decision.NextGoal + " " +
            string.Join(" ", current.Decision.Actions.Select(a => a.Rationale).Append(current.Decision.DraftRevisionReason)).TrimEnd(), current.Digest)
        {
            AdditionalDrafts = (current.Decision.AdditionalDraftCandidates ?? []).Select(i => candidateKeys[i]).ToArray(),
            Disposition = current.Decision.Disposition
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
        IReadOnlyList<WorkItem> items, IReadOnlyList<WorkDeliveryPlanResponse> plans, DateTimeOffset? cutoff,
        IReadOnlySet<string>? reviewedSources = null)
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
            var verified = reviewedSources?.Contains(source.SourceKey)==true && planned &&
                owners.All(e => stories[e!.Id].Any() && stories[e.Id].All(s => receipts.Contains(s.Id)));
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
            var key = $"game-sprint-review-v2:{state.WorkstreamId:N}:{sprint.Id:N}";
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
            await ReviewAdaptivePlanAsync(plan.Cycle, roster, progress, OwnershipCandidates(SuccessorCandidates(plan.DeliveryItems,
                roadmap.DetailedStoryKeys.ToHashSet()), plan.DeliveryItems, reviewed), board, deliveries, sprints, roadmap, handoff, context, token, reviewed);
            return;
        }
    }

    internal static ReviewChoice ValidateReview(ReviewChoice value, int candidateCount, IReadOnlyList<CoverageRoleChoice> roles, IReadOnlyList<ReviewFact> evidence,
        int? draftCandidate = null)
    {
        if (string.IsNullOrWhiteSpace(value.NextGoal) || string.IsNullOrWhiteSpace(value.RevisitWhen) || string.IsNullOrWhiteSpace(value.FeelSummary) ||
            (value.NextCandidate is int i && (i < 0 || i >= candidateCount)) ||
            value.Disposition is not (null or "select" or "refine" or "wait" or "conclude") ||
            (value.Disposition == "select" && value.NextCandidate is null) ||
            (value.Disposition is "refine" or "wait" or "conclude" && value.NextCandidate is not null) ||
            (value.Disposition == "conclude" && candidateCount > 0) ||
            (value.Disposition is null && candidateCount > 0 && value.NextCandidate is null) ||
            value.AdditionalDraftCandidates is { Count: > 0 } extras && (!value.DraftAhead || extras.Distinct().Count() != extras.Count ||
                extras.Any(i => i < 0 || i >= candidateCount || i == value.NextCandidate)) ||
            value.Actions is not { Count: > 0 } || value.Actions.Any(a => a == null ||
                a.Kind is not ("proceed" or "resequence" or "refine" or "consult" or "hire" or "spend" or "qa-disposition" or "escalate" or "ceo-hold") ||
                string.IsNullOrWhiteSpace(a.Rationale) || a.EvidenceIndexes is not { Count: > 0 } || a.EvidenceIndexes.Any(i => i < 0 || i >= evidence.Count) ||
                ((a.Kind is "hire" or "consult") ? !roles.Any(r => r.Capability == a.Capability) : a.Capability is not null) ||
                a.Kind == "ceo-hold" && !a.EvidenceIndexes.Any(i => evidence[i].Kind == "item") ||
                a.Kind == "qa-disposition" && !a.EvidenceIndexes.Any(i => evidence[i].Kind is "qa" or "finding")))
            throw new AgentModelJsonException("Supply an eligible next candidate, supported actions, named capability choices, valid evidence indexes and an evidence-backed feel summary.");
        if (value.NextCandidate is int selected && selected == draftCandidate && !value.KeepDraft && string.IsNullOrWhiteSpace(value.DraftRevisionReason))
            throw new AgentModelJsonException("Review the supplied task briefs. Keep a suitable draft, or provide draftRevisionReason naming the affected task or criterion, conflicting evidence and needed correction.");
        return value;
    }

    internal static async Task ApplyReviewEffectsAsync(ReviewReceipt receipt, GameProductionPlanningCycleV1 cycle,
        AgentTeamContext roster, ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken token, AgentLlmSelection? resolutionModel = null)
    {
        if (receipt.Decision.Disposition is "refine" or "wait" && !receipt.Decision.Actions.Any(a => a.Kind is "refine" or "consult" or "hire" or "spend" or "qa-disposition" or "escalate" or "ceo-hold"))
        {
            // A wait with no declared dependency has no wake path. Assign diagnosis of the named uncertainty,
            // rather than promising to wait for an event that nobody has been asked to produce.
            receipt = receipt with { Decision = receipt.Decision with { Actions = receipt.Decision.Actions.Append(
                new ReviewAction("refine", receipt.Decision.NextGoal + " " + receipt.Decision.RevisitWhen, null,
                    Enumerable.Range(0, receipt.Evidence.Count).ToArray())).ToArray() } };
        }
        for (int i = 0; i < receipt.Decision.Actions.Count; i++)
        {
            var action = receipt.Decision.Actions[i];
            var key = $"review-action:{receipt.Digest}:{i}";
            if (action.Kind is "proceed" or "resequence") continue;
            var refs = action.EvidenceIndexes.Select(index => receipt.Evidence[index]).ToArray();
            var journal = new RevisionSafeProjectState(context.Platform);
            var recorded = await context.Platform.ReadOperatingStateAsync<ReviewActionReceipt>(key, token);
            var protectedAction = action.Kind is "hire" or "spend" or "ceo-hold" or "escalate" or "qa-disposition";
            var registryKey = "game-review-proposals:" + cycle.WorkstreamId.ToString("N");
            var role = action.Capability is { } capability
                ? CoverageRoleCatalog(receipt.Roles).SingleOrDefault(r => r.Capability == capability && receipt.Roles.Contains(r.RoleKey))?.RoleKey : null;
            if (action.Kind is "hire" or "consult" && role is null)
                throw new AgentModelJsonException("Reassess this action using a named capability from its recorded catalog before applying it.");
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
                    [new("specialist-execution", role!, ["work.execution.run"], null, true, action.Rationale)],
                    context, token, handoff.RevisionDigest);
                if (staffing != null) await Record(staffing.Id, staffing.Status);
            }
            else if (action.Kind is "consult" or "refine")
            {
                var scope = receipt.Story ?? "project";
                var question = new WorkContributionRequest(cycle.WorkstreamId,cycle.BoardId,scope,action.Rationale,
                    [new(handoff.ArtifactId,handoff.AcceptedRevisionId,handoff.RevisionDigest)]);
                var artifact = CollaborationActions.WithDocuments(new(WorkContributions.RequestType,"1.0",key,1,true,
                    JsonSerializer.SerializeToElement(question)),[new(handoff.ArtifactId,handoff.AcceptedRevisionId,handoff.RevisionDigest)]);
                var (member,session) = await StartReviewConsultationAsync(key,action,roster,cycle,artifact,context,resolutionModel,token);
                await TrackedCollaboration.QueueCardAsync(context,session,new("Receive scoped planning advice",action.Rationale) {
                    WorkstreamId=cycle.WorkstreamId,TeamId=cycle.TeamId,BoardId=cycle.BoardId},token);
                await journal.MergeAsync<DesignConsultations>("design-consults:"+cycle.WorkstreamId.ToString("N"),"game.design-consults.v1",1,
                    prior=>new((prior?.Requests??[]).Append(new(session.Id,scope,Guid.Parse(member.EmployeeId),key)).DistinctBy(w=>w.ArtifactKey).ToArray()),
                    new Dictionary<string,string>(),key+":watch",token);
                await Record(session.Id,session.Status);
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
                var affected = refs.Where(f => f.Kind is "item" or "current-execution").Select(f => f.Id).Distinct().Order().ToArray();
                var live = await context.Platform.Work.ReadBoardAsync(cycle.BoardId, token);
                var observations = new List<object>();
                var stopped = new List<(Guid ItemId, WorkStageExecutionResponse Stage)>();
                foreach (var sprint in (await context.Platform.Work.ListSprintsAsync(cycle.BoardId, token)).Where(s => s.Status == "Active"))
                {
                    var execution = await context.Platform.Work.ReadOrchestrationAsync(new(cycle.BoardId, sprint.Id), token);
                    foreach (var item in execution?.Items.Where(i => affected.Length == 0 || affected.Contains(i.WorkItemId)) ?? [])
                    {
                        var stage = item.Stages.SingleOrDefault(s => s.StageKey == item.CurrentStageKey && s.Traversal == item.Traversal);
                        if (stage?.Status == "Blocked" && stage.LatestOutcome is not null) stopped.Add((item.WorkItemId, stage));
                        observations.Add(new { item.WorkItemId, item.Status, stage?.Id, stage?.LastOutcomeCode,
                            stage?.LastSummary, stage?.LastError, stage?.LatestOutcome });
                    }
                }
                var facts = JsonSerializer.SerializeToElement(new { items = live.Items.Where(i => affected.Contains(i.Id))
                    .OrderBy(i => i.Id).Select(i => new { i.Id, i.Status, i.PlanningRevision }), observations });
                var scope = await context.Platform.Artifacts.GetAsync(handoff.ArtifactId, token);
                var accepted = scope.Revisions.Single(r => r.Id == handoff.AcceptedRevisionId && r.ContentSha256 == handoff.RevisionDigest && r.Status == "Accepted");
                var dependency = stopped.Count == 1 ? StoppedWorkDependency(cycle.BoardId, stopped[0].ItemId, stopped[0].Stage)
                    : (Root: AgentOperatingStateKey.Create("planning-dependency", new { cycle.BoardId, affected }), Facts: facts);
                var decision = await WorkRequirementResolution.RequestAsync(cycle.WorkstreamId, "video-game.management-direction.v1", "work-planning",
                    dependency.Root, dependency.Facts,
                    accepted.Content, [new("artifact", handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest,
                        "video-game.production-plan.v1", "Accepted")], context,
                    resolutionModel ?? throw new InvalidOperationException("A configured planning model is required for inferred resolution."), null, token);
                await Record(decision.Id, decision.Status);
            }
        }
    }

    private sealed class PlanningDependencyPendingException(string message) : Exception(message);
    private sealed record ConsultationRecipient(Guid EmployeeId, Guid InstallationId, string Reason);
    private sealed record ConsultationRejection(string Message);

    private static async Task<(AgentTeammate,AgentCoordinationSession)> StartReviewConsultationAsync(string key, ReviewAction action,
        AgentTeamContext roster, GameProductionPlanningCycleV1 cycle, AgentCoordinationArtifactSubmission artifact,
        AgentRuntimeContext context, AgentLlmSelection? selection, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var dispatchKey = attempt == 0 ? key : key + ":corrected";
            var rejected = await context.Platform.ReadOperatingStateAsync<ConsultationRejection>(dispatchKey + ":rejection",token);
            if (rejected is not null)
            {
                if (attempt == 0) continue;
                throw new InvalidOperationException("Consultation remains blocked after one corrected request. The reporting manager must resolve the recorded routing or access failure before retry: " + rejected.Payload.Message);
            }
            var prior = attempt == 0 ? null : await context.Platform.ReadOperatingStateAsync<ConsultationRejection>(key + ":rejection",token);
            if (prior is not null)
                roster = (await PlanningResourceContext.ReadAsync(context.Platform,cycle.WorkstreamId,cycle.TeamId,token)).Roster ?? roster with { Members = [] };
            var member = await ResolveConsultationAsync(dispatchKey,action,roster,context,selection,token,prior?.Payload.Message);
            if (member is null) throw new PlanningDependencyPendingException("The Producer retains this draft and owns finding a qualified consultation recipient for: " + action.Rationale);
            try
            {
                var session = await context.Platform.Communication.StartBoardCoordinationAsync(new(Guid.Parse(member.EmployeeId),
                    cycle.BoardId,"Scoped contribution",action.Rationale,["Answer the scoped question with attributed judgment and evidence needed."],
                    action.Rationale,dispatchKey,artifact),token);
                return (member,session);
            }
            catch (PlatformCapabilityException error) when (error.Retryable != true && error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.ValidationFailed or PlatformCapabilityErrorCode.Conflict)
            {
                await new RevisionSafeProjectState(context.Platform).MergeAsync<ConsultationRejection>(dispatchKey + ":rejection",
                    "planning.consultation-rejection.v1",1,old => old ?? new(error.Message),new Dictionary<string,string>(),dispatchKey + ":rejected",token);
                if (attempt == 1) throw new InvalidOperationException("Consultation was rejected after one correction. The reporting manager owns resolving this failure before retry: " + error.Message,error);
            }
        }
        throw new InvalidOperationException("The reporting manager must resolve the retained consultation rejection before retry.");
    }

    private static async Task<AgentTeammate?> ResolveConsultationAsync(string actionKey, ReviewAction action,
        AgentTeamContext roster, AgentRuntimeContext context, AgentLlmSelection? selection, CancellationToken token, string? previousRejection = null)
    {
        var identity = context.Identity ?? throw new InvalidOperationException("Consultation requires the current employee identity.");
        var candidates = ConsultationRouting.Candidates(roster.Members, identity.EmployeeId);
        var key = actionKey + ":recipient:v1";
        var retained = await context.Platform.ReadOperatingStateAsync<ConsultationRecipient>(key, token);
        if (retained is not null && candidates.SingleOrDefault(p => p.EmployeeId == retained.Payload.EmployeeId.ToString() &&
            p.AgentInstallationId == retained.Payload.InstallationId) is { } existing) return existing;
        if (candidates.Count == 0) return null;
        AgentTeammate? member;
        string reason;
        if (selection is null && action.Kind == "refine")
        {
            // Compatibility for deterministic callers without a configured model.
            member = SelectRoleMember(roster with { Members = candidates }, VideoGameRoleKeys.TechnicalDirector);
            reason = "Available technical refinement contributor.";
        }
        else
        {
            if (selection is null) throw new InvalidOperationException("Consultation routing requires the configured planning model.");
            var choice = await ConsultationRouting.SelectAsync(candidates, identity, action.Rationale, action.Capability,
                context.CreateChatClient(selection), token, previousRejection);
            member = choice.Candidate is int index ? candidates[index] : null;
            reason = choice.Reason;
        }
        if (member is null) return null;
        var recipient = new ConsultationRecipient(Guid.Parse(member.EmployeeId), member.AgentInstallationId!.Value, reason);
        var saved = await new RevisionSafeProjectState(context.Platform).MergeAsync<ConsultationRecipient>(key,
            "planning.consultation-recipient.v1", 1, old => old is not null && candidates.Any(p =>
                p.EmployeeId == old.EmployeeId.ToString() && p.AgentInstallationId == old.InstallationId) ? old : recipient,
            new Dictionary<string,string>(), key + ":" + recipient.InstallationId.ToString("N"), token);
        return candidates.Single(p => p.EmployeeId == saved.Payload.EmployeeId.ToString() && p.AgentInstallationId == saved.Payload.InstallationId);
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
            var currentDecision = action.Kind is "escalate" or "ceo-hold" or "qa-disposition"
                ? DecisionContinuations.Current(action.Id, project, decisions) : null;
            if (action.Kind is "escalate" or "ceo-hold" or "qa-disposition") status = currentDecision?.Status;
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
            facts.Add(new("proposal", currentDecision?.Id ?? action.Id, status ?? "Unavailable", action.Kind + " | " +
                (currentDecision is null ? action.Rationale : currentDecision.Summary + " | " + currentDecision.Rationale)));
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
                    t.Artifact?.Type is "video-game.design.advice.v1" or WorkContributions.AnswerType && t.Artifact.Key == pending.ArtifactKey)?.Artifact is not { } reply) continue;
            WorkContributionAnswer? advice;
            if (reply.Type == WorkContributions.AnswerType)
            {
                var verified = await WorkContributions.ReadAnswerAsync(session.Turns, session.Initiator.OrganizationUserId,
                    pending.SpecialistId, project, board.Board.Id, context.Platform, token);
                if (verified?.Key != pending.ArtifactKey) continue;
                advice = verified.Answer;
            }
            else advice = reply.Payload.Deserialize<WorkContributionAnswer>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (advice is null) continue;
            var detail = $"{session.Target.DisplayName} on {pending.Story}: {advice.Assessment} Proposed change: {advice.ProposedChange}. Evidence needed: {advice.EvidenceNeeded}";
            facts.Add(new("design", session.Id, reply.Digest, detail));
            var owner = board.Items.FirstOrDefault(t => t.ProposalProvenance?.ProposalItemKey == pending.Story) ??
                board.Items.FirstOrDefault(t => t.Kind == "Epic" && pending.Story.StartsWith(t.ProposalProvenance?.ProposalItemKey + ".story-", StringComparison.Ordinal));
            if (owner != null) await WorkContributionLinks.AssociateAsync(new(project, board.Board.Id, owner.Id,
                session.Id, pending.SpecialistId, reply.Key, reply.Digest), context, token);
        }
        return facts;
    }

    private sealed record ReviewReportSent(Guid SprintId);
}
