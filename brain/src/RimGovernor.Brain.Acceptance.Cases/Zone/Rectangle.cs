using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>zone/rectangle</c>: a stockpile rectangle create through operations_apply over a staged site.</summary>
internal static class ZoneRectangle
{
    internal static TypedCase Case() => new()
    {
        Name = "zone/rectangle",
        Scope = "Native stockpile rectangle placement uses vanilla cell eligibility, keeps both components across a wall barrier and ground holding Steel, skips an existing player stockpile without changing it, and replays all created IDs without duplicates after 260 newer actions. A snapshot cannot verify the native designator, zone grid, hauling settings or replay cache.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = Fixture,
        Run = RunAsync,
    };

    // The pinned lab rectangle, 7x5 cells 14 east and 4 north of the map centre, cleared to bare soil: a wall barrier
    // splitting it at x=3 with an obstacle at (1,2), five Steel stored at its corner, and an existing low-priority player
    // stockpile allowing nothing on its far corner.
    private static readonly Fx.StageSpec Fixture = new()
    {
        Paused = true,
        Site = new Fx.StageSite { Width = 7, Height = 5, OffsetX = 14, OffsetZ = 4 },
        Clear = [new Fx.StageClear
        {
            Rect = new Fx.StageRect(0, 0, 7, 5),
            Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs, Fx.StageClearKind.Fog],
        }],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 7, 5), Terrain = "Soil" }],
        Buildings = [new Fx.StageBuilding { Name = "walls", Def = "Wall", Stuff = "WoodLog", Cells = [new(3, 0), new(3, 1), new(3, 2), new(3, 3), new(3, 4), new(1, 2)] }],
        Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 5, Cell = new(0, 0) }],
        Zones = [new Fx.StageZone { Name = "player", Kind = Fx.StageZoneKind.Stockpile, Label = "Player stockpile", Cells = [new(6, 4)], Priority = Fx.StageStoragePriority.Low, Allow = [] }],
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var (x, z) = (site.X, site.Z);
        var playerId = staged.Zones["player"];

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var intent = new Operations.ZoneIntent
        {
            Label = "Rectangle storage",
            Kind = Operations.ZoneType.Stockpile,
            AddCells = new Operations.Cells { Selection = new Operations.Rectangle { Origin = new Common.Cell { X = x, Z = z }, Width = 7, Height = 5 } },
            Stockpile = new Operations.StockpileSettings
            {
                Priority = Operations.StoragePriority.Important,
                Preset = Operations.FilterPreset.Nothing,
                Filter = new Operations.FilterPatch { Allow = [new Operations.FilterSelector { Definition = new Operations.FilterSelectorDefinition.ThingDefCase("Steel") }] },
            },
        };

        async Task<Receipts.ZoneEffect> ApplyAsync(string label)
        {
            var outcome = await TypedApply.AppliedAsync(s, "rectangle-stockpile", intent, cancellationToken).ConfigureAwait(false);
            var effect = outcome.Applied?.Outcome?.Applied?.Observed?.Effect?.Zone
                ?? throw new AcceptanceException($"{label}: missing applied stockpile evidence: {outcome}");
            s.RecordWire(label, effect);
            return effect;
        }

        var first = await ApplyAsync("rectangle-create").ConfigureAwait(false);
        var rows = first.Created.ToList();
        if (rows.Count != 2)
        {
            throw new AcceptanceException($"expected both components: {first}");
        }

        var ids = new List<string> { playerId };
        var actual = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var expected = (from dx in Enumerable.Range(0, 7)
                        from dz in Enumerable.Range(0, 5)
                        where dx != 3 && !(dx == 1 && dz == 2) && !(dx == 6 && dz == 4)
                        select new Cell(x + dx, z + dz)).ToHashSet();
        var count = 0;
        foreach (var row in rows)
        {
            var id = row.ZoneId ?? "";
            if (id.Length == 0 || id == playerId || actual.ContainsKey(id))
            {
                throw new AcceptanceException($"invalid created identity: {row}");
            }

            ids.Add(id);
            actual[id] = [];
            foreach (var c in row.Cells.Select(c => new Cell(c.X, c.Z)))
            {
                if (!expected.Remove(c))
                {
                    throw new AcceptanceException($"unexpected/duplicate cell {c}");
                }

                actual[id].Add(c);
                count++;
            }
        }

        if (count != 28 || expected.Count != 0)
        {
            throw new AcceptanceException($"lost usable cells: count={count} missing={string.Join(' ', expected)}");
        }

        var byZone = (await TypedApply.CellsAsync(s, "rectangle-grid", site, cancellationToken).ConfigureAwait(false)).ZoneCells();
        foreach (var (id, cells) in actual)
        {
            var gridCells = byZone.GetValueOrDefault(id) ?? [];
            if (!gridCells.ToHashSet().SetEquals(cells))
            {
                throw new AcceptanceException($"zone {id}: receipt/grid mismatch: {string.Join(' ', cells)} vs {string.Join(' ', gridCells)}");
            }
        }

        var zonesReply = await s.CallAsync("rectangle-zone-settings", NativeOps.ListZones, new Observations.ListZonesRequest
        {
            Scope = s.Scope,
            Ids = [.. ids],
            IncludeFilter = true,
        }, cancellationToken).ConfigureAwait(false);
        var zones = TypedReads.Observed("rectangle-zone-settings", NativeOps.ListZones, zonesReply, zonesReply.Outcome?.Observed).Zones.ToList();
        s.RecordWire("zones", zones);
        if (zones.Count != 3)
        {
            throw new AcceptanceException($"expected three zones, got {zones.Count}");
        }

        foreach (var row in zones)
        {
            var allowed = row.Filter?.AllowedDefNames.ToList() ?? [];
            if (row.Id == playerId)
            {
                if (row.Priority != "Low" || allowed.Count != 0)
                {
                    throw new AcceptanceException($"player zone changed: {row}");
                }
            }
            else if (row.Priority != "Important" || allowed is not ["Steel"])
            {
                throw new AcceptanceException($"created settings mismatch: {row}");
            }
        }

        // Creation receipts remain recoverable after ordinary replay entries age out.
        var churn = await s.CallAsync("rectangle-replay-churn", NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [.. Enumerable.Range(0, 260).Select(i => new Operations.Action
            {
                Key = $"rectangle-settings-{i:D3}",
                Intent = new Operations.ZoneIntent
                {
                    Zone = new Common.Ref { Id = ids[1] },
                    Stockpile = new Operations.StockpileSettings { Priority = Operations.StoragePriority.Important },
                },
            })],
        }, cancellationToken).ConfigureAwait(false);
        if (churn.BatchFailure is not null || churn.Results.Count != 260)
        {
            throw new AcceptanceException($"rectangle-replay-churn: {churn.BatchFailure} with {churn.Results.Count} results");
        }

        var replay = await ApplyAsync("rectangle-replay").ConfigureAwait(false);
        if (!replay.Created.SequenceEqual(first.Created))
        {
            throw new AcceptanceException($"replay lost creation identities: {replay}");
        }

        var after = (await TypedApply.CellsAsync(s, "rectangle-replay-grid", site, cancellationToken).ConfigureAwait(false)).ZoneCells();
        foreach (var id in ids)
        {
            if (!(byZone.GetValueOrDefault(id) ?? []).SequenceEqual(after.GetValueOrDefault(id) ?? []))
            {
                throw new AcceptanceException($"replay changed zone {id}");
            }
        }
    }
}
