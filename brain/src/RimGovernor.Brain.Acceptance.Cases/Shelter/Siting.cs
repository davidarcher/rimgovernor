using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/siting</c> (Go <c>shelter/siting.go</c>): the first real-game read of the shelter room the layout plan
/// carries at the start. The plan alone is the assertion, so the run stops after the first recorded plan.
/// </summary>
internal static class ShelterSiting
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "shelter/siting",
        Scope = "Issue #2048: from the tribal " + Baselines.BaselineName + " baseline the first recorded layout plan holds exactly one " +
            "shelter room, its floor overlaps no other room's, and its interior holds at least ShelterInteriorArea cells for the " +
            "colony's colonists with the latched climate's campfire and cooler slots (cold: two campfires, hot: a passive cooler). " +
            "The siting gap to the nearest other room is reported, not asserted: the planner falls back to a smaller gap when " +
            "the preferred one fits nowhere.",
        Start = new SaveStart { Name = Baselines.BaselineName },
        Serve = new ServeSpec { Families = ["shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(30), Prefix = "shelter-siting" },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Reason = "one short watch until the first review records a layout plan",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var listed = await Colonists.ListColonistsAsync(s.Harness, "colonists", false, false, cancellationToken).ConfigureAwait(false);
        var colonists = listed.Count;
        if (colonists == 0)
        {
            throw new AcceptanceException("the baseline has no colonists");
        }

        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        try
        {
            ShelterStage.PlanRecord record;
            try
            {
                record = await ShelterStage.WaitPlanAsync(s, service, TimeSpan.FromMinutes(3), _ => true, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("wait for the first layout plan: " + e.Message, e);
            }

            var plan = record.Plan;
            var shelters = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedShelter);
            report["plan"] = new JsonObject
            {
                ["tick"] = record.Tick,
                ["cold"] = plan.Cold,
                ["hot"] = plan.Hot,
                ["colonists"] = colonists,
                ["shelters"] = shelters.Count,
            };
            if (shelters.Count != 1)
            {
                throw new AcceptanceException($"the plan holds {shelters.Count} shelter rooms, want 1");
            }

            var shelter = shelters[0];
            var inner = shelter.Region.Bounds();
            var campfires = PolicyFunctions.ShelterCampfires(plan.Cold);
            var coolers = PolicyFunctions.ShelterCoolers(plan.Hot);
            var want = PolicyFunctions.ShelterInteriorArea(colonists, campfires, coolers);
            var got = inner.Width * inner.Height;
            if (got < want)
            {
                throw new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"shelter interior {inner.Width}x{inner.Height} holds {got} cells, {want} needed for {colonists} colonists, {campfires} campfires, {coolers} coolers"));
            }

            var gap = -1;
            foreach (var r in plan.Rooms)
            {
                if (r.Role == PolicyFunctions.PlannedShelter)
                {
                    continue;
                }

                var o = r.Region.Bounds();
                var dx = Math.Max(o.X - (inner.X + inner.Width - 1), inner.X - (o.X + o.Width - 1)) - 1;
                var dz = Math.Max(o.Z - (inner.Z + inner.Height - 1), inner.Z - (o.Z + o.Height - 1)) - 1;
                var d = Math.Max(dx, dz);
                if (d < 0)
                {
                    throw new AcceptanceException($"shelter interior {ShelterStage.Describe(inner)} overlaps {Wire.Name(r.Role)} room {ShelterStage.Describe(o)}");
                }

                if (gap < 0 || d < gap)
                {
                    gap = d;
                }
            }

            report["shelter"] = new JsonObject
            {
                ["interior"] = ShelterStage.RectJson(inner),
                ["door"] = CaseSupport.Cell(shelter.Door.X, shelter.Door.Z),
                ["campfire_slots"] = campfires,
                ["cooler_slots"] = coolers,
                ["gap_to_nearest_room"] = gap,
            };
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }
}
