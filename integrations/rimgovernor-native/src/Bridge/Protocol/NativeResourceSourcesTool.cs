#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Typed successor to the legacy untyped "home/resource_sources" tool
    // (ResourceAcquisitionTools.Sources) production_policy.py's resource_method
    // still calls directly. This adapter reuses that class's exact eligibility,
    // designation and safety logic (ResourceAcquisitionTools.Eligible/
    // Designated/MiningBlocker) so both the old and new surfaces agree on what
    // counts as a reachable, safe source, but reports only the bounded typed
    // ResourceSource rows the new ResourceSourcesSnapshot contract wants.
    // Deliberately narrower than the legacy read for this first typed slice:
    // no storage capacity or extraction-development detail (both left unset,
    // an explicit Unsupported failure when development is requested), and no
    // per-source CAS snapshot token yet (EntityRef.Snapshot stays unset) since
    // nothing dispatches AcquireResource against a mined source yet -- see
    // docs/BACKLOG.md 05.5.
    public sealed class NativeResourceSourcesTool
    {
        private const string ToolName = "rimgovernor/observations_list_resource_sources";
        private const int SourceLimit = 64;

        [Tool(ToolName, Title = "Read reachable native resource sources", Description = "Up to 64 visible, safely reachable native mining or mature wild-plant sources for one exact output resource definition. Yields are estimates; only ordinary pawn labor produces actual stock. Storage capacity and extraction-development detail are not yet implemented by this adapter.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ResourceSourcesReply.", Always = true)]
        public async Task<object> ListResourceSources(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ResourceSourcesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.ResourceSourcesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = error });
                try
                {
                    var definition = DefDatabase<ThingDef>.GetNamedSilentFail(parsed.Resource);
                    if (definition == null)
                        return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.NotApplicable, "Unknown resource definition.") });
                    var deposits = map.listerThings.AllThings.Where(t => ResourceAcquisitionTools.Product(t)?.defName == parsed.Resource && !t.Position.Fogged(map)).ToList();
                    var eligible = deposits.Where(t => ResourceAcquisitionTools.Eligible(t, map)).ToList();
                    double Distance(Thing t) => map.mapPawns.FreeColonistsSpawned.Min(p => p.Position.DistanceTo(t.Position));
                    var ordered = eligible.OrderByDescending(ResourceAcquisitionTools.Designated).ThenBy(Distance).ThenBy(t => t.thingIDNumber).ToList();
                    Require(ordered.Count <= SourceLimit, "Reachable resource source collection exceeds the read bound; narrow the query.");
                    var snapshot = new Obs.ResourceSourcesSnapshot { Context = context, Resource = parsed.Resource,
                        Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true },
                            Matched = (ulong)ordered.Count, Returned = (ulong)ordered.Count, Filtered = (ulong)(deposits.Count - eligible.Count) } };
                    foreach (var thing in ordered) snapshot.Sources.Add(Project(thing, map, Distance(thing)));
                    return Encode(new Obs.ResourceSourcesReply { Observed = snapshot });
                }
                catch (ReadLimit errorLimit) { return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Resource sources could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ResourceSourcesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope and an exact resource definition are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (!request.HasResource || !ProtoBoundary.IsIdentifier(request.Resource)) return false;
            if (request.Page != null && request.Page.HasCursor && request.Page.Cursor.Length != 0)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Paginated resource source reads are not supported by this adapter.");
                return false;
            }
            if (request.IncludeDevelopment)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Extraction-development detail is not implemented by this read adapter.");
                return false;
            }
            return true;
        }

        private static Obs.ResourceSource Project(Thing thing, Map map, double distance)
        {
            var mineable = thing is Mineable;
            var row = new Obs.ResourceSource
            {
                Source = new Obs.EntityRef { Id = thing.GetUniqueLoadID(), DefName = thing.def.defName, MapId = map.uniqueID,
                    Position = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } },
                Method = mineable ? "mine" : thing.def.plant.IsTree ? "cut" : "harvest",
                Yield = thing is Plant plant ? plant.YieldNow() : thing.def.building.mineableYield,
                Reachable = true,
                Designated = ResourceAcquisitionTools.Designated(thing),
                Safety = mineable ? "open_surface" : "native_eligible",
                Distance = distance,
            };
            return row;
        }

        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static object Encode(Obs.ResourceSourcesReply reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete resource sources reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
