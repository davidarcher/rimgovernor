using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    internal static class ProtoBoundary
    {
        internal const int MaximumEnvelopeBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool TryParse<T>(IRimBridgeContext ctx, string toolName, object request,
            MessageParser<T> parser, out T value, out Common.Failure failure) where T : IMessage<T>
        {
            value = default(T);
            string unavailable;
            var arguments = BridgeCommon.RawArguments(ctx, out unavailable);
            if (arguments == null)
            {
                failure = Fail(Common.FailureCode.Unavailable, "Original invocation arguments are unavailable.");
                return false;
            }
            foreach (var key in arguments.Keys)
            {
                if (key != "request" && key != "_rimBridgeTimeoutMs")
                {
                    failure = Fail(Common.FailureCode.InvalidRequest, "The sole caller argument must be request.");
                    return false;
                }
            }
            object raw;
            string json;
            if (!arguments.TryGetValue("request", out raw) || !TryString(raw, out json)
                || !(request is string) || !string.Equals(json, (string)request, StringComparison.Ordinal))
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request must be a ProtoJSON string.");
                return false;
            }
            try
            {
                if (Utf8.GetByteCount(json) > MaximumEnvelopeBytes)
                {
                    failure = Fail(Common.FailureCode.InvalidRequest, "request exceeds the one MiB control envelope limit.");
                    return false;
                }
                value = parser.ParseJson(json);
                failure = null;
                return true;
            }
            catch (InvalidProtocolBufferException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request is not valid ProtoJSON for this method.");
                return false;
            }
            catch (InvalidJsonException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request is not valid JSON.");
                return false;
            }
            catch (EncoderFallbackException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request must contain valid Unicode.");
                return false;
            }
        }

        private static bool TryString(object raw, out string value)
        {
            value = raw as string;
            if (value != null) return true;
            var token = raw as JValue;
            if (token == null || token.Type != JTokenType.String) return false;
            value = token.Value as string;
            return value != null;
        }

        // The SDK recognizes this concrete envelope; generated CLR properties are not its wire format.
        internal static string Format(IMessage reply, bool compact = false)
        {
            var payload = JsonFormatter.Default.Format(reply);
            if (!compact) return payload;
            // Keep official ProtoJSON values and field names; only omit formatting.
            // Identifier strings must never be interpreted as dates.
            using (var input = new StringReader(payload))
            using (var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None })
            using (var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(output) { Formatting = Formatting.None }) {
                writer.WriteToken(reader);
                writer.Flush();
                return output.ToString();
            }
        }

        internal static Dictionary<string, object> Encode(IMessage reply, bool compact = false)
        {
            var payload = Format(reply, compact);
            if (Utf8.GetByteCount(payload) > MaximumEnvelopeBytes)
                throw new InvalidOperationException("Reply exceeds the one MiB control envelope limit.");
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["payload"] = payload };
        }

        // MediaFrame replies (base64 PNG bytes) do not fit the one MiB control
        // envelope; presentation.proto documents a dedicated 48 MiB media
        // ProtoJSON envelope for these messages. Non-media replies must keep
        // using Encode() above so their bound stays at one MiB.
        internal const int MaximumMediaEnvelopeBytes = 48 * 1024 * 1024;

        internal static Dictionary<string, object> EncodeMedia(IMessage reply, bool compact = false)
        {
            var payload = Format(reply, compact);
            if (Utf8.GetByteCount(payload) > MaximumMediaEnvelopeBytes)
                throw new InvalidOperationException("Media reply exceeds the 48 MiB media envelope limit.");
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["payload"] = payload };
        }

        // Timing is reported beside the payload so the Go sampler can split
        // native main-thread scheduling from tool execution: queueMs is the
        // wait between requesting the main thread and the body starting,
        // executeMs is the body itself including ProtoJSON formatting. It
        // covers every tool whose single main-thread hop goes through
        // OnMainThread; multi-hop media captures stay unreported.
        internal const string TimingField = "timing";

        internal static Task<object> OnMainThread(IRimBridgeContext ctx, Func<object> body, CancellationToken cancellationToken)
        {
            var queued = Stopwatch.GetTimestamp();
            return ctx.MainThread.InvokeAsync<object>(() =>
            {
                var started = Stopwatch.GetTimestamp();
                var reply = body();
                return WithTiming(reply, queued, started, Stopwatch.GetTimestamp());
            }, cancellationToken);
        }

        internal static Task<object> OnMainThreadEncoded(IRimBridgeContext ctx, Func<IMessage> body, CancellationToken cancellationToken)
            => OnMainThread(ctx, () => Encode(body()), cancellationToken);

        internal static object WithTiming(object reply, long queued, long started, long finished)
        {
            var envelope = reply as Dictionary<string, object>;
            if (envelope == null || !envelope.ContainsKey("payload") || envelope.ContainsKey(TimingField)) return reply;
            envelope[TimingField] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["queueMs"] = Millis(started - queued),
                ["executeMs"] = Millis(finished - started),
            };
            return envelope;
        }

        private static double Millis(long ticks)
            => ticks <= 0 ? 0.0 : Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 3);

        internal static Common.Failure Fail(Common.FailureCode code, string detail)
        {
            return new Common.Failure { Code = code, Detail = detail };
        }

        internal static bool IsIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; }
            catch (EncoderFallbackException) { return false; }
        }

        private static bool Complete(Common.Identity expected) => expected != null && expected.HasColonyId && expected.HasLoadToken
            && expected.HasMapId && IsIdentifier(expected.ColonyId) && IsIdentifier(expected.LoadToken) && expected.MapId >= 0;

        /// <summary>
        /// The loaded map the identity names, or null. Every typed read and
        /// operation is scoped to this map, never to whichever map the player
        /// happens to be viewing. Call only on the game thread.
        /// </summary>
        internal static Map ResolveMap(Common.Identity expected)
        {
            if (!Complete(expected) || Current.Game == null || Find.Maps == null) return null;
            foreach (var map in Find.Maps)
                if (map != null && map.uniqueID == expected.MapId) return map;
            return null;
        }

        internal static Map ResolveMap(Common.ObservationContext context) => ResolveMap(context?.Identity);

        /// <summary>True while the map is one of the game's loaded maps.</summary>
        internal static bool IsLoaded(Map map)
        {
            if (map == null || Current.Game == null || Find.Maps == null) return false;
            foreach (var loaded in Find.Maps) if (ReferenceEquals(loaded, map)) return true;
            return false;
        }

        internal static bool ValidateIdentity(Common.Identity expected,
            out Common.ObservationContext context, out Common.Failure failure)
            => ValidateIdentity(expected, ResolveMap(expected), out context, out failure);

        /// <summary>
        /// For presentation state that lives on the viewed map (selection,
        /// camera, capture): the identity must name the map the player is
        /// looking at, otherwise StaleIdentity carrying the viewed context.
        /// </summary>
        internal static bool ValidateViewedIdentity(Common.Identity expected,
            out Common.ObservationContext context, out Common.Failure failure)
            => ValidateIdentity(expected, Find.CurrentMap, out context, out failure);

        internal static bool ValidateIdentity(Common.Identity expected, Map map,
            out Common.ObservationContext context, out Common.Failure failure)
        {
            context = null;
            if (!Complete(expected))
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "A complete valid colony, load and map identity is required.");
                return false;
            }
            Common.Unavailable unavailable;
            if (map == null && Current.Game != null && TryReadContext(Find.CurrentMap, out context, out unavailable))
            {
                // The named map is no longer loaded but the game is: stale, with
                // the viewed map's context so the caller can re-anchor.
                failure = Fail(Common.FailureCode.StaleIdentity, "The map this identity names is no longer loaded.");
                failure.ObservedContext = context;
                context = null;
                return false;
            }
            if (!TryReadContext(map, out context, out unavailable))
            {
                failure = Fail(Common.FailureCode.Unavailable, unavailable.Detail);
                return false;
            }
            if (!expected.Equals(context.Identity))
            {
                failure = Fail(Common.FailureCode.StaleIdentity, "The colony, load or map identity has changed.");
                failure.ObservedContext = context;
                return false;
            }
            failure = null;
            return true;
        }

        // Call only on the game thread. Reading never attaches or repairs game components.
        // Any loaded map is a valid context; the viewed map is not special here.
        internal static bool TryReadContext(Map map, out Common.ObservationContext context,
            out Common.Unavailable unavailable)
        {
            context = null;
            if (Current.Game == null || map == null || !IsLoaded(map) || Find.TickManager == null)
            {
                unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." };
                return false;
            }
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            if (identity == null || !IsIdentifier(identity.ColonyId) || !IsIdentifier(identity.LoadToken)
                || map.uniqueID < 0 || Find.TickManager.TicksGame < 0)
            {
                unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing,
                    Detail = "The native identity component is missing or invalid." };
                return false;
            }
            context = new Common.ObservationContext
            {
                Identity = new Common.Identity { ColonyId = identity.ColonyId, LoadToken = identity.LoadToken, MapId = map.uniqueID },
                Tick = Find.TickManager.TicksGame
            };
            NativeControlAuthority authority;
            if (NativeControlAuthority.TryGetForGame(Current.Game, out authority))
                context.NativeGeneration = authority.Status().Generation;
            unavailable = null;
            return true;
        }
    }
}
