#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The production <see cref="IStatSource"/>: the game's own <c>StatWorker</c> evaluation for a ThingDef or
    /// TerrainDef (with optional stuff and quality), plus the stuff and cost queries, answered on the game thread
    /// for brain code that runs on another one (the planner worker).
    /// </summary>
    /// <remarks>
    /// <para>
    /// How a call runs. The brain's calls are synchronous (<see cref="IStatSource"/>) but the game's stat code is
    /// game-thread only, so a call enqueues its request and blocks the calling thread until the game thread
    /// drains it. <see cref="Drain"/> runs once per frame from the frame hook (<see cref="ObservationFrameHook"/>)
    /// and takes every queued request, so a planner pass that asks for many stats at once is answered together
    /// rather than one frame per request.
    /// </para>
    /// <para>
    /// Per-tick game-thread time budget. A drain stops starting new requests once <see cref="DrainBudgetMicros"/>
    /// (1.5 ms) of game-thread time has been spent in it; the rest wait for the next frame. At least one request
    /// runs per drain, so the queue always makes progress, and a single request is never interrupted. A stat
    /// evaluation costs microseconds to a few hundred microseconds, so one drain answers dozens of them; the
    /// budget exists so a pathological batch can never stall a frame, not as the usual limit. The budget is
    /// separate from the main-thread admission allowance (<c>MainThreadAdmission</c>) that tool and facade hops
    /// share, because a drain is not a hop: it takes no admission slot and needs no watchdog entry.
    /// </para>
    /// <para>
    /// Memoization is per load. A result is kept until the loaded game changes (a different <c>Current.Game</c>
    /// seen by the drain), then dropped, since a def stat may read state a new load changes. Failures are never
    /// kept. There is no guessed default: an unknown def, stuff or stat, a request whose stat the game cannot
    /// evaluate, a hypothetical context and a non-finite value are all a <see cref="StatException"/>.
    /// </para>
    /// <para>
    /// Calling a member on the game thread throws <see cref="InvalidOperationException"/>: the drain runs on that
    /// thread, so the call would wait for itself. A call is bounded by <see cref="WaitTimeout"/> (no drain, for
    /// example no loaded game or a paused frame loop, is a <see cref="StatException"/>, never a hang).
    /// </para>
    /// </remarks>
    internal sealed class GameThreadStatService : IStatSource
    {
        /// <summary>The process-wide service the frame hook drains.</summary>
        internal static readonly GameThreadStatService Instance = new GameThreadStatService();

        /// <summary>Game-thread microseconds a drain may spend before it leaves the rest for the next frame.</summary>
        internal const long DrainBudgetMicros = 1_500;

        /// <summary>How long a call waits for a drain before it gives up.</summary>
        internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

        private enum Op { Evaluate, Value, Shown, RawValue, AllowedStuffs, Cost, TerrainCost }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly Op op;
            private readonly string stat, def, stuff;
            private readonly bool terrain;
            private readonly int quality;

            internal Key(Op op, string stat, string def, string stuff, bool terrain, int quality)
            { this.op = op; this.stat = stat; this.def = def; this.stuff = stuff; this.terrain = terrain; this.quality = quality; }

            public bool Equals(Key other) => op == other.op && terrain == other.terrain && quality == other.quality
                && string.Equals(stat, other.stat, StringComparison.Ordinal) && string.Equals(def, other.def, StringComparison.Ordinal)
                && string.Equals(stuff, other.stuff, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(op, stat, def, stuff, terrain, quality);
            public override string ToString() => $"{op} {stat} {def}" + (stuff.Length > 0 ? "/" + stuff : "");
        }

        private sealed class Request
        {
            internal readonly Key Key;
            internal readonly Func<object> Work;
            internal readonly TaskCompletionSource<object> Done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal volatile bool Abandoned;
            internal Request(Key key, Func<object> work) { Key = key; Work = work; }
        }

        private sealed class Epoch
        {
            internal readonly object? Game;
            internal readonly ConcurrentDictionary<Key, object> Memo = new ConcurrentDictionary<Key, object>();
            internal Epoch(object? game) { Game = game; }
        }

        private readonly ConcurrentQueue<Request> queue = new ConcurrentQueue<Request>();
        private Epoch epoch = new Epoch(null);

        /// <summary>Requests waiting for a drain.</summary>
        internal int Pending => queue.Count;

        // IStatSource. Every member funnels through Run.

        public StatResult Evaluate(string stat, StatSubject subject)
            => Run(new Key(Op.Evaluate, stat, subject.Def, subject.Stuff, subject.Terrain, QualityKey(subject)), () => EvaluateCore(stat, subject));

        public float Value(string stat, StatSubject subject)
            => Run(new Key(Op.Value, stat, subject.Def, subject.Stuff, subject.Terrain, QualityKey(subject)), () => ValueCore(stat, subject, true));

        public bool ShouldShowFor(string stat, StatSubject subject)
            => Run(new Key(Op.Shown, stat, subject.Def, subject.Stuff, subject.Terrain, QualityKey(subject)), () => Resolve(stat, subject).Shown);

        public float ValueWithoutPostProcess(string stat, StatSubject subject)
            => Run(new Key(Op.RawValue, stat, subject.Def, subject.Stuff, subject.Terrain, QualityKey(subject)), () => ValueCore(stat, subject, false));

        public IReadOnlyList<string> AllowedStuffsFor(string def)
            => Run(new Key(Op.AllowedStuffs, "", def, "", false, 0), () => AllowedStuffsCore(def));

        public IReadOnlyList<StatCost> CostListAdjusted(string def, string stuff)
            => Run(new Key(Op.Cost, "", def, stuff, false, 0), () => CostCore(def, stuff, false));

        public IReadOnlyList<StatCost> TerrainCostListAdjusted(string terrain)
            => Run(new Key(Op.TerrainCost, "", terrain, "", true, 0), () => CostCore(terrain, "", true));

        private static int QualityKey(StatSubject subject) => subject.Quality ?? -1;

        private T Run<T>(Key key, Func<T> work) where T : notnull
        {
            if (UnityData.IsInMainThread)
                throw new InvalidOperationException("The stat service cannot be called on the game thread: it waits for a game-thread drain.");
            if (Volatile.Read(ref epoch).Memo.TryGetValue(key, out var hit)) return (T)hit;
            var request = new Request(key, () => work());
            // The drain rides the frame hook; the hook installs on the first hop, so a stat asked before any hop installs it itself.
            ObservationFrameHook.Ensure();
            queue.Enqueue(request);
            if (!request.Done.Task.Wait(WaitTimeout))
            {
                request.Abandoned = true;
                throw new StatException($"stat request {key} was not answered within {WaitTimeout.TotalSeconds:0} s: no frame drained it.");
            }
            return (T)request.Done.Task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// On the game thread, once per frame: answers queued requests until <see cref="DrainBudgetMicros"/> of
        /// game-thread time is spent (at least one runs).
        /// </summary>
        internal void Drain()
        {
            if (queue.IsEmpty) return;
            var game = Current.Game;
            var current = epoch;
            if (!ReferenceEquals(current.Game, game)) Volatile.Write(ref epoch, current = new Epoch(game));
            var started = Stopwatch.GetTimestamp();
            var budget = DrainBudgetMicros * Stopwatch.Frequency / 1_000_000;
            while (queue.TryDequeue(out var request))
            {
                if (request.Abandoned) continue;
                if (game == null)
                {
                    request.Done.TrySetException(new StatException("No game is loaded."));
                    continue;
                }
                try
                {
                    var value = request.Work();
                    current.Memo[request.Key] = value;
                    request.Done.TrySetResult(value);
                }
                catch (StatException error) { request.Done.TrySetException(error); }
                catch (Exception error)
                {
                    request.Done.TrySetException(new StatException($"stat request {request.Key} failed in the game: {error.GetType().Name}: {error.Message}"));
                }
                if (Stopwatch.GetTimestamp() - started >= budget) break;
            }
        }

        // The cores below run on the game thread, inside a drain.

        private static StatResult EvaluateCore(string stat, StatSubject subject)
        {
            var resolved = Resolve(stat, subject);
            return new StatResult(ValueOf(resolved, subject, true), resolved.Shown);
        }

        private static float ValueCore(string stat, StatSubject subject, bool postProcess)
            => ValueOf(Resolve(stat, subject), subject, postProcess);

        private readonly struct Resolved
        {
            internal readonly StatDef Stat;
            internal readonly BuildableDef Def;
            internal readonly ThingDef? Stuff;
            internal readonly StatRequest Request;
            internal Resolved(StatDef stat, BuildableDef def, ThingDef? stuff, StatRequest request)
            { Stat = stat; Def = def; Stuff = stuff; Request = request; }
            internal bool Shown => Stat.Worker.ShouldShowFor(Request);
        }

        // A hypothetical context (a thing request with stated facts) is not something the game can evaluate: the
        // game reads the live thing, so the stated facts have no meaning here. That is Unknown, which the public
        // members surface as a StatException naming the subject.
        private static Fact<Resolved> TryResolve(string stat, StatSubject subject, out string reason)
        {
            reason = "";
            if (subject.Context != null)
            {
                reason = $"a thing request with a stated context (hypothetical {subject.Def}) cannot be evaluated by the game; only a definition request can.";
                return Fact.Unknown<Resolved>();
            }
            var statDef = DefDatabase<StatDef>.GetNamedSilentFail(stat);
            if (statDef == null) throw new StatException($"unknown stat {stat}");
            BuildableDef? def = subject.Terrain
                ? DefDatabase<TerrainDef>.GetNamedSilentFail(subject.Def)
                : DefDatabase<ThingDef>.GetNamedSilentFail(subject.Def);
            if (def == null) throw new StatException($"unknown {(subject.Terrain ? "terrain" : "def")} {subject.Def}");
            ThingDef? stuff = null;
            if (subject.Stuff.Length > 0)
            {
                stuff = DefDatabase<ThingDef>.GetNamedSilentFail(subject.Stuff);
                if (stuff == null) throw new StatException($"unknown stuff {subject.Stuff}");
            }
            if (subject.Quality is int q && (q < 0 || q > (int)QualityCategory.Legendary))
                throw new StatException($"quality {q} is not a quality category");
            var quality = subject.Quality is int c ? (QualityCategory)c : QualityCategory.Normal;
            return Fact.Known(new Resolved(statDef, def, stuff, StatRequest.For(def, stuff, quality)));
        }

        private static Resolved Resolve(string stat, StatSubject subject)
        {
            var fact = TryResolve(stat, subject, out var reason);
            return fact.IsKnown ? fact.Value : throw new StatException(reason);
        }

        private static float ValueOf(Resolved r, StatSubject subject, bool postProcess)
        {
            // A request with no quality reads the definition catalog's stat-table value, exactly as the
            // evaluate-stat tool does; one with a quality evaluates the worker for that quality.
            var value = postProcess && subject.Quality == null
                ? r.Def.GetStatValueAbstract(r.Stat, r.Stuff)
                : r.Stat.Worker.GetValue(r.Request, postProcess);
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new StatException($"stat {r.Stat.defName} of {r.Def.defName} is {value}");
            return value;
        }

        private static IReadOnlyList<string> AllowedStuffsCore(string def)
        {
            var buildable = (BuildableDef?)DefDatabase<ThingDef>.GetNamedSilentFail(def) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(def)
                ?? throw new StatException($"unknown def {def}");
            return GenStuff.AllowedStuffsFor(buildable).Select(s => s.defName).ToList();
        }

        private static IReadOnlyList<StatCost> CostCore(string defName, string stuffName, bool terrain)
        {
            BuildableDef? def = terrain ? DefDatabase<TerrainDef>.GetNamedSilentFail(defName) : DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null) throw new StatException($"unknown {(terrain ? "terrain" : "def")} {defName}");
            ThingDef? stuff = null;
            if (stuffName.Length > 0)
            {
                stuff = DefDatabase<ThingDef>.GetNamedSilentFail(stuffName)
                    ?? throw new StatException($"unknown stuff {stuffName}");
            }
            else if (def.MadeFromStuff)
            {
                throw new StatException($"{defName} is made from stuff; its cost needs one");
            }
            var costs = def.CostListAdjusted(stuff, false);
            return costs == null ? Array.Empty<StatCost>() : costs.Select(c => new StatCost(c.thingDef.defName, c.count)).ToList();
        }
    }
}
