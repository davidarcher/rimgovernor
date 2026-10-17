using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>temperature/building</c> (Go <c>temperature/building.go</c>): the SetBuildingTemperature vertical through
/// BuildingPatchIntent's target_temperature arm on Actions/Apply, against one unpowered heater staged on the lab.
/// </summary>
internal static class BuildingTemperature
{
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 1,
            Height = 1,
            OffsetX = 3,
            Radius = 12,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty],
        },
        Buildings = [new Fx.StageBuilding { Name = "heater", Def = "Heater", Cells = [new(0, 0)] }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "temperature/building",
        Scope = "BuildingPatchIntent target temperature against an exact CompTempControl building (#3403 stage): an "
            + "out-of-range target is refused, a real write is read back through observations_list_buildings with a new "
            + "settings token, and resending the key replays the same result. A unit test cannot see the native comp.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var thingId = s.Staged!.Buildings["heater"].Ids.Single();

        async Task<(double Temperature, string Token)> ReadAsync(string label)
        {
            var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [thingId] }, cancellationToken).ConfigureAwait(false);
            if (rows is not [{ } row] || row.Building?.Id != thingId)
            {
                throw new AcceptanceException($"{label}: want the staged heater {thingId}, got {rows.Count} rows: {string.Join("; ", rows)}");
            }

            s.RecordWire(label, row.Settings);
            var settings = row.Settings;
            if (settings?.Snapshot is not { EntityId: var entity, Token: { Length: > 0 } token } || entity != thingId)
            {
                throw new AcceptanceException($"{label}: missing or mismatched settings snapshot: {settings}");
            }

            return settings.TargetTemperatureC is { } target
                ? (target, token)
                : throw new AcceptanceException($"{label}: no target temperature: {settings}");
        }

        async Task<Operations.ActionResult> ApplyAsync(string key, float temperature)
        {
            var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
            {
                Identity = s.Identity,
                Actions =
                [
                    new Operations.Action
                    {
                        Key = key,
                        Intent = new Operations.BuildingPatchIntent { ThingId = thingId, Change = new Operations.BuildingPatchIntentChange.TargetTemperatureCase(temperature) },
                    },
                ],
            }, cancellationToken).ConfigureAwait(false);
            return reply.Results.ToList() is [{ } result]
                ? result
                : throw new AcceptanceException($"{key}: want one result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        }

        var (before, beforeToken) = await ReadAsync("target-before").ConfigureAwait(false);
        var target = (float)(before + 5 > 1000 ? before - 5 : before + 5);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        // A target outside the game's interface range.
        var outOfRange = await ApplyAsync("temperature-out-of-range", 5000).ConfigureAwait(false);
        if (outOfRange.Outcome?.Refused is null)
        {
            throw new AcceptanceException($"temperature-out-of-range: want a refusal, got {outOfRange}");
        }

        var set = await ApplyAsync("temperature-set", target).ConfigureAwait(false);
        var fields = set is { Outcome.Applied.Outcome.Applied.Observed.Effect.Settings: { } settings } ? settings.Fields.ToList() : null;
        if (fields is not [{ Field: Receipts.SettingsField.Temperature, Outcome: Receipts.FieldOutcome.Applied }])
        {
            throw new AcceptanceException($"temperature-set: want one applied temperature field, got {set}");
        }

        var (after, afterToken) = await ReadAsync("target-after").ConfigureAwait(false);
        if (afterToken == beforeToken)
        {
            throw new AcceptanceException("target-after: the settings token did not change");
        }

        if (Math.Abs(after - target) > 0.01)
        {
            throw new AcceptanceException($"target-after: native target {after}, want {target}");
        }

        var replay = await ApplyAsync("temperature-set", target).ConfigureAwait(false);
        if (WireJson.Serialize(replay) != WireJson.Serialize(set))
        {
            throw new AcceptanceException($"temperature-set: the resent key changed its result: {WireJson.Serialize(set)} then {WireJson.Serialize(replay)}");
        }
    }
}
