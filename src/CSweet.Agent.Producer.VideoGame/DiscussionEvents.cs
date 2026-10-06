using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using TicketConversations;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private Task<IChatClient> DiscussionClientAsync(AgentRuntimeContext context, CancellationToken token) =>
        Task.FromResult(context.CreateChatClient(new AgentLlmSelection(
            Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure a discussion provider."), Settings.GetString("llmModel"))));

    private Task RecoverDiscussionAsync(AgentRuntimeContext context, CancellationToken token) =>
        Discussion.RecoverAsync(context, ct => DiscussionClientAsync(context, ct), token);
}

