using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StaffingCorrectionRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorrectionReplacesOnlyUnfilledCapacityAndPreservesUnavailableEmployees(bool installed)
    {
        var team = Guid.NewGuid(); var member = new AgentTeammate(Guid.NewGuid().ToString(), "Existing employee", "Agent", null, null, "Report", "Offline")
            { AgentInstallationId = installed ? Guid.NewGuid() : null, DeclaredRoleKeys = ["disputed-role"], IsAvailable = false };
        var roster = new AgentTeamContext(team.ToString(), "team", "Team", 1, "", "", [member], [], 1, false);
        var roles = new[] { new ResourceChangeRole("disputed-role", "team", "Slot", "Original", 2, 1, "Now", [], false, null, null),
            new ResourceChangeRole("producer", "team", "Producer", "Keep sequencing", 1, 1, "Now", [], false, null, null) };
        var correction = new StaffingCorrectionResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), team, Guid.NewGuid(),
            "disputed-role", "Replace unfilled capacity", "Pending", null, 1, DateTimeOffset.UtcNow);
        var retained = SpecialistAgent.CorrectedBaseRoles(roles, correction, roster);
        Assert.Equal(1, retained.Single(x => x.RoleKey == "disputed-role").Headcount);
        Assert.Contains(retained, x => x.RoleKey == "producer");
        Assert.DoesNotContain(SpecialistAgent.CorrectedBaseRoles(roles, correction, roster with { Members = [] }), x => x.RoleKey == "disputed-role");
    }

    [Fact]
    public async Task NewCorrectionDecisionReleasesOnlyItsMatchingWaitOnce()
    {
        var changed = DateTimeOffset.UtcNow;
        var source = new PersonalTodoWorkContext(WorkstreamId: Guid.NewGuid(), TeamId: Guid.NewGuid(), SourceFingerprint: "correction");
        var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), CorrelationId = "producer-staffing-correction:case",
            Status = PersonalTodoStatuses.Running, Revision = 2, WorkContext = source, UpdatedAt = changed.AddMinutes(-1),
            Wait = new(changed.AddMinutes(15), "Await manager review") };
        var requeues = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
            (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [item])])))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (_, _) => {
                requeues++; item = item with { Status = PersonalTodoStatuses.Ready, Wait = null, UpdatedAt = changed.AddSeconds(1), Revision = 3 };
                return Task.FromResult(item); });
        await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Correct", "Correct", "High", source with { TeamId = Guid.NewGuid() }, runtime.CreateContext(), default, dependencyChangedAt: changed);
        Assert.Equal(0, requeues);
        for (int i = 0; i < 2; i++)
            await SpecialistAgent.EnsureCommitmentAsync(item.CorrelationId!, "Correct", "Correct", "High", source, runtime.CreateContext(), default, dependencyChangedAt: changed);
        Assert.Equal(1, requeues);
    }
}
