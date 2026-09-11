using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeConstructionHookSet
    {
        private readonly string owner;
        private readonly List<Func<bool>> checks = new List<Func<bool>>();
        internal NativeConstructionHookSet(string owner) { this.owner = owner; }
        internal void Add(MethodBase target, MethodInfo prefix = null, MethodInfo postfix = null, MethodInfo finalizer = null)
        {
            checks.Add(() =>
            {
                var patches = Harmony.GetPatchInfo(target);
                return patches != null
                    && (prefix == null || patches.Prefixes.Any(p => p.owner == owner && p.PatchMethod == prefix))
                    && (postfix == null || patches.Postfixes.Any(p => p.owner == owner && p.PatchMethod == postfix))
                    && (finalizer == null || patches.Finalizers.Any(p => p.owner == owner && p.PatchMethod == finalizer));
            });
        }
        internal bool Ready(int requiredTargets)
        {
            try { return requiredTargets > 0 && checks.Count == requiredTargets && checks.All(check => check()); }
            catch { return false; }
        }
    }
}
