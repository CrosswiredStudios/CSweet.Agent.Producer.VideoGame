using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class FinalReportingTests
{
    [Fact]
    public async Task FinalReportRetriesIndependentlyAcrossRestartAndNeverRestartsDelivery()
    {
        var project=Guid.NewGuid();var plan=Guid.NewGuid();
        var key=AgentOperatingStateKey.Create("final-product-report:v1",new {WorkstreamId=project,Id=plan,ScopeRevision=2});
        var now=DateTimeOffset.UtcNow;
        var states=new Dictionary<string,AgentOperatingStateResponse> {[key]=new(Guid.NewGuid(),key,"work.final-product.report-intent.v1",1,"Active",
            new Dictionary<string,string>(),[],"intent",[],Guid.NewGuid(),JsonSerializer.SerializeToElement(new {ProjectId=project,PlanId=plan,ScopeRevision=2}),1,now,now)};
        var calls=0;var starts=0;
        var runtime=new AgentTestRuntime()
            .RegisterCapability<StartWorkSprintExecutionRequest,WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Start,(_,_)=> {starts++;throw new InvalidOperationException("Reporting must not restart execution");})
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=> {
                var value=new AgentOperatingStateResponse(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,now,now);
                states[r.StateKey]=value;return Task.FromResult(value);
            })
            .RegisterCapability<ReadWorkDeliveryPlansRequest,IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,(_,_)=>Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>(
                [JsonSerializer.Deserialize<WorkDeliveryPlanResponse>("{}")! with {Id=plan,WorkstreamId=project,Status="Completed",ScopeRevision=2,Findings=[]}]))
            .RegisterCapability<ManagementStatusReport,JsonElement>("platform.management.status-report.v1",(report,_)=> {
                Assert.Equal(project,report.WorkstreamId);calls++;
                if(calls==1)throw new HttpRequestException("The report transport is temporarily unavailable");
                return Task.FromResult(JsonSerializer.SerializeToElement(new {recorded=true}));
            });
        var item=JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with {CorrelationId=key,WorkContext=new(WorkstreamId:project)};
        await Assert.ThrowsAsync<HttpRequestException>(()=>new SpecialistAgent().HandlePersonalTodoAsync(item,runtime.CreateContext(),default));
        await new SpecialistAgent().HandlePersonalTodoAsync(item,runtime.CreateContext(),default);
        await new SpecialistAgent().HandlePersonalTodoAsync(item,runtime.CreateContext(),default);
        Assert.Equal(2,calls);Assert.True(states.ContainsKey(key+":sent"));
        Assert.Equal(0,starts);
    }
}
