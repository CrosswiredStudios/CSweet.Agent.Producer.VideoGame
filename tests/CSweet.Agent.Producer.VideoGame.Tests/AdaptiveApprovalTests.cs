using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class AdaptiveApprovalTests
{
    [Theory]
    [InlineData("refine",false,0)]
    [InlineData("wait",false,0)]
    [InlineData("wait",true,0)]
    [InlineData("wait",true,1)]
    [InlineData("wait",true,2)]
    public async Task NonterminalReviewWithNoOwnedDependencyDispatchesOneSourceBoundQuestion(string disposition, bool selfCapabilityConsult, int rejections)
    {
        var project=Guid.NewGuid();var team=Guid.NewGuid();var self=Guid.NewGuid();var specialist=Guid.NewGuid();
        var cycle=new GameProductionPlanningCycleV1(project,team,1,Guid.NewGuid(),"profile",Guid.NewGuid(),1,"package","production","prototype","scope");
        var handoff=new ProducerAcceptedHandoff(project,Guid.NewGuid(),Guid.NewGuid(),"brief","handoff",Guid.NewGuid(),cycle.ApprovedPackageId,1,DateTimeOffset.UtcNow);
        var member=new AgentTeammate(specialist.ToString(),"Qualified planner","Agent",null,null,"Team","Available") {AgentInstallationId=Guid.NewGuid(),RuntimeEligibility="Eligible",DeclaredRoleKeys=["game-technical-director"]};
        var owner = member with { EmployeeId = self.ToString(), DisplayName = "Gabriel", DeclaredRoleKeys = ["game-producer"] };
        var roster=new AgentTeamContext(team.ToString(),"game","Game",1,self.ToString(),"Producer",selfCapabilityConsult ? [owner,member] : [member],[],selfCapabilityConsult ? 2 : 1,false);
        var states=new Dictionary<string,AgentOperatingStateResponse>();var starts=0;var cards=0;
        var runtime=new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>Task.FromResult(states[r.StateKey]=new(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,(states.GetValueOrDefault(r.StateKey)?.Revision??0)+1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)))
            .RegisterCapability<StartBoardCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard,(r,_)=> {
                starts++;Assert.Equal(specialist,r.TargetOrganizationUserId);Assert.Equal(WorkContributions.RequestType,r.Artifact!.Type);
                if (starts <= rejections) throw new PlatformCapabilityException(CommunicationCapabilities.CoordinationStartBoard,
                    PlatformCapabilityErrorCode.Denied,"Both board-collaboration participants must be active agents on the board team.");
                return Task.FromResult(new AgentCoordinationSession(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new(self,Guid.NewGuid(),"Owner","Producer"),new(specialist,member.AgentInstallationId!.Value,"Planner","Contributor"),"Refinement","Current uncertainty",[],"Active",1,0,specialist,false,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[]) {BoardSource=new(cycle.BoardId)});
            })
            .RegisterCapability<AddPersonalTodoItemRequest,PersonalTodoItem>(PersonalTodoCapabilities.Add,(r,_)=> {cards++;Assert.Equal(PersonalWorkPurposes.CollaborationTracking,r.WorkContext!.Purpose);return Task.FromResult(JsonSerializer.Deserialize<PersonalTodoItem>("{}")!);});
        var modelCalls = 0;
        runtime.RegisterCapability<TeamRosterV2Request,TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,(_,_)=>Task.FromResult(new TeamRosterV2Response(roster,project)))
            .RegisterCapability<ResourceChangeReadRequest,ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,(_,_)=>Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<ResourceRead,IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,(_,_)=>Task.FromResult<IReadOnlyList<ResourceRequestState>>([]));
        runtime.RegisterCapability<JsonElement,object>(PlatformCapabilities.LlmChatStream,(r,_) => {
            if (modelCalls > 0) Assert.Contains("Both board-collaboration participants",r.GetRawText());
            modelCalls++; return Task.FromResult<object>(new {text="{\"candidate\":0,\"reason\":\"The question requests the manager's judgment.\"}"}); });
        var actions = selfCapabilityConsult ? new[] { JsonSerializer.Deserialize<SpecialistAgent.ReviewAction>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/prism-self-consultation.json")),new JsonSerializerOptions(JsonSerializerDefaults.Web))! } : [];
        var decision=new SpecialistAgent.ReviewChoice("Resolve the current uncertainty",null,false,false,"Contributor supplies scoped evidence","No invented observations",actions) {Disposition=disposition};
        var receipt=new SpecialistAgent.ReviewReceipt("review",null,decision,[new("brief",handoff.ArtifactId,"accepted","Approved outcome")],["game-technical-director","game-producer"],DateTimeOffset.UtcNow,"current-evidence");
        var context=runtime.CreateContext(identity:new(self.ToString(),"Owner",null,null,null,[],null,specialist.ToString(),"Marcus"));
        AgentLlmSelection? model = selfCapabilityConsult ? new(Guid.NewGuid(),"test") : null;
        Task Apply() => SpecialistAgent.ApplyReviewEffectsAsync(receipt,cycle,roster,handoff,context,default,model);
        if (rejections == 2)
        {
            Assert.Contains("reporting manager",(await Assert.ThrowsAsync<InvalidOperationException>(Apply)).Message);
            await Assert.ThrowsAsync<InvalidOperationException>(Apply);
            Assert.Equal(2,modelCalls);Assert.Equal(2,starts);Assert.Equal(0,cards);return;
        }
        await Apply(); await Apply();
        Assert.Equal(selfCapabilityConsult ? rejections + 1 : 0,modelCalls);
        Assert.Equal(rejections + 1,starts);Assert.Equal(1,cards);runtime.AssertCollaborationsTracked();
    }

    [Fact]
    public async Task Replayed_review_reuses_the_pending_hire_and_reads_its_authorized_result()
    {
        var project = Guid.NewGuid();
        var team = Guid.NewGuid();
        var producer = Guid.NewGuid();
        var manager = Guid.NewGuid();
        var cycle = new GameProductionPlanningCycleV1(project, team, 1, Guid.NewGuid(), "profile", Guid.NewGuid(), 1,
            "package", "production", "prototype", "scope");
        var handoff = new ProducerAcceptedHandoff(project, Guid.NewGuid(), Guid.NewGuid(), "brief", "handoff",
            Guid.NewGuid(), cycle.ApprovedPackageId, 1, DateTimeOffset.UtcNow);
        var roster = new AgentTeamContext(team.ToString(), "game", "Game", 1, producer.ToString(), "Producer", [], [], 0, false);
        var identity = new AgentIdentity(producer.ToString(), "Gabriel", null, null, null, [], null, manager.ToString(), "Marcus");
        var hire = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Id = Guid.NewGuid(), WorkstreamId = project, TeamId = team, RequesterOrganizationUserId = producer, ManagerOrganizationUserId = manager, Status = "Pending",
            Roles = [new ResourceChangeRole("game-designer", "studio", "Game Designer", "Tune interacting power-ups", 1, 1,
                "next sprint", ["work.execution.run"], false, producer, null)] };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var unscopedReads = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                    new Dictionary<string,string>(), [], "", [], Guid.NewGuid(), r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                (r, _) => { if (r.RequestId == null) unscopedReads++; else Assert.Equal(hire.Id, r.RequestId);
                    return Task.FromResult(new ResourceChangeReadResponse([hire])); });
        var decision = new SpecialistAgent.ReviewChoice("Repair collision", 0, true, true, "QA retest", "No new build", [
            new("resequence", "Address collision before scoring", null, [0]), new("hire", "Tune interacting power-ups", "GameDesigner", [0])]);
        var receipt = new SpecialistAgent.ReviewReceipt("review", "repair", decision,
            [new("brief", handoff.ArtifactId, "brief", "approved rules")], ["game-designer"], DateTimeOffset.UtcNow, "evidence");
        await SpecialistAgent.ApplyReviewEffectsAsync(receipt, cycle, roster, handoff, runtime.CreateContext(identity: identity), default);
        var restarted = runtime.CreateContext(identity: identity);
        var pending = await SpecialistAgent.RefreshReviewProposalsAsync(project, [], restarted, default);
        Assert.Equal("Pending", Assert.Single(pending).Revision);
        await SpecialistAgent.ApplyReviewEffectsAsync(receipt, cycle, roster, handoff, restarted, default);
        hire = hire with { Status = "Approved", DecidedByOrganizationUserId = manager };
        var approved = Assert.Single(await SpecialistAgent.RefreshReviewProposalsAsync(project, [], restarted, default));
        Assert.Equal(hire.Id, approved.Id);
        Assert.Equal("Approved", approved.Revision);
        Assert.Equal(1, unscopedReads);
    }
}
