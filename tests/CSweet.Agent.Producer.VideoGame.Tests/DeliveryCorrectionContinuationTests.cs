using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DeliveryCorrectionContinuationTests
{
    [Fact]
    public async Task Manager_wake_uses_configured_source_planner_and_saves_selection_before_execution_or_reporting()
    {
        var manager = Guid.NewGuid();
        var project = Guid.NewGuid();
        var board = Guid.NewGuid();
        var repository = Guid.NewGuid();
        var story = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var findingId = Guid.NewGuid();
        var execution = Guid.NewGuid();
        var scopeText = "Implement the placeholder scene and sample mapping";
        var summary = "The explosive sample selects the tough brick colour.";
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var planning = new WorkItemPlanningSpecification([scopeText], ["Sample mapping matches documented colours"]);
        var source = JsonSerializer.Deserialize<WorkItem>("{}", json)! with {
            Id = Guid.NewGuid(), Title = "Scene implementation", Kind = "Task", TypeKey = "code-task", Status = "Completed",
            Revision = 1, PlanningRevision = 1, ParentItemId = story, Planning = planning,
            Delivery = new(repository, planning.Requirements, planning.AcceptanceCriteria) { DeliveryKind = "Code", DeliveryPlanId = planId },
            StageAssignments = [new("development", "Human", Guid.NewGuid())]
        };
        var dependency = source with { Id = Guid.NewGuid(), Title = "Dependency pin", Planning = new(["Pin Phaser only; scene code is out of scope."], ["Lockfile matches exact pin"]) };
        var correction = new WorkDeliveryCorrection(Guid.NewGuid(), execution, new string('a', 64), board, manager, "NeedsPlanning", 2, [findingId]);
        var prepare = new WorkDeliveryTicketStep("prepare", "completion", new("prepare", "Human", manager));
        var plan = new WorkDeliveryPlanResponse(planId, project, "Increment", manager, "Active", 1, 1, [],
            [new(repository, "Story", story, "story", "release")],
            [new("Story", story, board, 1, [source.Id, dependency.Id], planning, [prepare.Assignment]) {
                TicketWorkflow = new(Guid.NewGuid(), [prepare]) { Scope = "Story", ScopeItemId = story }
            }, new("Release", null, board, 1, [], planning, [])],
            [new(execution, "Release", null, board, "Blocked", "quality", 1,
                new(new string('a', 64), 1, [new(repository, "release", "integration", "a", "b", "c")], []), summary, [], [])],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) {
            Corrections = [correction], Findings = [new(findingId, execution, correction.CandidateDigest, summary, "Open", [], null)]
        };
        var modelCalls = 0;
        var effects = new List<string>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkDeliveryPlansRequest, IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>([plan]))
            .RegisterCapability<JsonElement, JsonElement>(WorkItemCapabilities.Read, (request, _) => Task.FromResult(
                request.TryGetProperty("itemId", out var itemId) ? JsonSerializer.SerializeToElement(source, json) :
                JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<WorkBoardDetail>("{}", json)! with { Items = [dependency, source] }, json)))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) => {
                modelCalls++;
                Assert.Equal(provider, request.GetProperty("providerProfileId").GetGuid());
                Assert.Equal("test-manager-model", request.GetProperty("model").GetString());
                Assert.Equal("delivery-correction-source", request.GetProperty("telemetry").GetProperty("invocationKind").GetString());
                Assert.Equal(0, request.GetProperty("tools").GetArrayLength());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant", text = JsonSerializer.Serialize(new {
                    disposition = "Select", reason = "The finding concerns scene mapping, excluded from dependency scope.",
                    choices = new[] { new { findingId, sourceItemId = source.Id, scopeEvidence = scopeText, findingEvidence = summary } }
                }) }));
            })
            .RegisterCapability<CoordinateWorkDeliveryCorrectionRequest, WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Correct, (request, _) => {
                Assert.Equal(source.Id, request.SourceItemId);
                effects.Add("source-selected");
                plan = plan with { Revision = plan.Revision + 1, Corrections = [correction with { SourceItemId = source.Id, Revision = correction.Revision + 1 }] };
                return Task.FromResult(plan);
            })
            .RegisterCapability<CreateWorkItemRequest, WorkItem>(WorkItemCapabilities.Create, (request, _) => {
                Assert.Equal("source-selected", Assert.Single(effects));
                Assert.Equal(source.ParentItemId, request.ParentItemId);
                effects.Add("engineering-create");
                throw new IOException("Stop at the execution boundary; no incident reporting was needed.");
            });
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Producer", null, null, null, [], null, null, null) {
            AssignedProject = new(project, "Prism Break", null, board, "Producer", 1, DateTimeOffset.UtcNow)
        });
        var agent = new SpecialistAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(provider),
                ["llmModel"] = JsonSerializer.SerializeToElement("test-manager-model")
            }))), context, default);
        Assert.True(configured.Succeeded, configured.Error);

        await Assert.ThrowsAsync<IOException>(() => agent.HandleAttentionReviewAsync(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "delivery.changed"), context, default));

        Assert.Equal(1, modelCalls);
        Assert.Equal(new[] { "source-selected", "engineering-create" }, effects);
    }
}
