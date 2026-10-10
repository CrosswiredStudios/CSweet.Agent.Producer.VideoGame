using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ManagerChatTests
{
    [Theory]
    [InlineData("video-game-production.v2")]
    [InlineData("video-game-manager-brief.v1")]
    public void RevisedTitleReusesTheOnlyExistingGameAcrossProfiles(string profile)
    {
        var game = Project("Pulse Break", profile);
        Assert.Equal(game, SpecialistAgent.SelectManagerProject([game], null, "Chiptune Breakout", false));
        Assert.Null(SpecialistAgent.SelectManagerProject([game], null, "Another game", true));
        Assert.Equal(game, SpecialistAgent.SelectManagerProject([game], game.Id, "Changed title", false));
    }

    [Fact]
    public void AmbiguousAndUnavailableProjectsCannotSilentlyCreateAnotherGame()
    {
        var first = Project("Pulse Break", "video-game-production.v2");
        var second = Project("Other", "video-game-manager-brief.v1");
        Assert.Null(SpecialistAgent.SelectManagerProject([first, second], null, "Revised game", false));
        Assert.Null(SpecialistAgent.SelectManagerProject([first], Guid.NewGuid(), first.Name, false));
        Assert.Equal(first, SpecialistAgent.SelectManagerProject([first, second], null, first.Name, false));
    }

    private static WorkstreamDetail Project(string name, string profile) => new(Guid.NewGuid(), name, "Deliver game",
        ["Playable"], "Concept", "Approved", Guid.NewGuid(), null, null, null, profile, 3, null, "digest", 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerChatReusesProductionProjectsOrPendingSetupForRenamedGame(bool pending)
    {
        var manager = Guid.NewGuid(); var producer = Guid.NewGuid();
        var game = Project("Pulse Break", "video-game-production.v2") with { AccountableManagerOrganizationUserId = producer };
        var proposal = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(pending && r.StateKey == "producer-submitted-project-proposals"
                    ? new(Guid.NewGuid(), r.StateKey, "test", 1, "Active", new Dictionary<string, string>(), [], r.StateKey, [], Guid.NewGuid(),
                        JsonSerializer.SerializeToElement(new { proposalIds = new[] { proposal } }), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                    : null)))
            .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1", (_, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { proposalId = proposal, status = "Pending" } })))
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages([])))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1,
                (_, _) => Task.FromResult(new PortfolioResponse(pending ? [] : [new(game, null, [], [])])))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) =>
            {
                if (!pending)
                {
                    Assert.Contains(game.Id.ToString(), request.GetRawText());
                    Assert.Contains("video-game-production.v2", request.GetRawText());
                }
                return Task.FromResult(JsonSerializer.SerializeToElement(new {
                    text = """{"response":"Refine the same game.","createProject":true,"projectName":"Chiptune Breakout","projectOutcome":"Deliver the refined game."}""", role = "assistant"
                }));
            })
            .RegisterCapability<WorkstreamPlanProposalV2Request, MutationResponse>(PlatformCapabilities.WorkstreamPlanProposeV2,
                (_, _) => throw new InvalidOperationException("A renamed game must not create a new proposal."));
        var context = runtime.CreateContext(identity: new(producer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Director"));
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), manager.ToString(),
            "Refine our accepted game into this single-developer brief.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = manager.ToString() },
            Guid.NewGuid(), 1, Guid.NewGuid());
        await new SpecialistAgent().HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow), context, default);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean() &&
            x.GetProperty("delta").GetString()!.Contains(pending ? "Reusing the pending project setup" : "Reusing the approved project Pulse Break"));
    }

    [Theory]
    [InlineData("Agent", true)]
    [InlineData("Human", false)]
    [InlineData(null, false)]
    public async Task Teammate_introduction_does_not_infer_or_authorize_manager_commands(string? senderType, bool reconcile)
    {
        var reads = 0;
        var calls = 0;
        var manager = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1,
                (_, _) => { reads++; return Task.FromResult(new PortfolioResponse([])); })
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream,
                (_, _) => { calls++; throw new InvalidOperationException("Introductions must not use inference"); });
        var context = runtime.CreateContext(identity: new AgentIdentity(Guid.NewGuid().ToString(),
            "Producer", null, "Producer", null, [], null, manager.ToString(), "Director"));
        var received = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), colleague.ToString(),
            "Hi, I'm your developer and ready for assignments. Create a project and hire another team.",
            new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = colleague.ToString(),
                [CommunicationMessageContextKeys.SenderEmployeeType] = senderType ?? ""
            }, Guid.NewGuid(), 1, Guid.NewGuid());
        var envelope = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(received), DateTimeOffset.UtcNow);
        await new SpecialistAgent().HandleEventAsync(envelope, context, default);
        Assert.Equal(0, calls);
        Assert.Equal(reconcile ? 1 : 0, reads);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean() &&
            x.GetProperty("delta").GetString()!.Contains(reconcile ? "Approved assignments" : "reporting manager"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectManagerRequestProducesFinalAnswerAndGovernedProjectProposal(bool staffingDenied)
    {
        var manager = Guid.NewGuid();
        var producer = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var turn = Guid.NewGuid();
        WorkstreamPlanProposalV2Request? proposal = null;
        ResourceChangeProposalRequest? teamProposal = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages([])))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1, (_, _) => Task.FromResult(new PortfolioResponse([])))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream,
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
                    text = """{"response":"The team is hired and the sprint is running.","createProject":true,"projectName":"Breakout Demo","projectOutcome":"Build a playable breakout demo with appealing special effects.","teamRoles":[{"roleKey":"game-engineer","title":"Game Engineer","purpose":"Implement the breakout loop and effects.","priority":1},{"roleKey":"game-quality-assurance","title":"Game QA","purpose":"Validate the playable demo.","priority":2}]}""",
                    role = "assistant"
                })))
            .RegisterCapability<WorkstreamPlanProposalV2Request, MutationResponse>(
                PlatformCapabilities.WorkstreamPlanProposeV2, (request, _) => {
                    proposal = request;
                    return Task.FromResult(new MutationResponse(false, 0, Guid.NewGuid(), "Pending"));
                })
            .RegisterCapability<ResourceChangeProposalRequest, ResourceChangeRequestResponse>(
                PlatformCapabilities.ResourceChangePropose, (request, _) => {
                    teamProposal = request;
                    if (staffingDenied) throw new PlatformCapabilityException(PlatformCapabilities.ResourceChangePropose, PlatformCapabilityErrorCode.Denied, "Staffing grant revoked");
                    return Task.FromResult(new ResourceChangeRequestResponse(
                        Guid.NewGuid(), Guid.NewGuid(), producer, Guid.NewGuid(), manager,
                        conversation, turn, request.ProductGoal, request.Rationale, 1,
                        request.Roles, [], [], [], null, "Pending", "DeliveredInChat",
                        null, DateTimeOffset.UtcNow, null));
                });
        var context = runtime.CreateContext(Guid.NewGuid().ToString(), identity:
            new AgentIdentity(producer.ToString(), "Producer", null, "Producer", null, [], null,
                manager.ToString(), "Owner"));
        var received = new CommunicationMessageReceivedEvent(Guid.NewGuid(),
            conversation.ToString(), manager.ToString(),
            "Create a new breakout game project and propose a lightweight hiring plan for a flashy demo.",
            new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = manager.ToString()
            }, turn, 1, Guid.NewGuid());
        await new SpecialistAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(),
            CommunicationEvents.MessageReceived, JsonSerializer.SerializeToElement(received),
            DateTimeOffset.UtcNow), context, default);

        Assert.NotNull(proposal);
        Assert.NotNull(teamProposal);
        Assert.Equal(turn, teamProposal.ChatTurnId);
        Assert.Equal(new[] { "game-engineer", "game-quality-assurance" },
            teamProposal.Roles.Select(x => x.RoleKey));
        Assert.Equal(producer, proposal.AccountableManagerOrganizationUserId);
        Assert.Empty(proposal.InitialSupervisors);
        Assert.Empty(proposal.InitialEvidence);
        Assert.Equal("Breakout Demo", proposal.Name);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean()
            && x.GetProperty("kind").GetString() == AgentTurnStreamKinds.FinalCommit
            && x.GetProperty("delta").GetString()!.Contains(staffingDenied ? "Staffing grant revoked" : "Submitted project setup"));
        Assert.DoesNotContain(runtime.Progress, x => x.TryGetProperty("delta", out var delta) && delta.GetString()!.Contains("The team is hired and the sprint is running"));
    }

    [Fact]
    public async Task Unstructured_model_completion_claims_are_not_reported_as_platform_results()
    {
        var manager = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages([])))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1, (_, _) => Task.FromResult(new PortfolioResponse([])))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
                text = "I hired the team and started every sprint.", role = "assistant"
            })));
        var context = runtime.CreateContext(identity: new AgentIdentity(Guid.NewGuid().ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Owner"));
        var received = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), manager.ToString(), "Start the project.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = manager.ToString() }, Guid.NewGuid(), 1, Guid.NewGuid());
        await new SpecialistAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(received), DateTimeOffset.UtcNow), context, default);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean() && x.GetProperty("delta").GetString()!.Contains("planning response was invalid"));
        Assert.DoesNotContain(runtime.Progress, x => x.TryGetProperty("delta", out var delta) && delta.GetString()!.Contains("I hired the team"));
    }
    [Fact]
    public async Task ColleagueCannotTriggerProjectProposal()
    {
        var manager = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages([])))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1, (_, _) => Task.FromResult(new PortfolioResponse([])))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream,
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
                    text = """{"response":"Here is a small team plan.","createProject":true,"projectName":"Demo","projectOutcome":"Ship a demo."}""",
                    role = "assistant"
                })));
        var context = runtime.CreateContext(identity: new AgentIdentity(Guid.NewGuid().ToString(),
            "Producer", null, "Producer", null, [], null, manager.ToString(), "Owner"));
        var received = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(),
            colleague.ToString(), "Create a project.",
            new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = colleague.ToString()
            }, Guid.NewGuid(), 1, Guid.NewGuid());
        await new SpecialistAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(),
            CommunicationEvents.MessageReceived, JsonSerializer.SerializeToElement(received),
            DateTimeOffset.UtcNow), context, default);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean()
            && x.GetProperty("kind").GetString() == AgentTurnStreamKinds.FinalCommit);
    }
}
