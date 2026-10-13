using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class HiringWakeTests
{
    [Theory]
    [InlineData("Eligible", true, true, true)]
    [InlineData("NeedsSetup", true, true, false)]
    [InlineData("Eligible", false, true, false)]
    [InlineData("Eligible", true, false, false)]
    public async Task PlanningDiscoveryRereadsRosterAndWakesOnlyMatchingHiringWaitOnce(
        string eligibility, bool available, bool sameTeam, bool wakes)
    {
        var project = JsonSerializer.Deserialize<WorkstreamDetail>("{}")! with { Id = Guid.NewGuid() };
        var team = Guid.NewGuid(); var session = Guid.NewGuid(); var digest = "accepted-brief";
        var fingerprint = ProducerPolicyFingerprint.ForPlanning(project.Id, 0, string.Empty, digest);
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with
        {
            Id = Guid.NewGuid(), Revision = 10, Status = PersonalTodoStatuses.Running,
            CorrelationId = $"producer-planning:{project.Id:N}:{fingerprint}",
            Wait = new(DateTimeOffset.UtcNow.AddMinutes(20), SpecialistAgent.InitialHiringWait),
            WorkContext = new(WorkstreamId: project.Id, TeamId: sameTeam ? team : Guid.NewGuid(), SourceFingerprint: fingerprint)
        };
        var member = JsonSerializer.Deserialize<AgentTeammate>("{}")! with
        {
            EmployeeId = Guid.NewGuid().ToString(), AgentInstallationId = Guid.NewGuid(),
            IsAvailable = available, RuntimeEligibility = eligibility, DeclaredRoleKeys = ["game-technical-director"]
        };
        var requeues = 0; var reads = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(WorkstreamCapabilityNames.TeamRosterReadV2, (r, _) =>
            {
                Assert.Equal(project.Id, r.WorkstreamId); Assert.Equal(team, r.TeamId); reads++;
                return Task.FromResult(new TeamRosterV2Response(new(team.ToString(), "Game", "Game", 2, "", "", [member], [], 1, false), null));
            })
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [item])])))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) =>
            {
                Assert.Equal(item.Revision, r.ExpectedRevision); requeues++;
                item = item with { Status = PersonalTodoStatuses.Ready, Wait = null, Revision = item.Revision + 1 };
                return Task.FromResult(item);
            });
        for (var i = 0; i < 3; i++)
            await SpecialistAgent.EnsurePlanningCommitmentAsync(project, team, null, session, digest, runtime.CreateContext(), default);
        Assert.Equal(3, reads); Assert.Equal(wakes ? 1 : 0, requeues);
    }

    [Theory]
    [InlineData("GitHost unavailable", true)]
    [InlineData(SpecialistAgent.InitialHiringWait, false)]
    public async Task HiringWakeDoesNotBypassOtherWaitsOrChangedPlanning(string reason, bool sameFingerprint)
    {
        var source = new PersonalTodoWorkContext(WorkstreamId: Guid.NewGuid(), TeamId: Guid.NewGuid(), SourceFingerprint: "old");
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with
        {
            Id = Guid.NewGuid(), Status = PersonalTodoStatuses.Running, CorrelationId = "producer-planning:cycle",
            Wait = new(DateTimeOffset.UtcNow.AddMinutes(20), reason), WorkContext = source
        };
        var runtime = new AgentTestRuntime().RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
            (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [item])])));
        var preserved = await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Plan", "Plan", "High",
            source with { SourceFingerprint = sameFingerprint ? "old" : "new" }, runtime.CreateContext(), default, hiringDependencyReady: true);
        Assert.Equal(item.Id, preserved.Id); Assert.Equal(item.Wait, preserved.Wait);
        Assert.Equal(PersonalTodoStatuses.Running, preserved.Status); Assert.Equal(item.Revision, preserved.Revision);
    }
}
