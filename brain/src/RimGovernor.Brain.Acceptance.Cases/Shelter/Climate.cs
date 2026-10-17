using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The shelter climate cases (Go <c>shelter/climate.go</c>) prove the shelter's campfires and cooler, and a mild map's
/// cooking campfire, follow the real map's temperature curve and the plan: the plan latches Cold or Hot from the live gear
/// snapshot, and the building the planner then places stands on the planned interior of the room that owns it, its ring
/// raised with it rather than before it. A snapshot test over recorded facts proves the placement search; only a real map
/// reads the curve, and only the native accepts the placement.
/// </summary>
internal static class ShelterClimate
{
    internal static readonly string[] ClimateFamilies =["shelter", "sleeping", "cooking", "temperature"];

    private static readonly TimeSpan ClimateWait = TimeSpan.FromMinutes(15);

    /// <summary>The shelter template's bench slot definition.</summary>
    internal const string CraftingSpot = "CraftingSpot";

    private sealed record Climate(string Name, string Scope, string[] Families, Start Start, bool Cold, bool Hot, string Build, PlannedRole Room);

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        Climate[] climates =
        [
            new(
                "shelter/climate-mild",
                "the shelter template holds no campfire slot and the cooking campfire is placed on the planned kitchen's interior",
                ClimateFamilies,
                new SaveStart { Name = Baselines.BaselineName },
                false,
                false,
                "Campfire",
                PolicyFunctions.PlannedKitchen),
            new(
                "shelter/climate-cold",
                "the plan latches Cold, the shelter template holds two campfire slots and the cooking campfire is placed on the planned shelter's interior, any crafting spot with it",
                [.. ClimateFamilies, "armory"],
                QuietStart(new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05, Biomes = "Tundra,BorealForest,ColdBog,SeaIce", Seed = "shelter-cold-2076" }),
                true,
                false,
                "Campfire",
                PolicyFunctions.PlannedShelter),
            new(
                "shelter/climate-hot",
                "the plan latches Hot, the shelter template holds a cooler slot and the temperature planner's passive cooler is placed on the planned shelter's interior",

                // A desert tile at a high latitude is neither hot nor warm all year; a very hot planet puts the whole small patch past HotEnter.
                ClimateFamilies,
                new FixtureStart
                {
                    Op = DebugGame.QuietWorldTool,
                    On = new ScenarioStart
                    {
                        Spec = new ScenarioSpec
                        {
                            Scenario = DebugGame.DebugScenario,
                            Count = DebugGame.DebugColonists,
                            Seed = "shelter-hot-2076",
                            Difficulty = DebugGame.DebugDifficulty,
                            Biome = "ExtremeDesert,Desert",
                            WorldTemperature = "VeryHot",
                            Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05 },
                            SaveName = "shelter-hot-2076",
                        },
                    },
                },
                false,
                true,
                "PassiveCooler",
                PolicyFunctions.PlannedShelter),
        ];
        foreach (var c in climates)
        {
            yield return new AcceptanceCase
            {
                Name = c.Name,
                Tier = CaseTier.Nightly,
                Scope = "Issues #2076 and #2278: from a " + c.Name["shelter/climate-".Length..] + " map, " + c.Scope + "; a refused furniture cell reports its blocker.",
                Start = c.Start,
                Serve = new ServeSpec { Families = c.Families, NativeTimeout = TimeSpan.FromSeconds(30), Prefix = "shelter-climate" },
                Budget = TimeSpan.FromMinutes(30),
                Crew = new Crew { Size = 3 },
                Reason = "one short serve window per climate: the first recorded plan and the first placement of the climate's building",
                Run = (s, ct) => RunAsync(s, c, ct),
            };
        }
    }

    /// <summary>A pinned fresh world with the quiet-world marker, so no wild spawner interrupts the first minutes.</summary>
    private static FixtureStart QuietStart(DebugStartSize size) => new() { Op = DebugGame.QuietWorldTool, On = new DebugStart { Size = size } };

    private static async Task RunAsync(ISession s, Climate c, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await ClimateAsync(s, service, report, c.Cold, c.Hot, c.Build, c.Room, c.Cold, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The shared climate audit (Go <c>runClimate</c>): the first plan with one shelter and a room of <paramref name="room"/>,
    /// its Cold and Hot latches, its campfire slots, and the first placement of <paramref name="build"/> on the owner's interior.
    /// </summary>
    internal static async Task ClimateAsync(
        ISession s,
        ServiceProcess service,
        Report report,
        bool cold,
        bool hot,
        string build,
        PlannedRole room,
        bool checkCrafting,
        CancellationToken cancellationToken)
    {
        var record = await WaitFirstPlanAsync(s, service, room, cancellationToken).ConfigureAwait(false);
        var plan = record.Plan;
        var shelter = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedShelter)[0];
        var owner = ShelterStage.RoomsOf(plan, room)[0];
        report["plan"] = new JsonObject
        {
            ["tick"] = record.Tick,
            ["cold"] = plan.Cold,
            ["hot"] = plan.Hot,
            ["shelter"] = ShelterStage.RectJson(shelter.Region.Bounds()),
            ["owner"] = ShelterStage.RectJson(owner.Region.Bounds()),
            ["campfire_slots"] = PolicyFunctions.ShelterCampfires(plan.Cold),
            ["cooler_slots"] = PolicyFunctions.ShelterCoolers(plan.Hot),
        };

        // A desert's winter can dip below freezing, so the hot map need not be warm all year: it must latch Hot, and its Cold latch is not asserted.
        if ((plan.Cold != cold && !hot) || plan.Hot != hot)
        {
            throw new AcceptanceException($"the plan latched cold={Lower(plan.Cold)} hot={Lower(plan.Hot)} on this map, want cold={Lower(cold)} hot={Lower(hot)}");
        }

        var got = PolicyFunctions.ShelterCampfires(plan.Cold);
        var want = plan.Cold ? 2 : 0;
        if (got != want)
        {
            throw new AcceptanceException($"the shelter template plans {got} campfire slots, want {want}");
        }

        // The temperature building waits for no standing shelter and the shelter's slots are keyed on the planned interior: no
        // stage is needed, and a placement outside the room that owns it fails the case.
        using var journal = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
        var ownerBounds = owner.Region.Bounds();
        List<Cell> seen = [];
        Cell cell;
        try
        {
            cell = await WaitBuildingAsync(journal, build, c => ShelterStage.Inside(ownerBounds, c), seen, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            report["placements"] = ShelterStage.CellsJson(seen);
            var core = plan.CoreBounds();
            var coreText = core is { } r ? ShelterStage.Describe(r) : "none";
            throw new AcceptanceException(
                $"{e.Message} ({Wire.Name(room)} {ShelterStage.Describe(ownerBounds)}, core {coreText}, placements [{string.Join(' ', seen.Select(ShelterStage.Describe))}])",
                e);
        }

        report["placements"] = ShelterStage.CellsJson(seen);
        report["placement"] = new JsonObject { ["definition"] = build, ["cell"] = CaseSupport.Cell(cell.X, cell.Z) };
        if (checkCrafting)
        {
            // The crafting spot, when the armory family asked for one, takes the shelter's slot too.
            var spots = Placements(journal, CraftingSpot, cancellationToken);
            report["crafting_spots"] = ShelterStage.CellsJson(spots);
            var shelterBounds = shelter.Region.Bounds();
            foreach (var spot in spots)
            {
                if (!ShelterStage.Inside(shelterBounds, spot))
                {
                    throw new AcceptanceException($"{CraftingSpot} placed at {ShelterStage.Describe(spot)} outside the planned shelter {ShelterStage.Describe(shelterBounds)}");
                }
            }
        }

        var refused = ServiceLogs.RefusalsNameBlockers(service.LogPath);
        report["placement_refused"] = new JsonArray([.. refused.Select(r => (JsonNode?)JsonValue.Create(r))]);
    }

    /// <summary>The first recorded plan with one shelter and a room of the owner role.</summary>
    internal static async Task<ShelterStage.PlanRecord> WaitFirstPlanAsync(ISession s, ServiceProcess service, PlannedRole owner, CancellationToken cancellationToken)
    {
        try
        {
            return await ShelterStage.WaitPlanAsync(
                s,
                service,
                ShelterStage.PlanWait,
                p => ShelterStage.RoomsOf(p, PolicyFunctions.PlannedShelter).Count == 1 && ShelterStage.RoomsOf(p, owner).Count > 0,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("wait for the first layout plan: " + e.Message, e);
        }
    }

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>The cells every journaled plan places <paramref name="definition"/> on.</summary>
    internal static List<Cell> Placements(Journal journal, string definition, CancellationToken cancellationToken)
    {
        var plans = journal.PlanHistoryWithMethods(256, ["*"], cancellationToken);
        var output = new List<Cell>();
        foreach (var plan in plans)
        {
            for (var i = 0; i < plan.Spec.Actions.Count; i++)
            {
                if (plan.Spec.Actions[i].Building is { } b && b.Definition == definition && !output.Contains(b.Cell))
                {
                    output.Add(b.Cell);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Waits for a plan that places <paramref name="definition"/> on a cell <paramref name="want"/> accepts and returns that
    /// cell and every cell of the definition the plans placed meanwhile.
    /// </summary>
    internal static async Task<Cell> WaitBuildingAsync(Journal journal, string definition, Func<Cell, bool> want, List<Cell> seen, CancellationToken cancellationToken)
    {
        Cell cell = default;
        Exception? readError = null;
        try
        {
            _ = await ServiceJournal.WaitReviewAsync(
                journal,
                new Wait { Ceiling = ClimateWait },
                _ =>
                {
                    IReadOnlyList<Store.PlanState> plans;
                    try
                    {
                        plans = journal.PlanHistoryWithMethods(256, ["*"], cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        readError = e;
                        return true;
                    }

                    foreach (var plan in plans)
                    {
                        for (var i = 0; i < plan.Spec.Actions.Count; i++)
                        {
                            if (plan.Spec.Actions[i].Building is not { } b || b.Definition != definition)
                            {
                                continue;
                            }

                            if (!seen.Contains(b.Cell))
                            {
                                seen.Add(b.Cell);
                            }

                            if (want(b.Cell))
                            {
                                cell = b.Cell;
                                return true;
                            }
                        }
                    }

                    return false;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no {definition} placed on its planned interior within the window: {e.Message}"), e);
        }

        if (readError is not null)
        {
            throw new AcceptanceException(readError.Message, readError);
        }

        return cell;
    }
}
