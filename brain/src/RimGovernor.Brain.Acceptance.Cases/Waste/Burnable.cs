using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>waste/burnable-filters</c>: the RimGovernorBurnable and RimGovernorNotBurnable special filters on real stockpile
/// zones under RimWorld's own hauling. test/burnable_stage stays bespoke: the stage primitives cannot spawn rotted
/// corpses or gear varied by hit points, quality and tainted.
/// </summary>
internal static class WasteBurnable
{
    private const string StageTool = "test/burnable_stage";
    private const string ReadTool = "test/burnable_read";

    private static readonly FixtureOp<NoArgs, Fx.BurnableStaged> Staged = new(
        StageTool,
        _ => [],
        (label, reply) => ServedOps.Reply<Fx.BurnableStaged>(StageTool)(label, reply) is { Success: true, Items.Count: > 0 } staged
            ? staged
            : throw new AcceptanceException($"{label}: {StageTool} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<string, Fx.BurnableRead> Read = new(ReadTool, ids => ServedOps.Args(("ids", ids)), ServedOps.Reply<Fx.BurnableRead>(ReadTool));

    /// <summary>Names the zone each staged item with a fixed verdict must end in; gear probes are judged by MarketValue instead.</summary>
    private static readonly Dictionary<string, string> Expect = new(StringComparer.Ordinal)
    {
        ["potatoes_rotted"] = "dump",
        ["potatoes_fresh"] = "store",
        ["corpse_animal_rotted"] = "dump",
        ["corpse_animal_fresh"] = "store",
        ["corpse_stranger_rotted"] = "dump",
        ["corpse_colonist_rotted"] = "store",
        ["revolver_normal"] = "store",
        ["steel_stack"] = "store",
    };

    private static readonly string[] GearDefs = ["Apparel_Pants", "MeleeWeapon_Club", "Gun_Revolver"];

    internal static TypedCase Case() => new()
    {
        Name = "waste/burnable-filters",
        Scope = "Special thing filters RimGovernorBurnable and RimGovernorNotBurnable on real stockpile zones: a Low zone "
            + "disallowing not-burnable beside a Normal zone disallowing burnable, with one colonist hauling, so RimWorld's own "
            + "hauling puts rotted rottables and worn-out gear only in the Low zone and everything else (fresh rottables, a "
            + "rotted colonist corpse, valuable gear, a resource stack) in the Normal one. The verdict is vanilla ThingFilter "
            + "and hauling semantics over live Things, which no unit test can run.",
        Ticks = 30000,
        Tier = CaseTier.OnDemand,
        Ops = [StageTool, ReadTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static string ZoneOf(Fx.BurnableRow row) => row.Priority switch
    {
        "Low" => "dump",
        "Normal" => "store",
        _ => "",
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var staged = await s.FixtureAsync("burnable-stage", Staged, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var items = staged.Items;
        var ids = string.Join(',', items.Select(i => i.Id));

        async Task<SortedDictionary<string, Fx.BurnableRow>> ReadAsync(string label, CancellationToken ct)
        {
            var read = await s.FixtureAsync(label, Read, ids, ct).ConfigureAwait(false);
            if (read.Rows.Count != items.Count)
            {
                throw new AcceptanceException($"{label}: want {items.Count} rows, got {read.Rows.Count}");
            }

            var byLabel = new SortedDictionary<string, Fx.BurnableRow>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                byLabel[items[i].Label] = read.Rows[i].Present
                    ? read.Rows[i]
                    : throw new AcceptanceException($"{label}: item {items[i].Label} ({items[i].Id}) is gone");
            }

            return byLabel;
        }

        // A fresh corpse starts rotting after a few days, so the haul must land well inside the window.
        var rows = new SortedDictionary<string, Fx.BurnableRow>(StringComparer.Ordinal);
        _ = await s.RunUntilAsync("burnable-haul", s.TicksLeft, async ct =>
        {
            rows = await ReadAsync("burnable-read", ct).ConfigureAwait(false);
            var stored = rows.Values.Count(row => ZoneOf(row).Length != 0);
            return (Wait.Signature(string.Create(CultureInfo.InvariantCulture, $"stored={stored}")), stored == rows.Count);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("placement", string.Join(' ', rows.Select(r => $"{r.Key}:{ZoneOf(r.Value)}")));

        // The filter verdict and the zones' own admission agree with the placement: the dump (Low) admits only
        // burnable, the store (Normal) only not-burnable.
        foreach (var (label, row) in rows)
        {
            var zone = ZoneOf(row);
            if (row.Burnable == row.NotBurnable && !label.StartsWith("steel", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"{label}: filters are not complements: {row}");
            }

            if ((zone == "dump" && !row.Burnable) || (zone == "store" && row.Burnable))
            {
                throw new AcceptanceException($"{label}: stored in the {zone}: {row}");
            }

            if (!row.ZoneAllows)
            {
                throw new AcceptanceException($"{label}: its zone does not admit it: {row}");
            }

            if (Expect.TryGetValue(label, out var want) && zone != want)
            {
                throw new AcceptanceException($"{label}: want the {want}, got the \"{zone}\": {row}");
            }
        }

        // A resource stack is outside both filters (CanEverMatch false), so neither disallow rejects it.
        if (rows.GetValueOrDefault("steel_stack") is { } steel && (steel.Burnable || steel.NotBurnable))
        {
            throw new AcceptanceException($"steel_stack: matched a filter that can never judge its def: {steel}");
        }

        // Gear is judged by MarketValue below the cutoff, whatever folds into it.
        var gear = rows.Where(r => GearDefs.Contains(r.Value.Def)).ToList();
        foreach (var (label, row) in gear)
        {
            if (row.Burnable != (row.MarketValue < staged.Cutoff))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: MarketValue {row.MarketValue:F2} against cutoff {staged.Cutoff:F2} but burnable={row.Burnable}"));
            }
        }

        if (!gear.Any(g => g.Value.Burnable) || gear.All(g => g.Value.Burnable))
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"gear probes did not straddle the {staged.Cutoff:F2} cutoff: {string.Join(' ', gear.Select(g => $"{g.Key}:{g.Value.MarketValue}"))}"));
        }

        s.Record("marketValue", string.Join(' ', gear.Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}:{g.Value.MarketValue:F2}"))));
    }
}
