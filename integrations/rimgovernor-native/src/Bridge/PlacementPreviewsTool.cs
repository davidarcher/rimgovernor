using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class PlacementPreviewsTools
    {
        [Tool("home/placement_previews", Title = "Bounded placement previews",
            Description = "Preview 1..16 placements in list order on one native main-thread turn. No construction, god mode, clock changes or camera changes. Every result uses ordinary placement rules; candidates are independent and do not reserve space or materials.")]
        public async Task<object> Preview(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "JSON list of 1..16 objects with exactly defName (string), x/z (integers), rotation (string), stuff (string; empty for default preview). No other fields. Maximum 32768 characters.")] string placements = null)
        {
            JArray candidates;
            try
            {
                if (placements == null || placements.Length > 32768) throw new ArgumentException("Missing or oversized placements");
                candidates = JArray.Parse(placements, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (candidates.Count < 1 || candidates.Count > 16) throw new ArgumentException("Use 1..16 placements");
                foreach (var item in candidates)
                {
                    var row = item as JObject;
                    if (row == null || row.Properties().Count() != 5
                        || row.Properties().Any(p => !new[] { "defName", "x", "z", "rotation", "stuff" }.Contains(p.Name)))
                        throw new ArgumentException("Use exactly defName, x, z, rotation and stuff");
                    foreach (var key in new[] { "defName", "rotation", "stuff" })
                        if (row[key].Type != JTokenType.String || ((string)row[key]).Length > 200
                            || key != "stuff" && string.IsNullOrWhiteSpace((string)row[key]))
                            throw new ArgumentException("Invalid definition, rotation or material");
                    foreach (var key in new[] { "x", "z" })
                        if (row[key].Type != JTokenType.Integer || !int.TryParse(row[key].ToString(), out _))
                            throw new ArgumentException("Coordinates must be 32-bit integers");
                }
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            { return new { success = false, error = "Invalid placement batch: " + error.Message }; }
            var watch = Stopwatch.StartNew();
            return BridgeCommon.WithUnknownArguments(await ctx.MainThread.InvokeAsync<object>(() => {
                var queueMs = watch.Elapsed.TotalMilliseconds;
                if (Current.Game == null || Find.CurrentMap == null)
                    return new { success = false, error = "Load a colony first" };
                var tick = Find.TickManager.TicksGame;
                var rows = new List<object>();
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add(HomePlaceBuildingTools.Preview((string)candidate["defName"], (int)candidate["x"],
                        (int)candidate["z"], (string)candidate["rotation"], (string)candidate["stuff"]));
                }
                return new { success = true, version = 1, tick, mapId = Find.CurrentMap.uniqueID, results = rows,
                    timing = new { mainThreadQueueMs = queueMs, executionMs = watch.Elapsed.TotalMilliseconds - queueMs } };
            }, cancellationToken).ConfigureAwait(false), ctx, typeof(PlacementPreviewsTools), "home/placement_previews");
        }
    }
}
