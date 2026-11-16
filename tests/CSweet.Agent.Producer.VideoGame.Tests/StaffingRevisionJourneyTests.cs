using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StaffingRevisionJourneyTests
{
    [Fact]
    public async Task Existing_Brickfall_follows_each_new_review_without_duplicate_proposals_or_early_QA()
    {
        var f=new Fixture(); var agent=await f.Agent();
        for(var round=0;round<4;round++)
        {
            var previous=f.Requests.OrderByDescending(r=>r.CreatedAt).First();
            var result=await agent.AssessKickoffCoverageAsync(f.Handoff,null,f.Roster,f.Context,default);
            var proposal=f.Proposals.Last();
            Assert.Equal(previous.Id,proposal.RevisesRequestId); Assert.Equal(f.Baseline.Id,proposal.SupersedesRequestId);
            Assert.Contains(proposal.Roles, r => r.RoleKey == "game-producer");
            Assert.Contains(proposal.Roles, r => r.RoleKey == "creative-director");
            var role=Assert.Single(proposal.Roles,r=>r.RoleKey=="game-technical-director");
            Assert.DoesNotContain(proposal.Roles,r=>r.RoleKey=="game-quality-assurance");
            Assert.Equal(f.Manager,role.ReportsToOrganizationUserId); Assert.Equal("Immediate technical planning",role.Timing);
            Assert.Contains("FINAL DELIVERABLE AND ESCALATION",role.Purpose);
            Assert.True(role.Purpose.Length>12000);
            Assert.Contains(PlatformCapabilities.ArtifactCreate,role.RequiredCapabilities);
            Assert.Contains(PlatformCapabilities.ArtifactSubmit,role.RequiredCapabilities);
            Assert.Equal("Pending",result.Response!.Status);
            var count=f.Proposals.Count;
            await agent.AssessKickoffCoverageAsync(f.Handoff,null,f.Roster,f.Context,default);
            Assert.Equal(count,f.Proposals.Count); Assert.Equal(round+1,f.ModelCalls);
            var index=f.Requests.FindIndex(r=>r.Id==result.Response.Id);
            f.Requests[index]=result.Response with {Status="RevisionRequested",DecisionComment="Additional current review "+round,DecidedAt=DateTimeOffset.UtcNow};
        }
        Assert.Equal(4,f.Proposals.Count);
    }

    [Fact]
    public async Task Accepted_submission_survives_interrupted_reply_without_another_model_or_proposal()
    {
        var f=new Fixture {InterruptReply=true}; var agent=await f.Agent();
        await Assert.ThrowsAsync<IOException>(()=>agent.AssessKickoffCoverageAsync(f.Handoff,null,f.Roster,f.Context,default));
        var recovered=await agent.AssessKickoffCoverageAsync(f.Handoff,null,f.Roster,f.Context,default);
        Assert.Equal("Pending",recovered.Response!.Status); Assert.Single(f.Proposals); Assert.Equal(1,f.ModelCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Platform_rejection_is_returned_to_model_once_and_a_second_failure_has_a_durable_owner(int rejections)
    {
        var f = new Fixture { RejectionsRemaining = rejections }; var agent = await f.Agent();
        var result = await agent.AssessKickoffCoverageAsync(f.Handoff, null, f.Roster, f.Context, default);
        Assert.Equal(2, f.SubmissionAttempts); Assert.Equal(2, f.ModelCalls);
        if (rejections == 1) Assert.Equal("Pending", result.Response!.Status);
        else { Assert.Null(result.Response); Assert.Contains("Producer owns", result.Reason); Assert.Contains("Full platform rejection", result.Reason); }
        await agent.AssessKickoffCoverageAsync(f.Handoff, null, f.Roster, f.Context, default);
        Assert.Equal(2, f.SubmissionAttempts); Assert.Equal(2, f.ModelCalls);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Blocked")]
    public async Task Reconnect_recovers_the_existing_Brickfall_commitment_without_an_event_or_new_ticket(string status)
    {
        var f=new Fixture(); var fingerprint=ProducerPolicyFingerprint.ForPlanning(f.Project,0,"",f.Handoff.HandoffDigest);
        var existing=JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with {Id=f.Data.GetProperty("task").GetGuid(),Revision=6,
            CorrelationId=$"producer-planning:{f.Project:N}:{fingerprint}",Status=status,UpdatedAt=DateTimeOffset.UtcNow.AddMinutes(-1),
            Wait=status=="Running"?new(DateTimeOffset.UtcNow.AddHours(2),"Old wording: awaiting a review that already finished"):null,
            WorkContext=new(WorkstreamId:f.Project,TeamId:f.Team,SourceFingerprint:fingerprint)};
        var unrelated=existing with {Id=Guid.NewGuid(),CorrelationId="another-project",WorkContext=new(WorkstreamId:Guid.NewGuid())};
        var requeues=0;
        f.Runtime.RegisterCapability<object,PersonalTodoDirectory>(PersonalTodoCapabilities.Read,(_,_)=>
                Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(),f.Owner,"Producer",f.Manager,"Manager",1,[existing,unrelated])])))
            .RegisterCapability<TeamRosterV2Request,TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,(_,_)=>Task.FromResult(new TeamRosterV2Response(f.Roster,f.Project)))
            .RegisterCapability<RequeuePersonalTodoItemRequest,PersonalTodoItem>(PersonalTodoCapabilities.Requeue,(r,_)=>{
                Assert.Equal(existing.Id,r.ItemId);Assert.Equal(existing.Revision,r.ExpectedRevision);requeues++;
                existing=existing with {Status="Ready",Wait=null,Revision=existing.Revision+1,UpdatedAt=DateTimeOffset.UtcNow};
                return Task.FromResult(existing);
            });
        var project=JsonSerializer.Deserialize<WorkstreamDetail>("{}")! with {Id=f.Project};
        for(var i=0;i<3;i++) await SpecialistAgent.EnsurePlanningCommitmentAsync(project,f.Team,null,f.Handoff.CoordinationSessionId,
            f.Handoff.HandoffDigest,f.Context,default,f.Handoff.RevisionDigest);
        Assert.Equal(1,requeues);Assert.Equal(f.Data.GetProperty("task").GetGuid(),existing.Id);Assert.Equal(7,existing.Revision);
        Assert.Equal(status,unrelated.Status); // Other work is not invalidated by this recovery.
    }

    private sealed class Fixture
    {
        public JsonElement Data {get;}=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","brickfall-staffing-return.json"))).RootElement.Clone();
        public Guid Owner=>Data.GetProperty("owner").GetGuid(); public Guid Manager=>Data.GetProperty("manager").GetGuid();
        public Guid Project=>Data.GetProperty("project").GetGuid(); public Guid Team=>Data.GetProperty("team").GetGuid();
        public AgentTestRuntime Runtime {get;}=new(); public AgentRuntimeContext Context {get;}
        public AgentTeamContext Roster {get;}
        public ProducerAcceptedHandoff Handoff {get;}
        public List<ResourceChangeRequestResponse> Requests {get;}=[];
        public List<ResourceChangeProposalRequest> Proposals {get;}=[];
        public ResourceChangeRequestResponse Baseline {get;}
        public int ModelCalls; public bool InterruptReply; public int RejectionsRemaining; public int SubmissionAttempts;
        private readonly Dictionary<string,AgentOperatingStateResponse> states=[];
        public Fixture()
        {
            Context=Runtime.CreateContext(identity:new(Owner.ToString(),"Producer",null,"Producer",null,[],null,Manager.ToString(),"Manager"));
            Roster=new(Team.ToString(),"studio","Studio",3,Manager.ToString(),"Manager",[],[],0,false);
            Handoff=new(Project,Guid.NewGuid(),Guid.NewGuid(),"accepted-brief","handoff",Guid.NewGuid(),Guid.NewGuid(),1,DateTimeOffset.UtcNow);
            ResourceChangeRequestResponse Empty()=>JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
                RequesterOrganizationUserId=Owner,ManagerOrganizationUserId=Manager,TeamId=Team,WorkstreamId=Project,
                Roles=[],Deltas=[],Evidence=[new("scope-capability-gap",Handoff.RevisionDigest,"Accepted sources")],CreatedAt=DateTimeOffset.UtcNow.AddHours(-2)};
            // The manager assembled the initial team before the Producer owned project staffing.
            Baseline=Empty() with {Id=Guid.NewGuid(),RequesterOrganizationUserId=Manager,ManagerOrganizationUserId=Guid.NewGuid(),
                Status="Approved",WorkstreamId=null,Roles=[
                new("game-producer","studio","Producer","Coordinate",1,1,"Now",["work.execution.run"],false,Manager,null),
                new("creative-director","studio","Creative","Creative direction",1,1,"Now",["work.execution.run"],false,Manager,null)]};
            Requests.Add(Baseline);
            Requests.Add(Empty() with {Id=Data.GetProperty("request").GetGuid(),Status="RevisionRequested",DecisionComment=Data.GetProperty("review").GetString(),
                DecidedAt=DateTimeOffset.UtcNow.AddHours(-1),CreatedAt=DateTimeOffset.UtcNow.AddHours(-1)});
            Runtime.RegisterCapability<ResourceChangeReadRequest,ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,(_,_)=>Task.FromResult(new ResourceChangeReadResponse(Requests.ToArray())))
                .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>{
                    var saved=new AgentOperatingStateResponse(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,[],"",[],Guid.NewGuid(),r.Payload,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                    states[r.StateKey]=saved;return Task.FromResult(saved);
                })
                .RegisterCapability<object,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(_,_)=>Task.FromResult(new ArtifactDocument(Handoff.ArtifactId,"Brief","scope","Accepted",Handoff.AcceptedRevisionId,null,Handoff.AcceptedRevisionId,null,null,
                    [new(Handoff.AcceptedRevisionId,1,null,"Accepted visual, audio and technical scope",Handoff.RevisionDigest,"Accepted",DateTimeOffset.UtcNow,null,null)])))
                .RegisterCapability<CreateCommunicationChat,object>(CommunicationCapabilities.ChatCreate,(_,_)=>Task.FromResult<object>(new{chat=new{id=Guid.NewGuid()}}))
                .RegisterCapability<ResourceChangeProposalRequest,ResourceChangeRequestResponse>(PlatformCapabilities.ResourceChangePropose,(r,_)=>{
                    SubmissionAttempts++;
                    if (RejectionsRemaining-- > 0) throw new PlatformCapabilityException(PlatformCapabilities.ResourceChangePropose,
                        PlatformCapabilityErrorCode.ValidationFailed, "Full platform rejection: revise the work description.");
                    Proposals.Add(r);var saved=Empty() with {Id=Guid.NewGuid(),Status="Pending",RevisesRequestId=r.RevisesRequestId,CreatedAt=DateTimeOffset.UtcNow,
                        Roles=r.Roles,Rationale=r.Rationale,Evidence=r.Evidence,SupersedesRequestId=r.SupersedesRequestId};Requests.Add(saved);
                    if(InterruptReply){InterruptReply=false;throw new IOException("Response interrupted after platform accepted request");}
                    return Task.FromResult(saved);
                })
                .RegisterCapability<JsonElement,object>(PlatformCapabilities.LlmChatStream,(r,_)=>{
                    ModelCalls++;var prompt=r.GetProperty("messages")[1].GetProperty("text").GetString()!;
                    Assert.Contains(Requests.OrderByDescending(x=>x.CreatedAt).First().DecisionComment!,prompt);
                    using var facts = JsonDocument.Parse(prompt);
                    if (facts.RootElement.GetProperty("platformRejection").ValueKind == JsonValueKind.String)
                        Assert.Contains("Full platform rejection", prompt);
                    var work=new string('x',12000)+" FINAL DELIVERABLE AND ESCALATION";
                    return Task.FromResult<object>(new{text=JsonSerializer.Serialize(new SpecialistAgent.CoverageAssessment("Evidence-backed technical work",[
                        new("TechnicalDirector",work){NeededNow=true,Timing="Immediate technical planning",ReportingLine="Manager",Accountability="Direct technical escalation",Engagement="Bounded feasibility and decomposition"},
                        new("QualityAssurance","Later independent acceptance"){NeededNow=false,Timing="After a testable build",ReportingLine="Manager",Accountability="Independent acceptance",Engagement="Acceptance contribution"}]),new JsonSerializerOptions(JsonSerializerDefaults.Web))});
                });
        }
        public async Task<SpecialistAgent> Agent()
        {
            var agent=new SpecialistAgent();
            var configured=await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement>{
                ["llmProviderId"]=JsonSerializer.SerializeToElement(Guid.NewGuid()),["llmModel"]=JsonSerializer.SerializeToElement("fixture-model")}))),Context,default);
            Assert.True(configured.Succeeded,configured.Error);return agent;
        }
    }
}
