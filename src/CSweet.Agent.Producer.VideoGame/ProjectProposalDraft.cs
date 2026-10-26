using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private sealed record ProjectDraftResult(WorkstreamPlanProposalV2Request? Plan, string? Error);
    private sealed record ProjectDraft(string? Outcome, string? Rationale,
        IReadOnlyList<string>? SuccessCriteria, IReadOnlyList<WorkstreamMilestoneProposal>? InitialMilestones,
        JsonElement? ProfileData, IReadOnlyList<string>? AuthorizedStaffingRoleKeys);

    // A terminal invalid draft is cached as well: duplicate wakes must not restart the correction budget.
    private async Task<ProjectDraftResult> PrepareProjectDraftAsync(string cacheKey,
        WorkstreamPlanProposalV2Request template, object evidence, string invocation,
        AgentRuntimeContext context, CancellationToken token, string recoveryEpoch = "initial")
    {
        var prior = await context.Platform.ReadOperatingStateAsync<WorkstreamPlanProposalV2Request>(cacheKey, token);
        if (prior is not null && ValidProjectDraft(prior.Payload, template))
            return new(prior.Payload, null);
        var previous = await context.Platform.ReadOperatingStateAsync<ProjectDraftResult>($"{cacheKey}:accepted-v1", token);
        if (previous?.Payload.Plan is { } previousPlan && ValidProjectDraft(previousPlan, template)) return previous.Payload;
        var acceptedKey = $"{cacheKey}:accepted-v2";
        var accepted = await context.Platform.ReadOperatingStateAsync<ProjectDraftResult>(acceptedKey, token);
        if (accepted?.Payload.Plan is { } acceptedPlan && ValidProjectDraft(acceptedPlan, template)) return accepted.Payload;
        // An explicit collaboration resume or a model configuration correction can retry a
        // rejected draft, but may never change a command that could already have been submitted.
        var attempt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            recoveryEpoch, provider = Settings.GetGuid("llmProviderId"), model = Settings.GetString("llmModel") }))))[..16];
        var result = await PitchProtocol.CachedAsync($"{cacheKey}:attempt-v2:{attempt}", context, async () =>
        {
            var model = context.CreateChatClient(new AgentLlmSelection(
                Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Producer model."),
                Settings.GetString("llmModel"), new AgentLlmInvocationContext(null, null, invocation)));
            List<ChatMessage> messages = [
                new(ChatRole.System, AgentInteractionInstructions.Compose("""
                    Prepare the project proposal as delivery lead using the accepted evidence and manager feedback.
                    Return a JSON object with outcome, rationale, successCriteria, initialMilestones, profileData,
                    and optionally authorizedStaffingRoleKeys at its root.
                    outcome and rationale must be nonempty strings of at most 4000 characters; successCriteria must
                    be a nonempty array of nonempty strings. initialMilestones must contain the template's milestones
                    with the same keys, lifecycleStage, targetDate, requiredEvidenceTypeKeys and requiredReviewerRoleKeys;
                    refine their names to describe delivery clearly. Do not expand accepted scope or invent authority,
                    identity, budget or dates. Missing budgets are allowed under the current Unlimited spending policy.
                    profileData is the complete descriptive metadata object for the existing profile. Correct its genre,
                    platform and accessibility fields against the accepted documents and manager feedback; do not retain
                    contradictory template defaults. Preserve unrelated metadata.
                    Exactly preserve the profileData property names and JSON value types from the template; no
                    additional properties are allowed. Put deployment details, pickup interpretations and other
                    descriptive clarifications in outcome or successCriteria, never in new metadata properties.
                    accountableManagerOrganizationUserId is the Producer delivery lead; the Director is the
                    supervising business approver. Never claim the Director replaces the Producer in that field,
                    even when earlier feedback mistakenly requested it. Explain this correction in rationale.
                    authorizedStaffingRoleKeys may only be a subset of the template's authorized staffing roles;
                    narrow it to roles justified by the approved scope. It is an authority ceiling, not actual hiring.
                    Keep milestone evidence type keys and clarify their scoped acceptance in successCriteria.
                    The final submitted command retains
                    the template's identity, profile key/version, team, evidence and authority fields in code. The response
                    field list describes your draft, not a request to omit required fields from the submitted command.
                    The manager reviews the submitted proposal; never assert that approval occurred.
                    """, new(AgentInteractionModes.SupportingSpecialist, "project-proposal",
                        ["approved-scope", "business-constraints"], ["delivery-planning", "staffing-evidence"],
                        AgentInteractionResponseContracts.DeliverableOrClarification) { CounterpartRoleKey = "game-director" })),
                new(ChatRole.User, JsonSerializer.Serialize(new { template, evidence }, ProjectFoundationJson))
            ];
            for (var generation = 0; generation < 2; generation++)
            {
                var response = await model.GetResponseAsync(messages, ResponseOptions(), token);
                try
                {
                    var raw = response.Text.Trim().Trim('`');
                    if (raw.StartsWith("json", StringComparison.OrdinalIgnoreCase)) raw = raw[4..].Trim();
                    var draft = JsonSerializer.Deserialize<ProjectDraft>(raw, ProjectFoundationJson);
                    var plan = draft is null ? null : template with {
                        Outcome = draft.Outcome!, Rationale = draft.Rationale!,
                        SuccessCriteria = draft.SuccessCriteria!, InitialMilestones = draft.InitialMilestones!,
                        ProfileData = draft.ProfileData ?? template.ProfileData,
                        AuthorityEnvelope = template.AuthorityEnvelope with {
                            AuthorizedStaffingRoleKeys = draft.AuthorizedStaffingRoleKeys ?? template.AuthorityEnvelope.AuthorizedStaffingRoleKeys } };
                    if (plan is not null && ValidProjectDraft(plan, template)) return new ProjectDraftResult(plan, null);
                }
                catch (JsonException) { /* A single corrective model request follows; no mutation has run. */ }
                messages.Add(new(ChatRole.Assistant, response.Text));
                messages.Add(new(ChatRole.User, "The proposal failed validation. Return only the required root JSON fields with valid types, nonempty text, and the exact milestone keys, stages, dates, evidence and reviewers from the template. profileData must contain exactly the template's properties with matching types, including array item types; do not add deploymentTargets, pickupInterpretation or other fields. authorizedStaffingRoleKeys must be a subset of the template. Do not wrap the result in another object."));
            }
            return new ProjectDraftResult(null,
                "The Producer could not prepare a valid project proposal after one correction attempt. No project proposal was submitted. Review the Producer model output/configuration and retry this collaboration after correction.");
        }, token);
        return result.Plan is null ? result : await PitchProtocol.CachedAsync(acceptedKey, context, () => Task.FromResult(result), token);
    }

    private static bool ValidProjectDraft(WorkstreamPlanProposalV2Request plan, WorkstreamPlanProposalV2Request template)
    {
        static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
        static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
        if (!Text(plan.Outcome, 4000) || !Text(plan.Rationale, 4000) ||
            plan.SuccessCriteria is not { Count: > 0 and <= 100 } || plan.SuccessCriteria.Any(x => !Text(x, 4000)) ||
            plan.InitialMilestones is null || plan.InitialMilestones.Count != template.InitialMilestones.Count ||
            plan.ProfileData.ValueKind != JsonValueKind.Object || plan.ProfileData.GetRawText().Length > 128000 ||
            !MatchesMetadataShape(plan.ProfileData, template.ProfileData) ||
            plan.AuthorityEnvelope.AuthorizedStaffingRoleKeys.Distinct(StringComparer.Ordinal).Count() != plan.AuthorityEnvelope.AuthorizedStaffingRoleKeys.Count ||
            plan.AuthorityEnvelope.AuthorizedStaffingRoleKeys.Except(template.AuthorityEnvelope.AuthorizedStaffingRoleKeys, StringComparer.Ordinal).Any())
            return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var milestone in plan.InitialMilestones)
        {
            if (milestone is null || !Text(milestone.Key, 160) || !keys.Add(milestone.Key) || !Text(milestone.Name, 240)) return false;
            var expected = template.InitialMilestones.SingleOrDefault(x => x.Key == milestone.Key);
            if (expected is null || milestone.LifecycleStage != expected.LifecycleStage || milestone.TargetDate != expected.TargetDate ||
                !Same(milestone.RequiredEvidenceTypeKeys, expected.RequiredEvidenceTypeKeys) ||
                !Same(milestone.RequiredReviewerRoleKeys, expected.RequiredReviewerRoleKeys)) return false;
        }
        return true;
    }

    // The Director supplies the complete metadata shape for the selected production profile.
    // Validate before caching or mutation so a model's extra fields cannot poison every retry.
    private static bool MatchesMetadataShape(JsonElement value, JsonElement template)
    {
        if (template.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (value.ValueKind != template.ValueKind) return false;
        if (template.ValueKind == JsonValueKind.Object)
        {
            var fields = value.EnumerateObject().ToArray();
            var expected = template.EnumerateObject().ToArray();
            return fields.Length == expected.Length && fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length &&
                expected.All(x => value.TryGetProperty(x.Name, out var actual) && MatchesMetadataShape(actual, x.Value));
        }
        if (template.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(x => template.GetArrayLength() == 0
                ? x.ValueKind == JsonValueKind.String : MatchesMetadataShape(x, template[0]));
        return template.ValueKind != JsonValueKind.Null && template.ValueKind != JsonValueKind.Undefined;
    }
}
