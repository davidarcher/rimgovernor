#nullable enable
using System.Collections.Generic;
using Verse;

namespace RimGovernor.Runtime
{
    // The ThingDefs the training range role scores (Defs/ThingDefs/TrainingRange.xml).
    public static class TrainingRangeDefs
    {
        public const string Stand = "RimGovernor_TrainingBowStand";
        public const string Dummy = "RimGovernor_TrainingDummy";
    }

    // Scores a room by its lanes: a lane is a training stand; a dummy only counts
    // in a room that has a stand. Scale: 100 per stand, 20 per dummy, so one
    // working lane (120) outscores a workshop bench (27) or a laboratory bench
    // (60), while any bed-driven role still wins (a bed scores 100100).
    public sealed class RoomRoleWorker_TrainingRange : RoomRoleWorker
    {
        public const float StandScore = 100f;
        public const float DummyScore = 20f;

        public static float Score(int stands, int dummies) =>
            stands == 0 ? 0f : StandScore * stands + DummyScore * dummies;

        public override float GetScore(Room room)
        {
            Count(room, out var stands, out var dummies);
            return Score(stands, dummies);
        }

        public override float GetScoreDeltaIfBuildingPlaced(Room room, ThingDef buildingDef)
        {
            Count(room, out var stands, out var dummies);
            var before = Score(stands, dummies);
            if (buildingDef.defName == TrainingRangeDefs.Stand) stands++;
            else if (buildingDef.defName == TrainingRangeDefs.Dummy) dummies++;
            else return 0f;
            return Score(stands, dummies) - before;
        }

        private static void Count(Room room, out int stands, out int dummies)
        {
            stands = 0;
            dummies = 0;
            List<Thing> things = room.ContainedAndAdjacentThings;
            for (var i = 0; i < things.Count; i++)
            {
                var name = things[i].def.defName;
                if (name == TrainingRangeDefs.Stand) stands++;
                else if (name == TrainingRangeDefs.Dummy) dummies++;
            }
        }
    }
}
