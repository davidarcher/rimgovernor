#nullable enable
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
    }
}
