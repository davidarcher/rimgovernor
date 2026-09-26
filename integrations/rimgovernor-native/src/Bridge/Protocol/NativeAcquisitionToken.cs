#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // Acquisition snapshot tokens, kept free of game types so the contract
    // probes can pin what they cover.
    internal static class NativeAcquisitionToken
    {
        internal static string Token(Common.Identity identity, string id, string resource, int x, int z, float growth, int yield, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(id); writer.Write(resource); writer.Write(x); writer.Write(z); writer.Write(growth); writer.Write(yield); writer.Write(designated); }
                using (var hash = SHA256.Create()) return "plant-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        // A plant's token covers only what eligibility reads (#689): the
        // resource, the cell, harvestability and the designation. Growth
        // moves every growth tick, so hashing it refused a harvestable plant
        // that kept growing between the read and the dispatch; YieldNow
        // rounds randomly, so a read's token would miss its own execute.
        internal static string Plant(Common.Identity identity, string id, string resource, int x, int z, bool harvestable, bool designated) =>
            Token(identity, id, resource, x, z, 0, harvestable ? 1 : 0, designated);
    }
}
