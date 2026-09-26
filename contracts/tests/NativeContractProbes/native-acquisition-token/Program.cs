using System;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;

// A plant's acquisition token covers only what eligibility reads (#689):
// resource, cell, harvestability and designation. Growth is not an input,
// so a harvestable plant that grows between the read and the dispatch keeps
// its token; each eligibility fact that moves changes it.
internal static class NativeAcquisitionTokenProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal static void Invoke()
    {
        var identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
        string Plant(string resource = "MedicineHerbal", int x = 10, int z = 12, bool harvestable = true, bool designated = false, string id = "Plant_Healroot1") =>
            NativeAcquisitionToken.Plant(identity, id, resource, x, z, harvestable, designated);
        var read = Plant();
        Check(read == Plant(), "the same eligibility facts give the same token");
        Check(read.StartsWith("plant-", StringComparison.Ordinal), "token keeps its prefix");
        Check(read != Plant(resource: "RawBerries"), "resource moves the token");
        Check(read != Plant(x: 11) && read != Plant(z: 13), "cell moves the token");
        Check(read != Plant(harvestable: false), "harvestability moves the token");
        Check(read != Plant(designated: true), "designation moves the token");
        Check(read != Plant(id: "Plant_Healroot2"), "the entity moves the token");
        Check(read != NativeAcquisitionToken.Plant(new Common.Identity { ColonyId = "colony", LoadToken = "other", MapId = 0 }, "Plant_Healroot1", "MedicineHerbal", 10, 12, true, false), "the load moves the token");
        Console.WriteLine("native-acquisition-token: " + checks + " checks passed");
    }
}
