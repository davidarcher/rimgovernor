#nullable enable
using Common = RimGovernor.Contracts.Wire.Common;

namespace HomeBridge.BridgeTools
{
    // Acquisition snapshot tokens, kept free of game types so the contract
    // probes can pin what they cover.
    internal static class NativeAcquisitionToken
    {
        // A plant's token covers only what eligibility reads: the
        // resource, the cell, harvestability and the designation. Growth
        // moves every growth tick, so covering it refused a harvestable plant
        // that kept growing between the read and the dispatch; YieldNow
        // rounds randomly, so a read's token would miss its own execute.
        internal static string Plant(Common.Identity identity, string id, string resource, int x, int z, bool harvestable, bool designated) =>
            NativeSnapshotVersions.Token("plant", identity, id, w => { w.Write(resource); w.Write(x); w.Write(z); w.Write(harvestable); w.Write(designated); });

        // A hunt follows its animal: the token binds the animal, its corpse
        // and its designation, not the cell or health the census read, which
        // move every tick under a running clock.
        internal static string Hunt(Common.Identity identity, string id, string corpse, bool designated) =>
            NativeSnapshotVersions.Token("hunt", identity, id, w => { w.Write(corpse); w.Write(designated); });
    }
}
