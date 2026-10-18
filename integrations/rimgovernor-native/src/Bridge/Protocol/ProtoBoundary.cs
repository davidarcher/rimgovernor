#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The game-thread boundary of the typed cores: <see cref="OnGameThread{T}"/> runs a core on the game thread,
    /// beside the identity and refusal helpers every core validates its request with.
    /// </summary>
    internal static class ProtoBoundary
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Runs body on the game thread under its admission class (control before observation), the per-frame
        /// allowance and the watchdog, and returns its reply as built. A hop that ran reports its queue wait and run
        /// time to <see cref="HopTimed"/> on the caller's flow.
        /// </summary>
        internal static async Task<T> OnGameThread<T>(string operation, Func<T> body, CancellationToken cancellationToken, string? cls = null) where T : class
        {
            var mainThread = RimBridgeMainThreadAccess.Current ?? throw new InvalidOperationException("The host's game-thread access is not available.");
            var hop = MainThreadWatchdog.Enqueue(operation);
            long queued = Stopwatch.GetTimestamp(), began = 0, ended = 0;
            var task = MainThreadAdmission.Enqueue(mainThread, MainThreadAdmission.RankOf(cls), () =>
            {
                began = Stopwatch.GetTimestamp();
                MainThreadWatchdog.Start(hop);
                try { return body(); }
                finally { MainThreadWatchdog.Finish(hop); ended = Stopwatch.GetTimestamp(); }
            }, cancellationToken);
            try { return (T)await task.ConfigureAwait(false); }
            // A hop the host never runs (cancelled while queued) still leaves the watchdog.
            finally
            {
                MainThreadWatchdog.Finish(hop);
                if (began != 0) HopTimed?.Invoke(Ms(began - queued), Ms(ended - began));
            }
        }

        // Receives each hop's queue wait and run time, in milliseconds, on the caller's flow: the brain's read
        // tally, installed by the in-process facade.
        internal static Action<double, double>? HopTimed { get; set; }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // refusalClass names how RimWorld answers the same request again; left
        // unspecified, RefusalClassOf(code) classifies it where it is sent.
        internal static Common.Failure Fail(Common.FailureCode code, string detail, Common.RefusalClass refusalClass = Common.RefusalClass.Unspecified)
        {
            var failure = new Common.Failure { Code = code, Detail = detail };
            if (refusalClass != Common.RefusalClass.Unspecified) failure = failure with { RefusalClass = refusalClass };
            return failure;
        }

        // RefusalClassOf is the class a refusal carries when its site names
        // none: a code that says the world moved (stale identity or
        // generation, lost authority, a busy owner) is transient, an
        // unsupported request is permanent, and the catch-all codes stay
        // unknown on purpose, since the game's own reason is what separates
        // them.
        internal static Common.RefusalClass RefusalClassOf(Common.FailureCode code)
        {
            switch (code)
            {
                case Common.FailureCode.Unavailable:
                case Common.FailureCode.StaleIdentity:
                case Common.FailureCode.StaleGeneration:
                case Common.FailureCode.AuthorityRequired:
                case Common.FailureCode.OwnerConflict:
                case Common.FailureCode.AttemptConflict:
                case Common.FailureCode.CapacityExhausted:
                case Common.FailureCode.Cancelled:
                case Common.FailureCode.DeadlineExceeded:
                    return Common.RefusalClass.Transient;
                case Common.FailureCode.Unsupported:
                    return Common.RefusalClass.Permanent;
                default:
                    return Common.RefusalClass.Unknown;
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

        internal static bool Complete([NotNullWhen(true)] Common.Identity? expected) => expected != null
            && IsIdentifier(expected.ColonyId) && IsIdentifier(expected.LoadToken) && expected.MapId >= 0;

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
                failure = failure with { ObservedContext = context };
                context = null;
                return false;
            }
            if (!TryReadContext(map, out context, out var unavailable))
            {
                failure = Fail(Common.FailureCode.Unavailable, (unavailable.Detail ?? ""));
                return false;
            }
            if (!expected.Equals(context.Identity))
            {
                failure = Fail(Common.FailureCode.StaleIdentity, "The colony, load or map identity has changed.");
                failure = failure with { ObservedContext = context };
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
                context = context with { NativeGeneration = authority.Status().Generation };
            unavailable = null;
            return true;
        }
    }
}
