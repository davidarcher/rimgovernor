using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Native
{
    /// <summary>
    /// The game-thread boundary of the typed cores: <see cref="OnGameThread{T}"/> runs a core on the game thread,
    /// beside the identity and refusal helpers every core validates its request with.
    /// </summary>
    internal static class GameThread
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Runs body on the game thread under its queue class (control before observation), the per-frame
        /// allowance and the watchdog, and returns its reply as built. A hop that ran reports its queue wait, run time
        /// and the bytes it allocated on the game thread to <see cref="HopTimed"/> on the caller's flow.
        /// </summary>
        internal static async Task<T> OnGameThread<T>(string operation, Func<T> body, CancellationToken cancellationToken, string? queueClass = null) where T : class
        {
            var mainThread = NativeMainThreadAccess.Current ?? throw new InvalidOperationException("The host's game-thread access is not available.");
            var hop = MainThreadWatchdog.Enqueue(operation);
            long queued = Stopwatch.GetTimestamp(), began = 0, ended = 0, allocated = 0;
            List<(string Part, double Ms)>? reported = null;
            var task = GameThreadQueue.Enqueue(mainThread, GameThreadQueue.RankOf(queueClass), () =>
            {
                began = Stopwatch.GetTimestamp();
                var before = GC.GetAllocatedBytesForCurrentThread();
                MainThreadWatchdog.Start(hop);
                GameThreadTrace.Mark("hop " + operation);
                parts = new List<(string, double)>();
                try { return body(); }
                finally
                {
                    MainThreadWatchdog.Finish(hop); GameThreadTrace.Mark("hop " + operation + " returned"); ended = Stopwatch.GetTimestamp(); allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    reported = parts; parts = null;
                }
            }, cancellationToken);
            try { return (T)await task.ConfigureAwait(false); }
            // A hop the host never runs (cancelled while queued) still leaves the watchdog.
            finally
            {
                MainThreadWatchdog.Finish(hop);
                if (began != 0) HopTimed?.Invoke(operation, Ms(began - queued), Ms(ended - began), allocated);
                if (reported != null) foreach (var (part, ms) in reported) HopPartTimed?.Invoke(operation + "/" + part, ms);
            }
        }

        // Receives each hop's operation, queue wait and run time in milliseconds, and game-thread allocated bytes, on
        // the caller's flow: the brain's read tally, installed by the in-process facade.
        internal static Action<string, double, double, long>? HopTimed { get; set; }

        // Receives each part a hop's core reported (HopPart) as "<operation>/<part>" with its run time in milliseconds,
        // on the caller's flow after the hop's own report.
        internal static Action<string, double>? HopPartTimed { get; set; }

        // The parts the running hop's core has reported so far; null outside a hop (the game thread runs one at a time).
        [ThreadStatic] private static List<(string Part, double Ms)>? parts;

        /// <summary>
        /// Reports where <paramref name="stopwatchTicks"/> of the running hop's run time went, as the hop's part
        /// <paramref name="part"/> (the brain's tally lists it as <c>hop_run["&lt;operation&gt;/&lt;part&gt;"]</c>);
        /// outside a hop, nothing.
        /// </summary>
        internal static void HopPart(string part, long stopwatchTicks) => parts?.Add((part, Ms(stopwatchTicks)));

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // A failure the site does not classify carries the class its code implies
        // (RefusalClassOf); refusalClass names how RimWorld answers the same
        // request again where the site knows.
        internal static RimGovernor.Brain.GameFacts.Common.Failure Fail(RimGovernor.Brain.GameFacts.Common.FailureCode code, string detail) => Fail(code, detail, RefusalClassOf(code));

        internal static RimGovernor.Brain.GameFacts.Common.Failure Fail(RimGovernor.Brain.GameFacts.Common.FailureCode code, string detail, RimGovernor.Brain.GameFacts.Common.RefusalClass refusalClass) =>
            new RimGovernor.Brain.GameFacts.Common.Failure { Code = code, RefusalClass = refusalClass, Detail = detail };

        // RefusalClassOf is the class a refusal carries when its site names
        // none: a code that says the world moved (stale identity or
        // generation, lost authority, a busy owner) is transient, an
        // unsupported request is permanent, and the catch-all codes stay
        // unclassified on purpose, since the game's own reason is what separates
        // them.
        internal static RimGovernor.Brain.GameFacts.Common.RefusalClass RefusalClassOf(RimGovernor.Brain.GameFacts.Common.FailureCode code)
        {
            switch (code)
            {
                case RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.AuthorityRequired:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.AttemptConflict:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.CapacityExhausted:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.Cancelled:
                case RimGovernor.Brain.GameFacts.Common.FailureCode.DeadlineExceeded:
                    return RimGovernor.Brain.GameFacts.Common.RefusalClass.Transient;
                case RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported:
                    return RimGovernor.Brain.GameFacts.Common.RefusalClass.Permanent;
                default:
                    return RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified;
            }
        }

        internal static bool IsIdentifier([NotNullWhen(true)] string? value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; }
            catch (EncoderFallbackException) { return false; }
        }

        /// <summary>
        /// A free-text wire field: valid UTF-8 (a lone surrogate throws), cut to 4096 characters without
        /// splitting a surrogate pair.
        /// </summary>
        internal static string WireText(string value)
        {
            Utf8.GetByteCount(value);
            int end = 0;
            for (int count = 0; end < value.Length && count < 4096; count++, end++) if (char.IsHighSurrogate(value[end])) end++;
            return value.Substring(0, end);
        }

        internal static bool Complete([NotNullWhen(true)] RimGovernor.Brain.GameFacts.Common.Identity? expected) => expected != null
            && IsIdentifier(expected.ColonyId) && IsIdentifier(expected.LoadToken) && expected.MapId >= 0;

        /// <summary>
        /// The loaded map the identity names, or null. Every typed read and
        /// operation is scoped to this map, never to whichever map the player
        /// happens to be viewing. Call only on the game thread.
        /// </summary>
        internal static Map? ResolveMap(RimGovernor.Brain.GameFacts.Common.Identity? expected)
        {
            if (!Complete(expected) || Current.Game == null || Find.Maps == null) return null;
            foreach (var map in Find.Maps)
                if (map != null && map.uniqueID == expected.MapId) return map;
            return null;
        }

        internal static Map? ResolveMap(RimGovernor.Brain.GameFacts.Common.ObservationContext? context) => ResolveMap(context?.Identity);

        /// <summary>
        /// The map an already validated or admitted context names. Validation
        /// proved it loaded on this game thread; a missing map here is an
        /// invariant failure, not a caller error.
        /// </summary>
        internal static Map LoadedMap(RimGovernor.Brain.GameFacts.Common.ObservationContext context) => ResolveMap(context.Identity)
            ?? throw new InvalidOperationException("The validated map is no longer loaded.");

        /// <summary>True while the map is one of the game's loaded maps.</summary>
        internal static bool IsLoaded([NotNullWhen(true)] Map? map)
        {
            if (map == null || Current.Game == null || Find.Maps == null) return false;
            foreach (var loaded in Find.Maps) if (ReferenceEquals(loaded, map)) return true;
            return false;
        }

        internal static bool ValidateIdentity(RimGovernor.Brain.GameFacts.Common.Identity? expected,
            [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Common.ObservationContext? context, [NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Failure? failure)
            => ValidateIdentity(expected, ResolveMap(expected), out context, out failure);

        /// <summary>Resolves and validates in one step; the map is non-null exactly when validation succeeds.</summary>
        internal static bool ValidateIdentity(RimGovernor.Brain.GameFacts.Common.Identity? expected, [NotNullWhen(true)] out Map? map,
            [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Common.ObservationContext? context, [NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Failure? failure)
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
        internal static bool ValidateViewedIdentity(RimGovernor.Brain.GameFacts.Common.Identity? expected,
            [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Common.ObservationContext? context, [NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Failure? failure)
            => ValidateIdentity(expected, Find.CurrentMap, out context, out failure);

        internal static bool ValidateIdentity(RimGovernor.Brain.GameFacts.Common.Identity? expected, Map? map,
            [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Common.ObservationContext? context, [NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Failure? failure)
        {
            context = null;
            if (!Complete(expected))
            {
                failure = Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A complete valid colony, load and map identity is required.");
                return false;
            }
            if (map == null && Current.Game != null && TryReadContext(Find.CurrentMap, out context, out _))
            {
                // The named map is no longer loaded but the game is: stale, with
                // the viewed map's context so the caller can re-anchor.
                failure = Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity, "The map this identity names is no longer loaded.");
                failure = failure with { ObservedContext = context };
                context = null;
                return false;
            }
            if (!TryReadContext(map, out context, out var unavailable))
            {
                failure = Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, (unavailable.Detail ?? ""));
                return false;
            }
            if (!expected.Equals(context.Identity))
            {
                failure = Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity, "The colony, load or map identity has changed.");
                failure = failure with { ObservedContext = context };
                return false;
            }
            failure = null;
            return true;
        }

        // Call only on the game thread. Reading never attaches or repairs game components.
        // Any loaded map is a valid context; the viewed map is not special here.
        internal static bool TryReadContext(Map? map, [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Common.ObservationContext? context,
            [NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Unavailable? unavailable)
        {
            context = null;
            if (Current.Game == null || map == null || !IsLoaded(map) || Find.TickManager == null)
            {
                unavailable = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." };
                return false;
            }
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            if (identity == null || !IsIdentifier(identity.ColonyId) || !IsIdentifier(identity.LoadToken)
                || map.uniqueID < 0 || Find.TickManager.TicksGame < 0)
            {
                unavailable = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NativeComponentMissing,
                    Detail = "The native identity component is missing or invalid." };
                return false;
            }
            context = new RimGovernor.Brain.GameFacts.Common.ObservationContext
            {
                NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(),
                Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = identity.ColonyId, LoadToken = identity.LoadToken, MapId = map.uniqueID },
                Tick = Find.TickManager.TicksGame
            };
            if (NativeControlAuthority.TryGetForGame(Current.Game, out var authority) && authority != null)
                context = context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(authority.Status().Generation) };
            unavailable = null;
            return true;
        }
    }
}
