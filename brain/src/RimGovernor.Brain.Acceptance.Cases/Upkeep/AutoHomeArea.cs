using RimGovernor.Brain.Bridge;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>upkeep/auto-home-area</c>.</summary>
internal static class UpkeepAutoHomeArea
{
    internal static TypedCase Case() => new()
    {
        Name = "upkeep/auto-home-area",
        Scope = "AutoHomeAreaIntent on Actions/Apply (#1322): flipping Find.PlaySettings.autoHomeArea applies, the same value "
            + "again is UNCHANGED, and the Home census reads each value back.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<bool> ReadAsync(string label)
        {
            var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
            var upkeep = facts.Upkeep;
            return upkeep.HomeCoverage.AutoHomeArea;
        }

        async Task ApplyAsync(string key, bool enabled, Receipts.FieldOutcome outcome)
        {
            var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
            {
                Identity = s.Identity,
                Actions = [new Operations.Action { Key = key, Intent = new Operations.AutoHomeAreaIntent { Enabled = enabled } }], DeferSnapshot = false }, cancellationToken).ConfigureAwait(false);
            var results = reply.Results.ToList();
            var fields = results is [{ Outcome.Applied.Outcome.Applied.Observed.Effect.Settings: { } settings }] ? settings.Fields.ToList() : null;
            if (fields is not [{ Field: Receipts.SettingsField.AutoHomeArea } field] || field.Outcome != outcome)
            {
                throw new AcceptanceException($"{key}: expected one auto-home-area field {outcome}, got {string.Join("; ", results)}");
            }

            var got = await ReadAsync(key + "-readback").ConfigureAwait(false);
            if (got != enabled)
            {
                throw new AcceptanceException($"{key}: fact reads {got}, want {enabled}");
            }
        }

        var initial = await ReadAsync("initial").ConfigureAwait(false);
        s.Record("initial", initial);
        await ApplyAsync("auto-home-flip", !initial, Receipts.FieldOutcome.Applied).ConfigureAwait(false);
        await ApplyAsync("auto-home-again", !initial, Receipts.FieldOutcome.Unchanged).ConfigureAwait(false);
        await ApplyAsync("auto-home-restore", initial, Receipts.FieldOutcome.Applied).ConfigureAwait(false);
    }
}
