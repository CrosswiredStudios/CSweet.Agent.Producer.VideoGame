using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class RoleReplanningSnapshotTests
{
    [Fact]
    public async Task LargeBoardSnapshotIsPagedWithoutLosingScopeAndRecoversAnInterruptedWrite()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var items = Enumerable.Range(0, 25).Select(i => JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = Guid.NewGuid(), Title = "Task " + i, Description = new string('d', 2000),
            Planning = new([new string('r', 2000)], [new string('a', 2000)], [new string('c', 2000)])
        }).ToArray();
        var request = new RoleRepairRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            items[0].Id, ["Preserve execution"], ["Split ownership"], items, 2, DateTimeOffset.UtcNow);
        Assert.True(JsonSerializer.Serialize(request, json).Length > 65536);
        var key = "producer-role-replan:" + request.ReviewStageId.ToString("N");
        var stored = new Dictionary<string, AgentOperatingStateResponse>();
        var writes = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (read, _) => Task.FromResult(new AgentOperatingStateReadResponse(stored.GetValueOrDefault(read.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (write, _) =>
                {
                    Assert.InRange(write.Payload.GetRawText().Length, 1, 65536);
                    Assert.False(stored.ContainsKey(write.StateKey));
                    var response = new AgentOperatingStateResponse(Guid.NewGuid(), write.StateKey, write.SchemaId, 1, "Active",
                        new Dictionary<string,string>(), [], write.StateKey, [], Guid.NewGuid(), write.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[write.StateKey] = response;
                    if (++writes == 3) throw new IOException("Lost response after third snapshot write");
                    return Task.FromResult(response);
                });
        await Assert.ThrowsAnyAsync<Exception>(() => SpecialistAgent.PersistRoleRepairRequestAsync(key, request, runtime.CreateContext(), CancellationToken.None));
        Assert.False(stored.ContainsKey(key));
        await SpecialistAgent.PersistRoleRepairRequestAsync(key, request, runtime.CreateContext(), CancellationToken.None);
        Assert.Equal(items.Length + 1, writes);
        var header = stored[key].Payload.Deserialize<RoleRepairRequest>(json)!;
        Assert.Empty(header.OriginalItems);
        Assert.Equal(items.Length, header.OriginalItemStateKeys.Count);
        var restored = await SpecialistAgent.ReadRoleRepairRequestAsync(key, runtime.CreateContext(), CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(items.OrderBy(x => x.Id), json), JsonSerializer.Serialize(restored!.OriginalItems, json));
        Assert.Equal(request.RoleRepairCriteria, restored.RoleRepairCriteria);
    }
}
