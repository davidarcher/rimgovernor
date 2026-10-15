using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class ExplicitDrugPolicy
{
    internal static async Task AssignAsync(ISession s, string pawn, string name, Op.DrugPolicyEntry[] entries, CancellationToken ct)
    {
        var write = await ApplyIntents.ApplyOneAsync(s.Harness, name + "-write", s.Identity, name + "-write",
            new() { ["drugPolicy"] = JsonNode.Parse(WireJson.Serialize(new Op.DrugPolicyIntent { Name = name, Entries = RimGovernor.Brain.Domain.EquatableArray.From(entries) })) }, ct).ConfigureAwait(false);
        _ = ApplyIntents.AppliedJob(name + "-write", write);
        var assign = await ApplyIntents.ApplyOneAsync(s.Harness, name + "-assign", s.Identity, name + "-assign",
            new() { ["pawnSettings"] = JsonNode.Parse(WireJson.Serialize(new Op.PawnSettingsIntent
            { PawnId = pawn, Setting = new Op.PawnSettingsIntentSetting.DrugPolicyCase(name) })) }, ct).ConfigureAwait(false);
        _ = ApplyIntents.AppliedJob(name + "-assign", assign);
        s.Report[name + "-receipt"] = assign.DeepClone();
    }

    internal static async Task RequireUnsupportedAsync(ISession s, string pawn, string drug, CancellationToken ct)
    {
        var reply = await s.Harness.WireAsync("drug-blocker", "observations_read_colony_facts",
            new JsonObject { ["scope"] = PawnSupport.Scope(s), ["planning"] = true }, ct).ConfigureAwait(false);
        var observed = WallCensus.Observed("drug-blocker", reply);
        var rows = observed["forecast"]?["observed"]?["needs"]?["pawns"].AsSlice() ?? [];
        var row = rows.Select(r => r.AsMap()).SingleOrDefault(r => r?["pawn"].AsString() == pawn)
            ?? throw new AcceptanceException("No native needs observation for configured drug user.");
        var blockers = row["voluntaryControlBlockers"].AsSlice().Select(r => r.AsString()).ToArray();
        if (row["state"].AsString() != "unsupported_voluntary_state"
            || !blockers.Any(b => b == "scheduled_drug_due:" + drug || b == "scheduled_drug_onset_unassessed:" + drug))
            throw new AcceptanceException("Scheduled drug did not remain an explicit unsupported state: " + row.ToJson());
        s.Report["drug_blockers"] = row.DeepClone();
    }
}
