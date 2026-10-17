using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/solo-cold</c> (Go <c>shelter/solo.go</c>): epic #2745's colony of one. The capability logic is proven by the
/// policy snapshot tests; this case keeps what needs a real world: a one-colonist sea-ice start whose recorded plan
/// latches Cold, holds the shelter, plans no laboratory, and whose shelter campfire the native accepts on the planned interior.
/// </summary>
internal static class ShelterSolo
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "shelter/solo-cold",
        Tier = CaseTier.Nightly,
        Scope = "Issue #2762: from a one-colonist sea-ice start the plan latches Cold, plans the shelter and no laboratory " +
            "(research is the shelter's capability), and the shelter's campfire stands on its planned interior. " +
            "A snapshot test covers the capability logic; only a real cold world reads the temperature curve and accepts the placement.",
        Start = new FixtureStart
        {
            Op = DebugGame.QuietWorldTool,
            On = new ScenarioStart
            {
                Spec = new ScenarioSpec
                {
                    Scenario = DebugGame.DebugScenario,
                    Count = 1,
                    Seed = "shelter-solo-2762",
                    Difficulty = DebugGame.DebugDifficulty,
                    Biome = "SeaIce",
                    WorldTemperature = "VeryCold",
                    Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05 },
                    SaveName = "shelter-solo-2762",
                },
            },
        },
        Serve = new ServeSpec { Families = ShelterClimate.ClimateFamilies, NativeTimeout = TimeSpan.FromSeconds(30), Prefix = "shelter-solo" },
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 1 },
        Reason = "a solo colony is the claim: one colonist, one serve window to the first recorded plan and campfire placement",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        try
        {
            ShelterStage.PlanRecord record;
            try
            {
                record = await ShelterStage.WaitPlanAsync(
                    s,
                    service,
                    ShelterStage.PlanWait,
                    p => ShelterStage.RoomsOf(p, PolicyFunctions.PlannedShelter).Count == 1,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("wait for the first layout plan: " + e.Message, e);
            }

            var plan = record.Plan;
            var shelter = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedShelter)[0];
            var bounds = shelter.Region.Bounds();
            report["plan"] = new JsonObject
            {
                ["tick"] = record.Tick,
                ["cold"] = plan.Cold,
                ["shelter"] = ShelterStage.RectJson(bounds),
                ["labs"] = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedLab).Count,
            };
            if (!plan.Cold)
            {
                throw new AcceptanceException("the plan did not latch Cold on an ice sheet");
            }

            var labs = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedLab).Count;
            if (labs != 0)
            {
                throw new AcceptanceException($"a solo colony's plan holds {labs} laboratories; the shelter hosts research");
            }

            using var journal = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            var seen = new List<Cell>();
            Cell cell;
            try
            {
                cell = await ShelterClimate.WaitBuildingAsync(journal, PolicyConstants.ShelterHeatDef, c => ShelterStage.Inside(bounds, c), seen, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                report["placements"] = ShelterStage.CellsJson(seen);
                throw new AcceptanceException($"{e.Message} (shelter {ShelterStage.Describe(bounds)}, placements [{string.Join(' ', seen.Select(ShelterStage.Describe))}])", e);
            }

            report["placements"] = ShelterStage.CellsJson(seen);
            report["placement"] = new JsonObject { ["definition"] = PolicyConstants.ShelterHeatDef, ["cell"] = CaseSupport.Cell(cell.X, cell.Z) };
            var refused = ServiceLogs.RefusalsNameBlockers(service.LogPath);
            report["placement_refused"] = new JsonArray([.. refused.Select(r => (JsonNode?)JsonValue.Create(r))]);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }
}
