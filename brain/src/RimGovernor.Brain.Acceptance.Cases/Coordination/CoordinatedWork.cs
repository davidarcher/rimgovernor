using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>coordination/*</c>: the native-led labor path (native-led-labor-execution task 6.1). The food cases stage a credible
/// food risk (no stock, recovery known too late) with one ripe wild potato no native setting harvests: the served brain's
/// comparison selects the harvest, the intervention gate justifies it, and it reaches Hands as an exact Harvest with its
/// purpose; native work and settings do the rest. The delegated one claims the harvested food and the released participant;
/// the carried-recovery one ends the harvest's job once, and that failed step ends its attempt: no later revision reissues
/// it (design decision 3, task 5.6). The construction case is one urgent build (#3537): on a colony with no bed the normal
/// rounds place the sleeping shelter and native builders finish its ring with no exact pawn order (no stall premise).
/// test/coordinated_work_prepare prepares the facts (no plan, order or task) and records what the native activities did,
/// which the primitives cannot express.
/// </summary>
internal static class CoordinatedWork
{
    private const string Prepare = "test/coordinated_work_prepare";
    private const string ReadTool = "test/coordinated_work_read";

    private static readonly FixtureOp<(string Mode, bool Interrupt), Fx.CoordinationPrepared> Prepared = new(
        Prepare,
        r => ServedOps.Args(("mode", r.Mode), ("interrupt", r.Interrupt)),
        (label, reply) => ServedOps.Reply<Fx.CoordinationPrepared>(Prepare)(label, reply) is { Success: true } prepared
            ? prepared
            : throw new AcceptanceException($"{label}: {Prepare} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<NoArgs, Fx.CoordinationRead> Read = ServedOps.Read<Fx.CoordinationRead>(ReadTool);

    internal static TypedCase Food(bool interrupted = false) => new()
    {
        Name = "coordination/food" + (interrupted ? "-carried-recovery" : "-delegated"),
        Scope = "A credible food risk (no stock, native recovery known too late) with one ripe wild potato no native setting "
            + "harvests: the served brain's comparison selects the harvest, the intervention gate justifies it and it reaches "
            + "Hands as an exact Harvest with its purpose, never player-forced"
            + (interrupted ? "; its harvest job is ended once and that failed step ends its attempt, never reissued, and the participant is released"
                : "; the native game harvests the food and the participant is released")
            + ". A unit test cannot prove the native harvest, its output or the release.",
        Ticks = 6000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coordination" },
        Run = (s, ct) => RunAsync(s, "food", interrupted, ct),
    };

    internal static TypedCase Construction() => new()
    {
        Name = "coordination/construction-delegated",
        Scope = "A bedless colony's urgent build (#3537): the normal rounds place the sleeping shelter and native builders "
            + "stand every wall of its ring with no exact pawn order, since no stall premise justifies one (native-led labor, "
            + "task 6.1). The selected shell reduces nonzero forecast hypothermia capability loss, with native comfort limits "
            + "compared to its exposure inputs. A unit test cannot prove native construction or actual completion.",
        Ticks = 6000,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec { World = new Fx.StageWorld { OutdoorTemperature = -10 } },
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coordination" },
        Run = (s, ct) => RunAsync(s, "construction", false, ct),
    };

    private static async Task RunAsync(TypedSession s, string mode, bool interrupted, CancellationToken cancellationToken)
    {
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? thermalSelection = null;
        var prepared = await s.FixtureAsync("coordination-prepare", Prepared, (mode, interrupted), cancellationToken).ConfigureAwait(false);
        if (mode == "construction")
        {
            s.Record("thermal-native", ServedOps.Json(prepared));
            // The staged site is the colony's one planned barracks, heated by its standing campfire: the normal rounds place its
            // shell and value its reduced hypothermia capability loss at -10 C (#3705). Its bed slot is what the sleeping step reconciles the
            // stand-in sleeping spots' barracks against.
            await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
            {
                Cold = true,
                NextRoomId = 2,
                Rooms =
                [
                    new PlannedRoom
                    {
                        Id = 1,
                        Role = PlannedRole.Barracks,
                        Region = RegionRules.RectRegion(new(prepared.X, prepared.Z, 3, 3)),
                        Door = new(prepared.DoorX, prepared.DoorZ),
                        DoorSide = new DoorSide.Doored(Rotation.South),
                    },
                ],
                Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
                {
                    [1] =
                    [
                        new InteriorPiece { Def = "Campfire", Rect = new Rectangle { X = prepared.X + 1, Z = prepared.Z + 1, Width = 1, Height = 1 } },
                        new InteriorPiece { Def = "Bed", Rect = new Rectangle { X = prepared.X, Z = prepared.Z + 1, Width = 1, Height = 2 }, Size = new Cell(1, 2), Rot = Rotation.North },
                    ],
                },
            }, cancellationToken).ConfigureAwait(false);
        }

        _ = await s.ServeUntilAsync("coordination", s.TicksLeft, async ct =>
        {
            if (mode == "construction")
                // The planned_unplaced comparison's compact companion: its coupled_selection vectors can pass the payload cap.
                thermalSelection ??= CoupledSelections.PlannedRoomsOf(s, before).FirstOrDefault(r => r.Verdict == "selected" && r.PlannedRooms.Contains(1));
            var read = await s.FixtureAsync("coordination-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("coordination", ServedOps.Json(read));
            if (read.Forced)
            {
                throw new AcceptanceException($"planner work became player-forced: {ServedOps.Json(read)}");
            }

            if (read.Recovered)
            {
                throw new AcceptanceException($"a failed harvest was reissued instead of ending its attempt: {ServedOps.Json(read)}");
            }

            if (mode == "construction")
            {
                // Ordinary construction has no intervention premise: an exact pawn order on it is the routine lane task 5.6 removed.
                if (read.Participants.Count > 0)
                    throw new AcceptanceException($"ordinary construction received an exact pawn order: {ServedOps.Json(read)}");
                return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick} ring={read.RingWalls}/{read.RingWallCells}"),
                    read.RingWallCells > 0 && read.RingWalls == read.RingWallCells);
            }

            // Food: the justified harvest reached Hands and its participant's ownership ended (a release order, or the native job
            // that took over the terminal order); the delegated one also claims the
            // completed harvest's food, the interrupted one the ended attempt.
            var chain = interrupted ? read.Interrupted : read.FinishedKinds.Contains("Work:Harvest") && read.PhysicalFood > 0;
            if (read.Purposes.Any(p => p != "FoodRisk"))
                throw new AcceptanceException($"the coordinated harvest carried a purpose other than its food risk: {ServedOps.Json(read)}");
            var done = chain && read.Purposes.Contains("FoodRisk") && read.Active == 0 && read.Participants.Count > 0
                && read.Released.Count == read.Participants.Count;
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick}"), done);
        }, cancellationToken).ConfigureAwait(false);
        if (mode == "construction") AssertThermal(s, prepared, thermalSelection);
    }

    private static void AssertThermal(TypedSession s, Fx.CoordinationPrepared prepared, CoupledSelectionRow? selection)
    {
        if (selection is null) throw new AcceptanceException("no selected planned shell thermal comparison was observed");
        s.Record("thermal-selection", string.Create(CultureInfo.InvariantCulture,
            $"sequence={selection.Sequence} baseline_capability_days={selection.Baseline.Thermal} selected_capability_days={selection.Selected.Thermal}"));
        s.Record("thermal-baseline-inputs", string.Join("; ", selection.BaselineExposure));
        s.Record("thermal-selected-inputs", string.Join("; ", selection.SelectedExposure));
        if (selection.Baseline.Thermal is not > 0 || selection.Selected.Thermal is not { } reduced || reduced < 0 || reduced >= selection.Baseline.Thermal)
            throw new AcceptanceException("selected shell must reduce known, nonzero baseline thermal capability loss");

        if (prepared.Comfort.Count == 0 || !prepared.Comfort.Any(p => prepared.OutdoorTemperature < p.SafeMin))
            throw new AcceptanceException("staged outdoors does not cross any native pawn's hypothermia-onset minimum");
        foreach (var pawn in prepared.Comfort)
        {
            var baseline = selection.BaselineExposure.SingleOrDefault(p => p.Pawn == pawn.Pawn);
            var selected = selection.SelectedExposure.SingleOrDefault(p => p.Pawn == pawn.Pawn);
            foreach (var exposure in new[] { baseline, selected })
            {
                if (exposure is null || exposure.AssessedTicks is not > 0 || exposure.UnknownTicks != 0
                    || exposure.InitialTemperature is null || exposure.FinalTemperature is null || exposure.ColdDegreeTicks is null
                    || exposure.InitialComfortableMin is not { } minimum || Math.Abs(minimum - pawn.ComfortableMin) > .001)
                    throw new AcceptanceException($"{pawn.Pawn}: missing exposure or forecast comfort minimum differs from native {pawn.ComfortableMin}");
            }
        }
    }
}
