#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // The def mirror (defs.proto: every def field filled by protobuf reflection
    // from the game's def objects) is not a wire contract of the record
    // contracts: the catalog reply no longer carries the def rows, the creation
    // def sets or the game constants.
    // TODO(#3100): defs. What stays is the class-chain table, the base classes of
    // each game class the catalog names, which the planner's def checks resolve
    // class relations against.
    internal static class DefMirrorFill
    {
        // Every class with its base classes, nearest first, up to but not
        // including System.Object, sorted by the class's full name.
        internal static EquatableArray<Obs.ClassChain> ClassChains(IEnumerable<Type> classes)
        {
            var chains = new List<Obs.ClassChain>();
            foreach (var type in new HashSet<Type>(classes))
            {
                var bases = new List<string>();
                for (var b = type.BaseType; b != null && b != typeof(object); b = b.BaseType)
                    bases.Add(b.FullName ?? throw new InvalidOperationException($"{type.FullName}: base class {b} has no full name"));
                chains.Add(new Obs.ClassChain { Name = type.FullName ?? throw new InvalidOperationException($"type {type} has no full name"), Bases = EquatableArray.From(bases) });
            }
            chains.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return EquatableArray.From(chains);
        }
    }
}
