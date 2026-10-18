using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apparel/harm_armor_direction</c> (#3547, the last child of the harm model, #3471): armor choices move the risk-averse
/// currency in the expected direction. One soldier wears a cloth duster and a short bow, a devilstrand duster lies in a stockpile,
/// the world runs under Cassandra on day 25 with the world's hostile factions, and the first review's coupled selection prices
/// every gear alternative of the soldier: each alternative's <c>value.harm</c> is below the baseline's (the worn outfit's), and its
/// death-risk floor was assessed.
/// <para>
/// The stage reaches a calibrated exposure cell: the quiet colony's raid points are the game's floor of 35 (threat scale zero),
/// and a skill-0 short-bow soldier's defense capacity (5 points per DPS of melee and ranged power, about 29) puts the ratio of raid points to
/// capacity near 1.2, in the 1 to 2 bin, which the committed table (<c>Policy/Harm/exposure.json</c>) calibrates for gunners. A ratio in
/// an uncalibrated bin would make harm unknown, and the case would fail on that, not on the direction.
/// </para>
/// </summary>
internal static class HarmArmorDirection
{
    private const string Cloth = "Cloth";
    private const string Devilstrand = "DevilstrandCloth";
    private const string Duster = "Apparel_Duster";
    private const string Bow = "Bow_Short";
    private const string GearPrefix = "gear/";

    // Cassandra's threat comps gate on days passed, and her raids are no longer all forced after day 20 (RaidsForcedBeforeDays).
    private const float Day = 25;

    internal static TypedCase Case() => new()
    {
        Name = "apparel/harm_armor_direction",
        Scope = "Issue #3547, the harm model (epic #3471): in a colony under Cassandra past day 20 with hostile factions on the "
            + "world, whose one soldier wears a cloth duster while a devilstrand duster lies in a stockpile, the first review's "
            + "coupled selection prices the soldier's gear alternative with a harm term below the baseline's, with the death-risk "
            + "floor assessed: armor moves the risk-averse currency the right way. A unit test cannot observe the live storyteller, "
            + "raid points, defense capacity, worn outfit and gear options the term reads together.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the service runs the game at speed and the wait reads the log ring seconds behind it; the decisive review lands near tick +2700
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = Day },
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 5,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 6, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 6, 5), Terrain = "Soil", Home = true }],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "stock",
                    Kind = Fx.StageZoneKind.Stockpile,
                    Allow = [Duster],
                    Cells = [new(1, 1), new(2, 1)],
                },
            ],
            Things = [new Fx.StageThing { Name = "devilstrand", Def = Duster, Stuff = Devilstrand, Cell = new(1, 1) }],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "soldier",
                    Colonist = 0,
                    Cell = new(4, 2),
                    SkillLevel = 0,
                    Gear = new Fx.StageGear { Weapon = Bow, Apparel = [Duster], ApparelStuff = Cloth, Strip = true },
                },
            ],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "harm-armor" },
        Run = RunAsync,
    };

    /// <summary>One priced outcome of a <c>coupled_selection</c> row: the commitments it holds and what its value logged.</summary>
    private sealed record Priced(IReadOnlyList<string> Ids, double? Harm, bool DeathAssessed, IReadOnlyList<string> Unknown, string Exposure);

    private sealed record Selection(ulong Sequence, Priced Baseline, IReadOnlyList<Priced> Alternatives);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        if (s.Staged?.Pawns.GetValueOrDefault("soldier") is not { } soldier)
        {
            throw new AcceptanceException("the stage made no soldier");
        }

        // The defense capacity the colony reads: 5 points per DPS of melee and ranged power (DefenseCapacityPolicy).
        var capacity = DefenseCapacityPolicy.DefensePointsPerDps * (soldier.MeleeDps + soldier.RangedDps);
        s.Record("soldier", string.Create(CultureInfo.InvariantCulture,
            $"weapon={soldier.Weapon} melee_dps={soldier.MeleeDps:0.###} ranged_dps={soldier.RangedDps:0.###} capacity={capacity:0.###} worn={soldier.Worn}"));

        var before = Rows(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<Selection> rows = [];
        _ = await s.ServeUntilAsync("harm-armor", s.TicksLeft, ct =>
        {
            rows = Rows(s, before);
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {rows.Count(r => r.Alternatives.Count > 0)}"), rows.Any(r => r.Alternatives.Count > 0)));
        }, cancellationToken).ConfigureAwait(false);

        var first = rows.FirstOrDefault(r => r.Alternatives.Count > 0)
            ?? throw new AcceptanceException($"no coupled selection compared a gear alternative in {rows.Count} rows");
        s.Record("baseline", Describe(first.Baseline));
        s.Record("alternatives", new JsonArray([.. first.Alternatives.Select(a => (JsonNode?)JsonValue.Create(Describe(a)))]));

        if (first.Baseline.Harm is not { } worn)
        {
            throw new AcceptanceException($"the baseline's harm is unknown ({Describe(first.Baseline)}): the stage did not reach a calibrated exposure cell (capacity {capacity:0.###})");
        }

        foreach (var alternative in first.Alternatives)
        {
            if (alternative.Harm is not { } harm)
            {
                throw new AcceptanceException($"a gear alternative's harm is unknown: {Describe(alternative)}");
            }

            if (!(harm < worn))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                    $"the devilstrand duster did not lower the harm: alternative {string.Join('+', alternative.Ids)} {harm} against the worn outfit's {worn}"));
            }

            if (!alternative.DeathAssessed)
            {
                throw new AcceptanceException($"the death-risk floor was not assessed for {string.Join('+', alternative.Ids)}: {Describe(alternative)}");
            }
        }
    }

    private static string Describe(Priced p) =>
        string.Create(CultureInfo.InvariantCulture, $"[{string.Join(' ', p.Ids)}] harm={p.Harm?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} death_assessed={p.DeathAssessed} exposure={p.Exposure} unknown=[{string.Join(' ', p.Unknown)}]");

    private static IReadOnlyList<Selection> Rows(TypedSession s, ulong after) =>
        [.. s.Telemetry(TelemetryKinds.CoupledSelection).Where(r => r.Sequence > after).Select(Row)];

    private static Selection Row(LogRow row)
    {
        var fields = row.Fields();
        var priced = new List<Priced>();
        if (fields.GetValueOrDefault("rejected") is List<object?> rejected)
        {
            foreach (var entry in rejected.OfType<Dictionary<string, object?>>())
            {
                var ids = (entry.GetValueOrDefault("ids") as List<object?> ?? []).OfType<string>().ToList();
                if (entry.GetValueOrDefault("vector") is Dictionary<string, object?> vector)
                {
                    priced.Add(Price(ids, vector));
                }
            }
        }

        var selectedIds = (fields.GetValueOrDefault("selected_ids") as List<object?> ?? []).OfType<string>().ToList();
        if (selectedIds.Count > 0 && fields.GetValueOrDefault("selected") is Dictionary<string, object?> selected)
        {
            priced.Add(Price(selectedIds, selected));
        }

        return new Selection(
            row.Sequence,
            Price([], fields.GetValueOrDefault("baseline") as Dictionary<string, object?> ?? []),
            [.. priced.Where(p => p.Ids.Any(id => id.StartsWith(GearPrefix, StringComparison.Ordinal)))]);
    }

    private static Priced Price(IReadOnlyList<string> ids, Dictionary<string, object?> vector)
    {
        var value = vector.GetValueOrDefault("value") as Dictionary<string, object?> ?? [];
        var floor = (value.GetValueOrDefault("floors") as Dictionary<string, object?>)?.GetValueOrDefault("death_risk_floor") as Dictionary<string, object?>;
        var detail = value.GetValueOrDefault("harm_detail") as Dictionary<string, object?>;
        var range = detail?.GetValueOrDefault("exposure_range") is List<object?> hits ? string.Join("..", hits.Select(h => Convert.ToString(h, CultureInfo.InvariantCulture))) : "none";
        return new Priced(
            ids,
            value.GetValueOrDefault("harm") switch { double d => d, long l => l, _ => null },
            floor?.GetValueOrDefault("assessed") is true,
            (value.GetValueOrDefault("unknown") as List<object?> ?? []).OfType<string>().ToList(),
            range);
    }
}
