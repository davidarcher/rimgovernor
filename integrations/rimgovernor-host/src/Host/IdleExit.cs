#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Host
{
    /// <summary>
    /// Harness-only idle exit: a game launched with
    /// <c>-rimgovernor-idle-exit=&lt;minutes&gt;</c> quits (Root.Shutdown on the
    /// main thread) once no bridge call has been in flight or finished for
    /// that long, at the main menu or in a loaded game alike. The acceptance
    /// harness keeps games warm between runs; this ends the ones nobody comes
    /// back to. Activity is every extension tool call through
    /// RimBridgeStartup.RegisterExtensionTools' handler; a call in flight (a
    /// long poll included) counts as activity until it returns. Without the
    /// flag nothing happens.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class IdleExit
    {
        public const string Argument = "-rimgovernor-idle-exit=";
        public const string Owner = "davidarcher.rimgovernor.host.idle-exit";

        /// <summary>The configured idle limit, or null when the flag is absent or malformed.</summary>
        public static readonly TimeSpan? Limit = Parse(Environment.GetCommandLineArgs());

        private static int inFlight;
        private static long lastActivity = Stopwatch.GetTimestamp();
        private static bool fired;

        static IdleExit()
        {
            if (Limit == null) return;
            try
            {
                new Harmony(Owner).Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)) ?? throw new MissingMethodException("Root.Update"),
                    postfix: new HarmonyMethod(typeof(IdleExit), nameof(AfterUpdate)));
                ModLog.Info("idle-exit", "idle exit armed: the game quits after " + Limit.Value.TotalMinutes + " minutes without a bridge call");
            }
            catch (Exception ex)
            {
                ModLog.Error("idle-exit", "idle exit not installed: " + ex);
            }
        }

        /// <summary>The limit named by the first -rimgovernor-idle-exit=&lt;minutes&gt; argument; null when absent, unparseable or not positive.</summary>
        public static TimeSpan? Parse(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg == null || !arg.StartsWith(Argument, StringComparison.Ordinal)) continue;
                if (double.TryParse(arg.Substring(Argument.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
                    return TimeSpan.FromMinutes(minutes);
                return null;
            }
            return null;
        }

        /// <summary>Whether the game should exit: nothing in flight and the last activity is at least limit ago.</summary>
        public static bool Due(int inFlightCalls, TimeSpan sinceActivity, TimeSpan limit) => inFlightCalls <= 0 && sinceActivity >= limit;

        /// <summary>A bridge call started; pair with <see cref="End"/>.</summary>
        internal static void Begin()
        {
            Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
        }

        /// <summary>A bridge call returned (or faulted).</summary>
        internal static void End()
        {
            Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref inFlight);
        }

        private static void AfterUpdate()
        {
            if (fired || Limit == null) return;
            var since = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastActivity)) / (double)Stopwatch.Frequency);
            if (!Due(Volatile.Read(ref inFlight), since, Limit.Value)) return;
            fired = true;
            ModLog.Info("idle-exit", "idle exit: no bridge call for " + (int)since.TotalMinutes + " minutes; quitting");
            Root.Shutdown();
        }
    }
}
