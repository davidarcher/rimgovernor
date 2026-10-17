#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.NativeFacade;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Read-only decoded reads for the out-of-process acceptance harness (#3117): the colony facts, blight census, first
    /// layout plan and map survey, and two catalog tables, which only exist decoded inside the game process. Each op
    /// runs <see cref="AcceptanceReads"/> over the published facade and returns its JSON, which the route sends as
    /// <c>{"payload": json}</c>; none writes, takes authority or needs the in-process brain to be running. The harness
    /// reaches them through <c>POST /native/acceptance_read_&lt;name&gt;</c> with a JSON object of arguments.
    /// </summary>
    internal static class AcceptanceReadTools
    {
        private static readonly Lazy<AcceptanceReads> Reads = new Lazy<AcceptanceReads>(
            () => AcceptanceReads.Create(NativeFacadeRegistry.Require(), NativeCatalogFactory.Create), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The acceptance-only ops by route name: arguments in, the payload JSON out.</summary>
        internal static readonly IReadOnlyDictionary<string, Func<JsonObject, CancellationToken, Task<string>>> Ops =
            new Dictionary<string, Func<JsonObject, CancellationToken, Task<string>>>(StringComparer.Ordinal)
            {
                // The decoded sleeping temperature band and power coverage; a member is absent when the colony read did not carry it.
                ["acceptance_read_colony_facts"] = (_, ct) => Reads.Value.ColonyFactsAsync(ct),
                // The planning window's blighted plants and the tick read at; unavailable true when the census is unread.
                ["acceptance_read_blight"] = (_, ct) => Reads.Value.BlightAsync(ct),
                // The first layout plan in the store codec; cells true adds the scored map survey (about two megabytes).
                ["acceptance_read_starter_site"] = (args, ct) => Reads.Value.StarterSiteAsync(args["cells"]?.GetValue<bool>() ?? false, ct),
                // The minimum scores of the Impressiveness room stat's named stages and every stage in order.
                ["acceptance_read_impressiveness"] = (_, ct) => Reads.Value.ImpressivenessAsync(ct),
                // Per capture span, the sample count, min/median/max ms and median allocated bytes; iterations 1-20 after one warm-up.
                ["acceptance_read_capture_profile"] = (args, ct) => ReadCaptureProfile(args["iterations"]?.GetValue<int>() ?? 5, ct),
                // One row per animal race of the definition catalog; a fact the catalog did not read is absent.
                ["acceptance_read_animal_races"] = (_, ct) => Reads.Value.AnimalRacesAsync(ct),
                // One row per TerrainDef: cleanliness stat and natural flag.
                ["acceptance_read_floor_terrains"] = (_, ct) => Reads.Value.FloorTerrainsAsync(ct),
            };

        private static async Task<string> ReadCaptureProfile(int iterations, CancellationToken cancellationToken)
        {
            var n = Math.Clamp(iterations, 1, 20);
            var rec = new CaptureProfile.Recorder();
            var side = await ProtoBoundary.OnGameThread("rimgovernor/acceptance_read_capture_profile", () => CaptureProfile.Capture(rec, n), cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                if (side.Frame != null)
                    foreach (var (span, ms, bytes) in AcceptanceReads.FrameDecodeProfile(side.Frame, n)) rec.Add(span, ms, bytes);
            }, cancellationToken).ConfigureAwait(false);
            return CaptureProfile.Summary(rec, side);
        }
    }
}
