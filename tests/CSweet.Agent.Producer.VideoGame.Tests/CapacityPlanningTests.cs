using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class CapacityPlanningTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task UnknownOrUnavailableCapacityGetsOneGroundedDecisionAndRetainsTheOwningTask(int? capacity)
    {
        var project = Guid.NewGuid(); var board = Guid.NewGuid(); var employee = Guid.NewGuid(); var manager = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var handoff = new ProducerAcceptedHandoff(project, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, now);
        var state = new ProducerOperatingState { AcceptedHandoffs = new Dictionary<Guid, ProducerAcceptedHandoff> { [project] = handoff } };
        var own = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Title = "Prepare the next increment", Description = "Plan scope", Priority = "High",
            Status = "Running", Revision = 1, WorkContext = new(project, BoardId: board, SourceFingerprint: "planning") };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var modelCalls = 0; var decisionCalls = 0; var dependencyWrites = 0; DecisionRecord? decision = null;
        var draft = new RequirementDecisionDraft("Which existing commitment should take priority?", "Only the affected increment waits.",
            [new("Prioritize the existing investigation", "Defer this increment", "Record the responsible owner's priority and revisit availability") { Impact = "The investigation goes first. The new work waits." },
             new("Make room for the next increment", "Resequence existing work within authority", "Obtain a fresh accountable-owner offer after the competing commitment changes") { Impact = "The team checks which work can move before setting a start date." }], 1);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, 1, "Active", new Dictionary<string, string>(), [], "test", [],
                    Guid.NewGuid(), r.Payload, 1, now, now)))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead,
                (_, _) => Task.FromResult(new ArtifactDocument(handoff.ArtifactId, "Brief", "video-game.production-plan.v1", "Accepted", handoff.AcceptedRevisionId, null,
                    handoff.AcceptedRevisionId, null, null, [new(handoff.AcceptedRevisionId, 1, null, "Deliver the authorized research increment", handoff.RevisionDigest, "Accepted", now, null, null)])))
            .RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) => {
                modelCalls++; Assert.Contains("Deliver the authorized research increment", r.GetRawText());
                return Task.FromResult<object>(new { text = JsonSerializer.Serialize(draft, JsonSerializerOptions.Web) });
            })
            .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
                decisionCalls++; Assert.Equal("work-planning", r.AuthorityRuleKey); Assert.Equal(project, r.WorkstreamId);
                Assert.Equal(handoff.AcceptedRevisionId, Assert.Single(r.Evidence).RevisionId);
                Assert.Equal(draft.Choices[0].Label, r.Options[0].Label);
                return Task.FromResult(decision = JsonSerializer.Deserialize<DecisionRecord>("{}")! with {
                    Id = Guid.NewGuid(), WorkstreamId = project, TypeKey = r.TypeKey, AuthorityRuleKey = r.AuthorityRuleKey, Summary = r.Summary,
                    TypeData = r.TypeData, Options = r.Options, Status = "Pending", Revision = 1 });
            })
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([decision!]))
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), employee, "Owner", manager, "Manager", 1, [own])], employee)))
            .RegisterCapability<UpdatePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Update, (r, _) => {
                dependencyWrites++; Assert.Equal(own.Id, r.ItemId); Assert.Equal(own.Revision, r.ExpectedRevision);
                return Task.FromResult(own = own with { WorkContext = r.WorkContext, Revision = own.Revision + 1 });
            });
        var context = runtime.CreateContext(identity: new(employee.ToString(), "Owner", null, null, null, [], null, manager.ToString(), "Manager"));
        var agent = new SpecialistAgent();
        await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update, JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(
            new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), context, default);
        var facts = JsonSerializer.SerializeToElement(new { capacity = new WorkCapacityAssessment(capacity, "Existing commitment needs priority resolution", capacity is null ? "Confirm priority" : null) });
        await agent.ResolveCapacityShortfallAsync(own, project, board, "planning", state, facts, context, default);
        await agent.ResolveCapacityShortfallAsync(own, project, board, "planning", state, facts, context, default);
        Assert.Equal(1, modelCalls); Assert.Equal(1, decisionCalls); Assert.Equal(1, dependencyWrites);
        Assert.Equal(decision!.Id, own.WorkContext!.DecisionId);
        Assert.Equal("Running", own.Status);
        Assert.True(RequirementDecisions.IsGrounded(decision));
    }
}
