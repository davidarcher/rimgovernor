using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/stranded_loot</c> (#3235): a loose stack no storage has room for is reported with the storage group
/// whose filter accepts it. A unit test cannot prove it: the accepting ids come from native storage settings
/// (<c>ISlotGroupParent.Accepts</c>) and the headroom from the native zone grid.
/// </summary>
internal static class StorageStrandedLoot
{
    internal static TypedCase Case() => new()
    {
        Name = "storage/stranded_loot",
        Scope = "Native loot census over a staged one-cell stockpile accepting Steel and WoodLog that holds a Steel stack: a loose "
            + "unforbidden WoodLog stack reads zero storage headroom and names that zone in acceptedBy. Read-only, paused.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 6,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 6, 6), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Zones] }],

            // Steel fills the only cell; WoodLog cannot stack on it, so the zone accepts WoodLog with no room for it.
            Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(1, 1)], Allow = ["Steel", "WoodLog"] }],
            Things =
            [
                new Fx.StageThing { Name = "steel", Def = "Steel", Count = 50, Cell = new(1, 1) },
                new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 20, Cell = new(1, 4) },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var zone = staged.Zones["store"];
        var wood = staged.Things["wood"].Ids.Single();

        var facts = await s.CallAsync("stranded-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var row = facts.EventLoot.FirstOrDefault(i => i.Item.Id == wood)
            ?? throw new AcceptanceException($"the loot census lists no row for the WoodLog stack {wood}: {string.Join("; ", facts.EventLoot)}");
        s.RecordWire("wood-row", row);

        if (row is not { Forbidden: false, Safety: Observations.HaulSafety.Safe, StorageHeadroom: 0 })
        {
            throw new AcceptanceException($"want an unforbidden safe WoodLog stack with zero storage headroom, got {row}");
        }

        var accepted = row.AcceptedBy.Select(r => r.Id).ToList();
        if (!accepted.Contains(zone))
        {
            throw new AcceptanceException($"the stranded WoodLog stack names [{string.Join(", ", accepted)}], not the accepting zone {zone}");
        }
    }
}
