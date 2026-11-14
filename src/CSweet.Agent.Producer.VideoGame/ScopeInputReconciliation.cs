using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task ReconcileAppliedScopeInputsAsync(Guid boardId, WorkDeliveryPlanResponse plan,
        AgentRuntimeContext context, CancellationToken token)
    {
        var saved = await context.Platform.ReadOperatingStateAsync<PendingScopeAmendment>(PendingScopeKey(boardId), token);
        if (saved?.Status != "Applied" || !await AuthorizesRetainedScopeAsync(saved.Payload,context,token)) return;
        var request = await ReadRoleRepairRequestAsync(ScopeRepairKey(saved.Payload.StageId,saved.Payload.Source.TurnId),context,token);
        if (request?.ScopeAuthorizingTurnId != saved.Payload.Source.TurnId || request.ScopeDirection != saved.Payload.Direction) return;
        var changed = request.ScopeReplacements.Select(r => r.ItemId).ToHashSet();
        var expected = ScopeExpectedItems(request).Where(i => changed.Contains(i.Id)).ToDictionary(i => i.Id);
        var current = await context.Platform.Work.ReadBoardAsync(boardId,token);
        var accepted = new Dictionary<Guid,long>();
        foreach (var (id,item) in expected)
        {
            var now = current.Items.Single(i => i.Id == id);
            if (JsonSerializer.Serialize(now.Planning) != JsonSerializer.Serialize(item.Planning))
                throw new InvalidOperationException("The applied amendment no longer matches canonical planning. Reconcile the new change with its accountable owner.");
            accepted[id] = now.PlanningRevision;
        }
        var originals=request.OriginalItems.Where(i=>changed.Contains(i.Id)&&i.Delivery is not null).ToArray();
        await DeliveryInputReconciliation.ApplyAsync(plan,accepted,context,token,
            originals.Select(i=>new WorkDeliveryTaskAssignment(i.Id,i.StageAssignments)).ToArray(),
            originals.Select(i=>new WorkDeliveryOriginalInput(i.Id,JsonSerializer.Serialize(i.Delivery,new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                i.AccountableOrganizationUserId,i.StageAssignments)).ToArray());
    }
}
