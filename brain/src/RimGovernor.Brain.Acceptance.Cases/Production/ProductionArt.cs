using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/art</c> (Go <c>art.go</c>) runs the art family end to end on the lab. test/art builds the artist's
/// furnished bedroom A below its build tier's impressiveness target (ImpressivenessLevels.Baseline) with beauty its weakest
/// stat, and an ascetic neighbour's plain room B. Jade, a sculpting table, a little silver and no medicine (a purchase need,
/// so art for sale is wanted) complete it.
/// Phase 1 (sleeping, art, trade): MaintainArt pins a sculpture bill to the artist, the sleeping upkeep installs the packed
/// piece in room A until it reaches its target, and art for sale leaves surplus packed art. Phase 2: an exotic caravan
/// arrives and TradeWithCaravan sells the surplus sculpture through SelectTrade's art step, matched by packed item id.
/// Why not a snapshot test: the sculpture is vanilla production (a packed MinifiedThing with a quality roll), the install is
/// a native relocate of that packed item, room impressiveness is vanilla room stats, and the sale matches
/// Tradeable.FirstThingColony on a live trade sheet to the packed item id ReadPackedItems reported.
/// </summary>
/// <remarks>
/// Adaptation: the room's tier target needs the Impressiveness stage scores of the game's definition catalog, which is
/// game-side now; it comes from <see cref="GameCatalogReader.ImpressivenessLevelsAsync"/>.
/// </remarks>
internal static class ProductionArt
{
    private const string Op = "test/art";

