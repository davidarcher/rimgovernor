using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using FilterBase = RimGovernor.Brain.Domain.FilterBase;
using StockpilePriority = RimGovernor.Brain.Domain.StockpilePriority;
using Quality = RimGovernor.Brain.Domain.Quality;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>zone/delete</c>: native create, settings, preset, cell-edit and delete ZoneIntents on a staged hut interior, each
/// read back through observations_list_zones and the cell grid; refusals leave the zone untouched and resends of effects
/// that already hold apply again. The re-add runs on a ticking clock and native's clock_invalidation row names the
/// zone and its footprint (#359).
/// </summary>
internal static class ZoneDelete
{
    // Game ticks the re-add runs for native to emit the zone's clock_invalidation row.
    private const int ProbeTicks = 300;

    // The colony watch policy with the brain's hazard thresholds, which native requires of every clock run.
    private static readonly Clock.WatchPolicy Policy = new Clock.WatchPolicy
    {
        SeriousSingleHitDamage = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSingleHitDamage,
        SeriousSummaryHealthFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSummaryHealthFloor,
        SeriousBleedRateFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousBleedRateFloor,
        SeriousVitalPartFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousVitalPartFloor,
        ExplosiveNearMarginCells = RimGovernor.Brain.Policy.HazardThresholdsRules.ExplosiveNearMarginCells,
        MeleeReachCells = RimGovernor.Brain.Policy.HazardThresholdsRules.MeleeReachCells,
        InjurySeverityFloorTicks = RimGovernor.Brain.Policy.HazardThresholdsRules.InjurySeverityFloorTicks,
        HungerHoldCategory = RimGovernor.Brain.Policy.HazardThresholdsRules.HungerHoldCategory,
        Mode = Clock.WatchMode.Colony,
        HealthDropFraction = .1f,
        MinHealthFraction = .5f,
        HostileWithin = 20f,
        InjuryStopCooldownMs = 0,
    };

