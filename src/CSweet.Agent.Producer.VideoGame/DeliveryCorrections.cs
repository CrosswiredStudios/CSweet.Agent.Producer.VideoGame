using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static IReadOnlyList<Guid> RecoveryProjects(AgentRuntimeContext context) =>
        (context.Identity?.ManagedWorkstreams.Select(p => p.WorkstreamId) ?? [])
        .Concat(context.Identity?.AssignedProject is { } assigned ? [assigned.WorkstreamId] : [])
        .Distinct().Take(25).ToArray();

    protected override async Task HandleManagerRecoveryAsync(AgentRuntimeContext context, CancellationToken token)
    {
        foreach (var project in RecoveryProjects(context))
            await ReconcileDeliveryCorrectionAsync(project, context, token);
    }

    // A newly approved project has no delivery access until its board exists, and it cannot hold
    // delivery plans or corrections before then. A denied plan read therefore means there is nothing
    // to reconcile. It must not fail the review that goes on to create the board and its access.
    private async Task ReconcileDeliveryCorrectionAsync(Guid project, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            await DeliveryCorrectionCoordinator.ReconcileProjectAsync(project, context,
                () => CreateDelegationChatClient(context, null, "delivery-correction-source"), token);
        }
        catch (PlatformCapabilityException ex) when (ex.Code == PlatformCapabilityErrorCode.Denied &&
            (ex.Capability == "work.delivery.read.v1" ||
             ex.Message.Contains("work.delivery.read.v1", StringComparison.Ordinal)))
        {
        }
    }
}
