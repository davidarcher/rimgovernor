using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;

// Compiles the production LoadIdIndex and drives it the way RefIndex wires it per map:
// spawned things, the inner thing of a spawned minified thing, zones, and bills on spawned givers.
internal static class NativeRefIndexProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private sealed class FakeThing
    {
        public string Id; public bool Spawned = true; public FakeThing Inner; public FakeThing Holder;
        public List<FakeBill> Bills = new List<FakeBill>();
    }
    private sealed class FakeZone { public string Id; }
    private sealed class FakeBill { public string Id; public FakeThing Giver; }

    private static KeyValuePair<string, T> Pair<T>(string id, T value) => new KeyValuePair<string, T>(id, value);

    internal static void Invoke()
    {
        Check(NativeQuestTargetIdentity.WorshippedTerminalSignal(7, "Quest7.terminal.HackingStarted"),
            "spawned terminal retains exact quest signal identity");
        foreach (var signal in new[] { "Quest8.terminal.HackingStarted", "Quest7.terminal.HackingStarted.extra",
            "prefix.Quest7.terminal.HackingStarted", "Quest7.terminal", "", null })
            Check(!NativeQuestTargetIdentity.WorshippedTerminalSignal(7, signal),
                "terminal identity refuses another quest or nonexact signal");
        var things = new List<FakeThing>();
        var zones = new List<FakeZone>();
        var thingIndex = new LoadIdIndex<FakeThing>(() => things.Select(t => Pair(t.Id, t)), t => t.Id, t => t.Spawned && things.Contains(t));
        var minifiedIndex = new LoadIdIndex<FakeThing>(
            () => things.Where(m => m.Inner != null).Select(m => Pair(m.Inner.Id, m.Inner)),
            t => t.Id, t => t.Holder != null && t.Holder.Inner == t && t.Holder.Spawned && things.Contains(t.Holder));
        var zoneIndex = new LoadIdIndex<FakeZone>(() => zones.Select(z => Pair(z.Id, z)), z => z.Id, zones.Contains);
        var billIndex = new LoadIdIndex<FakeBill>(
            () => things.SelectMany(t => t.Bills).Select(b => Pair(b.Id, b)), b => b.Id,
            b => b.Giver != null && b.Giver.Spawned && things.Contains(b.Giver) && b.Giver.Bills.Contains(b));

        var stove = new FakeThing { Id = "Thing_Stove1" };
        var bill = new FakeBill { Id = "Bill_7", Giver = stove }; stove.Bills.Add(bill);
        var bed = new FakeThing { Id = "Thing_Bed2" };
        var crate = new FakeThing { Id = "Thing_MinifiedThing3", Inner = bed }; bed.Holder = crate;
        var zone = new FakeZone { Id = "Zone_4" };
        things.Add(stove); things.Add(crate); zones.Add(zone);

        Check(thingIndex.Find("Thing_Stove1") == stove, "resolves a spawned thing");
        Check(thingIndex.Find("Thing_Bed2") == null, "a minified inner thing is not a spawned thing");
        Check(minifiedIndex.Find("Thing_Bed2") == bed, "resolves a minified inner thing");
        Check(zoneIndex.Find("Zone_4") == zone, "resolves a zone");
        Check(billIndex.Find("Bill_7") == bill, "resolves a bill");
        Check(thingIndex.Find(null) == null && thingIndex.Find("") == null, "empty ids resolve to nothing");

        var rebuilds = thingIndex.Rebuilds;
        Check(thingIndex.Find("Thing_Stove1") == stove && thingIndex.Rebuilds == rebuilds, "a live hit needs no rebuild");

        var shelf = new FakeThing { Id = "Thing_Shelf5" }; things.Add(shelf);
        Check(thingIndex.Find("Thing_Shelf5") == shelf, "a thing spawned after the last rebuild is found");

        stove.Spawned = false; things.Remove(stove);
        Check(thingIndex.Find("Thing_Stove1") == null, "a despawned thing no longer resolves");
        Check(billIndex.Find("Bill_7") == null, "a bill on a despawned giver no longer resolves");

        crate.Inner = null; bed.Holder = null; bed.Spawned = true; things.Add(bed);
        Check(minifiedIndex.Find("Thing_Bed2") == null, "an unpacked thing is no longer minified");
        Check(thingIndex.Find("Thing_Bed2") == bed, "an unpacked thing resolves as spawned");

        zones.Remove(zone);
        Check(zoneIndex.Find("Zone_4") == null, "a deleted zone no longer resolves");

        Console.WriteLine("Ref index passed " + checks + " assertions; production LoadIdIndex over fake sources, no native gameplay.");
    }
}
