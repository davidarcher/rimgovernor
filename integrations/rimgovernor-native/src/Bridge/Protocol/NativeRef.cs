using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimGovernor.Native
{
    // The one way a message points at a thing, pawn, building, zone, bill,
    // room or faction: its load id, nothing else. The reader takes
    // definition, label and position from the row the id resolves to.
    internal static class NativeRef
    {
        internal static RimGovernor.Brain.GameFacts.Common.Ref? Of(string? id) => string.IsNullOrEmpty(id) ? null : new RimGovernor.Brain.GameFacts.Common.Ref { Id = id };
        internal static RimGovernor.Brain.GameFacts.Common.Ref? Of(ILoadReferenceable? target) => target == null ? null : Of(target.GetUniqueLoadID());
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Common.Ref> All(IEnumerable<string> ids) => ids.Select(id => new RimGovernor.Brain.GameFacts.Common.Ref { Id = id });
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Common.Ref> All<T>(IEnumerable<T> targets) where T : ILoadReferenceable => targets.Select(t => new RimGovernor.Brain.GameFacts.Common.Ref { Id = t.GetUniqueLoadID() });
        // A room's id: rooms carry no load id, so the reference is the room's number.
        internal static RimGovernor.Brain.GameFacts.Common.Ref Room(Room room) => new RimGovernor.Brain.GameFacts.Common.Ref { Id = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        // The things a frame's sections reference while it is captured
        //: its things table. Null outside a capture. Main thread only.
        private static List<Thing>? referenced;

        // Thing points at a thing's row: during a frame capture a thing
        // other than a pawn (the pawn table holds those) joins the frame's
        // things table.
        internal static RimGovernor.Brain.GameFacts.Common.Ref Thing(Thing thing)
        {
            if (!(thing is Pawn)) referenced?.Add(thing);
            return new RimGovernor.Brain.GameFacts.Common.Ref { Id = thing.GetUniqueLoadID() };
        }

        // Collect runs read and returns every thing it referenced through
        // Thing: the frame's things table.
        internal static List<Thing> Collect(Action read)
        {
            var outer = referenced;
            var collected = new List<Thing>();
            referenced = collected;
            try { read(); }
            finally { referenced = outer; }
            return collected;
        }
    }
}
