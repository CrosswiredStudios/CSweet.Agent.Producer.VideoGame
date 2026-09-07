using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent : VideoGameSpecialistAgentBase
{
    private const string VisionBriefArtifactType = "creative-direction.game-vision-brief.v1";
    private const string VisionAcknowledgementArtifactType = "video-game.production.game-vision-acknowledgement.v1";
    public override string AgentId => "com.csweet.video-game-producer";
    private const string PlanningCommitmentPrefix = "producer-planning:";
    private const string EstimationCommitmentPrefix = "producer-estimation:";
    private const string SprintReadinessCommitmentPrefix = "producer-readiness:";
    private const string StaffingGapCommitmentPrefix = "producer-staffing-gap:";
    private static readonly TimeSpan CoordinationReviewDelay = TimeSpan.FromMinutes(15);
    public override string Version => "2.3.2";
    protected override string RoleKey => "game-producer";
    protected override string ArtifactTypeKey => "video-game.production-plan.v1";
    protected override string RolePrompt => "You are the operational lead for one video game team. Own board health, sprint planning, schedule, budget, dependencies, staffing, risks, and attributed portfolio reporting. Convert uncertainty into assigned work or durable decisions.";
    protected override IReadOnlyList<string> RequiredSections => ["Schedule", "Budget", "Dependencies", "Staffing", "Risks", "Status Reporting"];

    public override async Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(
        AgentCoordinationTurnRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        return await RefinePitchAsync(request, context, cancellationToken);
    }

    private async Task<AgentCoordinationTurnResult> AcceptRefinedHandoffAsync(
        AgentCoordinationTurnRequest request, AgentRuntimeContext context,
        CrosswiredStudios.VideoGame.PitchCollaboration.PitchBrief pitchBrief, AgentCoordinationArtifact latest,
        ArtifactDocument refinedDocument, ArtifactRevision refinedRevision,
        ArtifactDocument pitchDocument, ArtifactDocument gddDocument, CancellationToken cancellationToken)
    {
        var brief = pitchBrief.Vision;
        var workstreamId = request.WorkContext!.WorkstreamId;
        var artifactId = brief.HighLevelGddArtifactId!.Value;
        var revisionId = brief.HighLevelGddAcceptedRevisionId!.Value;
        var revision = gddDocument.Revisions.Single(x => x.Id == revisionId);
        // The approved collaborative document contains immutable exact pitch/GDD source appendices.
        // Package the project-owned document rather than mixing intake and project file authorities.
        var packageMembers = new List<ArtifactPackageMember>
        {
            new(refinedDocument.Id, 0, refinedDocument.DocumentType, refinedRevision.Id)
        };
        var planningPackage = await context.Platform.Artifacts.CreatePackageAsync(
            new CreateArtifactPackage("Approved game-production planning inputs",
                "video-game.production-planning-input.v1",
                packageMembers,
                $"producer-handoff-package:{workstreamId:N}:{refinedRevision.ContentSha256}"), cancellationToken);
        planningPackage = await context.Platform.Artifacts.SubmitPackageAsync(planningPackage.Id,
            $"producer-handoff-package-submit:{planningPackage.Id:N}:{planningPackage.Version}", cancellationToken);

        var stateStore = new RevisionSafeProjectState(context.Platform);
        _ = await stateStore.MergeAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"),
            "com.csweet.video-game.producer-operating-state.v1", 1,
            current => (current ?? new ProducerOperatingState()) with
            {
                Phase = ProducerManagementPhase.ProductionPlanAndTeamReadiness,
                AcceptedVisionDigests = new Dictionary<Guid, string>(current?.AcceptedVisionDigests ??
                    new Dictionary<Guid, string>()) { [workstreamId] = brief.AcceptedPitchDigest },
                AcceptedHandoffs = new Dictionary<Guid, ProducerAcceptedHandoff>(current?.AcceptedHandoffs ??
                    new Dictionary<Guid, ProducerAcceptedHandoff>())
                {
                    [workstreamId] = new(workstreamId, refinedDocument.Id, refinedRevision.Id, refinedRevision.ContentSha256,
                        refinedRevision.ContentSha256, request.SessionId, planningPackage.Id, planningPackage.Version,
                        DateTimeOffset.UtcNow)
                },
                PhaseCommitments = ["Maintain the production plan and prepare the team and board from the accepted vision."],
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new Dictionary<string, string>
            {
                [revisionId.ToString("D")] = revision.ContentSha256,
                [request.SessionId.ToString("D")] = latest.Digest
            },
            $"producer-vision-ack:{workstreamId:N}:{refinedRevision.ContentSha256}", cancellationToken);

        var acknowledgement = new GameVisionAcknowledgement(
            brief.AcceptedPitchDigest, true, [], DateTimeOffset.UtcNow)
        {
            HighLevelGddArtifactId = artifactId,
            HighLevelGddAcceptedRevisionId = revisionId,
            HighLevelGddRevisionSha256 = revision.ContentSha256,
            PlanningPackageId = planningPackage.Id,
            PlanningPackageVersion = planningPackage.Version
        };
        return AgentCoordinationTurnResult.Completed(
            "We have refined the pitch into an accepted production brief. I have no remaining planning questions and will now prepare the workload-backed staffing proposal.",
            new AgentCoordinationArtifactSubmission(VisionAcknowledgementArtifactType, "1.0",
                brief.AcceptedPitchDigest, 1, true, JsonSerializer.SerializeToElement(acknowledgement)));
    }

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(
        PersonalTodoItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (item.CorrelationId?.StartsWith(PlanningCommitmentPrefix, StringComparison.Ordinal) == true)
            return await ReconcilePlanningAsync(item, context, cancellationToken);
        if (item.CorrelationId?.StartsWith(EstimationCommitmentPrefix, StringComparison.Ordinal) == true)
            return await ReconcileEstimatesAndQaReadinessAsync(item, context, cancellationToken);
        if (item.CorrelationId?.StartsWith(SprintReadinessCommitmentPrefix, StringComparison.Ordinal) == true)
            return await ReconcileSprintReadinessAsync(item, context, cancellationToken);
        if (item.CorrelationId?.StartsWith(StaffingGapCommitmentPrefix, StringComparison.Ordinal) == true)
        {
            if (item.WorkContext?.BoardId is not { } gapBoard || item.WorkContext.WorkItemId is not { } gapItem)
                return PersonalTodoResult.Blocked("Staffing follow-up requires an authoritative ticket.");
            var ticket = (await context.Platform.Work.ReadBoardAsync(gapBoard, cancellationToken)).Items.Single(x => x.Id == gapItem);
            return ticket.StageAssignments.Count > 0 ? PersonalTodoResult.Completed("The existing ticket now has an eligible owner.")
                : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay), "Waiting for the approved delivery coverage.");
        }
        return PersonalTodoResult.Blocked("This Producer personal commitment has no supported authoritative correlation.");
    }

    public override async Task HandleAttentionReviewAsync(
        AgentAttentionReviewContext review,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var assignments = context.Identity?.ManagedWorkstreams
            .Where(x => !x.EndsAt.HasValue || x.EndsAt > review.OccurredAt)
            .ToList() ?? [];
        if (assignments.Count == 0)
            return;

        var portfolio = await context.Platform.ReadPortfolioAsync(
            new ReadPortfolioRequest(assignments.Select(x => x.WorkstreamId).Distinct().ToList()), cancellationToken);
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: cancellationToken);
        var snapshots = new List<ProducerMetricSnapshot>();
        var phases = new Dictionary<Guid, ProducerManagementPhase>();
        var commitments = new List<string>();
        var risks = new List<string>();

        foreach (var entry in portfolio.Workstreams)
        {
            var workstream = entry.Workstream;
            phases[workstream.Id] = ProducerPhaseResolver.Derive(workstream, entry.Gates);
            var board = boards.SingleOrDefault(x => x.WorkstreamId == workstream.Id && !x.IsArchived);
            if (board is null && entry.ActiveTeam is not null)
            {
                board = await context.Platform.Work.CreateBoardAsync(new CreateWorkBoardRequest(
                    workstream.Name, $"Producer-managed delivery for {workstream.Outcome}",
                    $"producer-board:{workstream.Id:N}")
                {
                    TeamId = entry.ActiveTeam.TeamId,
                    WorkstreamId = workstream.Id,
                    Key = $"VG-{workstream.Id:N}"[..11],
                    ProfileKey = "video-game-production-board.v2"
                }, cancellationToken);
                commitments.Add($"Created the authoritative delivery board for {workstream.Name}.");
            }

            if (board is null)
            {
                risks.Add($"{workstream.Name}: no active team or delivery board.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(workstream.ProfileDefinitionDigest))
            {
                _ = await context.Platform.Work.ConfigureProfileOrchestrationAsync(
                    new ConfigureProfileOrchestrationRequest(workstream.Id, board.Id, board.Revision,
                        workstream.ProfileDefinitionDigest,
                        $"producer-profile:{workstream.Id:N}:{workstream.ProfileDefinitionDigest}"), cancellationToken);
            }

            var accepted = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
                ProjectStateKeys.Portfolio("producer"), cancellationToken);
            if (entry.ActiveTeam is not null &&
                accepted?.Payload.AcceptedHandoffs.TryGetValue(workstream.Id, out var handoff) == true)
            {
                var activeRoster = (await context.Platform.ReadTeamRosterAsync(
                    new TeamRosterV2Request(entry.ActiveTeam.TeamId, workstream.Id, 1, 100), cancellationToken)).Team;
                if (activeRoster is not null)
                    await BindAvailableWorkAsync(board.Id, activeRoster, workstream.ProfileDefinitionDigest ?? string.Empty, context, cancellationToken);
                var fingerprint = ProducerPolicyFingerprint.ForPlanning(
                    workstream.Id, entry.ActiveTeam.Revision, workstream.ProfileDefinitionDigest ?? string.Empty,
                    handoff.HandoffDigest);
                _ = await EnsureCommitmentAsync(
                    $"{PlanningCommitmentPrefix}{workstream.Id:N}:{fingerprint}",
                    "Reconcile scope, technical backlog and delivery staffing",
                    "Reconcile player outcomes with technical feasibility, publish only provenance-bound canonical tickets, and leave unready work in Backlog.",
                    "High",
                    new PersonalTodoWorkContext
                    {
                        WorkstreamId = workstream.Id,
                        TeamId = entry.ActiveTeam.TeamId,
                        BoardId = board.Id,
                        CoordinationSessionId = handoff.CoordinationSessionId,
                        SourceFingerprint = fingerprint
                    },
                    context, cancellationToken);
            }

            var metrics = await context.Platform.Work.ReadFlowMetricsAsync(new ReadWorkFlowMetricsRequest(board.Id)
            {
                TeamId = entry.ActiveTeam?.TeamId,
                WorkstreamId = workstream.Id,
                WindowStart = review.OccurredAt.AddDays(-28),
                WindowEnd = review.OccurredAt,
                CompletedSprintLimit = 6
            }, cancellationToken);
            snapshots.Add(new ProducerMetricSnapshot(workstream.Id, board.Id, metrics.SourceRevision,
                metrics.GeneratedAt, metrics.Team, metrics.Principals, metrics.ConditionCodes));
            risks.AddRange(BuildMetricRisks(workstream.Name, metrics));

            var sprints = await context.Platform.Work.ListSprintsAsync(board.Id, cancellationToken);
            if (!sprints.Any(x => x.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)))
            {
                var planned = sprints.Where(x => x.Status.Equals("Planned", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Sequence).ThenBy(x => x.StartsAt).FirstOrDefault();
                if (planned is not null && planned.CapacityPoints > 0 && (await context.Platform.Work.ReadBoardAsync(board.Id, cancellationToken)).Items.Any(x => x.SprintId == planned.Id))
                {
                    var planningRevision = (await context.Platform.Work.ReadBoardAsync(board.Id, cancellationToken))
                        .Items.Where(x => x.SprintId == planned.Id).Select(x => x.PlanningRevision).DefaultIfEmpty(0).Max();
                    _ = await EnsureCommitmentAsync(
                        $"{SprintReadinessCommitmentPrefix}{planned.Id:N}:{planningRevision}",
                        $"Preflight sprint {planned.Name}",
                        "Verify QA readiness, exact staffing and assignment evidence, capacity, estimates, dependencies, and the approved artifact package before starting execution.",
                        "Urgent",
                        new PersonalTodoWorkContext
                        {
                            WorkstreamId = workstream.Id,
                            TeamId = entry.ActiveTeam?.TeamId,
                            BoardId = board.Id,
                            SprintId = planned.Id,
                            SourceFingerprint = $"{planned.Revision}:{planningRevision}"
                        },
                        context, cancellationToken);
                }
                if (accepted?.Payload.PlanningCycles.TryGetValue(workstream.Id, out var planningCycle) == true &&
                         !string.IsNullOrWhiteSpace(planningCycle.ReconciledDigest) && planningCycle.OutstandingAuthorityQuestions.Count == 0)
                {
                    var detail = await context.Platform.Work.ReadBoardAsync(board.Id, cancellationToken);
                    var candidateExists = detail.Items.Any(x =>
                        x.ExecutionMode == WorkItemExecutionModes.Executable && (x.SprintId is null || sprints.Any(s => s.Id == x.SprintId && s.Status == "Planned")) &&
                        x.StageAssignments.Count == 1 && x.ProposalProvenance is not null);
                    if (candidateExists)
                    {
                        var nextSequence = sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;
                        _ = await EnsureCommitmentAsync(
                            $"{EstimationCommitmentPrefix}{board.Id:N}:{planningCycle.ReconciledDigest}:{nextSequence}",
                            $"Prepare candidate scope for Production Sprint {nextSequence}",
                            "Refresh role-owned estimates and capacity, obtain QA readiness evidence, and pull only a capacity-bounded dependency-consistent scope to Ready.",
                            "Urgent",
                            new PersonalTodoWorkContext
                            {
                                WorkstreamId = workstream.Id,
                                TeamId = entry.ActiveTeam?.TeamId,
                                BoardId = board.Id,
                                SourceFingerprint = planningCycle.ReconciledDigest
                            }, context, cancellationToken);
                    }
                }
            }
        }

        var personalDirectory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        await PrioritizeCommitmentsAsync(personalDirectory, context, cancellationToken);
        personalDirectory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var personalItems = personalDirectory.Boards
            .Where(x => x.OwnerOrganizationUserId == personalDirectory.CurrentOrganizationUserId)
            .SelectMany(x => x.Items).Where(x => x.ArchivedAt is null &&
                x.Status is not PersonalTodoStatuses.Completed).ToList();
        var personalSnapshot = new ProducerCommitmentSnapshot(
            personalItems.Count,
            personalItems.Count(x => x.Status == PersonalTodoStatuses.Ready),
            personalItems.Count(x => x.Status == PersonalTodoStatuses.Running && x.Wait is null),
            personalItems.Count(x => x.Status == PersonalTodoStatuses.Blocked),
            personalItems.Count(x => x.Wait is not null),
            personalItems.Count(x => review.OccurredAt - x.CreatedAt >= TimeSpan.FromDays(7)));

        var prior = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), cancellationToken);
        var weeklyDigestDue = ShouldPublishWeekly(prior?.Payload.LastWeeklyDigestAt, review.OccurredAt);
        var risksChanged = !risks.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
            (prior?.Payload.OpenRisks ?? []).OrderBy(x => x, StringComparer.Ordinal));
        var stateStore = new RevisionSafeProjectState(context.Platform);
        var written = await stateStore.MergeAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"),
            "com.csweet.video-game.producer-operating-state.v1", 1,
            current => new ProducerOperatingState
            {
                Phase = phases.Count == 0 ? ProducerManagementPhase.HandoffAndCharterValidation : phases.Values.Min(),
                WorkstreamPhases = phases,
                AcceptedVisionDigests = current?.AcceptedVisionDigests ?? new Dictionary<Guid, string>(),
                AcceptedHandoffs = current?.AcceptedHandoffs ?? new Dictionary<Guid, ProducerAcceptedHandoff>(),
                PlanningCycles = current?.PlanningCycles ?? new Dictionary<Guid, ProducerPlanningCycleState>(),
                MetricSnapshots = snapshots,
                OpenRisks = risks.Distinct(StringComparer.Ordinal).ToList(),
                PhaseCommitments = commitments,
                DecisionFingerprints = current?.DecisionFingerprints ?? [],
                PersonalCommitments = personalSnapshot,
                LastWeeklyDigestAt = ShouldPublishWeekly(current?.LastWeeklyDigestAt, review.OccurredAt)
                    ? review.OccurredAt : current?.LastWeeklyDigestAt,
                UpdatedAt = review.OccurredAt
            },
            snapshots.ToDictionary(x => x.BoardId.ToString("D"), x => x.SourceRevision),
            $"producer-attention:{review.ReviewId:N}", cancellationToken);
        if (weeklyDigestDue || risksChanged)
            _ = await context.Platform.InvokeAsync<ManagementStatusReport, JsonElement>(
                "platform.management.status-report.v1",
                BuildManagementReport(Guid.NewGuid(), null, written.Payload), cancellationToken);
    }

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (request.Capability != ManagementCapabilities.CheckIn)
            return await base.ExecuteCapabilityCoreAsync(request, context, cancellationToken);
        var checkIn = DeserializePayload<ManagementCheckInRequest>(request.Arguments);
        if (checkIn is null) return AgentWorkResult.Failure("A management check-in request is required.");
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), cancellationToken);
        var payload = state?.Payload ?? new ProducerOperatingState();
        return AgentWorkResult.Success(BuildManagementReport(checkIn.CycleId, checkIn.RequestId, payload));
    }

    public override async Task HandleEventAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (message.EventType is "com.csweet.workforce.changed.v1" or "com.csweet.hiring-recommendation.fulfilled.v1" or
            "com.csweet.workstream.changed.v2" or "com.csweet.work.item.changed.v1")
        {
            await HandleAttentionReviewAsync(new AgentAttentionReviewContext(message.EventId, message.OccurredAt, message.OccurredAt.AddMinutes(5), message.EventType), context, cancellationToken);
            return;
        }
        if (message.EventType != ManagementEvents.ReviewDue && message.EventType != "sprint.completed" &&
            message.EventType != ManagementEvents.ResourceChangeDecided)
            return;
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), cancellationToken);
        if (state is null) return;
        var cycleId = message.EventType == ManagementEvents.ReviewDue
            ? message.Data.Deserialize<ManagementReviewDueEvent>()?.CycleId ?? message.EventId
            : message.EventId;
        _ = await context.Platform.InvokeAsync<ManagementStatusReport, JsonElement>(
            "platform.management.status-report.v1",
            BuildManagementReport(cycleId, null, state.Payload), cancellationToken);
    }

    private static async Task<PersonalTodoItem> EnsureCommitmentAsync(
        string correlationId,
        string title,
        string description,
        string priority,
        PersonalTodoWorkContext workContext,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var directory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var existing = directory.Boards.SelectMany(x => x.Items).FirstOrDefault(x =>
            x.ArchivedAt is null && string.Equals(x.CorrelationId, correlationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            var waiting = existing.Status == PersonalTodoStatuses.Running && existing.Wait is not null;
            if (existing.Status is PersonalTodoStatuses.Backlog or PersonalTodoStatuses.Blocked || waiting)
            {
                try
                {
                    return await context.Platform.PersonalTodo.RequeueAsync(
                        new RequeuePersonalTodoItemRequest(existing.Id, existing.Revision,
                            $"producer-requeue:{existing.Id:N}:{existing.Revision}:{workContext.SourceFingerprint}"), cancellationToken);
                }
                catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.Conflict)
                {
                    return (await context.Platform.PersonalTodo.ListAsync(cancellationToken)).Boards
                        .SelectMany(x => x.Items).Single(x => x.Id == existing.Id);
                }
            }
            return existing;
        }
        return await context.Platform.PersonalTodo.AddAsync(
            new AddPersonalTodoItemRequest(title, description, priority, null,
                $"producer-commitment:{correlationId}", CorrelationId: correlationId)
            { WorkContext = workContext }, cancellationToken);
    }

    private static async Task PrioritizeCommitmentsAsync(
        PersonalTodoDirectory directory,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var board = directory.Boards.SingleOrDefault(x => x.OwnerOrganizationUserId == directory.CurrentOrganizationUserId);
        if (board is null) return;
        var current = board.Items.Where(x => x.ArchivedAt is null && x.Status == PersonalTodoStatuses.Ready)
            .OrderBy(x => x.Rank).ThenBy(x => x.Id).ToList();
        var desired = current.OrderBy(CommitmentPriority).ThenBy(x => x.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        if (current.Select(x => x.Id).SequenceEqual(desired.Select(x => x.Id))) return;
        Guid? beforeId = null;
        foreach (var commitment in desired.AsEnumerable().Reverse())
        {
            var updated = await context.Platform.PersonalTodo.ReorderAsync(
                new ReorderPersonalTodoItemRequest(commitment.Id, beforeId, commitment.Revision,
                    $"producer-priority:{commitment.Id:N}:{beforeId?.ToString("N") ?? "last"}:{board.Revision}"),
                cancellationToken);
            beforeId = updated.Id;
        }
    }

    private static int CommitmentPriority(PersonalTodoItem item)
    {
        var correlation = item.CorrelationId ?? string.Empty;
        if (correlation.StartsWith("producer-blocker:", StringComparison.Ordinal) ||
            correlation.StartsWith(StaffingGapCommitmentPrefix, StringComparison.Ordinal)) return 0;
        if (correlation.StartsWith("producer-gate:", StringComparison.Ordinal)) return 1;
        if (correlation.StartsWith(SprintReadinessCommitmentPrefix, StringComparison.Ordinal)) return 2;
        if (correlation.StartsWith(PlanningCommitmentPrefix, StringComparison.Ordinal) ||
            correlation.StartsWith(EstimationCommitmentPrefix, StringComparison.Ordinal)) return 3;
        if (correlation.StartsWith("producer-report:", StringComparison.Ordinal)) return 4;
        return 5;
    }

    private async Task<PersonalTodoResult> ReconcilePlanningAsync(
        PersonalTodoItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var source = item.WorkContext;
        if (source?.WorkstreamId is not { } workstreamId || source.TeamId is not { } teamId ||
            source.BoardId is not { } boardId || string.IsNullOrWhiteSpace(source.SourceFingerprint))
            return PersonalTodoResult.Blocked("The planning commitment is missing authoritative workstream, team, board, or fingerprint context.");

        var workstream = await context.Platform.ReadWorkstreamAsync(new ReadWorkstreamRequest(workstreamId), cancellationToken);
        var stateStore = new RevisionSafeProjectState(context.Platform);
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(
            ProjectStateKeys.Portfolio("producer"), cancellationToken);
        var operatingState = state?.Payload;
        if (operatingState is null || !operatingState.AcceptedHandoffs.TryGetValue(workstreamId, out var handoff))
            return PersonalTodoResult.Blocked("The exact Creative Director handoff is no longer available.");

        var rosterResponse = await context.Platform.ReadTeamRosterAsync(
            new TeamRosterV2Request(teamId, workstreamId, 1, 100), cancellationToken);
        var roster = rosterResponse.Team;
        if (roster is null || roster.Revision < 1)
            return PersonalTodoResult.Blocked("The active team roster is unavailable.");
        var designer = SelectRoleMember(roster, VideoGameRoleKeys.GameDesigner);
        var technicalDirector = SelectRoleMember(roster, VideoGameRoleKeys.TechnicalDirector);
        await EnsureDraftSprintAsync(boardId, handoff.RevisionDigest, context, cancellationToken);
        if (technicalDirector is null)
        {
            await ProposeCoverageAsync(workstreamId, boardId, roster,
                [TechnicalLeadershipRequirement()], context, cancellationToken);
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "Milestones and a draft sprint are available. Technical decomposition is waiting for the proposed technical lead.");
        }

        operatingState.PlanningCycles.TryGetValue(workstreamId, out var priorCycle);
        ArtifactPackage package;
        if (priorCycle?.ArtifactPackageId is not { } packageId)
        {
            package = await context.Platform.Artifacts.GetPackageAsync(handoff.PlanningPackageId, cancellationToken);
            priorCycle = new ProducerPlanningCycleState(workstreamId, boardId, teamId,
                source.SourceFingerprint, package.Id, null, null, null, null, null, DateTimeOffset.UtcNow);
            await PersistPlanningCycleAsync(stateStore, operatingState, priorCycle, cancellationToken);
        }
        else
            package = await context.Platform.Artifacts.GetPackageAsync(packageId, cancellationToken);

        if (package.Status is not ("Accepted" or "Approved") || package.AcceptedAt is null)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "Waiting for creative authority to approve the exact planning artifact package.");

        var memberDigests = new List<ArtifactPackageMemberDigest>();
        foreach (var member in package.Members)
        {
            var document = await context.Platform.Artifacts.GetAsync(member.ArtifactId, cancellationToken);
            var accepted = document.Revisions.SingleOrDefault(x => x.Id == member.AcceptedRevisionId && x.Status == "Accepted")
                ?? throw new InvalidOperationException("A planning package member no longer identifies an accepted revision.");
            memberDigests.Add(new(document.Id, accepted.Id, member.RequiredDocumentType, accepted.ContentSha256));
        }
        var packageDigest = ArtifactPackageDigestCalculator.Calculate(package.Id, package.Version, memberDigests);
        var cycle = new GameProductionPlanningCycleV1(workstreamId, teamId, roster.Revision, boardId,
            workstream.ProfileDefinitionDigest ?? string.Empty, package.Id, package.Version, packageDigest,
            workstream.LifecycleStage, TargetMilestone(workstream.LifecycleStage), source.SourceFingerprint);

        var technicalSession = await EnsurePlanningSessionAsync(technicalDirector, boardId, cycle,
            "Technical delivery and decomposition proposal",
            "Decompose the accepted brief into a lean complete backlog, including containers, technical discovery, implementation, QA and packaging. Justify specialist roles with actual work; do not require a full studio roster.",
            "video-game.production.technical-delivery-proposal.v1", context, cancellationToken);
        var designerSession = designer is null ? technicalSession : await EnsurePlanningSessionAsync(designer, boardId, cycle,
            "Player-outcome and game-design backlog proposal",
            "Define player outcomes and testable acceptance criteria within the accepted brief. Coordinate technical feasibility separately.",
            "video-game.production.designer-backlog-proposal.v1", context, cancellationToken);
        priorCycle = (priorCycle ?? new ProducerPlanningCycleState(workstreamId, boardId, teamId,
            source.SourceFingerprint, package.Id, null, null, null, null, null, DateTimeOffset.UtcNow)) with
        {
            DesignerSessionId = designerSession.Id,
            TechnicalDirectorSessionId = technicalSession.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await PersistPlanningCycleAsync(stateStore, operatingState, priorCycle, cancellationToken);

        if (designerSession.Status is AgentCoordinationStatuses.Blocked or AgentCoordinationStatuses.Cancelled ||
            technicalSession.Status is AgentCoordinationStatuses.Blocked or AgentCoordinationStatuses.Cancelled)
            return PersonalTodoResult.Blocked("Designer / Technical Director planning reached a terminal conflict requiring Creative Director authority.");
        if (designerSession.Status != AgentCoordinationStatuses.Completed ||
            technicalSession.Status != AgentCoordinationStatuses.Completed)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "Waiting for both role authorities to complete their correlated planning proposals.");

        var designerArtifact = designerSession.Turns.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.designer-backlog-proposal.v1")?.Artifact;
        var technicalArtifact = technicalSession.Turns.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact;
        var technicalProposal = technicalArtifact?.Payload.Deserialize<GameTechnicalDeliveryProposalV1>();
        // An accepted creative brief supplies design constraints when no dedicated designer is justified.
        // All published leaves still have technical-authority provenance and exact role requirements.
        if (designer is null) designerArtifact = technicalArtifact;
        var designerProposal = designer is null
            ? new GameDesignerBacklogProposalV1(cycle, [], ["Preserve the exact accepted creative brief and its non-goals."], [], handoff.RevisionDigest)
            : designerArtifact?.Payload.Deserialize<GameDesignerBacklogProposalV1>();
        if (designerProposal is null || technicalProposal is null ||
            designerProposal.Cycle.PlanningFingerprint != cycle.PlanningFingerprint ||
            technicalProposal.Cycle.PlanningFingerprint != cycle.PlanningFingerprint)
            return PersonalTodoResult.Blocked("The completed planning sessions do not contain current, correlated proposal artifacts.");

        var questions = designerProposal.OpenCreativeDecisions.Concat(technicalProposal.OpenFeasibilityDecisions).ToList();
        if (questions.Count > 0 && Guid.TryParse(context.Identity?.ManagerEmployeeId, out var creativeDirectorId))
            await context.Platform.Communication.SendDirectAgentMessageAsync(creativeDirectorId,
                $"Planning for workstream {workstreamId:D} needs your input: {string.Join("; ", questions)}. The Producer is drafting the backlog; affected scope remains uncommitted.",
                $"producer-planning-questions:{cycle.PlanningFingerprint}", cancellationToken);
        var published = await PublishCanonicalBacklogAsync(boardId, roster, cycle, memberDigests,
            designerSession, designerArtifact!, designerProposal,
            technicalSession, technicalArtifact!, technicalProposal,
            context, cancellationToken);
        var reconciledDigest = ProducerPolicyFingerprint.Digest(string.Join("|",
            designerArtifact!.Digest, technicalArtifact!.Digest, published));
        await PersistPlanningCycleAsync(stateStore, operatingState, priorCycle with
        {
            DesignerProposalDigest = designerArtifact.Digest,
            TechnicalProposalDigest = technicalArtifact.Digest,
            ReconciledDigest = reconciledDigest,
            OutstandingAuthorityQuestions = questions,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        await BindAvailableWorkAsync(boardId, roster, cycle.ProfileDigest, context, cancellationToken);
        var currentBoard = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
        var unassigned = currentBoard.Items.Where(x =>
                x.ExecutionMode == WorkItemExecutionModes.Executable &&
                x.ProposalProvenance is not null && x.StageAssignments.Count == 0)
            .ToList();
        foreach (var gap in unassigned)
        {
            var requiredRole = gap.Planning?.DelegationRecommendations.FirstOrDefault()?.RequiredRoleKey ?? "unknown-role";
            _ = await EnsureCommitmentAsync(
                $"{StaffingGapCommitmentPrefix}{teamId:N}:{requiredRole}:{reconciledDigest}",
                $"Resolve {requiredRole} delivery coverage",
                $"Ticket {gap.Identifier ?? gap.Id.ToString("D")} remains in Backlog until an exact eligible installation satisfies its role, skill, and capability requirements.",
                "High",
                source with { WorkItemId = gap.Id, SourceFingerprint = reconciledDigest },
                context, cancellationToken);
        }
        await ProposeCoverageAsync(workstreamId, boardId, roster,
            unassigned.SelectMany(x => x.Planning?.DelegationRecommendations ?? []).ToList(), context, cancellationToken);
        await EnsureDraftSprintAsync(boardId, reconciledDigest, context, cancellationToken);
        await PopulateDraftScopeAsync(boardId, context, cancellationToken);
        if (questions.Count == 0 && currentBoard.Items.Any(x => x.ExecutionMode == WorkItemExecutionModes.Executable && x.StageAssignments.Count == 1))
        {
            _ = await EnsureCommitmentAsync(
                $"{EstimationCommitmentPrefix}{boardId:N}:{reconciledDigest}:1",
                "Collect estimates and QA sprint-readiness evidence",
                "Collect role-owned estimates and capacity for the reconciled executable scope, obtain QA readiness evidence, pull eligible leaves to Ready, and create the bounded planned sprint.",
                "Urgent",
                source with { SourceFingerprint = reconciledDigest },
                context, cancellationToken);
        }
        return questions.Count == 0
            ? PersonalTodoResult.Completed($"Published {published} canonical Backlog items from the accepted brief and technical planning evidence.")
            : PersonalTodoResult.Blocked("Draft backlog and staffing proposal are recorded. Resolve the escalated authority questions in an updated accepted brief before committing this scope.");
    }

    private static async Task<PersonalTodoResult> ReconcileEstimatesAndQaReadinessAsync(
        PersonalTodoItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var source = item.WorkContext;
        if (source?.WorkstreamId is not { } workstreamId || source.TeamId is not { } teamId ||
            source.BoardId is not { } boardId || string.IsNullOrWhiteSpace(source.SourceFingerprint))
            return PersonalTodoResult.Blocked("The estimation commitment is missing authoritative workstream, team, board, or planning-digest context.");

        var rosterResponse = await context.Platform.ReadTeamRosterAsync(
            new TeamRosterV2Request(teamId, workstreamId, 1, 100), cancellationToken);
        var roster = rosterResponse.Team;
        if (roster is null)
            return PersonalTodoResult.Blocked("The current team roster is unavailable.");
        var workstream = await context.Platform.ReadWorkstreamAsync(new ReadWorkstreamRequest(workstreamId), cancellationToken);
        var planningState = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"), cancellationToken);
        if (planningState?.Payload.PlanningCycles.TryGetValue(workstreamId, out var currentCycle) != true ||
            currentCycle is null || currentCycle.ReconciledDigest != source.SourceFingerprint || currentCycle.OutstandingAuthorityQuestions.Count > 0)
            return PersonalTodoResult.Blocked("The candidate planning evidence changed or has unresolved authority questions.");
        await BindAvailableWorkAsync(boardId, roster, workstream.ProfileDefinitionDigest ?? string.Empty, context, cancellationToken);
        var board = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
        var plannedSprints = (await context.Platform.Work.ListSprintsAsync(boardId, cancellationToken)).Where(x => x.Status == "Planned").Select(x => x.Id).ToHashSet();
        var candidates = board.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Executable &&
                (x.SprintId is null || plannedSprints.Contains(x.SprintId.Value)) && x.ProposalProvenance is not null)
            .OrderBy(x => x.Rank).ThenBy(x => x.Id).ToList();
        if (candidates.Count == 0)
            return PersonalTodoResult.Completed("No unscheduled executable leaves remain in the reconciled candidate scope.");
        candidates = EligibleCandidateScope(candidates, board.Items).ToList();
        if (candidates.Count == 0)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "The draft sprint remains available; no dependency-consistent staffed leaves can be committed yet.");

        var planningRevision = candidates.Max(x => x.PlanningRevision);
        var planningDigest = source.SourceFingerprint;
        var roleProposals = new List<(AgentCoordinationSession Session, AgentCoordinationArtifact Artifact,
            GameRoleEstimateCapacityProposalV1 Proposal)>();
        var pendingRoles = new List<string>();
        foreach (var group in candidates.GroupBy(x => x.StageAssignments[0].Requirements!.RequiredRoleKey,
                     StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var installationId = group.Select(x => x.StageAssignments[0].AgentInstallationId)
                .Distinct().SingleOrDefault();
            var teammate = roster.Members.SingleOrDefault(x => x.AgentInstallationId == installationId);
            if (teammate is null)
                return PersonalTodoResult.Blocked($"The selected {group.Key} installation is no longer on the current roster.");
            var requestFingerprint = ProducerPolicyFingerprint.Digest(string.Join("|", boardId, group.Key,
                planningRevision, planningDigest, string.Join(",", group.Select(x => x.Id).OrderBy(x => x))));
            var estimationCandidates = group.Select(ToSprintCandidate).OrderBy(x => x.WorkItemId).ToList();
            var estimateRequest = new GameRoleEstimateCapacityRequestV1(boardId, group.Key,
                planningRevision, planningDigest, estimationCandidates, requestFingerprint);
            var session = await EnsureTypedBoardSessionAsync(teammate, boardId,
                $"{group.Key} sprint estimate and capacity",
                "Supply estimates, confidence, assumptions, blockers, and available sprint capacity for only the exact accountable-role items.",
                ["Every requested item has a role-owned estimate.", "Planning revision and digest match.",
                    "Capacity and blockers are explicit."],
                "video-game.production.role-estimate-request.v1", requestFingerprint, estimateRequest,
                $"producer-estimation-session:{requestFingerprint}", context, cancellationToken);
            if (session.Status != AgentCoordinationStatuses.Completed)
            {
                pendingRoles.Add($"{group.Key} ({session.Status})");
                continue; // Start every role's estimation and keep independent completed proposals usable.
            }
            var artifact = session.Turns.LastOrDefault(x =>
                x.Artifact?.Type == "video-game.production.role-estimate-capacity-proposal.v1")?.Artifact;
            var proposal = artifact?.Payload.Deserialize<GameRoleEstimateCapacityProposalV1>();
            var expectedIds = group.Select(x => x.Id).OrderBy(x => x).ToList();
            if (artifact is null || proposal is null || proposal.RoleKey != group.Key ||
                proposal.BoardId != boardId || proposal.PlanningRevision != planningRevision ||
                proposal.PlanningDigest != planningDigest ||
                !proposal.Estimates.Select(x => x.WorkItemId).OrderBy(x => x).SequenceEqual(expectedIds) ||
                proposal.Estimates.Any(x => x.EstimatePoints <= 0))
                return PersonalTodoResult.Blocked($"The {group.Key} estimate proposal is stale, incomplete, or not bound to the candidate scope.");
            roleProposals.Add((session, artifact, proposal));
        }

        if (roleProposals.Count == 0)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                $"Role estimation is pending: {string.Join(", ", pendingRoles)}.");
        foreach (var sourceProposal in roleProposals)
        {
            foreach (var estimate in sourceProposal.Proposal.Estimates)
            {
                var current = (await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken)).Items
                    .Single(x => x.Id == estimate.WorkItemId);
                if (current.EstimatePoints == estimate.EstimatePoints && current.EstimateProvenance?.SourceDigest == sourceProposal.Artifact.Digest)
                    continue;
                _ = await context.Platform.Work.EstimateAsync(new EstimateWorkItemRequest(boardId, current.Id,
                    estimate.EstimatePoints, current.Revision,
                    $"producer-estimate:{current.Id:N}:{sourceProposal.Artifact.Digest}")
                {
                    Provenance = new WorkEstimateProvenance(sourceProposal.Session.Target.OrganizationUserId,
                        sourceProposal.Session.Target.AgentInstallationId, sourceProposal.Session.Id,
                        sourceProposal.Session.Revision, sourceProposal.Artifact.Digest,
                        ConfidenceValue(estimate.Confidence))
                }, cancellationToken);
            }
        }

        board = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
        candidates = candidates.Select(candidate => board.Items.Single(x => x.Id == candidate.Id)).ToList();
        var selectedCandidates = SelectCapacityBoundedScope(candidates, roleProposals.Select(x => x.Proposal).ToList());
        if (selectedCandidates.Count == 0)
            return PersonalTodoResult.Blocked("Specialist-supplied capacity cannot accommodate a dependency-consistent executable leaf.");
        var qa = SelectRoleMember(roster, VideoGameRoleKeys.QualityAssurance);
        if (qa is null)
            return PersonalTodoResult.Blocked("QA is required before executable work may move to Ready.");
        var qaFingerprint = ProducerPolicyFingerprint.Digest(string.Join("|", boardId, planningRevision,
            planningDigest, string.Join(",", selectedCandidates.Select(x => x.Id).OrderBy(x => x)),
            string.Join(",", selectedCandidates.Select(x => x.EstimateProvenance?.SourceDigest).OrderBy(x => x))));
        var qaRequest = new GameQaSprintReadinessRequestV1(boardId, planningRevision, planningDigest,
            selectedCandidates.Select(ToSprintCandidate).OrderBy(x => x.WorkItemId).ToList(), qaFingerprint);
        var qaSession = await EnsureTypedBoardSessionAsync(qa, boardId, "QA sprint-readiness assessment",
            "Assess the exact estimated candidate scope for testable requirements, acceptance criteria, dependencies, artifact integrity, assignment coverage, and sprint readiness.",
            ["Every candidate receives an explicit readiness decision.", "Blocking findings remain visible.",
                "The assessment binds the exact planning revision and digest."],
            "video-game.production.qa-readiness-request.v1", qaFingerprint, qaRequest,
            $"producer-qa-readiness:{qaFingerprint}", context, cancellationToken);
        if (qaSession.Status is AgentCoordinationStatuses.Blocked or AgentCoordinationStatuses.Cancelled)
            return PersonalTodoResult.Blocked("QA readiness is blocked and the candidate work remains in Backlog.");
        if (qaSession.Status != AgentCoordinationStatuses.Completed)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "Waiting for QA sprint-readiness evidence.");
        var qaArtifact = qaSession.Turns.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.qa-sprint-readiness-assessment.v1")?.Artifact;
        var assessment = qaArtifact?.Payload.Deserialize<GameQaSprintReadinessAssessmentV1>();
        if (qaArtifact is null || assessment is null || !assessment.Ready ||
            assessment.PlanningRevision != planningRevision || assessment.PlanningDigest != planningDigest ||
            !assessment.ReadyWorkItemIds.OrderBy(x => x).SequenceEqual(selectedCandidates.Select(x => x.Id).OrderBy(x => x)) ||
            assessment.Findings.Any(x => x.Blocking))
            return PersonalTodoResult.Blocked("QA did not approve the exact current candidate scope for Ready.");

        var readyColumn = board.Columns.SingleOrDefault(x => string.Equals(x.Name, "Ready", StringComparison.OrdinalIgnoreCase));
        if (readyColumn is null)
            return PersonalTodoResult.Blocked("The pinned profile does not expose a Ready column.");
        foreach (var candidate in selectedCandidates)
        {
            var current = (await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken)).Items.Single(x => x.Id == candidate.Id);
            if (current.ColumnId != readyColumn.Id)
                current = await context.Platform.Work.MoveItemAsync(new MoveWorkItemRequest(boardId, current.Id,
                    readyColumn.Id, current.Revision, $"producer-ready:{current.Id:N}:{qaArtifact.Digest}"), cancellationToken);
            _ = await context.Platform.Work.CommentAsync(new CommentOnWorkItemRequest(boardId, current.Id,
                "QA approved this exact planning revision for candidate sprint readiness.",
                $"producer-ready-evidence:{current.Id:N}:{qaArtifact.Digest}")
            {
                Kind = "qa-sprint-readiness",
                CoordinationSessionId = qaSession.Id,
                ArtifactDigest = qaArtifact.Digest
            }, cancellationToken);
        }

        var sprints = await context.Platform.Work.ListSprintsAsync(boardId, cancellationToken);
        var sprint = sprints.Where(x => x.Status == "Planned").OrderBy(x => x.Sequence).FirstOrDefault();
        if (sprint is null)
        {
            var sequence = sprints.Select(x => x.Sequence ?? 0).DefaultIfEmpty().Max() + 1;
            sprint = await context.Platform.Work.CreateSprintAsync(new CreateWorkSprintRequest(boardId,
                $"Production Sprint {sequence}", $"Deliver reconciled planning package {planningDigest}.",
                DateTimeOffset.UtcNow.Date, DateTimeOffset.UtcNow.Date.AddDays(14),
                $"producer-sprint:{boardId:N}:{planningDigest}") { Sequence = sequence }, cancellationToken);
        }
        // Tentative draft tickets that cannot be committed return to the unscheduled backlog.
        foreach (var deferred in board.Items.Where(x => x.SprintId == sprint.Id && !selectedCandidates.Any(s => s.Id == x.Id)))
            await context.Platform.Work.SetItemSprintAsync(new SetWorkItemSprintRequest(boardId, deferred.Id, null,
                deferred.Revision, $"producer-defer-draft:{sprint.Id:N}:{deferred.Id:N}:{qaArtifact.Digest}"), cancellationToken);
        var totalCapacity = roleProposals.Sum(x => x.Proposal.AvailableSprintCapacity);
        if (sprint.CapacityPoints != totalCapacity)
            sprint = await context.Platform.Work.SetSprintCapacityAsync(new SetWorkSprintCapacityRequest(boardId,
                sprint.Id, totalCapacity, sprint.Revision, $"producer-capacity:{sprint.Id:N}:{planningDigest}"), cancellationToken);
        foreach (var candidate in selectedCandidates)
        {
            var current = (await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken)).Items.Single(x => x.Id == candidate.Id);
            if (current.SprintId is null)
                _ = await context.Platform.Work.SetItemSprintAsync(new SetWorkItemSprintRequest(boardId,
                    current.Id, sprint.Id, current.Revision, $"producer-scope:{sprint.Id:N}:{current.Id:N}:{planningDigest}"), cancellationToken);
        }
        _ = await EnsureCommitmentAsync(
            $"{SprintReadinessCommitmentPrefix}{sprint.Id:N}:{planningRevision}",
            $"Preflight sprint {sprint.Name}",
            "Revalidate QA evidence, exact staffing and assignment evidence, estimates, capacity, dependencies, and artifact package before starting execution.",
            "Urgent", source with { SprintId = sprint.Id, CoordinationSessionId = qaSession.Id,
                SourceFingerprint = $"{planningRevision}:{qaArtifact.Digest}" }, context, cancellationToken);
        return PersonalTodoResult.Completed($"Recorded specialist estimates, QA readiness, and planned {sprint.Name} with {selectedCandidates.Count} capacity-bounded executable leaves and {totalCapacity} capacity points.");
    }

    private static async Task<PersonalTodoResult> ReconcileSprintReadinessAsync(
        PersonalTodoItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (item.WorkContext?.BoardId is not { } boardId || item.WorkContext.SprintId is not { } sprintId)
            return PersonalTodoResult.Blocked("The sprint-readiness commitment is missing board or sprint context.");
        var sprint = (await context.Platform.Work.ListSprintsAsync(boardId, cancellationToken))
            .SingleOrDefault(x => x.Id == sprintId);
        if (sprint is null) return PersonalTodoResult.Blocked("The authoritative sprint no longer exists.");
        if (!string.Equals(sprint.Status, "Planned", StringComparison.OrdinalIgnoreCase))
            return PersonalTodoResult.Completed($"Sprint {sprint.Name} is already {sprint.Status}.");
        if (!(sprint.CapacityPoints > 0))
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay), "This sprint is still a provisional draft awaiting estimates and QA readiness.");
        var start = new StartWorkSprintExecutionRequest(boardId, sprintId, sprint.Revision,
            $"producer-readiness:{sprintId:N}:{sprint.Revision}");
        var preflight = await context.Platform.Work.PreflightSprintAsync(start, cancellationToken);
        if (!preflight.IsValid)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),
                "Sprint preflight is waiting: " + string.Join("; ", preflight.Errors.Select(x => x.Message)));
        _ = await context.Platform.Work.StartSprintExecutionAsync(start, cancellationToken);
        return PersonalTodoResult.Completed($"Started sprint {sprint.Name} after authoritative preflight passed.");
    }

    private static AgentTeammate? SelectRoleMember(AgentTeamContext roster, string roleKey) =>
        roster.Members.Where(x => x.IsAvailable && x.AgentInstallationId.HasValue &&
                string.Equals(x.RuntimeEligibility, "Eligible", StringComparison.OrdinalIgnoreCase) &&
                x.DeclaredRoleKeys.Contains(roleKey, StringComparer.Ordinal))
            .OrderBy(x => x.AgentInstallationId).FirstOrDefault();

    private static async Task<AgentCoordinationSession> EnsurePlanningSessionAsync(
        AgentTeammate teammate,
        Guid boardId,
        GameProductionPlanningCycleV1 cycle,
        string subject,
        string objective,
        string expectedArtifactType,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(teammate.EmployeeId, out var targetUserId))
            throw new InvalidOperationException($"{subject} target has no authoritative organization-user identity.");
        var message = $"Planning cycle {cycle.PlanningFingerprint}. {objective} Return final artifact type {expectedArtifactType}.";
        _ = await context.Platform.Communication.SendDirectAgentMessageAsync(targetUserId, message,
            $"producer-planning-kickoff:{cycle.PlanningFingerprint}:{targetUserId:N}", cancellationToken);
        return await context.Platform.Communication.StartBoardCoordinationAsync(
            new StartBoardCoordinationRequest(targetUserId, boardId, subject, objective,
                ["Proposal binds the exact planning cycle.", "Every leaf has testable acceptance criteria and one accountable role.",
                    "Required and preferred skills are explicit; estimates are not invented."],
                message, $"producer-planning-session:{cycle.PlanningFingerprint}:{targetUserId:N}",
                new AgentCoordinationArtifactSubmission("video-game.production.planning-cycle.v1", "1.0",
                    cycle.PlanningFingerprint, 1, true, JsonSerializer.SerializeToElement(cycle))), cancellationToken);
    }

    private static async Task<AgentCoordinationSession> EnsureTypedBoardSessionAsync<T>(
        AgentTeammate teammate,
        Guid boardId,
        string subject,
        string objective,
        IReadOnlyList<string> successCriteria,
        string artifactType,
        string artifactKey,
        T payload,
        string idempotencyKey,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(teammate.EmployeeId, out var targetUserId))
            throw new InvalidOperationException($"{subject} target has no authoritative organization-user identity.");
        return await context.Platform.Communication.StartBoardCoordinationAsync(
            new StartBoardCoordinationRequest(targetUserId, boardId, subject, objective, successCriteria,
                objective, idempotencyKey,
                new AgentCoordinationArtifactSubmission(artifactType, "1.0", artifactKey, 1, true,
                    JsonSerializer.SerializeToElement(payload))), cancellationToken);
    }

    private static decimal ConfidenceValue(string confidence) => confidence.Trim().ToLowerInvariant() switch
    {
        "high" => 0.9m,
        "medium" => 0.7m,
        "low" => 0.4m,
        _ => 0.5m
    };

    private static GameSprintCandidateV1 ToSprintCandidate(WorkItem item)
    {
        var assignment = item.StageAssignments.Single();
        return new GameSprintCandidateV1(item.Id, item.Title,
            assignment.Requirements!.RequiredRoleKey,
            item.Planning?.Requirements ?? [], item.Planning?.AcceptanceCriteria ?? [],
            item.Planning?.Constraints ?? [], item.Planning?.DependencyItemIds ?? [],
            item.Planning?.ArtifactPackageDigest?.Sha256 ?? string.Empty,
            assignment.SelectionEvidence!.DecisionFingerprint,
            item.EstimatePoints, item.EstimateProvenance?.SourceDigest);
    }

    private static IReadOnlyList<WorkItem> SelectCapacityBoundedScope(
        IReadOnlyList<WorkItem> candidates,
        IReadOnlyList<GameRoleEstimateCapacityProposalV1> proposals)
    {
        var capacity = proposals.ToDictionary(x => x.RoleKey, x => x.AvailableSprintCapacity,
            StringComparer.Ordinal);
        var used = capacity.Keys.ToDictionary(x => x, _ => 0m, StringComparer.Ordinal);
        var candidateIds = candidates.Select(x => x.Id).ToHashSet();
        var selected = new List<WorkItem>();
        var selectedIds = new HashSet<Guid>();
        var remaining = candidates.OrderBy(x => x.Rank).ThenBy(x => x.Id).ToList();
        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var candidate in remaining.ToList())
            {
                if (candidate.Planning?.DependencyItemIds.Any(id => candidateIds.Contains(id) &&
                        !selectedIds.Contains(id)) == true)
                    continue;
                var role = candidate.StageAssignments.Single().Requirements!.RequiredRoleKey;
                var estimate = candidate.EstimatePoints ?? 0;
                if (estimate <= 0 || !capacity.TryGetValue(role, out var available) || used[role] + estimate > available)
                {
                    remaining.Remove(candidate);
                    continue;
                }
                selected.Add(candidate);
                selectedIds.Add(candidate.Id);
                used[role] += estimate;
                remaining.Remove(candidate);
                progressed = true;
            }
            if (!progressed) break;
        }
        return selected;
    }

    private static async Task PersistPlanningCycleAsync(
        RevisionSafeProjectState store,
        ProducerOperatingState current,
        ProducerPlanningCycleState cycle,
        CancellationToken cancellationToken)
    {
        await store.MergeAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"),
            "com.csweet.video-game.producer-operating-state.v1", 1,
            latest => (latest ?? current) with
            {
                PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState>(latest?.PlanningCycles ??
                    current.PlanningCycles) { [cycle.WorkstreamId] = cycle },
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new Dictionary<string, string> { [cycle.WorkstreamId.ToString("D")] = cycle.PlanningFingerprint },
            $"producer-planning-state:{cycle.WorkstreamId:N}:{cycle.PlanningFingerprint}:{cycle.UpdatedAt.UtcTicks}",
            cancellationToken);
    }

    private static async Task<int> PublishCanonicalBacklogAsync(
        Guid boardId,
        AgentTeamContext roster,
        GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<ArtifactPackageMemberDigest> memberDigests,
        AgentCoordinationSession designerSession,
        AgentCoordinationArtifact designerArtifact,
        GameDesignerBacklogProposalV1 designer,
        AgentCoordinationSession technicalSession,
        AgentCoordinationArtifact technicalArtifact,
        GameTechnicalDeliveryProposalV1 technical,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var proposals = designer.PlayerOutcomes.Concat(technical.DeliveryItems)
            .GroupBy(x => x.ProposalKey, StringComparer.Ordinal)
            .Select(group => group.Last()).ToList();
        if (proposals.Count == 0 || proposals.Any(x => string.IsNullOrWhiteSpace(x.ProposalKey) ||
                string.IsNullOrWhiteSpace(x.Title) || x.AcceptanceCriteria.Count == 0))
            throw new InvalidOperationException("Reconciled planning must contain substantive, testable work proposals.");
        var known = proposals.Select(x => x.ProposalKey).ToHashSet(StringComparer.Ordinal);
        if (proposals.Any(x => x.DependencyProposalKeys.Any(key => !known.Contains(key)) ||
                               (x.ParentProposalKey is not null && !known.Contains(x.ParentProposalKey))))
            throw new InvalidOperationException("Reconciled planning contains unresolved dependency or parent keys.");

        var board = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
        var byKey = board.Items.Select(x => (Key: ExtractKey(x.Title), Item: x))
            .Where(x => x.Key is not null).ToDictionary(x => x.Key!, x => x.Item, StringComparer.Ordinal);
        var remaining = proposals.Where(x => !byKey.ContainsKey(x.ProposalKey)).ToList();
        var package = new ArtifactPackageDigest(cycle.ApprovedPackageId, cycle.ApprovedPackageVersion,
            cycle.ApprovedPackageDigest, DateTimeOffset.UtcNow, memberDigests);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(x => x.DependencyProposalKeys.All(byKey.ContainsKey) &&
                                              (x.ParentProposalKey is null || byKey.ContainsKey(x.ParentProposalKey)))
                .OrderBy(x => x.ProposalKey, StringComparer.Ordinal).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Reconciled planning contains a cycle.");
            foreach (var proposal in ready)
            {
                var executable = proposal.WorkItemTypeKey is VideoGameWorkItemTypeKeys.Task or
                    VideoGameWorkItemTypeKeys.Bug or VideoGameWorkItemTypeKeys.ResearchSpike or
                    VideoGameWorkItemTypeKeys.CreativeReview;
                IReadOnlyList<WorkStageAssignment> assignments = [];
                Guid? accountable = null;
                if (executable)
                {
                    var requirements = new WorkAssignmentRequirements(proposal.AccountableRoleKey,
                        proposal.RequiredSpecializationKeys, proposal.PreferredSpecializationKeys,
                        proposal.RequiredCapabilityKeys.Append("work.execution.run.v1").Distinct(StringComparer.Ordinal).ToList());
                    var selected = RoleTaxonomy.SelectAssignment(roster.Members, requirements);
                    if (selected is not null && Guid.TryParse(selected.Teammate.EmployeeId, out var userId))
                    {
                        accountable = userId;
                        var matched = requirements.RequiredSpecializationKeys.Concat(
                                requirements.PreferredSpecializationKeys.Where(key =>
                                    selected.Teammate.SpecializationKeys.Contains(key, StringComparer.Ordinal)))
                            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                        var decision = ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new
                        {
                            proposal.ProposalKey, selected.Teammate.AgentInstallationId, roster.Revision,
                            cycle.ProfileDigest, requirements, matched
                        }));
                        assignments = [new WorkStageAssignment("specialist-execution", "AgentInstallation", userId,
                            selected.Teammate.AgentInstallationId)
                        {
                            Requirements = requirements,
                            SelectionEvidence = new WorkAssignmentSelectionEvidence(
                                selected.Teammate.AgentInstallationId!.Value, roster.Revision, cycle.ProfileDigest,
                                matched, decision, DateTimeOffset.UtcNow)
                        }];
                    }
                }
                var sourceIsTechnical = technical.DeliveryItems.Any(x => x.ProposalKey == proposal.ProposalKey);
                var sourceSession = sourceIsTechnical ? technicalSession : designerSession;
                var sourceArtifact = sourceIsTechnical ? technicalArtifact : designerArtifact;
                var item = await context.Platform.Work.CreateItemAsync(new CreateWorkItemRequest(
                    boardId, $"[{proposal.ProposalKey}] {proposal.Title}", proposal.Description,
                    KindFor(proposal.WorkItemTypeKey), WorkPriorities.High, null,
                    proposal.ParentProposalKey is null ? null : byKey[proposal.ParentProposalKey].Id,
                    null, $"producer-ticket:{cycle.PlanningFingerprint}:{proposal.ProposalKey}")
                {
                    TypeKey = proposal.WorkItemTypeKey,
                    AccountableOrganizationUserId = accountable,
                    Planning = new WorkItemPlanningSpecification(
                        [proposal.Description], proposal.AcceptanceCriteria,
                        designer.DesignConstraints.Concat(technical.TechnicalConstraints).Distinct().ToList())
                    {
                        DependencyItemIds = proposal.DependencyProposalKeys.Select(key => byKey[key].Id).ToList(),
                        DelegationRecommendations = executable
                            ? [new WorkTechnicalDelegationRecommendation("specialist-execution", proposal.AccountableRoleKey,
                                proposal.RequiredCapabilityKeys.Append("work.execution.run.v1").Distinct().ToList(), null, true,
                                "Reconciled Designer / Technical Director proposal.")
                            {
                                RequiredSpecializationKeys = proposal.RequiredSpecializationKeys,
                                PreferredSpecializationKeys = proposal.PreferredSpecializationKeys
                            }]
                            : [],
                        ArchitectureArtifactDigest = technicalArtifact.Digest,
                        ArtifactPackageDigest = package
                    },
                    StageAssignments = assignments,
                    ProposalProvenance = new WorkItemProposalProvenance(sourceSession.Id, sourceArtifact.Digest,
                        proposal.ProposalKey)
                }, cancellationToken);
                byKey[proposal.ProposalKey] = item;
                remaining.Remove(proposal);
            }
        }
        return proposals.Count;
    }

    private static string? ExtractKey(string title)
    {
        if (!title.StartsWith("[", StringComparison.Ordinal)) return null;
        var end = title.IndexOf(']');
        return end > 1 ? title[1..end] : null;
    }

    private static string KindFor(string typeKey) => typeKey switch
    {
        VideoGameWorkItemTypeKeys.Milestone => WorkItemKinds.Epic,
        VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content => WorkItemKinds.Story,
        VideoGameWorkItemTypeKeys.Bug => WorkItemKinds.Bug,
        _ => WorkItemKinds.Task
    };

    private static string TargetMilestone(string phase) => phase switch
    {
        "concept" => VideoGameMilestoneKeys.VisionApproved,
        "pre-production" => VideoGameMilestoneKeys.PreProductionReady,
        "prototype" => VideoGameMilestoneKeys.PrototypeValidated,
        "vertical-slice" => VideoGameMilestoneKeys.VerticalSliceApproved,
        "production" => VideoGameMilestoneKeys.ProductionReady,
        "alpha" => VideoGameMilestoneKeys.AlphaExit,
        "beta" => VideoGameMilestoneKeys.BetaExit,
        "release-candidate" => VideoGameMilestoneKeys.ReleaseCandidateApproved,
        "launch" => VideoGameMilestoneKeys.LaunchApproved,
        _ => VideoGameMilestoneKeys.StabilizationExit
    };

    private static ManagementStatusReport BuildManagementReport(
        Guid cycleId, Guid? requestId, ProducerOperatingState state) =>
        new(cycleId, $"Video-game delivery is in {state.Phase}.", [], state.PhaseCommitments,
            state.OpenRisks, state.OpenRisks, [], [], [],
            state.MetricSnapshots.Count == 0 ? 0.35m : 0.9m, DateTimeOffset.UtcNow)
        {
            RequestId = requestId,
            ReporterRole = "game-producer",
            ImmediateActions = state.PhaseCommitments,
            Markdown = BuildMetricsMarkdown(state)
        };

    private static string BuildMetricsMarkdown(ProducerOperatingState state)
    {
        var lines = new List<string> { $"# Producer delivery report", $"Phase: {state.Phase}" };
        foreach (var snapshot in state.MetricSnapshots)
            lines.Add($"- Board {snapshot.BoardId:D}: velocity {snapshot.Team.AverageVelocity:F1}; throughput {snapshot.Team.ThroughputPerWeek:F1}/week; WIP {snapshot.Team.CurrentWorkInProgress}; backlog {snapshot.Team.PendingDemand}; carryover {snapshot.Team.CarryoverItemCount}; P85 {snapshot.Team.P85CycleTimeHours:F1}h; blockers {snapshot.Team.BlockedCount}.");
        lines.Add($"- Producer commitments (not delivery velocity): open {state.PersonalCommitments.Open}; ready {state.PersonalCommitments.Ready}; running {state.PersonalCommitments.Running}; blocked {state.PersonalCommitments.Blocked}; waiting {state.PersonalCommitments.Waiting}; aging {state.PersonalCommitments.Aging}.");
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildMetricRisks(string name, WorkFlowMetricsReport metrics)
    {
        var risks = new List<string>();
        if (metrics.Team.IsOverCapacity) risks.Add($"{name}: capacity utilization exceeds the trusted limit.");
        if (metrics.Team.BlockedCount > 0) risks.Add($"{name}: {metrics.Team.BlockedCount} blocked flow items ({metrics.Team.BlockedDurationHours:F1}h). ");
        if (metrics.Team.CarryoverItemCount > 0) risks.Add($"{name}: {metrics.Team.CarryoverItemCount} carryover items.");
        risks.AddRange(metrics.ConditionCodes.Select(x => $"{name}: metrics condition {x}."));
        return risks;
    }

    private static bool ShouldPublishWeekly(DateTimeOffset? prior, DateTimeOffset now) =>
        prior is null || now - prior >= TimeSpan.FromDays(7);
}
