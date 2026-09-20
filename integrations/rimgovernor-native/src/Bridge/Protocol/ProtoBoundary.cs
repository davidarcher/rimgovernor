#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
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

        internal static bool TryParse<T>(IRimBridgeContext ctx, string toolName, object? request,
            MessageParser<T> parser, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out Common.Failure? failure) where T : class, IMessage<T>
        {
            value = null;
            string? unavailable;
            var arguments = BridgeCommon.RawArguments(ctx, out unavailable);
            if (arguments == null)
            {
                failure = Fail(Common.FailureCode.Unavailable, "Original invocation arguments are unavailable.");
                return false;
            }
            foreach (var key in arguments.Keys)
            {
                if (key != "request" && key != TraceArgument && key != MainThreadAdmission.ClassArgument && key != "_rimBridgeTimeoutMs")
                {
                    failure = Fail(Common.FailureCode.InvalidRequest, "The sole caller argument must be request.");
                    return false;
                }
            }
            if (!arguments.TryGetValue("request", out var raw) || !TryString(raw, out var json)
                || !(request is string expected) || !string.Equals(json, expected, StringComparison.Ordinal))
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

        private static bool TryString(object? raw, [NotNullWhen(true)] out string? value)
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

        internal static Dictionary<string, object?> Encode(IMessage reply, bool compact = false)
        {
            var payload = Format(reply, compact);
            if (Utf8.GetByteCount(payload) > MaximumEnvelopeBytes)
                throw new InvalidOperationException("Reply exceeds the one MiB control envelope limit.");
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["payload"] = payload };
        }

        // MediaFrame replies (base64 PNG bytes) do not fit the one MiB control
        // envelope; presentation.proto documents a dedicated 48 MiB media
        // ProtoJSON envelope for these messages. Non-media replies must keep
        // using Encode() above so their bound stays at one MiB.
        internal const int MaximumMediaEnvelopeBytes = 48 * 1024 * 1024;

        internal static Dictionary<string, object?> EncodeMedia(IMessage reply, bool compact = false)
        {
            var payload = Format(reply, compact);
            if (Utf8.GetByteCount(payload) > MaximumMediaEnvelopeBytes)
                throw new InvalidOperationException("Media reply exceeds the 48 MiB media envelope limit.");
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["payload"] = payload };
        }

        // Timing is reported beside the payload so the Go sampler can split
        // native main-thread scheduling from tool execution: queueMs is the
        // wait between requesting the main thread and the body starting,
        // executeMs is the body itself including ProtoJSON formatting,
        // class is the admission class the hop ran under and queueDepth how
        // many hops were pending when it was queued (#631). It
        // covers every tool whose single main-thread hop goes through
        // OnMainThread; multi-hop media captures stay unreported. The
        // caller's trace argument ("<trace_id>/<span_id>", the controller's
        // scheduler step or worker dispatch) is echoed as timing.trace so
        // the phases join the controller's trace.
        internal const string TimingField = "timing";
        internal const string TraceArgument = "trace";

        // Hops are ordered by the caller's class argument (control,
        // observation, media) through MainThreadAdmission, so a queued
        // renew or stop runs before the reads queued ahead of it.
        internal static Task<object> OnMainThread(IRimBridgeContext ctx, Func<object> body, CancellationToken cancellationToken)
        {
            var queued = Stopwatch.GetTimestamp();
            var trace = TraceOf(ctx);
            var rank = MainThreadAdmission.RankOf(ClassOf(ctx));
            var hop = MainThreadWatchdog.Enqueue(OperationOf(ctx), trace);
            int depth = 0;
            var task = MainThreadAdmission.Enqueue(ctx, rank, () =>
            {
                MainThreadWatchdog.Start(hop);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var reply = body();
                    return WithTiming(reply, queued, started, Stopwatch.GetTimestamp(), trace, MainThreadAdmission.ClassOf(rank), depth);
                }
                finally { MainThreadWatchdog.Finish(hop); }
            }, cancellationToken, out depth);
            // A hop the host never runs (cancelled while queued) still leaves
            // the watchdog once its task settles.
            task.ContinueWith(_ => MainThreadWatchdog.Finish(hop), TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        // The caller's class argument, or null when it sent none.
        internal static string? ClassOf(IRimBridgeContext ctx)
        {
            var arguments = BridgeCommon.RawArguments(ctx, out _);
            if (arguments == null || !arguments.TryGetValue(MainThreadAdmission.ClassArgument, out var raw) || !TryString(raw, out var cls)) return null;
            return cls;
        }

        // The host's capability id (the tool) and operation id for the
        // watchdog line; a context that cannot be read is never a failure.
        private static string OperationOf(IRimBridgeContext ctx)
        {
            try
            {
                var capability = ctx?.CapabilityId; var operation = ctx?.OperationId;
                return (string.IsNullOrEmpty(capability) ? "tool" : capability!) + (string.IsNullOrEmpty(operation) ? "" : " op=" + operation);
            }
            catch (Exception) { return "tool"; }
        }

        // The caller's trace argument, or null when it sent none or the raw
        // arguments are unreadable; a missing echo is never a failure.
        internal static string? TraceOf(IRimBridgeContext ctx)
        {
            var arguments = BridgeCommon.RawArguments(ctx, out _);
            if (arguments == null || !arguments.TryGetValue(TraceArgument, out var raw) || !TryString(raw, out var trace)) return null;
            return trace.Length > 0 && trace.Length <= 64 ? trace : null;
        }

        internal static Task<object> OnMainThreadEncoded(IRimBridgeContext ctx, Func<IMessage> body, CancellationToken cancellationToken)
            => OnMainThread(ctx, () => Encode(body()), cancellationToken);

        internal static object WithTiming(object reply, long queued, long started, long finished, string? trace = null, string? cls = null, int queueDepth = -1)
        {
            var envelope = reply as Dictionary<string, object?>;
            if (envelope == null || !envelope.ContainsKey("payload") || envelope.ContainsKey(TimingField)) return reply;
            var timing = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queueMs"] = Millis(started - queued),
                ["executeMs"] = Millis(finished - started),
            };
            if (trace != null) timing[TraceArgument] = trace;
            if (cls != null) timing[MainThreadAdmission.ClassArgument] = cls;
            if (queueDepth >= 0) timing["queueDepth"] = queueDepth;
            envelope[TimingField] = timing;
            return envelope;
        }

        private static double Millis(long ticks)
            => ticks <= 0 ? 0.0 : Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 3);

        internal static Common.Failure Fail(Common.FailureCode code, string detail)
        {
            return new Common.Failure { Code = code, Detail = detail };
        }

        internal static bool IsIdentifier([NotNullWhen(true)] string? value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool Complete([NotNullWhen(true)] Common.Identity? expected) => expected != null && expected.HasColonyId && expected.HasLoadToken
            && expected.HasMapId && IsIdentifier(expected.ColonyId) && IsIdentifier(expected.LoadToken) && expected.MapId >= 0;

        /// <summary>
        /// The loaded map the identity names, or null. Every typed read and
        /// operation is scoped to this map, never to whichever map the player
        /// happens to be viewing. Call only on the game thread.
        /// </summary>
        internal static Map? ResolveMap(Common.Identity? expected)
        {
            if (!Complete(expected) || Current.Game == null || Find.Maps == null) return null;
            foreach (var map in Find.Maps)
                if (map != null && map.uniqueID == expected.MapId) return map;
            return null;
        }

        internal static Map? ResolveMap(Common.ObservationContext? context) => ResolveMap(context?.Identity);

        /// <summary>
        /// The map an already validated or admitted context names. Validation
        /// proved it loaded on this game thread; a missing map here is an
        /// invariant failure, not a caller error.
        /// </summary>
        internal static Map LoadedMap(Common.ObservationContext context) => ResolveMap(context.Identity)
            ?? throw new InvalidOperationException("The validated map is no longer loaded.");

        /// <summary>True while the map is one of the game's loaded maps.</summary>
        internal static bool IsLoaded([NotNullWhen(true)] Map? map)
        {
            if (map == null || Current.Game == null || Find.Maps == null) return false;
            foreach (var loaded in Find.Maps) if (ReferenceEquals(loaded, map)) return true;
            return false;
        }

        internal static bool ValidateIdentity(Common.Identity? expected,
            [NotNullWhen(true)] out Common.ObservationContext? context, [NotNullWhen(false)] out Common.Failure? failure)
            => ValidateIdentity(expected, ResolveMap(expected), out context, out failure);

        /// <summary>Resolves and validates in one step; the map is non-null exactly when validation succeeds.</summary>
        internal static bool ValidateIdentity(Common.Identity? expected, [NotNullWhen(true)] out Map? map,
            [NotNullWhen(true)] out Common.ObservationContext? context, [NotNullWhen(false)] out Common.Failure? failure)
        {
            var resolved = ResolveMap(expected);
            var valid = ValidateIdentity(expected, resolved, out context, out failure);
            map = valid ? resolved : null;
            return valid && map != null;
        }

        /// <summary>
        /// For presentation state that lives on the viewed map (selection,
        /// camera, capture): the identity must name the map the player is
        /// looking at, otherwise StaleIdentity carrying the viewed context.
        /// </summary>
        internal static bool ValidateViewedIdentity(Common.Identity? expected,
            [NotNullWhen(true)] out Common.ObservationContext? context, [NotNullWhen(false)] out Common.Failure? failure)
            => ValidateIdentity(expected, Find.CurrentMap, out context, out failure);

        internal static bool ValidateIdentity(Common.Identity? expected, Map? map,
            [NotNullWhen(true)] out Common.ObservationContext? context, [NotNullWhen(false)] out Common.Failure? failure)
        {
            context = null;
            if (!Complete(expected))
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "A complete valid colony, load and map identity is required.");
                return false;
            }
            if (map == null && Current.Game != null && TryReadContext(Find.CurrentMap, out context, out _))
            {
                // The named map is no longer loaded but the game is: stale, with
                // the viewed map's context so the caller can re-anchor.
                failure = Fail(Common.FailureCode.StaleIdentity, "The map this identity names is no longer loaded.");
                failure.ObservedContext = context;
                context = null;
                return false;
            }
            if (!TryReadContext(map, out context, out var unavailable))
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
        internal static bool TryReadContext(Map? map, [NotNullWhen(true)] out Common.ObservationContext? context,
            [NotNullWhen(false)] out Common.Unavailable? unavailable)
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
            if (NativeControlAuthority.TryGetForGame(Current.Game, out var authority) && authority != null)
                context.NativeGeneration = authority.Status().Generation;
            unavailable = null;
            return true;
        }
    }
}
