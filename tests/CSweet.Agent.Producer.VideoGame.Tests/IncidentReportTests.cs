using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class IncidentReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportIsOneBoundedCallPerEvidenceHashAcrossRestartIncludingInvalidOutput(bool invalid)
    {
        var f = new Fixture(invalid);
        var first = await f.Agent.GenerateIncidentOwnerReportAsync(f.Review, f.Context, default);
        Assert.NotNull(first);
        ManagerIncidentCoordinator.ValidateReport(first);
        if (!invalid) Assert.Equal("Planning needs task detail", first.Title);
        var request = Assert.Single(f.Requests);
        Assert.Equal(1600, request.GetProperty("maxOutputTokens").GetInt32());
        Assert.True(!request.TryGetProperty("tools", out var tools) || tools.ValueKind == JsonValueKind.Null || tools.GetArrayLength() == 0);
        Assert.True(request.GetProperty("messages").EnumerateArray().Sum(x => x.GetProperty("text").GetString()!.Length) < 20_000);
        var restarted = new SpecialistAgent(); // No provider: persisted result must be reusable.
        var repeated = await restarted.GenerateIncidentOwnerReportAsync(f.Review with {
            Incident = f.Review.Incident with { Revision = 99, ReportRevision = 40 }
        }, f.Context, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
        Assert.Equal(1, f.Calls);
        Assert.Single(f.States);
        await f.Agent.GenerateIncidentOwnerReportAsync(f.Review with {
            Diagnostics = f.Review.Diagnostics with { CurrentState = f.Review.Diagnostics.CurrentState! with { Hash = new string('B', 64) } }
        }, f.Context, default);
        Assert.Equal(2, f.Calls);
        Assert.Equal(2, f.States.Count);
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
            var evidence = Enumerable.Range(0, 100).Select(i => new ProjectDiagnosticEvidence("CurrentCollaboration", Guid.NewGuid(), now,
                "Planning: Active. " + new string('x', 100_000))).ToArray();
            Review = new(incident, new(evidence, 100, [new string('x', 100_000)]) {
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