    internal static TypedCase Case() => new()
    {
        Name = "zone/delete",
        Scope = "ZoneIntents on operations_apply over a staged 2x2 hut interior: an allow-list stockpile with hit-point and "
            + "quality ranges is created (a second create over it refuses INVALID_REQUEST without adopting it), patched (an unresolvable selector refused "
            + "INVALID_REQUEST without a change), switched through the presets (indoor_only and outdoor_safe partition the "
            + "nonperishables; outdoor_safe_waste takes rotten corpses, not Cloth), loses and regains one corner (a missing "
            + "zone refused NOT_FOUND) and is deleted, each read back from observations_list_zones and the cell grid. The "
            + "re-add on a ticking clock emits a clock_invalidation row of the colony family naming the zone over its "
            + "footprint (#359).",
        Ticks = ProbeTicks,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 4,
                Height = 4,
                Near = Fx.StageAnchor.Colonist,
                Radius = 40,
                Require = [Fx.StageGround.Unfogged, Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Heavy],
            },
            Rooms = [new Fx.StageRoom { Name = "hut", Rect = new Fx.StageRect(0, 0, 4, 4), Stuff = "WoodLog", Roof = true }],
        },
        Run = RunAsync,
    };

    private static Operations.FilterSelector Def(string def) => new() { Definition = new Operations.FilterSelectorDefinition.ThingDefCase(def) };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var interior = s.Staged!.Rooms["hut"].Interior;
        var corner = new Common.Cell { X = interior.Min(c => c.X), Z = interior.Min(c => c.Z) };
        var footprint = new Fx.StageRect(corner.X, corner.Z, 2, 2);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Observations.ZoneState?> RowAsync(string label, string zoneId)
        {
            var reply = await s.CallAsync(label, NativeOps.ListZones, new Observations.ListZonesRequest { Scope = s.Scope, Ids = [zoneId], IncludeFilter = true }, cancellationToken).ConfigureAwait(false);
            var rows = reply.Zones.ToList();
            return rows switch
            {
                [] => null,
                [var row] when row.Id == zoneId => row,
                _ => throw new AcceptanceException($"{label}: want at most zone {zoneId}, got {string.Join("; ", rows)}"),
            };
        }

        // The zone's row and grid cells: a token that shows whether a refused or resent intent touched the zone.
        async Task<(Observations.ZoneState? Row, string Cells)> StateAsync(string label, string zoneId)
        {
            var row = await RowAsync(label, zoneId).ConfigureAwait(false);
            var grid = await TypedApply.CellsAsync(s, label + "-cells", footprint, cancellationToken).ConfigureAwait(false);
            return (row, string.Join(' ', grid.ZoneCells().GetValueOrDefault(zoneId) ?? []));
        }

        async Task FilterAsync(string label, string zoneId, string priority, string[] allowed, double hpMin, double hpMax)
        {
            var row = await RowAsync(label, zoneId).ConfigureAwait(false) ?? throw new AcceptanceException($"{label}: zone not listed");
            if (row is not Observations.ZoneState.Stockpile stockpile || stockpile.Priority != priority || stockpile.Filter is not { } filter || !filter.AllowedDefNames.SequenceEqual(allowed)
                || filter.HitPointsMin != hpMin || filter.HitPointsMax != hpMax || filter.QualityMin != Quality.Normal || filter.QualityMax != Quality.Legendary)
            {
                throw new AcceptanceException($"{label}: want {priority} [{string.Join(' ', allowed)}] hp [{hpMin}, {hpMax}] quality [Normal, Legendary], got {row}");
            }
        }

        async Task<Receipts.ZoneEffect> AppliedEffectAsync(string key, Operations.ZoneIntent intent)
        {
            var outcome = await TypedApply.AppliedAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
            var effect = outcome.Applied?.Outcome?.Applied?.Observed?.Effect?.Zone ?? throw new AcceptanceException($"{key}: no zone evidence: {outcome}");
            s.RecordWire(key, effect);
            return effect;
        }

        async Task<Receipts.ZoneEffect.ZoneCase> AppliedAsync(string key, Operations.ZoneIntent intent) =>
            await AppliedEffectAsync(key, intent).ConfigureAwait(false) as Receipts.ZoneEffect.ZoneCase
                ?? throw new AcceptanceException($"{key}: no edited-zone evidence");

        async Task RefusedAsync(string key, Operations.ZoneIntent intent, Common.FailureCode code)
        {
            var refusal = await TypedApply.RefusedAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
            if (refusal.Code != code)
            {
                throw new AcceptanceException($"{key}: want {code}, got {refusal}");
            }
        }

        // --- create ---
        var create = new Operations.ZoneIntent
        {
            Label = "Steel, WoodLog",
            Kind = Operations.ZoneType.Stockpile,
            AddCells = new Operations.Cells { Selection = new Operations.Rectangle { Origin = corner, Width = 2, Height = 2 } },
            Stockpile = new Operations.StockpileSettings
            {
                Priority = new Operations.StockpilePriorityChange.Selected(StockpilePriority.Important),
                Preset = new Operations.StockpilePresetChange.Selected(FilterBase.Nothing),
                Filter = new Operations.FilterPatch { Allow = [Def("Steel"), Def("WoodLog")], HitPoints = new Operations.HitPointRange.Selected(0.5, 1), QualityMin = "Normal", QualityMax = "Legendary" },
            }, Delete = false, RequireCoveredEmpty = false };
        var created = await AppliedEffectAsync("zone-create", create).ConfigureAwait(false);
        if (created is not Receipts.ZoneEffect.CreatedCase parts || parts.Zones.ToList() is not [{ ZoneId: { Length: > 0 } zoneId } component] || component.Cells.Count != interior.Count)
        {
            throw new AcceptanceException($"zone-create: want one {interior.Count}-cell component, got {created}");
        }

        var afterCreate = await StateAsync("after-create", zoneId).ConfigureAwait(false);
        if (afterCreate.Row is null)
        {
            throw new AcceptanceException("after-create: the created zone is not listed");
        }

        await FilterAsync("filter-after-create", zoneId, "Important", ["Steel", "WoodLog"], 0.5, 1).ConfigureAwait(false);

        // Native keeps no receipt replay (the in-process hop loses no reply): a second create over the zoned
        // rectangle finds no free cell, refuses, and does not adopt the standing zone.
        await RefusedAsync("zone-create-again", create, Common.FailureCode.InvalidRequest).ConfigureAwait(false);
        if (await StateAsync("after-create-again", zoneId).ConfigureAwait(false) != afterCreate)
        {
            throw new AcceptanceException("zone-create-again: a refused create changed the zone");
        }

        // --- settings ---
        var zone = new Common.Ref { Id = zoneId };
        await RefusedAsync("stockpile-unresolved", new Operations.ZoneIntent
        {
            Zone = zone,
            Stockpile = new Operations.StockpileSettings { Filter = new Operations.FilterPatch { Allow = [Def("NoSuchThingDefForZoneAccept")], HitPoints = new Operations.HitPointRange.Preserve() }, Priority = new Operations.StockpilePriorityChange.Preserve(), Preset = new Operations.StockpilePresetChange.Preserve() }, Delete = false, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing }, Common.FailureCode.InvalidRequest).ConfigureAwait(false);
        if (await StateAsync("after-unresolved", zoneId).ConfigureAwait(false) != afterCreate)
        {
            throw new AcceptanceException("stockpile-unresolved: a refused body changed the zone");
        }

        var patch = new Operations.ZoneIntent
        {
            Zone = zone,
            Stockpile = new Operations.StockpileSettings
            {
                Priority = new Operations.StockpilePriorityChange.Selected(StockpilePriority.Normal),
                Filter = new Operations.FilterPatch { HitPoints = new Operations.HitPointRange.Selected(0.25, 0.75)}, Preset = new Operations.StockpilePresetChange.Preserve() }, Delete = false, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing };
        if ((await AppliedAsync("stockpile-patch", patch).ConfigureAwait(false)).ZoneId != zoneId)
        {
            throw new AcceptanceException("stockpile-patch: the effect names another zone");
        }

        await FilterAsync("filter-after-patch", zoneId, "Normal", ["Steel", "WoodLog"], 0.25, 0.75).ConfigureAwait(false);
        var afterPatch = await StateAsync("after-patch", zoneId).ConfigureAwait(false);
        _ = await AppliedAsync("stockpile-patch-resend", patch).ConfigureAwait(false);
        if (await StateAsync("after-patch-resend", zoneId).ConfigureAwait(false) != afterPatch)
        {
            throw new AcceptanceException("stockpile-patch-resend: settings that already hold were written again");
        }

        // --- presets ---
        async Task<(HashSet<string> Defs, Observations.StockpileFilter? Filter)> PresetAsync(FilterBase preset)
        {
            var key = "preset-" + preset;
            _ = await AppliedAsync(key, new Operations.ZoneIntent { Zone = zone, Stockpile = new Operations.StockpileSettings { Preset = new Operations.StockpilePresetChange.Selected(preset), Priority = new Operations.StockpilePriorityChange.Preserve() }, Delete = false, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing }).ConfigureAwait(false);
            var filter = (await RowAsync(key + "-readback", zoneId).ConfigureAwait(false) as Observations.ZoneState.Stockpile)?.Filter;
            return (filter?.AllowedDefNames.ToHashSet(StringComparer.Ordinal) ?? [], filter);
        }

        var (nonperishable, _) = await PresetAsync(FilterBase.Nonperishables).ConfigureAwait(false);
        var (outdoor, _) = await PresetAsync(FilterBase.OutdoorSafe).ConfigureAwait(false);
        var (indoor, _) = await PresetAsync(FilterBase.IndoorOnly).ConfigureAwait(false);
        if (outdoor.Count == 0 || indoor.Count == 0 || indoor.Overlaps(outdoor) || !nonperishable.SetEquals(indoor.Union(outdoor)))
        {
            throw new AcceptanceException($"presets: indoor_only ({indoor.Count}) and outdoor_safe ({outdoor.Count}) do not partition nonperishables ({nonperishable.Count})");
        }

        // outdoor_safe_waste is the materials yard's (#3232): rotten corpses and the toxic wastepack, not Cloth.
        var (waste, wasteFilter) = await PresetAsync(FilterBase.OutdoorSafeWaste).ConfigureAwait(false);
        if (!waste.Any(n => n.StartsWith("Corpse_", StringComparison.Ordinal)) || wasteFilter is not { AllowFresh: false, AllowRotten: true })
        {
            throw new AcceptanceException($"presets: outdoor_safe_waste must allow corpse defs with allowFresh off and allowRotten on; got {waste.Count} defs, {wasteFilter}");
        }

        if ((indoor.Contains("Wastepack") && !waste.Contains("Wastepack")) || !indoor.Contains("Cloth") || waste.Contains("Cloth"))
        {
            throw new AcceptanceException("presets: outdoor_safe_waste must allow the wastepack and not Cloth, which is indoor_only");
        }

        s.Record("preset_partition", $"indoor_only {indoor.Count} + outdoor_safe {outdoor.Count} = nonperishables {nonperishable.Count}; outdoor_safe_waste {waste.Count}");

        // --- cells ---
        // Removing one corner leaves a contiguous L and frees the cell; re-adding that cell is the one add the site allows.
        Operations.ZoneIntent Edit(Common.Ref target, bool remove)
        {
            var cells = new Operations.Cells { Selection = new Operations.CellList { Cells = [corner] } };
            return remove ? new Operations.ZoneIntent { Zone = target, RemoveCells = cells, Delete = false, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing } : new Operations.ZoneIntent { Zone = target, AddCells = cells, Delete = false, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing };
        }

        await RefusedAsync("zone-edit-missing", Edit(new Common.Ref { Id = "Zone_999999" }, remove: true), Common.FailureCode.NotFound).ConfigureAwait(false);
        var removed = await AppliedAsync("zone-edit-remove", Edit(zone, remove: true)).ConfigureAwait(false);
        if (removed.Present != true || removed.ListedCellCount != interior.Count - 1)
        {
            throw new AcceptanceException($"zone-edit-remove: want {interior.Count - 1} listed cells, got {removed}");
        }

        var afterRemove = await StateAsync("after-remove", zoneId).ConfigureAwait(false);
        if (afterRemove.Row is null || afterRemove == afterPatch)
        {
            throw new AcceptanceException("after-remove: the zone is gone or unchanged by the removal");
        }

        _ = await AppliedAsync("zone-edit-remove-resend", Edit(zone, remove: true)).ConfigureAwait(false);
        if (await StateAsync("after-remove-resend", zoneId).ConfigureAwait(false) != afterRemove)
        {
            throw new AcceptanceException("zone-edit-remove-resend: a cell already out of the zone was edited again");
        }

        // The re-add lands under a running native clock: only the supervised run digests zones, and its first digest
        // baselines, so an edit made while paused never reaches a row. The run ends on the zone's clock_invalidation row.
        var request = new ClockRunPlan { ExpectedGeneration = generation, Speed = Clock.Speed.Normal, Policy = Policy };
        Receipts.ZoneEffect.ZoneCase? added = null;
        long addedSince = 0;
        LogRow? invalidated = null;
        var status = await s.RunClockUntilHeldAsync(
            "probe",
            s.TicksLeft,
            request,
            async (_, _) =>
            {
                if (added is null)
                {
                    addedSince = ClockRows.NowUnixMs();
                    added = await AppliedAsync("zone-edit-add", Edit(zone, remove: false)).ConfigureAwait(false);
                    if (added.ListedCellCount != interior.Count)
                    {
                        throw new AcceptanceException($"zone-edit-add: want {interior.Count} listed cells, got {added}");
                    }
                }

                invalidated = s.Telemetry(TelemetryKinds.ClockInvalidation).FirstOrDefault(row =>
                    ClockRows.ObservedSince(row, addedSince) && ClockRows.Strings(row, "entity_ids").Contains(zoneId, StringComparer.Ordinal));
                return invalidated is not null;
            },
            cancellationToken).ConfigureAwait(false);
        if (invalidated is null)
        {
            throw new AcceptanceException(added is null
                ? $"probe: the clock held before the re-add ({(status.Hold as Clock.HoldState.HeldCase)?.Reason}): {status.Hold}"
                : $"probe: the clock held ({(status.Hold as Clock.HoldState.HeldCase)?.Reason}) before native named zone {zoneId}: {status.Hold}");
        }

        var families = ClockRows.Strings(invalidated, "families");
        var ids = ClockRows.Strings(invalidated, "entity_ids");
        var rect = ClockRows.Map(invalidated, "cells");
        long? Edge(string key) => ClockRows.Whole(rect.GetValueOrDefault(key));
        var shown = $"families [{string.Join(' ', families)}] ids [{string.Join(' ', ids)}] cells ({Edge("min_x")},{Edge("min_z")})-({Edge("max_x")},{Edge("max_z")})";
        s.Record("zone_invalidation", shown);
        if (families is not [nameof(Clock.FactFamily.Colony)] || ids is not [_]
            || Edge("min_x") != footprint.X || Edge("min_z") != footprint.Z || Edge("max_x") != footprint.X + 1 || Edge("max_z") != footprint.Z + 1)
        {
            throw new AcceptanceException($"probe: want the colony family naming only {zoneId} over the footprint {footprint}, got {shown}");
        }

        if ((await RowAsync("after-add", zoneId).ConfigureAwait(false)) is null)
        {
            throw new AcceptanceException("after-add: the zone is not listed");
        }

        // --- delete ---
        var delete = new Operations.ZoneIntent { Zone = zone, Delete = true, RequireCoveredEmpty = false, Kind = Operations.ZoneType.Existing };
        var deleted = await AppliedAsync("zone-delete", delete).ConfigureAwait(false);
        if (deleted.ZoneId != zoneId || deleted.Present == true)
        {
            throw new AcceptanceException($"zone-delete: want {zoneId} absent in the evidence, got {deleted}");
        }

        if ((await RowAsync("after-delete", zoneId).ConfigureAwait(false)) is not null)
        {
            throw new AcceptanceException("after-delete: the deleted zone is still listed");
        }

        // A zone already gone is the deletion's effect: the resend applies.
        _ = await AppliedAsync("zone-delete-resend", delete).ConfigureAwait(false);
    }
}
