using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private async Task<PersonalTodoResult> ReconcileFinalProductAsync(GameProductionPlanningCycleV1 cycle,
        ProducerAcceptedHandoff handoff, OwnershipReview ownership, AgentRuntimeContext context, CancellationToken ct)
    {
        var project=await context.Platform.ReadWorkstreamAsync(new(cycle.WorkstreamId),ct);
        if (project.Status is "Completed" or "Cancelled") return PersonalTodoResult.Completed("The project is "+project.Status+"; no successor sprint was created.");
        var board=await context.Platform.Work.ReadBoardAsync(cycle.BoardId,ct);
        if (board.Board.ManagerOrganizationUserId?.ToString()!=context.Identity?.EmployeeId)
            return PersonalTodoResult.Blocked("Final delivery needs the current accountable project manager.");
        var plans=await ReadProjectDeliveryHistoryAsync(cycle.WorkstreamId,context,ct);
        var source=new WorkApprovedSource(handoff.ArtifactId,handoff.AcceptedRevisionId,handoff.RevisionDigest);
        var successors=plans.SelectMany(p=>p.Corrections).Where(c=>c.FollowUpPlanId.HasValue).Select(c=>c.FollowUpPlanId!.Value).ToHashSet();
        var finalRoots=plans.Where(p=>!successors.Contains(p.Id)&&p.Scopes.Any(s=>s.TicketWorkflow?.ProjectCompletionSource==source)).ToArray();
        if (finalRoots.Length>1) return PersonalTodoResult.Blocked("Select the authoritative final product before project closure; multiple declarations exist.");
        var final=finalRoots.SingleOrDefault();
        if (final is null)
        {
            if (ownership.Gaps.Count>0 || ownership.Commitments.Count==0)
                return PersonalTodoResult.Blocked("Final preparation requires explicit ownership of every approved obligation.");
            var accepted=plans.Where(p=>p.Status=="Completed").ToArray();
            var pending=plans.Where(p=>p.Status is "Active" or "Paused" or "Draft").ToArray();
            if (pending.Length>0) return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),"Current delivery and its corrections must reach acceptance before final product preparation.");
            var evidence=JsonSerializer.SerializeToElement(new { obligations=ownership.Commitments,
                deliveries=accepted.Select(p=>new {p.Id,p.ScopeRevision,scopes=p.Scopes.Select(s=>new {s.Planning,s.ItemId}),
                    reviews=p.Executions.SelectMany(e=>e.Stages.Select(s=>new {s.StageKey,s.LastSummary,candidate=e.Candidate?.Digest}))}) });
            var basis=AgentOperatingStateKey.Create("product-sufficiency:v1",new {handoff.RevisionDigest,evidence});
            var document=await context.Platform.Artifacts.GetAsync(handoff.ArtifactId,ct);
            var brief=document.Revisions.Single(r=>r.Id==handoff.AcceptedRevisionId&&r.ContentSha256==handoff.RevisionDigest&&r.Status=="Accepted");
            var assessment=await DurableWorkHandoff.GetOrCreateAsync(basis,"work.scope-sufficiency.v1",async()=> {
                using var client=context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId")??
                    throw new InvalidOperationException("Configure the planning model provider."),Settings.GetString("llmModel")));
                return await WorkPlanningJudgments.AssessCompletionAsync(client,brief.Content,evidence,ResponseOptions(),ct);
            },context,ct);
            if (!assessment.ScopeSufficient)
            {
                return await RefineRemainingApprovedWorkAsync(cycle, handoff, basis,
                    assessment.Rationale + " " + string.Join(" ", assessment.RemainingWork), context, ct);
            }
            var roots=accepted.SelectMany(p=>p.Scopes.Where(s=>s.Scope==WorkExecutionScopes.Release).Select(s=>(Plan:p,Scope:s))).ToArray();
            if (roots.Length==0) return PersonalTodoResult.Blocked("Final preparation needs accepted delivery evidence; an empty candidate list is insufficient.");
            var template=roots.OrderBy(x=>x.Plan.UpdatedAt).Last();
            var repositories=await context.Platform.SourceControl.ListTeamRepositoryOptionsAsync(new(cycle.TeamId),ct);
            var bindings=accepted.OrderBy(p=>p.UpdatedAt).SelectMany(p=>p.Branches.Where(b=>b.Scope==WorkExecutionScopes.Release))
                .GroupBy(b=>b.RepositoryId).Select(g=> {
                    var branch=g.Last();var repository=repositories.Single(r=>r.RepositoryId==g.Key);
                    return branch with {SourceBranch=branch.TargetBranch,TargetBranch=repository.DefaultBranch!,TargetBaseBranch=null};
                }).ToArray();
            var obligations=new WorkItemPlanningSpecification(ownership.Commitments.Select(o=>o.Statement).ToArray(),
                ownership.Commitments.Select(o=>o.Statement).ToArray()) {DependencyItemIds=roots.SelectMany(r=>r.Scope.TicketWorkflow!.FinalReleaseDependencyItemIds).Distinct().ToArray()};
            final=await FinalProductDelivery.PrepareAsync(new(cycle.WorkstreamId,cycle.BoardId,board.Board.ManagerOrganizationUserId!.Value,
                handoff.RevisionDigest,obligations,roots.Select(r=>r.Scope.TicketWorkflow!.TicketId).Distinct().ToArray(),bindings,
                template.Scope.TicketWorkflow!.Steps) {CompletionSource=source},context,ct);
        }
        var acceptedFinal=FinalProductDelivery.AcceptedSuccessor(final,plans);
        if (acceptedFinal is null)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),"The explicit final product awaits its declared verification, acceptance or corrective work. Public launch remains separately authorized.");
        var reportKey=AgentOperatingStateKey.Create("final-product-report:v1",new {cycle.WorkstreamId,acceptedFinal.Id,acceptedFinal.ScopeRevision});
        await DurableWorkHandoff.GetOrCreateAsync(reportKey,"work.final-product.report-intent.v1",
            ()=>Task.FromResult(new FinalReportIntent(cycle.WorkstreamId,acceptedFinal.Id,acceptedFinal.ScopeRevision)),context,ct);
        await EnsureCommitmentAsync(reportKey,"Report verified product delivery",
            "Report authoritative final-product evidence independently of project closure. A report failure cannot restart production.",
            WorkPriorities.Medium,new(WorkstreamId:cycle.WorkstreamId,TeamId:cycle.TeamId,BoardId:cycle.BoardId,SourceFingerprint:reportKey),context,ct);
        var completion=new WorkstreamDeliveryCompletion(acceptedFinal.Id,acceptedFinal.ScopeRevision,source);
        var result=await context.Platform.CompleteWorkstreamDeliveryAsync(new(cycle.WorkstreamId,project.Revision,completion,
            $"product-completion:{acceptedFinal.Id:N}:{acceptedFinal.ScopeRevision}:{project.Revision}"),ct);
        return result.Applied?PersonalTodoResult.Completed("Recorded verified final delivery; no empty successor sprint or public launch was created."):
            PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.Add(CoordinationReviewDelay),"Verified delivery completion needs attention: "+result.Message);
    }

    private sealed record FinalReportIntent(Guid ProjectId,Guid PlanId,long ScopeRevision);

    private static async Task<PersonalTodoResult> ReconcileFinalReportAsync(PersonalTodoItem item,AgentRuntimeContext context,CancellationToken ct)
    {
        var intent=await context.Platform.ReadOperatingStateAsync<FinalReportIntent>(item.CorrelationId!,ct);
        if(intent is null||item.WorkContext?.WorkstreamId!=intent.Payload.ProjectId)
            return PersonalTodoResult.Blocked("The exact final reporting intent is unavailable.");
        var proof=intent.Payload;
        var plan=(await context.Platform.Work.ReadDeliveryPlansAsync(new(proof.ProjectId,proof.PlanId),ct)).Single();
        if(plan.Status!="Completed"||plan.ScopeRevision!=proof.ScopeRevision||plan.Findings.Any(f=>f.Status=="Open"))
            return PersonalTodoResult.Blocked("The retained final delivery evidence changed; reconcile the current delivery before reporting it as accepted.");
        await DurableWorkHandoff.GetOrCreateAsync(item.CorrelationId!+":sent","work.final-product.report-sent.v1",async()=> {
            var report=new ManagementStatusReport(proof.PlanId,"Final integrated product accepted; delivery evidence is retained in plan "+proof.PlanId,
                [],[],[],[],[],[],[],1m,DateTimeOffset.UtcNow) {WorkstreamId=proof.ProjectId,ReporterRole="game-producer"};
            await context.Platform.InvokeAsync<ManagementStatusReport,JsonElement>("platform.management.status-report.v1",report,ct);
            return proof;
        },context,ct);
        return PersonalTodoResult.Completed("Reported verified final delivery without blocking or restarting execution.");
    }

    private static async Task<IReadOnlyList<WorkDeliveryPlanResponse>> ReadProjectDeliveryHistoryAsync(Guid project,
        AgentRuntimeContext context,CancellationToken ct)
    {
        var result=new List<WorkDeliveryPlanResponse>();Guid? after=null;
        while (true) {
            var page=await context.Platform.Work.ReadDeliveryPlansAsync(new(project,Limit:100,AfterId:after),ct);
            result.AddRange(page);if(page.Count<100)return result;
            var next=page.Last().Id;if(next==after)throw new InvalidOperationException("Delivery discovery cursor did not advance.");after=next;
        }
    }
}
