using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ProjectProposalValidationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outcome\":null}")]
    [InlineData("{\"outcome\":{\"text\":\"Game\"}}")]
    [InlineData("not JSON")]
    [InlineData("{\"outcome\":\"Game\",\"rationale\":\"Deliver\",\"successCriteria\":null,\"initialMilestones\":[]}")]
    public async Task InvalidOutputIsCorrectedBeforeSavingOrSubmitting(string invalid)
    {
        var f = new Fixture();
        f.Responses.Enqueue(invalid);
        f.Responses.Enqueue(f.ValidJson);
        var result = await f.RunAsync();
        Assert.Equal("Continue", result.Disposition);
        Assert.Equal(2, f.ModelCalls);
        Assert.Equal("Playable game", Assert.Single(f.Submissions).Outcome);
        Assert.Contains(f.ProposalId.ToString(), f.Cache["producer-submitted-project-proposals"].Payload.GetRawText());
        await f.RunAsync();
        Assert.Equal(2, f.ModelCalls);
        Assert.Equal(f.Submissions[0].IdempotencyKey, f.Submissions[1].IdempotencyKey);
    }

    [Fact]
    public async Task CorrectedModelSettingsRetryInvalidDraftButCannotChangeAnAcceptedCommand()
    {
        var f = new Fixture(); f.Responses.Enqueue("{}"); f.Responses.Enqueue("{}");
        Assert.Equal("Blocked", (await f.RunAsync()).Disposition);
        f.ModelName = "corrected-model"; f.Responses.Enqueue(f.ValidJson);
        Assert.Equal("Continue", (await f.RunAsync()).Disposition);
        Assert.Equal(3, f.ModelCalls);
        f.ModelName = "another-model";
        await f.RunAsync();
        Assert.Equal(3, f.ModelCalls);
        Assert.Equal(f.Submissions[0].IdempotencyKey, f.Submissions[1].IdempotencyKey);
    }

    [Fact]
    public async Task RepeatedInvalidOutputBlocksWithNoSubmissionAndNoUnboundedRedeliveryLoop()
    {
        var f = new Fixture(); f.Responses.Enqueue("{}"); f.Responses.Enqueue("{}");
        var result = await f.RunAsync();
        var replay = await f.RunAsync();
        Assert.Equal("Blocked", result.Disposition);
        Assert.Equal(result.Content, replay.Content);
        Assert.Empty(f.Submissions);
        Assert.Equal(2, f.ModelCalls);
    }

    [Fact]
    public async Task InvalidLegacyCachedPlanIsRepairedInsteadOfReplayed()
    {
        var f = new Fixture();
        f.Cache[f.PlanKey] = f.State(f.PlanKey, JsonSerializer.SerializeToElement(f.Template with { Outcome = null! }));
        f.Responses.Enqueue(f.ValidJson);
        Assert.Equal("Continue", (await f.RunAsync()).Disposition);
        Assert.Equal("Playable game", Assert.Single(f.Submissions).Outcome);
    }

    [Fact]
    public async Task ValidLegacyCommandIsReusedWithoutGeneratingAConflictingProposal()
    {
        var f = new Fixture();
        f.Cache[f.PlanKey] = f.State(f.PlanKey, JsonSerializer.SerializeToElement(f.Template with { IdempotencyKey = "already-submitted" }));
        await f.RunAsync();
        Assert.Equal(0, f.ModelCalls);
        Assert.Equal("already-submitted", Assert.Single(f.Submissions).IdempotencyKey);
    }

    [Fact]
    public async Task MilestoneAuthorityCannotBeRemovedByTheModel()
    {
        var f = new Fixture();
        f.Template = f.Template with { InitialMilestones = [new("launch", "Launch review", "Concept", null, ["release"], ["owner"])] };
        f.Responses.Enqueue(JsonSerializer.Serialize(f.Template with { InitialMilestones = [new("launch", "Launch", "Concept", null, [], [])] }, Fixture.Json));
        f.Responses.Enqueue(f.ValidJson);
        await f.RunAsync();
        Assert.Equal(2, f.ModelCalls);
        Assert.Contains("owner", Assert.Single(Assert.Single(f.Submissions).InitialMilestones).RequiredReviewerRoleKeys);
    }

    [Fact]
    public async Task PlatformValidationFailureBecomesAnActionableCollaborationBlocker()
    {
        var f = new Fixture { RejectSubmission = true }; f.Responses.Enqueue(f.ValidJson);
        var result = await f.RunAsync();
        Assert.Equal("Blocked", result.Disposition);
        Assert.Contains("platform rejected", result.Content);
        Assert.DoesNotContain("producer-submitted-project-proposals", f.Cache.Keys);
    }

    private sealed class Fixture
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Guid Producer { get; } = Guid.NewGuid();
        public Guid Manager { get; } = Guid.NewGuid();
        public Guid Session { get; } = Guid.NewGuid();
        public Guid ProposalId { get; } = Guid.NewGuid();
        public string PlanKey => $"project-foundation-plan:{Session:N}:1";
        public WorkstreamPlanProposalV2Request Template { get; set; }
        public string ValidJson => JsonSerializer.Serialize(Template, Json);
        public Queue<string> Responses { get; } = new();
        public Dictionary<string, AgentOperatingStateResponse> Cache { get; } = new();
        public List<WorkstreamPlanProposalV2Request> Submissions { get; } = [];
        public int ModelCalls { get; private set; }
        public bool RejectSubmission { get; init; }
        public string ModelName { get; set; } = "test";
        private readonly SpecialistAgent agent = new();
        private readonly AgentRuntimeContext context;
        public Fixture()
        {
            Template = new("Game", "Playable game", ["Playable"], "Concept", Producer, null, [], [], null, null, null, null,
                "Deliver", "template", "game", 1, JsonSerializer.SerializeToElement(new { }), new(null, 14, [], ["launch"], [], null), [], []);
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(Cache.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => Task.FromResult(Cache[r.StateKey] = State(r.StateKey, r.Payload)))
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) => {
                    ModelCalls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { text = Responses.Dequeue(), role = "assistant" })); })
                .RegisterCapability<WorkstreamPlanProposalV2Request, MutationResponse>("platform.workstream.plan.propose.v2", (r, _) => {
                    if (RejectSubmission) throw new PlatformCapabilityException("platform.workstream.plan.propose.v2", PlatformCapabilityErrorCode.ValidationFailed,
                        "Profile is invalid.", failureCode: "platform.capability.validation_failed");
                    Submissions.Add(r); return Task.FromResult(new MutationResponse(false, 0, ProposalId, "Pending")); });
            context = runtime.CreateContext(identity: new(Producer.ToString(), "Producer", null, "Producer", null, [], null, Manager.ToString(), "Director"));
        }
        public AgentOperatingStateResponse State(string key, JsonElement payload) => new(Guid.NewGuid(), key, "test", 1, "Active",
            new Dictionary<string, string>(), [], key, [], Guid.NewGuid(), payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public async Task<AgentCoordinationTurnResult> RunAsync()
        {
            await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
                JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Producer), ["llmModel"] = JsonSerializer.SerializeToElement(ModelName) }))), context, default);
            return await agent.HandleCoordinationTurnAsync(new(Session, 1, 1, "Create project", "Review", [],
                new(Producer, Guid.NewGuid(), "Producer", "Producer"), new(Manager, Guid.NewGuid(), "Director", "Director"), false,
                [new(Guid.NewGuid(), 0, Manager, "Continue", "Prepare proposal", DateTimeOffset.UtcNow,
                    new("video-game.project-foundation.request.v1", "1.0", "vision", 1, true, JsonSerializer.SerializeToElement(Template, Json), "digest"))]), context, default);
        }
    }
}
