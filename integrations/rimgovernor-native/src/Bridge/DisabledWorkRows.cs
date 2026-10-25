using System;
using System.Collections.Generic;

namespace RimGovernor.Native
{
    // Which requested work rows a work settings write skips (#3678). A row asking a nonzero priority for a work type the
    // pawn can no longer do is skipped: the game refuses SetPriority on a disabled type and zeroes the type itself when it
    // becomes disabled (Pawn_WorkSettings.Notify_DisabledWorkTypesChanged), so the row has nothing to write and the rest
    // of the request still applies. A zero is always written. Whether a type is disabled is the game's
    // (Pawn.WorkTypeIsDisabled), passed in so the rule is independent of the game assemblies.
    internal static class DisabledWorkRows
    {
        internal static bool Skipped(RimGovernor.Brain.GameOrders.Operations.WorkPriority row, Func<string, bool> disabled) =>
            row.Priority != 0 && disabled(row.WorkTypeDef ?? "");

        // The skipped rows' work type names, in request order.
        internal static List<string> SkippedNames(IEnumerable<RimGovernor.Brain.GameOrders.Operations.WorkPriority> rows, Func<string, bool> disabled)
        {
            var names = new List<string>();
            foreach (var row in rows)
                if (Skipped(row, disabled)) names.Add(row.WorkTypeDef ?? "");
            return names;
        }
    }
}
