#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // The one way a message points at a thing, pawn, building, zone, bill,
    // room or faction (#1342): its load id, nothing else. The reader takes
    // definition, label and position from the row the id resolves to.
    internal static class NativeRef
    {
        internal static Common.Ref? Of(string? id) => string.IsNullOrEmpty(id) ? null : new Common.Ref { Id = id };
        internal static Common.Ref? Of(ILoadReferenceable? target) => target == null ? null : Of(target.GetUniqueLoadID());
        internal static IEnumerable<Common.Ref> All(IEnumerable<string> ids) => ids.Select(id => new Common.Ref { Id = id });
        internal static IEnumerable<Common.Ref> All<T>(IEnumerable<T> targets) where T : ILoadReferenceable => targets.Select(t => new Common.Ref { Id = t.GetUniqueLoadID() });
        // A room's id: rooms carry no load id, so the reference is the room's number.
        internal static Common.Ref Room(Room room) => new Common.Ref { Id = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        // The things a frame's sections reference while it is captured
        // (#1342): its things table. Null outside a capture. Main thread only.
        private static List<Thing>? referenced;

        // Thing points at a thing's row: during a frame capture a thing
        // other than a pawn (the pawn table holds those) joins the frame's
        // things table.
        internal static Common.Ref Thing(Thing thing)
        {
            if (!(thing is Pawn)) referenced?.Add(thing);
            return new Common.Ref { Id = thing.GetUniqueLoadID() };
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