    internal static AcceptanceCase Case() => new()
    {
        Name = "production/art",
        Scope = "An artist sculpts and the piece is installed until their bedroom reaches its tier target; with an exotic caravan present the surplus packed sculpture sells (#1195). Native: vanilla sculpture production and room stats, the packed install, and the trade row's FirstThingColony matching the packed item id.",
        Start = new FixtureStart { Op = Op, Args = new JsonObject { ["action"] = "prepare" }, On = new LabStart { Colonists = 2 } },
        RequiredOps = [Op],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["sleeping", "art", "trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "art" },
        Budget = TimeSpan.FromMinutes(45),
        Crew = new Crew { Size = 3 },
        Reason = "two phases on one colony: a sculpture, its install and a sale piece take two in-game days before the caravan phase can sell the surplus",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        report["fixture"] = prepared;
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture: {prepared.ToJson()}");
        }

        var (a, _) = Rooms(prepared);
        var levels = await GameCatalogReader.ImpressivenessLevelsAsync(s.Harness, s.Identity, cancellationToken).ConfigureAwait(false);
        var target = Target(prepared, levels);
        report["target"] = target;
        if (target <= 0 || a["impressiveness"].AsNumber() >= target)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"room A is not below its target {target}: {prepared["rooms"].ToJson()}"));
        }

        var observed = ObservedSession.Of(s);

        // Phase 1: sculpt, install, and sculpt for sale. Three in-game days: a large sculpture (30000 work), its install and a sale piece.
        _ = await SustainedFood.ObserveAsync(
            observed,
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(25),
                    Window = 180000,
                    Concern = PolicyFunctions.MaintainArt.Value,
                    Extra = [PolicyFunctions.TradeWithCaravan.Value, PolicyFunctions.MaintainTrade.Value],
                },
                Audit = async (h, rep, ct) =>
                {
                    var after = await h.CallAsync("audit-install", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
                    rep["installed"] = after;
                    InstallVerdict(after, target);
                },
            },
            cancellationToken).ConfigureAwait(false);

        // Phase 2: the caravan buys the surplus.
        var surplus = new List<string>();
        _ = await SustainedFood.ObserveAsync(
            observed,
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(10),
                    Window = 30000,
                    Concern = PolicyFunctions.TradeWithCaravan.Value,
                    Extra = [PolicyFunctions.MaintainArt.Value, PolicyFunctions.MaintainTrade.Value],
                },
                Prepare = async (h, rep, ct) =>
                {
                    var before = await h.CallAsync("audit-before-trade", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
                    rep["before_trade"] = before;
                    surplus.AddRange(PackedIds(before));
                    var trader = await h.CallAsync("trader", Op, new JsonObject { ["action"] = "trader" }, ct).ConfigureAwait(false);
                    rep["trader"] = trader;
                    if (trader["success"].AsBool() != true)
                    {
                        throw new AcceptanceException($"trader: {trader.ToJson()}");
                    }

                    if (trader["buyable"].AsSlice().Count == 0)
                    {
                        throw new AcceptanceException($"the caravan buys none of the packed sculptures [{string.Join(' ', surplus)}]: {trader.ToJson()}");
                    }
                },
                Audit = async (h, rep, ct) =>
                {
                    var after = await h.CallAsync("audit-after-trade", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
                    rep["after_trade"] = after;
                    SaleVerdict(rep["before_trade"], after, surplus);
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Splits a fixture read's rooms into the artist's (A) and the neighbour's (B).</summary>
    private static (JsonObject A, JsonObject B) Rooms(JsonObject read)
    {
        JsonObject? a = null;
        JsonObject? b = null;
        foreach (var row in read["rooms"].AsSlice())
        {
            var room = row.AsMap() ?? [];
            if (room["artist"].AsBool() == true)
            {
                a = room;
            }
            else
            {
                b = room;
            }
        }

        return a is null || b is null ? throw new AcceptanceException($"fixture rooms unreadable: {read["rooms"].ToJson()}") : (a, b);
    }

    /// <summary>Room A's impressiveness target: the tier baseline from the fixture's faction tech level and finished research.</summary>
    private static double Target(JsonObject prepared, ImpressivenessLevels levels)
    {
        var finished = new List<ResearchProjectId>();
        foreach (var r in prepared["research"].AsSlice())
        {
            finished.Add(new ResearchProjectId(r.AsString()));
        }

        var tier = PolicyFunctions.SelectTechTier(Fact.Known(new EquatableArray<ResearchProjectId>([.. finished])), Fact.Known(prepared["techLevel"].AsString()));
        return tier.TryGetValue(out var known) ? levels.Baseline(known) : levels.Baseline(default);
    }

    /// <summary>The packed sculptures a fixture audit lists.</summary>
    private static List<string> PackedIds(JsonObject audit) =>
        [.. audit["packed"].AsSlice().Select(row => (row.AsMap() ?? [])["id"].AsString())];

    /// <summary>A sculpture stands in room A, room A reached its target, and surplus packed art is left to sell.</summary>
    private static void InstallVerdict(JsonObject audit, double target)
    {
        var (a, _) = Rooms(audit);
        var failures = new List<string>();
        if (a["sculptures"].AsSlice().Count == 0)
        {
            failures.Add("no sculpture installed in the artist's room");
        }

        if (a["impressiveness"].AsNumber() < target)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"room A impressiveness {a["impressiveness"].ToJson()} below its target {target}"));
        }

        if (PackedIds(audit).Count == 0)
        {
            failures.Add("no surplus packed sculpture to sell");
        }

        if (failures.Count > 0)
        {
            throw new AcceptanceException($"[{string.Join(", ", failures)}] (audit {audit.ToJson()})");
        }
    }

    /// <summary>A surplus sculpture left the colony for the caravan and the colony's silver rose.</summary>
    private static void SaleVerdict(JsonNode? before, JsonObject after, IReadOnlyList<string> surplus)
    {
        var prior = before.AsMap() ?? [];
        var left = new HashSet<string>(PackedIds(after), StringComparer.Ordinal);
        var sold = surplus.Count(id => !left.Contains(id));
        var failures = new List<string>();
        if (sold == 0)
        {
            failures.Add($"none of the surplus sculptures [{string.Join(' ', surplus)}] sold");
        }

        // The caravan may have left by the audit; its stock is checked only while it stays.
        if (after["traders"].AsNumber() > 0 && after["traderArt"].AsSlice().Count == 0)
        {
            failures.Add("the caravan holds no sculpture");
        }

        if (after["colonySilver"].AsNumber() <= prior["colonySilver"].AsNumber())
        {
            failures.Add($"colony silver did not rise: {prior["colonySilver"].ToJson()} -> {after["colonySilver"].ToJson()}");
        }

        if (failures.Count > 0)
        {
            throw new AcceptanceException($"[{string.Join(", ", failures)}] (audit {after.ToJson()})");
        }
    }
}
