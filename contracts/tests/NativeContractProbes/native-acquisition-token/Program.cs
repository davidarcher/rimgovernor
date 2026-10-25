using System;
using RimGovernor.Native;

// A plant's acquisition token is the version of what eligibility reads:
// resource, cell, harvestability and designation. Growth is not an input,
// so a harvestable plant that grows between the read and the dispatch keeps
// its token; each eligibility fact that moves moves the version, and a token
// read before a move never matches again, even once the facts move back.
internal static class NativeAcquisitionTokenProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal static void Invoke()
    {
        var identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
        string Plant(string resource = "MedicineHerbal", int x = 10, int z = 12, bool harvestable = true, bool designated = false, string id = "Plant_Healroot1") =>
            NativeAcquisitionToken.Plant(identity, id, resource, x, z, harvestable, designated);
        var read = Plant();
        Check(read == Plant(), "the same eligibility facts keep the token");
        Check(read.StartsWith("plant-", StringComparison.Ordinal), "token keeps its kind prefix");
        foreach (var (moved, name) in new (Func<string>, string)[] {
            (() => Plant(resource: "RawBerries"), "resource"), (() => Plant(x: 11), "cell x"), (() => Plant(z: 13), "cell z"),
            (() => Plant(harvestable: false), "harvestability"), (() => Plant(designated: true), "designation") })
        {
            var before = Plant();
            var after = moved();
            Check(after != before, name + " moves the token");
            Check(Plant() != before, name + " moved back is still not the stale read's token");
        }
        var settled = Plant();
        Check(settled == Plant(), "a settled row keeps its token again");
        Check(Plant(id: "Plant_Healroot2") != settled, "another entity has its own token");
        Check(NativeAcquisitionToken.Plant(new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "other", MapId = 0 }, "Plant_Healroot1", "MedicineHerbal", 10, 12, true, false) != settled,
            "another load has its own token");
        Check(settled == Plant(), "another entity's or load's read leaves this row's token");
        var hunt = NativeAcquisitionToken.Hunt(identity, "Plant_Healroot1", "Corpse_Hare", false);
        Check(hunt.StartsWith("hunt-", StringComparison.Ordinal) && hunt != settled, "a hunt row of the same id is its own kind");
        Console.WriteLine("native-acquisition-token: " + checks + " checks passed");
    }
}
