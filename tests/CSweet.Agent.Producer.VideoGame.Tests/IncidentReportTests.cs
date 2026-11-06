using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class IncidentReportTests
{
    [Fact]
    public async Task Current_qa_operation_mismatch_corrects_cached_capacity_advice_without_a_model_call()
    {
        var f = new Fixture(false);
        var review = f.Review with { Diagnostics = f.Review.Diagnostics with { CurrentState = f.Review.Diagnostics.CurrentState! with {
            Facts = [new("WorkStageExecution", Guid.NewGuid(), DateTimeOffset.UtcNow,
                "quality: Failed. The QA compute operation is unavailable or its generation changed.")]
        } } };
        review = review with { Incident = review.Incident with { OwnerReport = ManagerIncidentCoordinator.FactualFallback(review)! with {
            Title = "Restore the browser environment", RequiresOwnerInput = true } } };
        var report = await f.Agent.GenerateIncidentOwnerReportAsync(review, f.Context, default);
        ManagerIncidentCoordinator.ValidateReport(report!);
        Assert.Contains("tracking mismatch", report!.Title); Assert.False(report.RequiresOwnerInput);
        Assert.Contains("verdict remains incomplete", report.Summary); Assert.Empty(report.Options); Assert.Equal(0, f.Calls);
        Assert.Null(SpecialistAgent.QaOperationIncidentReport(review with { Incident = review.Incident with { Status = "Resolved" } }));
        Assert.Null(SpecialistAgent.QaOperationIncidentReport(review with { Diagnostics = review.Diagnostics with {
            CurrentState = review.Diagnostics.CurrentState with { Facts = [] } } }));
    }
    [Fact]
    public async Task Blocked_mcp_error_gets_an_actionable_owner_without_another_model_call_or_unproved_capacity_claim()
    {
        var f = new Fixture(false);
        var state = f.Review.Diagnostics.CurrentState!;
        var review = f.Review with { Diagnostics = f.Review.Diagnostics with { CurrentState = state with {
            Facts = [new("WorkStageExecution", Guid.NewGuid(), DateTimeOffset.UtcNow,
                "specialist-execution: Blocked. The MCP service rejected the request: Internal Server Error")]
        } } };
        var report = await f.Agent.GenerateIncidentOwnerReportAsync(review, f.Context, default);
        Assert.NotNull(report);
        ManagerIncidentCoordinator.ValidateReport(report);
        Assert.Contains("Producer:", report.NextStep);
        Assert.Contains("exact stopped stage", report.NextStep);
        Assert.DoesNotContain("certified", report.Recovery);
        Assert.False(report.RequiresOwnerInput);
        Assert.Equal(0, f.Calls);
        Assert.Null(SpecialistAgent.TransportIncidentReport(review with { Incident=review.Incident with {Status="Resolved"} }));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_reporting_retains_full_evidence_without_tools_or_cached_execution_promises(bool invalid)
    {
        var f = new Fixture(invalid);
        var first = await f.Agent.GenerateIncidentOwnerReportAsync(f.Review, f.Context, default);
        Assert.NotNull(first);
        ManagerIncidentCoordinator.ValidateReport(first);
        if (!invalid) Assert.Equal("Planning needs task detail", first.Title);
        var request = Assert.Single(f.Requests);
        Assert.Equal(JsonValueKind.Null, request.GetProperty("maxOutputTokens").ValueKind);
        Assert.True(!request.TryGetProperty("tools", out var tools) || tools.ValueKind == JsonValueKind.Null || tools.GetArrayLength() == 0);
        var evidence = request.GetProperty("messages")[1].GetProperty("text").GetString()!;
        Assert.Contains("critical-fact-24", evidence);
        Assert.Contains("critical-limitation-tail", evidence);
        Assert.Equal(25, JsonDocument.Parse(evidence).RootElement.GetProperty("current").GetArrayLength());
        Assert.Empty(f.States);
        await f.Agent.GenerateIncidentOwnerReportAsync(f.Review, f.Context, default);
        Assert.Equal(2, f.Calls);
        Assert.Empty(f.States);
    }

    [Fact]
    public async Task ExistingCurrentReportNeedsNeitherModelNorStateRead()
    {
        var f = new Fixture(false);
        var report = ManagerIncidentCoordinator.FactualFallback(f.Review)!;
        var result = await new SpecialistAgent().GenerateIncidentOwnerReportAsync(f.Review with {
            Incident = f.Review.Incident with { OwnerReport = report }
        }, new AgentTestRuntime().CreateContext(), default);
        Assert.Same(report, result);
        Assert.Equal(0, f.Calls);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoACachedReport()
    {
        var f = new Fixture(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Agent.GenerateIncidentOwnerReportAsync(f.Review, f.Context, cancelled.Token));
        Assert.Empty(f.States);
    }

    private sealed class Fixture
    {
        public SpecialistAgent Agent { get; } = new();
        public AgentRuntimeContext Context { get; }
        public ManagerIncidentContext Review { get; }
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public int Calls;
        public List<JsonElement> Requests { get; } = [];

        public Fixture(bool invalid)
        {
            var self = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var incident = new ManagementIncident(Guid.NewGuid(), Guid.NewGuid(), self, self, "Open", "Planning interrupted", null,
                now, now, now.AddMinutes(15), 3, "Recorded failure", "Unknown", "Recovery unconfirmed", "Check planning", []);
            var evidence = Enumerable.Range(0, 25).Select(i => new ProjectDiagnosticEvidence("CurrentCollaboration", Guid.NewGuid(), now,
                "Planning: Active. " + new string('x', 2000) + "critical-fact-" + i)).ToArray();
            Review = new(incident, new(evidence, 100, [new string('x', 2000) + "critical-limitation-tail"]) {
                CurrentState = new(new string('A', 64), "Game", evidence, []) }, "stable-action");
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    var value = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, 1, "Active", new Dictionary<string, string>(), [], r.DecisionFingerprint,
                        [], Guid.NewGuid(), r.Payload, 1, now, now);
                    States.Add(r.StateKey, value);
                    return Task.FromResult(value);
                })
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, token) => {
                    token.ThrowIfCancellationRequested();
                    Calls++;
                    Requests.Add(r.Clone());
                    return Task.FromResult(JsonSerializer.SerializeToElement(new {
                        role = "assistant", text = invalid ? "invalid report" : JsonSerializer.Serialize(ManagerIncidentCoordinator.FactualFallback(Review)! with {
                            Title = "Planning needs task detail"
                        })
                    }));
                });
            Context = runtime.CreateContext();
            var configured = Agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
                JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                    ["llmModel"] = JsonSerializer.SerializeToElement("test")
                }))), Context, default).GetAwaiter().GetResult();
            Assert.True(configured.Succeeded, configured.Error);
        }
    }
}
