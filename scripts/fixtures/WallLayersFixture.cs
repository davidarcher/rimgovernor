using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native
{
    // The wall-layer probe (#4043, epic #2795): two staged, roofed, doored rooms
    // on a pinned cold map, one single-walled and one with a second wall layer
    // two cells out. The op sets both rooms to the same temperature and reads
    // the tracker's own wall and roof equalization deltas per interval, so the
    // climate model's leakage terms rest on the game's arithmetic, not the wiki.
    public sealed class WallLayersFixture
    {
        private const int Samples = 5;

        [Tool("test/wall_layers", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#4043): set the rooms holding two cells to `target` C and report each room's cell count, equalize cells and the mean of RoomTempTracker's private wall, thin-roof and no-roof per-interval deltas over five calls, with the tracker's constants. Test builds only.")]
        public async Task<object> Read(INativeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "An interior cell (absolute x) of the single-walled room.")] int singleX,
            [ToolParameter(Description = "An interior cell (absolute z) of the single-walled room.")] int singleZ,
            [ToolParameter(Description = "An interior cell (absolute x) of the double-walled room.")] int doubleX,
            [ToolParameter(Description = "An interior cell (absolute z) of the double-walled room.")] int doubleZ,
            [ToolParameter(Description = "The temperature both rooms are set to before the read.")] float target = 21f)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap ?? throw new System.InvalidOperationException("A loaded map is required.");
                if (float.IsNaN(AcceptanceWorld.Lab)) throw new System.InvalidOperationException("A lab start (pinned outdoor temperature) is required.");
                var wall = Method("WallEqualizationTempChangePerInterval");
                var thinRoof = Method("ThinRoofEqualizationTempChangePerInterval");
                var noRoof = Method("NoRoofEqualizationTempChangePerInterval");
                return FixtureKit.Reply(new Fx.WallLayersRead
                {
                    OutdoorC = map.mapTemperature.OutdoorTemp,
                    TargetC = target,
                    WallEqualizeFactor = RoomTempTracker.WallEqualizeFactor,
                    IntervalTicks = TemperatureTuning.RoomTempEqualizeInterval,
                    SingleWalled = ReadRoom(map, new IntVec3(singleX, 0, singleZ), target, wall, thinRoof, noRoof),
                    DoubleWalled = ReadRoom(map, new IntVec3(doubleX, 0, doubleZ), target, wall, thinRoof, noRoof),
                });
            }, cancellationToken);

        private static MethodInfo Method(string name) =>
            typeof(RoomTempTracker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.InvalidOperationException($"RoomTempTracker.{name} is not a method in this game version.");

        private static Fx.WallLayersRoom ReadRoom(Map map, IntVec3 cell, float target, MethodInfo wall, MethodInfo thinRoof, MethodInfo noRoof)
        {
            var room = cell.GetRoom(map) ?? throw new System.InvalidOperationException($"No room at {cell.x},{cell.z}.");
            if (room.UsesOutdoorTemperature || room.TouchesMapEdge)
                throw new System.InvalidOperationException($"The room at {cell.x},{cell.z} is outdoors (cells={room.CellCount}, openRoof={room.OpenRoofCount}).");
            var tracker = room.TempTracker;
            tracker.RoomChanged();
            room.Temperature = target;
            return new Fx.WallLayersRoom
            {
                Cells = room.CellCount,
                OpenRoofCells = room.OpenRoofCount,
                EqualizeCells = tracker.EqualizeCellsForReading.Count,
                WallDeltaPerInterval = Mean(tracker, wall),
                ThinRoofDeltaPerInterval = Mean(tracker, thinRoof),
                NoRoofDeltaPerInterval = Mean(tracker, noRoof),
            };
        }

        // The wall sampler cycles a fifth of the equalize cells per call; five calls visit them all.
        private static double Mean(RoomTempTracker tracker, MethodInfo delta) =>
            Enumerable.Range(0, Samples).Select(_ => delta.Invoke(tracker, null) is float d ? (double)d : throw new System.InvalidOperationException($"RoomTempTracker.{delta.Name} did not return a float.")).Average();
    }
}
