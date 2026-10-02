#nullable enable
using System;
using HomeBridge.BridgeTools;

// The guard registry contract (#1350): every named guard refuses at
// admission when its rule fails, and its in-progress re-check cancels the
// job once the rule fails mid-work; a wait holds the job without
// cancelling. Fake subjects stand in for the game's rules, which are
// registered under the same names in NativeDesignationGuards.
internal static class NativeDesignationGuardsProbe
{
    private sealed class Site { public string? Unsafe; public string? Waiting; }

    internal static void Invoke()
    {
        var registry = new GuardRegistry<Site>()
            .Register(GuardNames.Enclosure, s => s.Unsafe, s => s.Waiting)
            .Register(GuardNames.MineSafety, s => s.Unsafe)
            .Register(GuardNames.WallUpgrade, s => s.Unsafe)
            .Register(GuardNames.Acquisition, s => s.Unsafe);
        foreach (var name in new[] { GuardNames.Enclosure, GuardNames.MineSafety, GuardNames.WallUpgrade, GuardNames.Acquisition })
        {
            var site = new Site();
            Require(registry.Admit(name, site) == null, name + " admits a safe site");
            Require(registry.Recheck(name, site, out _) == GuardVerdict.Proceed, name + " lets a safe job proceed");
            site.Unsafe = "roof support is unproven";
            Require(registry.Admit(name, site) == "roof support is unproven", name + " refuses at admission");
            Require(registry.Recheck(name, site, out var blocker) == GuardVerdict.Cancel && blocker == "roof support is unproven", name + " re-check cancels in progress");
        }
        var held = new Site { Waiting = "roof still up" };
        Require(registry.Admit(GuardNames.Enclosure, held) == null, "a wait is no admission refusal");
        Require(registry.Recheck(GuardNames.Enclosure, held, out var wait) == GuardVerdict.Wait && wait == "roof still up", "a wait holds without cancelling");
        held.Unsafe = "enclosing wall";
        Require(registry.Recheck(GuardNames.Enclosure, held, out _) == GuardVerdict.Cancel, "a failed check outranks a wait");
        Require(registry.Admit("roof_support", new Site()) != null, "an unknown guard refuses");
        var duplicate = false;
        try { registry.Register(GuardNames.MineSafety, s => null); } catch (ArgumentException) { duplicate = true; }
        Require(duplicate, "guard names are unique");
        Console.WriteLine("native-designation-guards: ok");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("native-designation-guards: " + message);
    }
}
