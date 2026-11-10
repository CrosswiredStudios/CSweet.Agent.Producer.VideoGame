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
        IReadOnlyList<string>? SuccessCriteria, IReadOnlyList<MilestoneDraft?>? InitialMilestones,
        JsonElement? ProfileData);
    // The model only names milestones. Stages, dates, evidence and reviewers stay with the template.
    private sealed record MilestoneDraft(string? Key, string? Name);

    // Staffing authority is the Director-approved template ceiling. The platform requires every
    // active profile staffing role in it, so the model never edits it and cached drafts are healed on read.
    private static WorkstreamPlanProposalV2Request WithTemplateAuthority(
        WorkstreamPlanProposalV2Request plan, WorkstreamPlanProposalV2Request template) =>
        plan with { AuthorityEnvelope = template.AuthorityEnvelope };

    // Only a nonempty, in-range name for a known key is taken from the model.
    private static IReadOnlyList<WorkstreamMilestoneProposal> MergeMilestoneNames(
        WorkstreamPlanProposalV2Request template, IReadOnlyList<MilestoneDraft?>? drafts) =>
        template.InitialMilestones.Select(expected =>
        {
            var name = drafts?.FirstOrDefault(x => x is not null && x.Key == expected.Key)?.Name?.Trim();
            return string.IsNullOrEmpty(name) || name.Length > 240 ? expected : expected with { Name = name };
        }).ToArray();

    // A terminal invalid draft is cached as well: duplicate wakes must not restart the correction budget.
    private async Task<ProjectDraftResult> PrepareProjectDraftAsync(string cacheKey,
        WorkstreamPlanProposalV2Request template, object evidence, string invocation,
        AgentRuntimeContext context, CancellationToken token, string recoveryEpoch = "initial")
    {
        var prior = await context.Platform.ReadOperatingStateAsync<WorkstreamPlanProposalV2Request>(cacheKey, token);
        if (prior is not null && ValidProjectDraft(prior.Payload, template))
            return new(WithTemplateAuthority(prior.Payload, template), null);
        var previous = await context.Platform.ReadOperatingStateAsync<ProjectDraftResult>($"{cacheKey}:accepted-v1", token);
        if (previous?.Payload.Plan is { } previousPlan && ValidProjectDraft(previousPlan, template)) return new(WithTemplateAuthority(previousPlan, template), null);
        var acceptedKey = $"{cacheKey}:accepted-v2";
        var accepted = await context.Platform.ReadOperatingStateAsync<ProjectDraftResult>(acceptedKey, token);
        if (accepted?.Payload.Plan is { } acceptedPlan && ValidProjectDraft(acceptedPlan, template)) return new(WithTemplateAuthority(acceptedPlan, template), null);
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
                    Return a JSON object with only outcome, rationale, successCriteria, initialMilestones and profileData
                    at its root.
                    outcome and rationale must be nonempty strings of at most 4000 characters; successCriteria must
                    be a nonempty array of nonempty strings. initialMilestones must contain one object per template milestone
                    with only its exact key and a name of at most 240 characters that describes delivery clearly.
                    Stages, dates, evidence types and reviewers are kept from the template in code; do not return them. Do not expand accepted scope or invent authority,
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
                    Staffing authority is fixed by the template and applied in code. Never return
                    authorizedStaffingRoleKeys and never add or remove staffing roles.
                    If evidence.platformRejection is present, the platform rejected the previous proposal for that
                    exact reason; correct only what it names and keep everything else.
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
                        SuccessCriteria = draft.SuccessCriteria!,
                        InitialMilestones = MergeMilestoneNames(template, draft.InitialMilestones),
                        ProfileData = draft.ProfileData ?? template.ProfileData };
                    if (plan is not null && ValidProjectDraft(plan, template)) return new ProjectDraftResult(plan, null);
                }
                catch (JsonException) { /* A single corrective model request follows; no mutation has run. */ }
                messages.Add(new(ChatRole.Assistant, response.Text));
                messages.Add(new(ChatRole.User, "The proposal failed validation. Return only outcome, rationale, successCriteria, initialMilestones and profileData at the root, with valid types and nonempty text. initialMilestones needs only each template milestone's key and name. profileData must contain exactly the template's properties with matching types, including array item types; do not add deploymentTargets, pickupInterpretation or other fields. Do not return authorizedStaffingRoleKeys. Do not wrap the result in another object."));
            }
            return new ProjectDraftResult(null,
                "The Producer could not prepare a valid project proposal after one correction attempt. No project proposal was submitted. Review the Producer model output/configuration and retry this collaboration after correction.");
        }, token);
        if (result.Plan is null) return result;
        var saved = await PitchProtocol.CachedAsync(acceptedKey, context, () => Task.FromResult(result), token);
        return saved.Plan is null ? saved : new ProjectDraftResult(WithTemplateAuthority(saved.Plan, template), null);
    }

    private static bool ValidProjectDraft(WorkstreamPlanProposalV2Request plan, WorkstreamPlanProposalV2Request template)
    {
        static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
        static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
        if (plan.ExistingProjectId != template.ExistingProjectId || plan.ExpectedProjectRevision != template.ExpectedProjectRevision || !Text(plan.Outcome, 4000) || !Text(plan.Rationale, 4000) ||
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
