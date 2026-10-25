using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>supplies/reads</c>: the supply census is internally consistent over staged WoodLog and Steel, for held and
/// spawned-only ownership, by default, and for the weapons category; the read pass leaves the paused game untouched.
/// </summary>
internal static class SuppliesReads
{
    private static readonly string[] Defs = ["WoodLog", "Steel"];

    internal static TypedCase Case() => new()
    {
        Name = "supplies/reads",
        Scope = "observations_list_supplies over staged WoodLog and Steel: in each row items match stacks with a CAS snapshot "
            + "each, spawned plus the holders' units equal the total (held mode) or spawned is the total with no holders "
            + "(spawned mode); the default request reads positive ours stock, the weapons census lists WoodLog as a makeshift "
            + "equippable, and the paused game's tick does not move. No gameplay orders.",
        Ticks = 0,
        Tier = CaseTier.Smoke,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 4,
                Height = 2,
                Near = Fx.StageAnchor.MapCenter,
                OffsetX = 3,
                OffsetZ = 3,
                Radius = 20,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty],
            },
            Things =
            [
                new Fx.StageThing { Name = "wood", Def = Defs[0], Count = 50, Cell = new(0, 0) },
                new Fx.StageThing { Name = "steel", Def = Defs[1], Count = 50, Cell = new(1, 0) },
            ],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await IdentityAsync(s, "identity-before", cancellationToken).ConfigureAwait(false);
        if (before.Paused != true)
        {
            throw new AcceptanceException("identity-before: the staged game is not paused");
        }

        foreach (var includeHeld in new[] { true, false })
        {
            var label = includeHeld ? "held" : "spawned";
            var supplies = await CensusAsync(s, label, new RimGovernor.Brain.GameFacts.Observations.StockFilter { DefNames = [.. Defs], Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.All, IncludeHeld = includeHeld }, cancellationToken).ConfigureAwait(false);
            if (supplies.Context != before.Context)
            {
                throw new AcceptanceException($"{label}: context drifted mid-run: {supplies.Context} vs {before.Context}");
            }

            var rows = supplies.Stocks.ToList();
            if (rows.Count != Defs.Length)
            {
                throw new AcceptanceException($"{label}: expected {Defs.Length} stock rows, found {rows.Count}");
            }

            foreach (var row in rows)
            {
                Check(label, row, includeHeld);
            }

            s.Record(label + "_stocks", new JsonArray([.. rows.Select(r => JsonNode.Parse(OrdersJson.Serialize(r)))]));
        }

        var defaults = await CensusAsync(s, "default", new RimGovernor.Brain.GameFacts.Observations.StockFilter { DefNames = [.. Defs], Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours, IncludeHeld = true }, cancellationToken).ConfigureAwait(false);
        foreach (var row in defaults.Stocks)
        {
            if (row.Ours <= 0)
            {
                throw new AcceptanceException($"default: want a positive ours quantity, got {row.DefName}: ours {row.Ours}");
            }
        }

        var weapons = await CensusAsync(s, "weapons", new RimGovernor.Brain.GameFacts.Observations.StockFilter { Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Weapons, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.All, IncludeHeld = true }, cancellationToken).ConfigureAwait(false);
        var weaponDefs = weapons.Stocks.Select(row => row.DefName).ToList();
        s.Record("weapon_census", new JsonArray([.. weaponDefs.Select(d => (JsonNode?)d)]));
        if (!weaponDefs.Contains(Defs[0]))
        {
            throw new AcceptanceException($"weapons: WoodLog missing from the census as a makeshift equippable: {string.Join(", ", weaponDefs)}");
        }

        var after = await IdentityAsync(s, "identity-after", cancellationToken).ConfigureAwait(false);
        if (after.Paused != true || after.Context != before.Context)
        {
            throw new AcceptanceException($"identity-after: the read pass moved the game: paused {after.Paused}, {after.Context} vs {before.Context}");
        }
    }

    private static Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(TypedSession s, string label, CancellationToken cancellationToken) =>
        s.CallAsync(label, NativeOps.ReadIdentity, new RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest(), cancellationToken);

    private static async Task<RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot> CensusAsync(TypedSession s, string label, RimGovernor.Brain.GameFacts.Observations.StockFilter filter, CancellationToken cancellationToken)
    {
        return await s.CallAsync(label, NativeOps.ListSupplies, new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest { Scope = s.Scope, Filter = filter }, cancellationToken).ConfigureAwait(false);
    }

    // One row's counts agree: each stack is one item (with its CAS snapshot unless it is a loose supply the Allow rules
    // do not admit), and spawned plus the holders' units is the total (held mode) or spawned
    // is the total with no holders (spawned mode).
    private static void Check(string label, RimGovernor.Brain.GameFacts.Observations.ResourceStock row, bool includeHeld)
    {
        var what = $"{label}: {row.DefName}";
        if (string.IsNullOrEmpty(row.DefName) || row.Units <= 0)
        {
            throw new AcceptanceException($"{what}: a stock row without a definition or units: {row}");
        }

        if (row.PlayerFaction > row.Units || row.Ours > row.Units)
        {
            throw new AcceptanceException($"{what}: playerFaction {row.PlayerFaction} / ours {row.Ours} above units {row.Units}");
        }

        var items = row.Items.ToList();
        if (items.Count != row.Stacks || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Count || items.Any(i => string.IsNullOrEmpty(i.Id)))
        {
            throw new AcceptanceException($"{what}: {items.Count} distinct-id items for {row.Stacks} stacks");
        }

        var holders = row.Holders.ToList();
        if (!includeHeld)
        {
            if (holders.Count > 0 || row.Spawned != row.Units)
            {
                throw new AcceptanceException($"{what}: {holders.Count} holders, spawned {row.Spawned} of {row.Units} units without includeHeld");
            }

            return;
        }

        var held = holders.Sum(h => h.Units);
        if (holders.Any(h => h.Units <= 0 || string.IsNullOrEmpty(h.Holder)) || row.Spawned + held != row.Units)
        {
            throw new AcceptanceException($"{what}: spawned {row.Spawned} + holder units {held} != units {row.Units}, or a holder without id or units");
        }
    }
}
