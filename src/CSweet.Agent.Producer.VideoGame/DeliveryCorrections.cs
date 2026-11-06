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
            await DeliveryCorrectionCoordinator.ReconcileProjectAsync(project, context, token);
    }
}
