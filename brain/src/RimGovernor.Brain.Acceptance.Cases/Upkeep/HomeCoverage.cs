using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Policy;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/home-coverage</c>: test/sleeping_setup(connectedRooms) leaves two bed chambers and the corridor joining them
/// outside Home with the game's auto-home off. Under the served home-coverage family the native Home mask grows over the
/// corridor and the base margin, the first cell past the margin stays outside, and a corridor cell taken out of Home is
/// restored. The fixture stays bespoke: the stage primitives cannot claim beds or sleeping spots for colonists.
/// </summary>
internal static class UpkeepHomeCoverage
{
    private const string Setup = "test/sleeping_setup";

    private static readonly FixtureOp<NoArgs, Fx.SleepingPrepared> Prepared = new(
        Setup,
        _ => ServedOps.Args(("connectedRooms", true)),
        (label, reply) => ServedOps.Reply<Fx.SleepingPrepared>(Setup)(label, reply) is { Success: true } prepared
            ? prepared
            : throw new AcceptanceException($"{label}: {Setup} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "upkeep/home-coverage",
        Scope = "Connected autonomous Home (#452): served Home maintenance covers a ready corridor and the base margin, leaves "
            + "the first cell beyond the margin outside Home, and restores a corridor cell removed from Home. Only the native "
            + "Home mask under the live service proves the write lands and the margin holds.",
        Ticks = 10000,
        Tier = CaseTier.OnDemand,
        Ops = [Setup, FixtureStage.Tool],
        Keep = ["Rest"],
        Serve = new ServeSpec { Families = ["home-coverage", "work"], Prefix = "upkeep-home" },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("sleeping-setup", Prepared, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(prepared));
        var cx = prepared.Corridor.X;
        var cz = prepared.Corridor.Z;

        // The fixture point lies one cell past the east wall, inside the Home margin; the first cell the policy
        // excludes is a full margin farther east.
        var margin = new Cell(prepared.Outside.X, prepared.Outside.Z);
        var beyond = new Cell(margin.X + PolicyFunctions.HomeAreaMargin, margin.Z);
        Cell[] corridor = [new(cx, cz), new(cx + 1, cz), new(cx + 2, cz)];
        var removed = corridor[1];
        s.Record("beyond", beyond.ToString());

        var grid = await ReadHomeAsync(s, "home-before", corridor, margin, beyond, cancellationToken).ConfigureAwait(false);
        if (grid.At(margin)?.InHome != false || grid.At(beyond)?.InHome != false)
        {
            throw new AcceptanceException($"home-before: margin {margin} or beyond {beyond} already Home; the fixture left nothing to cover");
        }

        var restoring = false;
        _ = await s.ServeUntilAsync("home-coverage", s.TicksLeft, async ct =>
        {
            var grid = await ReadHomeAsync(s, restoring ? "home-restore" : "home-cover", corridor, margin, beyond, ct).ConfigureAwait(false);
            if (grid.At(beyond)?.InHome != false)
            {
                throw new AcceptanceException($"cell {beyond} beyond the Home margin became Home");
            }

            var covered = corridor.Append(margin).All(c => grid.At(c)?.InHome == true);
            var signature = string.Create(CultureInfo.InvariantCulture, $"restoring={restoring} {string.Concat(corridor.Append(margin).Select(c => grid.At(c)?.InHome == true ? '1' : '0'))}");
            if (!covered)
            {
                return (signature, false);
            }

            if (restoring)
            {
                return (signature, true);
            }

            s.Record("covered", signature);
            _ = await s.StageAsync("home-remove", new Fx.StageSpec
            {
                Cells = [new Fx.StageCells { Rect = new Fx.StageRect(removed.X, removed.Z, 1, 1), Home = false }],
            }, ct).ConfigureAwait(false);
            restoring = true;
            return (signature, false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cells read over the corridor, the margin cell and the cell beyond it.</summary>
    private static async Task<MapCellGrid> ReadHomeAsync(ITypedSession s, string label, IReadOnlyList<Cell> corridor, Cell margin, Cell beyond, CancellationToken cancellationToken)
    {
        var cells = corridor.Append(margin).Append(beyond).ToList();
        var reply = await s.CallAsync(label, NativeOps.GetCells, new Observations.GetCellsRequest
        {
            Scope = s.Scope,
            Rectangle = new Observations.Rectangle
            {
                Minimum = new Common.Cell { X = cells.Min(c => c.X), Z = cells.Min(c => c.Z) },
                Maximum = new Common.Cell { X = cells.Max(c => c.X), Z = cells.Max(c => c.Z) },
            },
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = TypedReads.Observed(label, NativeOps.GetCells, reply, reply.Outcome?.Observed);
        return MapCellGrid.Of(snapshot.Grid);
    }
}
