using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private const string ManagerDeliveryPrefix = "producer-manager-delivery:";
    private static readonly JsonSerializerOptions ManagerJson = new(JsonSerializerDefaults.Web);
    internal sealed record ManagerDeliveryState(string Direction = "", Guid? SessionId = null,
        string? Fingerprint = null, ProjectDeliveryPlan? Plan = null, string? LastStatus = null);

    private static Task<AgentOperatingState<ManagerDeliveryState>> SaveManagerDeliveryAsync(Guid project,
        Func<ManagerDeliveryState, ManagerDeliveryState> change, AgentRuntimeContext context, CancellationToken ct) =>
        new RevisionSafeProjectState(context.Platform).MergeAsync<ManagerDeliveryState>(ManagerDeliveryPrefix + project.ToString("N"),
            "producer.manager-delivery.v1", 1, current => change(current ?? new()), new Dictionary<string, string>(),
            $"manager-delivery-state:{project:N}:{Guid.NewGuid():N}", ct);

    private static async Task<PersonalTodoItem> EnsureManagerDeliveryAsync(WorkstreamDetail project, AgentRuntimeContext context, CancellationToken ct, bool wake = true)
    {
        var correlation = ManagerDeliveryPrefix + project.Id.ToString("N");
        if (!wake)
        {
            var existing = (await context.Platform.PersonalTodo.ListAsync(ct)).Boards.SelectMany(x => x.Items)
                .FirstOrDefault(x => x.CorrelationId == correlation && x.ArchivedAt is null);
            if (existing is not null) return existing; // The durable wait deadline handles bounded missed-event recovery.
        }
        return await EnsureCommitmentAsync(correlation, "Coordinate " + project.Name,
            "Attach approved staffing, obtain the technical plan, populate tickets and advance each sprint through verified delivery.",
            "High", new(WorkstreamId: project.Id, SourceFingerprint: ProjectDeliveryPlanning.Fingerprint(project)), context, ct);
    }
    private static async Task<AgentTeamContext?> ReadManagerTeamAsync(Guid team, AgentRuntimeContext context, CancellationToken ct)
    {
        AgentTeamContext? result = null;
        var members = new List<AgentTeammate>();
        for (var page = 1; page <= 10; page++)
        {
            var current = (await context.Platform.ReadTeamRosterAsync(new TeamRosterV2Request(team, null, page, 100), ct)).Team;
            if (current is null) return null;
            if (result is not null && result.Revision != current.Revision) throw new InvalidOperationException("The team roster changed during discovery; retry against its current revision.");
            result ??= current; members.AddRange(current.Members);
            if (!current.HasMore) return result with { Members = members.DistinctBy(x => x.EmployeeId).ToArray(), HasMore = false };
        }
        throw new InvalidOperationException("Team discovery exceeded its bounded page limit.");
    }

    private async Task<PersonalTodoResult> ReconcileManagerDeliveryAsync(PersonalTodoItem commitment, AgentRuntimeContext context, CancellationToken ct)
    {
        if (commitment.WorkContext?.WorkstreamId is not { } projectId) return PersonalTodoResult.Blocked("The delivery commitment needs a project.");
        try
        {
            var result = await AdvanceManagerDeliveryAsync(projectId, context, ct);
            await SaveManagerDeliveryAsync(projectId, state => state with { LastStatus = result.Message }, context, ct);
            return result.Complete ? PersonalTodoResult.Completed(result.Message)
                : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(30), result.Message);
        }
        catch (Exception error) when (error is PlatformCapabilityException or InvalidOperationException or ArgumentException)
        {
            await SaveManagerDeliveryAsync(projectId, state => state with { LastStatus = error.Message }, context, ct);
            return PersonalTodoResult.Blocked("Delivery requires attention: " + error.Message);
        }
    }

    internal async Task<(bool Complete, string Message)> AdvanceManagerDeliveryAsync(Guid projectId, AgentRuntimeContext context, CancellationToken ct)
    {
        var project = await context.Platform.ReadWorkstreamAsync(new(projectId), ct);
        if (project.AccountableManagerOrganizationUserId.ToString() != context.Identity?.EmployeeId)
            throw new InvalidOperationException("This project no longer belongs to the producer.");
        if (project.Status is "Completed" or "Cancelled") return (true, "The project is " + project.Status.ToLowerInvariant() + ".");
        if (project.Status is not ("Approved" or "Active")) return (false, "Waiting for project approval.");
        var plans = (await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(Statuses: ["Approved"]), ct)).Requests;
        var approved = plans.Where(x => x.RequesterInstallationId.ToString() == context.InstallationId && x.TeamId.HasValue &&
                (!x.WorkstreamId.HasValue || x.WorkstreamId == projectId)).OrderByDescending(x => x.DecidedAt ?? x.CreatedAt).FirstOrDefault();
        if (approved?.TeamId is not { } teamId) return (false, "Waiting for the existing staffing request to be approved and fulfilled.");
        var roster = await ReadManagerTeamAsync(teamId, context, ct);
        if (roster is null || roster.LeadEmployeeId != context.Identity.EmployeeId) return (false, "Waiting for the approved team roster.");
        var architect = RoleTaxonomy.SelectAssignment(roster.Members,
            new WorkAssignmentRequirements("game-technical-director", [], [], ["work.execution.run.v1"]))?.Teammate
            ?? RoleTaxonomy.SelectAssignment(roster.Members, new WorkAssignmentRequirements("software-architect", [], [], ["work.execution.run.v1"]))?.Teammate;
        // This policy uses the software delivery execution contract, including source publication and real tests.
        var developer = RoleTaxonomy.SelectAssignment(roster.Members.Where(x => x.DeclaredRoleKeys.Contains("software-developer")).ToArray(),
            new WorkAssignmentRequirements("software-developer", [], [], ["work.execution.run.v1", "software-development.implement.v1"]))?.Teammate;
        if (architect is null || developer is null || architect.EmployeeId == developer.EmployeeId)
            return (false, "Waiting for an approved Technical Director or Software Architect and a Software Developer with delivery execution enabled.");
        var participants = roster.Members.Where(x => x.IsAvailable && Guid.TryParse(x.EmployeeId, out _)).Select(x => Guid.Parse(x.EmployeeId)).ToArray();
        var setup = await context.Platform.Projects.PrepareDeliveryAsync(new(projectId, approved.Id, participants,
            project.Revision, $"producer-project-setup:{projectId:N}"), ct);
        project = await context.Platform.ReadWorkstreamAsync(new(projectId), ct);
        if (project.ProfileVersion != 2)
        {
            if (setup.AvailableProfile is not { Version: 2 } target) return (false, "The delivery profile update must be installed before this project can execute.");
            var upgrade = await context.Platform.ProposeWorkstreamChangeAsync(new(projectId, project.Revision,
                "Enable the approved team's delivery workflow", JsonSerializer.SerializeToElement(new { profileUpgrade = target }, ManagerJson),
                "Add technical planning, implementation, independent review and governed merge to this existing empty project.",
                $"producer-profile-upgrade:{projectId:N}:{target.DefinitionDigest}"), ct);
            return (false, $"Delivery workflow update: {upgrade.Message ?? (upgrade.Applied ? "Applied." : "Awaiting a recorded decision.")}. The approved team is attached.");
        }
        var board = await context.Platform.Work.ReadBoardAsync(setup.BoardId, ct);
        await context.Platform.Work.ConfigureProfileOrchestrationAsync(new(projectId, setup.BoardId, board.Board.Revision,
            project.ProfileDefinitionDigest!, $"producer-manager-policy:{projectId:N}:{project.ProfileDefinitionDigest}"), ct);
        var state = (await context.Platform.ReadOperatingStateAsync<ManagerDeliveryState>(ManagerDeliveryPrefix + projectId.ToString("N"), ct))?.Payload ?? new();
        var fingerprint = ProjectDeliveryPlanning.Fingerprint(project);
        var planRequest = new ProjectDeliveryPlanRequest(projectId, setup.BoardId, fingerprint,
            string.IsNullOrWhiteSpace(state.Direction) ? (project.ProfileData is { } metadata && metadata.TryGetProperty("managerDirection", out var direction) ? direction.GetString() ?? project.Outcome : project.Outcome) : state.Direction);
        if (state.Plan is not null && state.Fingerprint != fingerprint && board.Items.Any(x => x.ExecutionMode == WorkItemExecutionModes.Executable))
            return (false, "The approved project changed after planning. Existing work is retained; the technical plan needs explicit reconciliation before another sprint starts.");
        AgentCoordinationSession session;
        if (state.SessionId is null || state.Fingerprint != fingerprint)
        {
            session = await context.Platform.Communication.StartBoardCoordinationAsync(new(Guid.Parse(architect.EmployeeId), setup.BoardId,
                "Technical plan for " + project.Name, "Create the implementation backlog and sprint sequence for the approved brief.",
                ["Bounded tickets with testable acceptance criteria, dependencies, estimates and sprint order"], project.Outcome,
                $"producer-manager-plan:{projectId:N}:{fingerprint}",
                new(ProjectDeliveryPlanning.RequestType, "1.0", fingerprint, 1, true, JsonSerializer.SerializeToElement(planRequest, ManagerJson))), ct);
            state = (await SaveManagerDeliveryAsync(projectId, current => current with { SessionId = session.Id, Fingerprint = fingerprint, Plan = null }, context, ct)).Payload;
        }
        else session = await context.Platform.Communication.ReadCoordinationAsync(state.SessionId.Value, ct);
        var artifact = session.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId &&
            x.Artifact?.Type == ProjectDeliveryPlanning.ProposalType && x.Artifact.IsFinalPage)?.Artifact;
        if (artifact is null) return (false, $"Technical planning is {session.Status.ToLowerInvariant()} (session {session.Id:D}). " + session.FinalSummary);
        if (session.Target.OrganizationUserId != Guid.Parse(architect.EmployeeId) || session.WorkContext?.WorkstreamId != projectId || artifact.Key != fingerprint)
            throw new InvalidOperationException("The technical proposal no longer matches the current architect and project.");
        var plan = artifact.Payload.Deserialize<ProjectDeliveryPlan>(ManagerJson) ?? throw new InvalidOperationException("The technical proposal is empty.");
        var errors = ProjectDeliveryPlanning.Validate(plan, planRequest);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (state.Plan is null) await SaveManagerDeliveryAsync(projectId, current => current with { Plan = plan }, context, ct);
        await PopulateManagerBacklogAsync(project, setup, roster, architect.DeclaredRoleKeys.Contains("game-technical-director") ? "game-technical-director" : "software-architect", plan, session, artifact, context, ct);
        var repository = await context.Platform.SourceControl.ProvisionRepositoryAsync(new(projectId, $"game-{projectId:N}",
            "Source for the approved manager-directed game project.", Guid.Empty, $"producer-repository:{projectId:N}"), ct);
        if (repository.Status != "Completed" || repository.RepositoryId is not { } repositoryId)
            return (false, $"The technical backlog and sprints exist. Repository setup is {repository.Status}: {repository.Remediation}");
        var repositoryOption = (await context.Platform.SourceControl.ListTeamRepositoryOptionsAsync(new(teamId), ct)).SingleOrDefault(x => x.RepositoryId == repositoryId);
        if (repositoryOption is null) return (false, "The provisioned repository is not yet available to the approved team.");
        board = await context.Platform.Work.ReadBoardAsync(setup.BoardId, ct);
        foreach (var ticket in board.Items.Where(x => x.ProposalProvenance?.CoordinationSessionId == session.Id && x.ExecutionMode == WorkItemExecutionModes.Executable && x.Delivery is null))
        {
            var assignments = RefreshAssignments(ticket, roster, project.ProfileDefinitionDigest!);
            if (assignments.Count != 3) return (false, "A planned delivery stage no longer has eligible approved staffing.");
            var delivery = new WorkItemDeliverySpecification(repositoryId, ticket.Planning!.Requirements, ticket.Planning.AcceptanceCriteria, ticket.Planning.Constraints)
                { BaseBranch = repositoryOption.DefaultBranch, DependencyItemIds = ticket.Planning.DependencyItemIds };
            await context.Platform.Work.FinalizeItemDeliveryAsync(new(setup.BoardId, ticket.Id, delivery,
                Guid.Parse(developer.EmployeeId), assignments, ticket.Revision, $"producer-manager-finalize:{ticket.Id:N}:{ticket.PlanningRevision}:{roster.Revision}"), ct);
        }
        var sprints = await context.Platform.Work.ListSprintsAsync(setup.BoardId, ct);
        var active = sprints.SingleOrDefault(x => x.Status == "Active");
        if (active is not null)
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(setup.BoardId, active.Id), ct);
            if (execution is null) return (false, $"{active.Name} is active but has no current execution receipt. Its existing sprint needs reconciliation.");
            var blockers = execution.Items.Where(x => x.Status is "Blocked" or "Failed" || !string.IsNullOrWhiteSpace(x.BlockedReason))
                .Select(x => $"{x.ItemIdentifier} ({x.CurrentStageKey}): {x.BlockedReason ?? x.Status}").ToArray();
            return (false, blockers.Length > 0
                ? $"{active.Name} requires attention: " + string.Join("; ", blockers)
                : $"{active.Name} execution is {execution.Status.ToLowerInvariant()}. Execution events will resume coordination when work advances.");
        }
        var next = sprints.Where(x => x.Status == "Planned").OrderBy(x => x.Sequence).FirstOrDefault();
        if (next is null)
        {
            board = await context.Platform.Work.ReadBoardAsync(setup.BoardId, ct);
            if (!board.Items.Any(x => x.ExecutionMode == WorkItemExecutionModes.Executable) || board.Items.Any(x => x.ExecutionMode == WorkItemExecutionModes.Executable && x.Status is not ("Completed" or "Done")))
                return (false, "Sprint execution ended with unresolved delivery work. Reconcile the existing tickets before completion.");
            var doneColumn = board.Columns.Single(x => x.Name == "Done").Id;
            foreach (var container in board.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Container && x.Status is not ("Completed" or "Done" or "Cancelled")).OrderByDescending(x => x.ParentItemId.HasValue))
                await context.Platform.Work.MoveItemAsync(new(setup.BoardId, container.Id, doneColumn, container.Revision, $"producer-container-complete:{container.Id:N}"), ct);
            // The final project transition remains governed by its approved lifecycle and authority envelope.
            var targetStage = project.LifecycleStage switch { "concept" => "prototype", "prototype" => "vertical-slice", "vertical-slice" => "completed", _ => null };
            if (targetStage is null) return (false, "All planned sprints have delivered; the current lifecycle stage needs reconciliation before project closure.");
            var transition = await context.Platform.ProposeWorkstreamChangeAsync(new(projectId, project.Revision,
                "Record verified delivery completion", JsonSerializer.SerializeToElement(new { lifecycleStage = targetStage }),
                "All planned executable tickets have completed their technical review, producer acceptance and governed merge stages.",
                $"producer-manager-complete:{projectId:N}:{project.Revision}"), ct);
            return (false, $"All planned sprints have delivered. Lifecycle transition to {targetStage}: {transition.Message ?? (transition.Applied ? "Applied." : "Awaiting a recorded decision.")}");
        }
        var start = new StartWorkSprintExecutionRequest(setup.BoardId, next.Id, next.Revision, $"producer-manager-start:{next.Id:N}");
        var preflight = await context.Platform.Work.PreflightSprintAsync(start, ct);
        if (!preflight.IsValid) return (false, "Sprint readiness requires: " + string.Join("; ", preflight.Errors.Select(x => x.Message)));
        var running = await context.Platform.Work.StartSprintExecutionAsync(start, ct);
        return (false, $"{next.Name} execution is {running.Status.ToLowerInvariant()}. {plan.Items.Count} technical tickets cover the approved project.");
    }

    private static async Task PopulateManagerBacklogAsync(WorkstreamDetail project, PreparedProjectDelivery setup, AgentTeamContext roster,
        string technicalRole, ProjectDeliveryPlan plan, AgentCoordinationSession session, AgentCoordinationArtifact artifact, AgentRuntimeContext context, CancellationToken ct)
    {
        var board = await context.Platform.Work.ReadBoardAsync(setup.BoardId, ct);
        var ready = board.Columns.Single(x => x.Name == "Ready").Id;
        var prefix = $"delivery:{project.Id:N}:{plan.Fingerprint[..16]}";
        var epic = await context.Platform.Work.CreateItemAsync(new(setup.BoardId, project.Name, plan.Architecture, "Epic", "High", null, null, null, prefix + ":epic") { TypeKey = WorkItemTypeKeys.GeneralEpicV1 }, ct);
        var story = await context.Platform.Work.CreateItemAsync(new(setup.BoardId, project.Outcome[..Math.Min(240, project.Outcome.Length)], plan.Architecture, "Story", "High", null, epic.Id, null, prefix + ":story") { TypeKey = WorkItemTypeKeys.GeneralStoryV1 }, ct);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var sprintGroup in plan.Items.GroupBy(x => x.Sprint).OrderBy(x => x.Key))
        {
            var sprint = await context.Platform.Work.CreateSprintAsync(new(setup.BoardId, $"Delivery Sprint {sprintGroup.Key}",
                string.Join("; ", sprintGroup.Select(x => x.Title)), null, null, prefix + $":sprint:{sprintGroup.Key}") { Sequence = sprintGroup.Key }, ct);
            foreach (var proposed in sprintGroup)
            {
                var constraints = (proposed.Constraints ?? []).Append("project-delivery:manager-brief-v2").Distinct().ToArray();
                var planning = new WorkItemPlanningSpecification(proposed.Requirements, proposed.AcceptanceCriteria, constraints)
                {
                    ArchitectureArtifactDigest = artifact.Digest,
                    DependencyItemIds = (proposed.Dependencies ?? []).Select(key => ids[key]).ToArray(),
                    DelegationRecommendations = [new("development", "software-developer", ["work.execution.run.v1", "software-development.implement.v1"], null, true, "Implement and validate the ticket."),
                        new("quality", technicalRole, ["work.execution.run.v1"], null, true, "Independently review the exact patch and actual implementation validation evidence."),
                        new("merge-decision", "game-producer", ["work.execution.run.v1"], null, true, "Accept the delivered criteria and authorize governed merge.")]
                };
                var ticket = await context.Platform.Work.CreateItemAsync(new(setup.BoardId, proposed.Title, proposed.Description, "Task", "High", ready,
                    story.Id, null, prefix + ":ticket:" + proposed.Key) { TypeKey = WorkItemTypeKeys.GeneralTaskV1, Planning = planning,
                    ProposalProvenance = new(session.Id, artifact.Digest, proposed.Key) }, ct);
                ids[proposed.Key] = ticket.Id;
                // Re-read after replay: receipts describe the original mutation, not the current item revision.
                ticket = await context.Platform.Work.ReadItemAsync(new(setup.BoardId, ticket.Id), ct);
                if (ticket.EstimatePoints is null) ticket = await context.Platform.Work.EstimateAsync(new(setup.BoardId, ticket.Id, proposed.EstimatePoints,
                    ticket.Revision, prefix + ":estimate:" + proposed.Key) { Provenance = new(session.Target.OrganizationUserId,
                    session.Target.AgentInstallationId, session.Id, session.Revision, artifact.Digest, 0.7m) }, ct);
                if (ticket.SprintId is null) await context.Platform.Work.SetItemSprintAsync(new(setup.BoardId, ticket.Id, sprint.Id, ticket.Revision, prefix + ":scope:" + proposed.Key), ct);
            }
        }
    }
}
