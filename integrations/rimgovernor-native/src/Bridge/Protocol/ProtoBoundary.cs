#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool TryParse<T>(IRimBridgeContext ctx, string toolName, object? request,
            MessageParser<T> parser, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out Common.Failure? failure) where T : class, IMessage<T>
        {
            value = null;
            callerBinary.Value = BinaryOf(ctx);
            string? unavailable;
            var arguments = BridgeCommon.RawArguments(ctx, out unavailable);
            if (arguments == null)
            {
                failure = Fail(Common.FailureCode.Unavailable, "Original invocation arguments are unavailable.");
                return false;
            }
            foreach (var key in arguments.Keys)
            {
                if (key != "request" && key != TraceArgument && key != MainThreadAdmission.ClassArgument && key != EncodingArgument && key != "_rimBridgeTimeoutMs")
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
                Utf8.GetByteCount(json); // strict: invalid Unicode throws below
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
        // Every pass is charged to the open observation hop (#642), including
        // the repeated passes a size check pays for.
        internal static string Format(IMessage reply, bool compact = false)
        {
            var began = Stopwatch.GetTimestamp();
            try { return FormatPass(reply, compact); }
            finally { ObservationWork.Formatted(Stopwatch.GetTimestamp() - began); }
        }

        private static string FormatPass(IMessage reply, bool compact)
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

        // The Go controller always sends encoding=proto-gzip (#757) and accepts
        // only field "proto": base64 of the gzip-compressed binary protobuf
        // message; it refuses a ProtoJSON reply. ProtoJSON in field "payload"
        // remains solely for callers that omit the argument: the acceptance
        // harness and cases that call the companion directly and assert on
        // that text.
        //
        // Every reply of a call follows its argument, including refusals
        // encoded before or without a main-thread hop: TryParse records the
        // form in the call's async flow (callerBinary), and a hop pins it on
        // the thread that runs it (RunHop reads the argument, a detached
        // encode inherits it), since the main thread and encoder workers do
        // not share the caller's flow.
        internal const string EncodingArgument = "encoding";
        internal const string BinaryEncoding = "proto-gzip";
        internal const string PayloadField = "payload";
        internal const string ProtoField = "proto";
        [ThreadStatic] private static bool? binary;
        private static readonly AsyncLocal<bool> callerBinary = new AsyncLocal<bool>();

        /// <summary>Runs encode with the binary reply form on or off for this thread.</summary>
        internal static T WithBinary<T>(bool on, Func<T> encode)
        {
            var previous = binary;
            binary = on;
            try { return encode(); }
            finally { binary = previous; }
        }

        // True when the caller asked for the binary reply form.
        internal static bool BinaryOf(IRimBridgeContext ctx)
        {
            var arguments = BridgeCommon.RawArguments(ctx, out _);
            return arguments != null && arguments.TryGetValue(EncodingArgument, out var raw) && TryString(raw, out var value)
                && string.Equals(value, BinaryEncoding, StringComparison.Ordinal);
        }

        // The reply body in the form this thread's caller asked for, and the
        // envelope field that carries it; charged to the hop like Format.
        private static string Body(IMessage reply, bool compact, out string field)
        {
            if (!(binary ?? callerBinary.Value))
            {
                field = PayloadField;
                return Format(reply, compact);
            }
            field = ProtoField;
            var began = Stopwatch.GetTimestamp();
            try
            {
                using (var buffer = new MemoryStream())
                {
                    using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
                        reply.WriteTo(gzip);
                    return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
            }
            finally { ObservationWork.Formatted(Stopwatch.GetTimestamp() - began); }
        }

        internal static Dictionary<string, object?> Encode(IMessage reply, bool compact = false)
        {
            var payload = Body(reply, compact, out var field);
            Measure(payload);
            return Envelope(field, payload);
        }

        private static Dictionary<string, object?> Envelope(string field, string payload)
            => new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = payload };

        // The payload's UTF-8 length, recorded as the bytes the hop returns (#642).
        private static int Measure(string payload)
        {
            var began = Stopwatch.GetTimestamp();
            var bytes = Utf8.GetByteCount(payload);
            ObservationWork.SizeChecked(Stopwatch.GetTimestamp() - began);
            ObservationWork.Payload(bytes);
            return bytes;
        }

        // Timing is reported beside the payload so the Go sampler can split
        // native main-thread scheduling from tool execution: queueMs is the
        // wait between requesting the main thread and the body starting,
        // executeMs is the body itself including ProtoJSON formatting (a
        // detached hop's formatting runs on an encoder worker instead and is
        // reported apart, in the observation account's encode block),
        // class is the admission class the hop ran under and queueDepth how
        // many hops were pending when it was queued (#631). It
        // covers every tool whose single main-thread hop goes through
        // OnMainThread. The
        // caller's trace argument ("<trace_id>/<span_id>", the controller's
        // scheduler step or worker dispatch) is echoed as timing.trace so
        // the phases join the controller's trace.
        internal const string TimingField = "timing";
        internal const string TraceArgument = "trace";

        // Hops are ordered by the caller's class argument (control,
        // observation, mirror) through MainThreadAdmission, so a queued
        // renew or stop runs before the reads queued ahead of it.
        internal static Task<object> OnMainThread(IRimBridgeContext ctx, Func<object> body, CancellationToken cancellationToken)
            => RunHop(ctx, body, (reply, hop) => WithTiming(reply, hop.Queued, hop.Started, hop.Finished, hop.Trace, hop.Class, hop.Depth, hop.Work), cancellationToken);

        /// <summary>One main-thread hop's timing and account, for its reply's timing block.</summary>
        internal sealed class HopTiming
        {
            internal long Queued, Started, Finished;
            internal string? Trace, Class;
            internal int Depth;
            internal ObservationWork.Hop? Work;
            internal int GameThread;
            internal bool Binary;
        }

        /// <summary>
        /// A value captured on the game thread for an encoder to own (#644),
        /// with the hop that captured it. The value must not reference live
        /// game objects, engine collections, deferred iterators or state that
        /// anything else mutates: it is read on another thread after the game
        /// thread has moved on.
        /// </summary>
        internal sealed class Captured<T>
        {
            internal readonly T Value;
            internal readonly HopTiming Hop;
            internal Captured(T value, HopTiming hop) { Value = value; Hop = hop; }
        }

        /// <summary>
        /// The first half of a detached hop (#644): capture runs on the game
        /// thread under the hop's admission class, watchdog and observation
        /// account, and must only read (no formatting, no byte counting, no
        /// waiting on a worker). Its main-thread time is the hop's executeMs.
        /// </summary>
        internal static async Task<Captured<T>> CaptureOnMainThread<T>(IRimBridgeContext ctx, Func<T> capture, CancellationToken cancellationToken)
            => (Captured<T>)await RunHop(ctx, () => capture()!, (value, hop) => new Captured<T>((T)value, hop), cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// The second half: encode formats the captured value into its
        /// envelope on an encoder worker the lease reserved, never on the game
        /// thread (checked by thread identity), and the envelope carries the
        /// capture hop's timing with the worker's queue wait and encode time
        /// in its encode block. The lease is owned from this call on and
        /// released when the encode settles, however it settles; a token
        /// cancelled before the worker starts skips the encode.
        /// </summary>
        internal static Task<object> EncodeDetached<T>(Captured<T> captured, ReplyEncoder.Lease lease, Func<T, Dictionary<string, object?>> encode, CancellationToken cancellationToken)
        {
            var queued = Stopwatch.GetTimestamp();
            var hop = captured.Hop;
            Task<object> task;
            try
            {
                task = ReplyEncoder.Run(() =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == hop.GameThread)
                        throw new InvalidOperationException("A detached reply must not encode on the game thread.");
                    var work = hop.Work ?? new ObservationWork.Hop();
                    var started = Stopwatch.GetTimestamp();
                    ObservationWork.Resume(work);
                    Dictionary<string, object?> envelope;
                    try { envelope = WithBinary(hop.Binary, () => encode(captured.Value)); }
                    catch (Exception) { ObservationWork.Outcome("error"); throw; }
                    finally
                    {
                        work.EncodeQueueTicks = Math.Max(0, started - queued);
                        work.EncodeTicks = Math.Max(0, Stopwatch.GetTimestamp() - started);
                        work.Encoding = false;
                        ObservationWork.End();
                    }
                    return WithTiming(envelope, hop.Queued, hop.Started, hop.Finished, hop.Trace, hop.Class, hop.Depth, work);
                }, cancellationToken);
            }
            catch (Exception)
            {
                lease.Dispose();
                throw;
            }
            task.ContinueWith(_ => lease.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        private static Task<object> RunHop(IRimBridgeContext ctx, Func<object> body, Func<object, HopTiming, object> complete, CancellationToken cancellationToken)
        {
            var queued = Stopwatch.GetTimestamp();
            var trace = TraceOf(ctx);
            var wantsBinary = BinaryOf(ctx);
            var rank = MainThreadAdmission.RankOf(ClassOf(ctx));
            var hop = MainThreadWatchdog.Enqueue(OperationOf(ctx), trace);
            int depth = 0;
            var task = MainThreadAdmission.Enqueue(ctx, rank, () =>
            {
                MainThreadWatchdog.Start(hop);
#if !NATIVE_CONTRACT_PROBES
                // The frame boundary's patch installs on the first hop (#642):
                // nothing runs a startup constructor in this assembly. The
                // contract probes build has no game boundary to patch, so the
                // install is gated out of it rather than faked.
                ObservationFrameHook.Ensure();
                PlayerSpeedHook.Ensure();
#endif
                var started = Stopwatch.GetTimestamp();
                var work = ObservationWork.Begin();
                try
                {
                    var reply = WithBinary(wantsBinary, body);
                    var finished = Stopwatch.GetTimestamp();
                    FrameAccounting.Observed(finished - started, trace);
                    return complete(reply, new HopTiming { Queued = queued, Started = started, Finished = finished, Trace = trace,
                        Class = MainThreadAdmission.ClassOf(rank), Depth = depth, Work = ObservationWork.End(), GameThread = Thread.CurrentThread.ManagedThreadId, Binary = wantsBinary });
                }
                catch (Exception)
                {
                    // A hop that threw still spent its main-thread time and
                    // still belongs in the account (#642).
                    ObservationWork.Outcome("error");
                    FrameAccounting.Observed(Stopwatch.GetTimestamp() - started, trace);
                    ObservationWork.End();
                    throw;
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

        internal static object WithTiming(object reply, long queued, long started, long finished, string? trace = null, string? cls = null, int queueDepth = -1,
            ObservationWork.Hop? work = null)
        {
            var envelope = reply as Dictionary<string, object?>;
            if (envelope == null || !(envelope.ContainsKey(PayloadField) || envelope.ContainsKey(ProtoField)) || envelope.ContainsKey(TimingField)) return reply;
            var timing = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queueMs"] = Millis(started - queued),
                ["executeMs"] = Millis(finished - started),
            };
            if (trace != null) timing[TraceArgument] = trace;
            if (cls != null) timing[MainThreadAdmission.ClassArgument] = cls;
            if (queueDepth >= 0) timing["queueDepth"] = queueDepth;
            // The hop's own observation account and the session's update
            // intervals (#642); both absent when nothing recorded them.
            var observation = ObservationWork.Report(work);
            if (observation != null) timing["observation"] = observation;
            var frames = FrameAccounting.Report();
            if (frames != null) timing["frames"] = frames;
            // The per-frame allowance's account (#988), once it deferred a hop.
            var budget = MainThreadAdmission.BudgetReport();
            if (budget != null) timing["mainThreadBudget"] = budget;
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
