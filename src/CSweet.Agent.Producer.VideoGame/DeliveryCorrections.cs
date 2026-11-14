using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static IReadOnlyList<Guid> RecoveryProjects(AgentRuntimeContext context) =>
        (context.Identity?.ManagedWorkstreams.Select(p => p.WorkstreamId) ?? [])
        .Concat(context.Identity?.AssignedProject is { } assigned ? [assigned.WorkstreamId] : [])
        .Distinct().Take(25).ToArray();

    protected override async Task HandleManagerRecoveryAsync(AgentRuntimeContext context, CancellationToken token)
    {
        foreach (var project in RecoveryProjects(context))
            await ReconcileDeliveryCorrectionAsync(project, context, token);
    }

    // A newly approved project has no delivery access until its board exists, and it cannot hold
    // delivery plans or corrections before then. A denied plan read therefore means there is nothing
    // to reconcile. It must not fail the review that goes on to create the board and its access.
    private async Task ReconcileDeliveryCorrectionAsync(Guid project, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            await DeliveryCorrectionCoordinator.ReconcileProjectAsync(project, context,
                () => CreateDelegationChatClient(context, null, "delivery-correction-source"),
                (request,ct)=>PrepareCorrectiveWorkAsync(request,context,ct),token);
        }
        catch (PlatformCapabilityException ex) when (ex.Code == PlatformCapabilityErrorCode.Denied &&
            (ex.Capability == "work.delivery.read.v1" ||
             ex.Message.Contains("work.delivery.read.v1", StringComparison.Ordinal)))
        {
        }
    }

    private static async Task PrepareCorrectiveWorkAsync(WorkCorrectionPreparation request,AgentRuntimeContext context,CancellationToken ct)
    {
        var board=await context.Platform.Work.ReadBoardAsync(request.BoardId,ct);
        if(request.RefinementReason is {} reason) {
            if(board.Board.WorkstreamId!=request.WorkstreamId||board.Board.ManagerOrganizationUserId?.ToString()!=context.Identity?.EmployeeId)
                throw new UnauthorizedAccessException("Corrective refinement needs the current accountable manager.");
            await EnsureCommitmentAsync("producer-corrective-planning:"+request.CorrectionId.ToString("N"),"Refine retained corrective work",
                "Retained correction "+request.CorrectionId+" needs a manager-authored source/workflow decision: "+reason+
                " Preserve findings and approved criteria. Declare the appropriate source bindings and successor workflow, then retry correction preparation. No original sprint is reopened.",
                WorkPriorities.High,new(WorkstreamId:request.WorkstreamId,TeamId:board.Board.TeamId,BoardId:request.BoardId,SourceFingerprint:request.CorrectionId.ToString("D")),context,ct);
            return;
        }
        var state=await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"),ct);
        if(board.Board.WorkstreamId!=request.WorkstreamId||board.Board.TeamId is not {} team||
            board.Board.ManagerOrganizationUserId?.ToString()!=context.Identity?.EmployeeId||
            state?.Payload.PlanningCycles.TryGetValue(request.WorkstreamId,out var cycle)!=true||cycle?.ReconciledDigest is null)
            throw new InvalidOperationException("The correction needs current manager-owned planning context before sprint preparation.");
        var sprints=await context.Platform.Work.ListSprintsAsync(request.BoardId,ct);
        await EnsureCommitmentAsync(SprintEstimationCorrelation(request.BoardId,cycle.ReconciledDigest,PlanningSprintSequence(sprints))+":correction:"+request.CorrectionId.ToString("N"),
            "Prepare retained corrective work","Estimate the authorized repair, assess current staffing and capacity, then choose a coherent sprint scope. Keep existing successful history.",
            WorkPriorities.High,new(WorkstreamId:request.WorkstreamId,TeamId:team,BoardId:request.BoardId,WorkItemId:request.TaskIds.FirstOrDefault(),SourceFingerprint:cycle.ReconciledDigest),context,ct);
    }

    private async Task<PersonalTodoResult> ReconcileCorrectivePlanningAsync(PersonalTodoItem item,AgentRuntimeContext context,CancellationToken ct)
    {
        if(item.WorkContext?.WorkstreamId is not {} project||!Guid.TryParse(item.WorkContext.SourceFingerprint,out var id))
            return PersonalTodoResult.Blocked("Corrective planning needs its retained project and correction identity.");
        await ReconcileDeliveryCorrectionAsync(project,context,ct);
        var current=(await ReadProjectDeliveryHistoryAsync(project,context,ct)).SelectMany(p=>p.Corrections).Single(c=>c.Id==id);
        return current.Status=="Completed"||current.FollowUpPlanId.HasValue?PersonalTodoResult.Completed("Corrective work is durably owned by its successor plan and ordinary sprint preparation."):
            PersonalTodoResult.Blocked("Gabriel owns corrective refinement for "+id+": "+current.WaitingReason);
    }

    private static async Task<IReadOnlySet<Guid>> ReadAuthorizedCorrectiveItemsAsync(Guid project,AgentRuntimeContext context,CancellationToken ct)
    {
        var history=await ReadProjectDeliveryHistoryAsync(project,context,ct);
        return history.Where(p=>p.ManagerOrganizationUserId.ToString()==context.Identity?.EmployeeId&&p.Status is "Active" or "Paused")
            .SelectMany(p=>p.Corrections.Where(c=>c.OwnerOrganizationUserId==p.ManagerOrganizationUserId&&c.Status!="Completed"&&
                c.FollowUpPlanId is {} next&&history.Any(n=>n.Id==next&&n.Status=="Active"))).SelectMany(c=>c.RemediationTaskIds??[]).ToHashSet();
    }
}
