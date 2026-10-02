#nullable enable
using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // What the in-progress re-check of a guarded designation decides.
    internal enum GuardVerdict { Proceed, Wait, Cancel }

    // The registry of named tick guards a Designate selects (#1350). A guard
    // is a Check, the safety rule refused at admission and re-checked before
    // the job's work lands until it finishes (a failure cancels the job and
    // drops the designation), and an optional Wait, a condition that holds
    // the job without cancelling it. Game-free so the contract probes run it
    // over fake subjects; NativeDesignationGuards registers the game's rules.
    internal sealed class GuardRegistry<T>
    {
        private sealed class Guard
        {
            internal Func<T, string?> Check = _ => null;
            internal Func<T, string?>? Wait;
        }

        private readonly Dictionary<string, Guard> guards = new Dictionary<string, Guard>(StringComparer.Ordinal);

        internal GuardRegistry<T> Register(string name, Func<T, string?> check, Func<T, string?>? wait = null)
        {
            if (string.IsNullOrEmpty(name) || guards.ContainsKey(name)) throw new ArgumentException("Guard names are unique and non-empty: " + name);
            guards[name] = new Guard { Check = check, Wait = wait };
            return this;
        }

        internal IEnumerable<string> Names => guards.Keys;
        internal bool Has(string name) => guards.ContainsKey(name);

        // Admit is the admission refusal, null when the guard admits.
        internal string? Admit(string name, T subject) =>
            guards.TryGetValue(name, out var guard) ? guard.Check(subject) : "Unknown designation guard: " + name + ".";

        // Recheck is the in-progress decision before the job's work lands;
        // blocker names the reason for Wait or Cancel.
        internal GuardVerdict Recheck(string name, T subject, out string? blocker)
        {
            blocker = Admit(name, subject);
            if (blocker != null) return GuardVerdict.Cancel;
            blocker = guards[name].Wait?.Invoke(subject);
            return blocker != null ? GuardVerdict.Wait : GuardVerdict.Proceed;
        }
    }

    internal static class GuardNames
    {
        internal const string Enclosure = "enclosure", MineSafety = "mine_safety";
    }
}
