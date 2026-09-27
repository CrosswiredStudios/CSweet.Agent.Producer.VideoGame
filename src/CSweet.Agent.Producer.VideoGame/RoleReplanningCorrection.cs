using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static async Task<AgentCoordinationSession> EnsureRoleRepairCorrectionAsync(
        RoleRepairRequest request, AgentCoordinationSession original, GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<ArtifactPackageMemberDigest> members, IReadOnlyList<string> managerDirections,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (original.Status != AgentCoordinationStatuses.Completed) return original;
        var proposal = original.Turns.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact?.Payload
            .Deserialize<GameTechnicalDeliveryProposalV1>();
        if (proposal is null || proposal.Cycle != cycle) return original;
        var findings = RoleRepairCorrectionFindings(request, proposal);
        if (findings.Count == 0) return original;
        var message = RoleRepairObjective(request) + "\n" + RoleRepairContext(request) +
            "\nThe previous proposal failed coverage validation. Correct all of these exact discrepancies; " +
            "return a complete replacement proposal using the canonical board as the unchanged source scope. " +
            "Preserve the exact authorized text even when adding explanation. Do not report execution as complete.\n" +
            JsonSerializer.Serialize(findings, AcceptanceJson);
        if (managerDirections.Count > 0)
            message += "\nRecorded Creative Director decisions: " + JsonSerializer.Serialize(managerDirections, AcceptanceJson);
        var handoff = BoundPlanningHandoff(message, RoleRepairArtifact(cycle, members, request));
        // One stable correction session per original planning session and role policy. Repeated attention reviews
        // recover that session, including terminal failure; they never create another retry generation.
        return await context.Platform.Communication.StartBoardCoordinationAsync(new(
            original.Target.OrganizationUserId, request.BoardId, "Correct role-replanning scope coverage",
            RoleRepairObjective(request), [request.ScopeDirection is null ? "Preserve every original requirement, criterion and constraint." : "Apply only the recorded owner-authorized replacements; preserve unrelated planning.",
                "Assign implementation to engineering and independent validation to QA.",
                "Make downstream consumers depend on the delivered implementation and validation."],
            handoff.Message, $"producer-role-repair-correction:{original.Id:N}:structured-v1", handoff.Artifact), token);
    }

    internal static AgentCoordinationArtifactSubmission RoleRepairArtifact(GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<ArtifactPackageMemberDigest> members, RoleRepairRequest request)
    {
        var source = request.OriginalItems.Single(x => x.Id == request.WorkItemId);
        if (source.Planning is null) throw new InvalidOperationException("Role repair requires pinned canonical planning.");
        var artifact = PlanningArtifact(cycle, members);
        if (request.ScopeDirection is not null) return artifact;
        var payload = JsonNode.Parse(artifact.Payload.GetRawText())!.AsObject();
        payload["roleRepair"] = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            sourceWorkItemId = source.Id,
            sourcePlanningSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source.Planning))).ToLowerInvariant()
        });
        return artifact with { Payload = JsonSerializer.SerializeToElement(payload) };
    }
    internal static IReadOnlyList<string> RoleRepairCorrectionFindings(RoleRepairRequest request,
        GameTechnicalDeliveryProposalV1 proposal)
    {
        var items = proposal.DeliveryItems;
        // The creative-brief invariant is supplied by the Producer when there is no designer.
        var constraints = proposal.TechnicalConstraints.Concat(RetainedRoleRepairConstraints(request)).Append("Preserve the exact accepted creative brief and its non-goals.").ToArray();
        try { ValidateRoleRepairCoverage(request, items, constraints); return []; }
        catch (InvalidOperationException error)
        {
            var findings = new List<string> { error.Message };
            if (request.ScopeDirection is not null)
            {
                foreach (var expected in ScopeExpectedItems(request).Where(x => x.Status != "Cancelled"))
                {
                    var key = ProposalKey(expected);
                    var actual = items.SingleOrDefault(x => x.ProposalKey == key);
                    if (actual is null) { findings.Add($"Restore existing ticket {key}."); continue; }
                    if (expected.Planning is not { } planning) continue;
                    if (!planning.AcceptanceCriteria.SequenceEqual(actual.AcceptanceCriteria))
                        findings.Add($"{key}: use exactly these criteria in this order: " + JsonSerializer.Serialize(planning.AcceptanceCriteria));
                    foreach (var requirement in planning.Requirements.Where(x => !actual.Description.Contains(x, StringComparison.Ordinal)))
                        findings.Add($"{key}: retain this authorized requirement verbatim: {requirement}");
                    if (PrimaryExecutionAssignment(expected)?.Requirements?.RequiredRoleKey is { } role && actual.AccountableRoleKey != role)
                        findings.Add($"{key}: preserve accountable role {role}.");
                }
                return findings;
            }
            var criteria = items.SelectMany(x => x.AcceptanceCriteria).ToHashSet(StringComparer.Ordinal);
            foreach (var original in request.OriginalItems.Where(x => x.Status != "Cancelled" && x.Planning is not null))
            {
                var key = ProposalKey(original);
                foreach (var text in original.Planning!.Requirements.Where(r => !items.Any(p => p.Description.Contains(r, StringComparison.Ordinal))))
                    findings.Add($"{key}: retain this original requirement verbatim in a resulting description: {text}");
                foreach (var text in original.Planning.AcceptanceCriteria.Where(c => !criteria.Contains(c)))
                    findings.Add($"{key}: retain this acceptance criterion verbatim under its correct owner: {text}");
                foreach (var text in (original.Planning.Constraints ?? []).Where(c => !constraints.Contains(c, StringComparer.Ordinal)))
                    findings.Add($"Retain this exact technical constraint: {text}");
                if (original.Id != request.WorkItemId && items.FirstOrDefault(x => x.ProposalKey == key) is { } replacement &&
                    !original.Planning.AcceptanceCriteria.SequenceEqual(replacement.AcceptanceCriteria))
                    findings.Add($"{key}: restore its original acceptance criteria and order; unrelated scope must remain unchanged.");
            }
            foreach (var text in RoleRepairDeliveryCriteria(request))
                foreach (var item in items.Where(x => x.AcceptanceCriteria.Contains(text, StringComparer.Ordinal) &&
                    x.AccountableRoleKey is not ("game-engineer" or "game-quality-assurance")))
                    findings.Add($"{item.ProposalKey}: move this execution criterion entirely to engineering or independent QA: {text}");
            var moved = items.Where(x => x.AcceptanceCriteria.Intersect(RoleRepairDeliveryCriteria(request), StringComparer.Ordinal).Any())
                .Select(x => x.ProposalKey).ToHashSet(StringComparer.Ordinal);
            bool DependsOn(string from, string target, HashSet<string> visited) => visited.Add(from) &&
                items.Where(x => x.ProposalKey == from).SelectMany(x => x.DependencyProposalKeys)
                    .Any(x => x == target || DependsOn(x, target, visited));
            foreach (var original in request.OriginalItems.Where(x => x.Planning?.DependencyItemIds.Contains(request.WorkItemId) == true &&
                x.Status is not ("Done" or "Completed" or "Cancelled")))
                foreach (var owner in moved.Where(owner => owner != ProposalKey(original) &&
                    !DependsOn(ProposalKey(original)!, owner, new(StringComparer.Ordinal))))
                    findings.Add($"{ProposalKey(original)}: add a direct or transitive dependency on {owner}; the planning document alone does not satisfy this dependency.");
            return findings.Distinct(StringComparer.Ordinal).ToArray();
        }
    }
}
