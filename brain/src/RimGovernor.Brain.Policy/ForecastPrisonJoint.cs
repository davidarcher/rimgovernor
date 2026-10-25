using System.Numerics;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class ForecastPrison
{
    public const int JointStateLimit = 4096;
    public sealed record RecruitmentLeg(double There, double Back, Cell Arrival);
    /// <summary>One target in native scan order, with the existing native route owner and work-cost function.</summary>
    public sealed record RecruitmentTarget(PrisonerRecruitRead Prisoner, double Cell, double ServedAt, double Units,
        ConversionReductionRead? Converter = null, double Certainty = 0, PrisonCellForecast? Exposure = null,
        Func<double, double>? MoodShift = null)
    {
        public Cell Position { get; init; }
        public double CellServedAt { get; init; } = ServedAt;
        public Func<string, Cell, RecruitmentLeg?>? Route { get; init; }
        public Func<string, double, double>? WorkCost { get; init; }
        public double MaximumWalk { get; init; }
    }
    private sealed record Interaction(string Warden, bool Conversion, double Start);
    private sealed record Progress(double Resistance, double Certainty, bool Converted, double Next, double Updated,
        EquatableArray<Interaction> Chats);
    private sealed record Chat(int Target, bool Conversion, double Attempt, double End, Cell Arrival,
        double Back, bool Attempted);
    private sealed record Lane(string Pawn, Cell Position, double Available, Chat? Chat, double Back, int LastTarget, bool Conversion);
    private readonly record struct JointState(BigInteger Recruited, string? Inspiration, int Expires,
        EquatableArray<Progress> Progress, EquatableArray<Lane> Lanes);

    public static class Inspirations
    {
        public const double CheckTicks = 100;
        public static double StartChance(double? mood, double ticks)
        {
            if (mood is not { } level || level < 0.5) return 0;
            var mtb = 210 + ((Math.Min(level, 1) - 0.5) / 0.5 * (10 - 210));
            var p = CheckTicks / (mtb * GameTime.TicksPerDay);
            return 1 - Math.Pow(1 - p, ticks / CheckTicks);
        }
    }

    // Bound every destination ledger as masses arrive, retaining the largest masses deterministically.
    private sealed class JointMass(Action<JointState, double> discard)
    {
        private readonly Dictionary<JointState, (double Mass, long Serial)> rows = [];
        private readonly SortedSet<(double Mass, long Serial)> rank = [];
        private readonly Dictionary<long, JointState> keys = [];
        private long serial;
        internal IEnumerable<(JointState State, double Mass)> Rows => rows.Select(r => (r.Key, r.Value.Mass));
        internal void Add(JointState state, double mass)
        {
            if (mass <= 0) return;
            if (rows.TryGetValue(state, out var old))
            {
                rank.Remove((old.Mass, old.Serial));
                rows[state] = (old.Mass + mass, old.Serial);
                rank.Add((old.Mass + mass, old.Serial));
                return;
            }
            if (rows.Count == JointStateLimit)
            {
                var smallest = rank.Min;
                if (mass <= smallest.Mass) { discard(state, mass); return; }
                var removed = keys[smallest.Serial];
                rows.Remove(removed);
                rank.Remove(smallest);
                keys.Remove(smallest.Serial);
                discard(removed, smallest.Mass);
            }
            var id = serial++;
            rows.Add(state, (mass, id));
            keys.Add(id, state);
            rank.Add((mass, id));
        }
    }

    /// <summary>
    /// Shared recruitment, conversion and hourly inspiration renewal. Retained branches carry each worker's actual native
    /// interaction position and occupied job interval. A free worker takes the closest reachable eligible prisoner, ties
    /// retaining native scan order. Discarded branches earn no future benefit and pay maximum remaining work.
    /// </summary>
    public static IReadOnlyList<Expected> JointRecruitment(PrisonRecruitRead read, WardenRead warden, double? wardenMood,
        IReadOnlyList<RecruitmentTarget> targets, IReadOnlyDictionary<string, Cell>? workerPositions = null)
    {
        DomainException.Require(read.MaxInteractionsPerDay > 0, "recruit chats a day");
        var days = targets.Select(t => t.Units).ToArray();
        var costs = new double[targets.Count];
        var convertCosts = new double[targets.Count];
        var pruned = new double[targets.Count];
        var interval = (double)GameTime.TicksPerDay / read.MaxInteractionsPerDay;
        var end = targets.Select(t => t.ServedAt + t.Units * GameTime.TicksPerDay).DefaultIfEmpty(0).Max();
        double clock = 0;
        bool Held(JointState s, int n) => (s.Recruited & (BigInteger.One << n)) != 0;
        double End(int n) => targets[n].ServedAt + targets[n].Units * GameTime.TicksPerDay;
        double? MoodAt(RecruitmentTarget t, double at, bool upper = false)
        {
            var service = t.CellServedAt;
            return Mood(t.Prisoner.Mood, at < service ? t.Prisoner.Mood?.CellThought : t.Cell,
                at, service, t.Exposure, upper, t.MoodShift?.Invoke(at) ?? 0);
        }
        double Cost(int n, string worker, double ticks) => targets[n].WorkCost?.Invoke(worker, ticks) ?? 0;
        void Discard(JointState s, double mass)
        {
            for (var n = 0; n < targets.Count; n++)
            {
                var t = targets[n];
                var returnDebt = 0d;
                foreach (var lane in s.Lanes.Where(l => l.LastTarget == n))
                {
                    var cost = Cost(n, lane.Pawn, lane.Chat?.Back ?? lane.Back);
                    returnDebt += cost;
                    if (lane.Conversion) convertCosts[n] += mass * cost;
                    else costs[n] += mass * cost;
                }
                if (Held(s, n))
                {
                    if (returnDebt > 0) pruned[n] += mass;
                    continue;
                }
                // An extra job covers a job that can start at the boundary and any currently occupied attempt.
                var maximum = Math.Max(0, Math.Floor((End(n) - Math.Max(clock, s.Progress[n].Next) - Math.Min(RapportToils, read.ConvertTalks) * ToilTicks) / interval) + 1);
                var walk = t.MaximumWalk;
                costs[n] += mass * maximum * (Cost(n, warden.Pawn, ChatJobTicks + walk) + Cost(n, warden.Pawn, walk));
                if (!s.Progress[n].Converted && t.Converter is { } converter)
                {
                    convertCosts[n] += mass * maximum * (Cost(n, converter.Warden, ConvertJobTicks(read) + walk) + Cost(n, converter.Warden, walk));
                }
                pruned[n] += mass;
            }
        }
        static EquatableArray<T> Replace<T>(EquatableArray<T> rows, int n, T value) =>
            EquatableArray.From(rows.Select((row, i) => i == n ? value : row));
        static int Steps(double duration) => Math.Max(1, (int)Math.Ceiling(duration * GameTime.TicksPerDay / GameTime.TicksPerHour));
        var allRecruited = (BigInteger.One << targets.Count) - 1;
        var workers = new[] { warden.Pawn }.Concat(targets.Select(t => t.Converter?.Warden).OfType<string>()).Distinct().ToArray();
        Cell Origin(string worker) => workerPositions?.GetValueOrDefault(worker) ?? default;
        var initial = new JointState(BigInteger.Zero, warden.Inspired?.Def,
            warden.Inspired is { } now ? Steps(now.DurationDays - now.AgeDays) : 0,
            EquatableArray.From(targets.Select(t => new Progress(t.Prisoner.Resistance, t.Certainty,
                t.Converter is null, t.ServedAt, 0, []))),
            EquatableArray.From(workers.Select(p => new Lane(p, Origin(p), 0, null, 0, -1, false))));
        var states = new JointMass(Discard);
        states.Add(initial, 1);
        var total = warden.Inspirations.Sum(i => i.Commonality);
        var startChance = warden.InspirationBlocked || total <= 0 ? 0 : Inspirations.StartChance(wardenMood, GameTime.TicksPerHour);
        var hour = 0;
        foreach (var t in targets.Where(t => t.Exposure is not null))
            DomainException.Require(read.MoodCurve.Zip(read.MoodCurve.Skip(1)).All(p => p.First.Y <= p.Second.Y)
                && read.CertaintyDrift.Zip(read.CertaintyDrift.Skip(1)).All(p => p.First.Y <= p.Second.Y), "monotone prison mood curves");

        while (clock <= end || states.Rows.Any(r => r.State.Lanes.Any(l => l.Chat is not null || l.Back > 0)))
        {
            // Native expiry/start checks precede an attempt falling on the same hourly boundary.
            if (clock > 0 && clock == (hour + 1) * (double)GameTime.TicksPerHour)
            {
                hour++;
                var renewed = new JointMass(Discard);
                foreach (var (state, mass) in states.Rows)
                {
                    var s = state.Inspiration is not null && state.Expires <= hour ? state with { Inspiration = null, Expires = 0 } : state;
                    if (s.Recruited == allRecruited || s.Inspiration is not null || startChance <= 0) { renewed.Add(s, mass); continue; }
                    renewed.Add(s, mass * (1 - startChance));
                    foreach (var inspiration in warden.Inspirations)
                        renewed.Add(s with { Inspiration = inspiration.Def, Expires = hour + Steps(inspiration.DurationDays) },
                            mass * startChance * inspiration.Commonality / total);
                }
                states = renewed;
            }
            // Each lane has at most one pending attempt. Different converters can work concurrently; a converter that
            // is also the recruiter occupies that same lane through the entire native job.
            for (var laneIndex = 0; laneIndex < workers.Length; laneIndex++)
            {
                var advanced = new JointMass(Discard);
                foreach (var (state, mass) in states.Rows)
                {
                    var lane = state.Lanes[laneIndex];
                    if (lane.Chat is not { } chat) { advanced.Add(state, mass); continue; }
                    var n = chat.Target;
                    var t = targets[n];
                    var p = state.Progress[n];
                    if (!chat.Attempted && chat.Attempt == clock)
                    {
                        var finishedLane = lane with { Chat = chat with { Attempted = true } };
                        var updated = state with { Lanes = Replace(state.Lanes, laneIndex, finishedLane) };
                        if (chat.Conversion)
                        {
                            var conversion = t.Prisoner.Conversion ?? throw new RequiredFactException("prisoner conversion:" + t.Prisoner.Pawn);
                            DomainException.Require(conversion.ChangeFactor > 0, "certainty change factor");
                            var certainty = p.Certainty;
                            for (var at = p.Updated; at < clock;)
                            {
                                var step = Math.Min(GameTime.TicksPerHour, clock - at);
                                var mood = MoodAt(t, at, true);
                                certainty = Math.Min(1, certainty + (mood is { } level ? Curve(read.CertaintyDrift, level, "certainty drift curve") : 0)
                                    / conversion.ChangeFactor * step / GameTime.TicksPerDay);
                                at += step;
                            }
                            certainty = Math.Max(0, certainty - t.Converter!.Reduction * conversion.ChangeFactor);
                            updated = updated with { Progress = Replace(updated.Progress, n, p with
                                { Certainty = certainty, Converted = certainty <= 0, Updated = clock }) };
                            advanced.Add(updated, mass);
                            continue;
                        }
                        var parts = t.Prisoner.Opinions.FirstOrDefault(o => o.Warden == warden.Pawn)
                            ?? throw new RequiredFactException("prisoner opinion:" + t.Prisoner.Pawn);
                        var opinion = new Opinion(read, parts);
                        foreach (var interaction in p.Chats.Where(c => c.Warden == warden.Pawn))
                        {
                            var talks = interaction.Conversion ? read.ConvertTalks : RapportToils;
                            for (var i = 0; i < talks; i++)
                                if (interaction.Conversion) opinion.ConversionChat(interaction.Start + i * ToilTicks);
                                else opinion.Chat(interaction.Start + i * ToilTicks);
                        }
                        var inspired = state.Inspiration == (t.Prisoner.WildMan ? read.TamingInspiration : read.RecruitInspiration);
                        var chance = inspired || !t.Prisoner.WildMan && p.Resistance <= 0 ? 1 : t.Prisoner.WildMan ? parts.TameChance : 0;
                        var recruited = mass * chance;
                        days[n] -= recruited * Math.Max(0, (End(n) - clock) / GameTime.TicksPerDay);
                        advanced.Add(updated with { Recruited = updated.Recruited | (BigInteger.One << n),
                            Progress = Replace(updated.Progress, n, initial.Progress[n]),
                            Inspiration = inspired ? null : updated.Inspiration, Expires = inspired ? 0 : updated.Expires }, recruited);
                        var resistance = t.Prisoner.WildMan ? p.Resistance : Math.Max(0, p.Resistance - PerChat(read, warden,
                            MoodAt(t, clock), opinion.At(clock)));
                        advanced.Add(updated with { Progress = Replace(updated.Progress, n, p with { Resistance = resistance }) }, mass - recruited);
                    }
                    else if (chat.End == clock)
                        advanced.Add(state with { Lanes = Replace(state.Lanes, laneIndex, lane with
                            { Position = chat.Arrival, Available = clock, Chat = null, Back = chat.Back, LastTarget = chat.Back > 0 ? n : -1 }) }, mass);
                    else advanced.Add(state, mass);
                }
                states = advanced;
            }
            // Start free lanes now, preserving every pending job in the joint key.
            var scheduled = new JointMass(Discard);
            foreach (var (state, mass) in states.Rows)
            {
                var s = state;
                for (var li = 0; li < workers.Length; li++)
                {
                    var lane = s.Lanes[li];
                    if (lane.Chat is not null || lane.Available > clock) continue;
                    var eligible = Enumerable.Range(0, targets.Count).Where(n => !Held(s, n) && s.Progress[n].Next <= clock
                        && !s.Lanes.Any(l => l.Chat?.Target == n)
                        && (s.Progress[n].Converted ? lane.Pawn == warden.Pawn : targets[n].Converter?.Warden == lane.Pawn))
                        .OrderBy(n => Squared(lane.Position, targets[n].Position)).ThenBy(n => n);
                    var started = false;
                    foreach (var n in eligible)
                    {
                        var t = targets[n];
                        var conversion = !s.Progress[n].Converted;
                        var leg = t.Route is null ? new RecruitmentLeg(0, 0, t.Position) : t.Route(lane.Pawn, lane.Position);
                        if (leg is null) continue;
                        var rapport = clock + leg.There;
                        var attempt = rapport + (conversion ? read.ConvertTalks : RapportToils) * ToilTicks;
                        if (attempt > End(n)) continue;
                        var job = new Chat(n, conversion, attempt, attempt + ToilTicks, leg.Arrival, leg.Back, false);
                        var progress = s.Progress[n];
                        s = s with
                        {
                            Lanes = Replace(s.Lanes, li, lane with { Chat = job, Back = 0, LastTarget = n, Conversion = conversion }),
                            Progress = Replace(s.Progress, n, progress with { Next = clock + interval,
                                Chats = [.. progress.Chats, new Interaction(lane.Pawn, conversion, rapport)] }),
                        };
                        if (conversion) convertCosts[n] += mass * Cost(n, lane.Pawn, ConvertJobTicks(read) + leg.There);
                        else costs[n] += mass * Cost(n, lane.Pawn, ChatJobTicks + leg.There);
                        started = true;
                        break;
                    }
                    // The existing opportunity-cost model returns to the observed work origin when no next prisoner is
                    // eligible. Within a run of jobs, the next target is selected from the actual interaction endpoint.
                    if (!started && lane.Back > 0 && lane.LastTarget >= 0)
                    {
                        var n = lane.LastTarget;
                        if (!lane.Conversion) costs[n] += mass * Cost(n, lane.Pawn, lane.Back);
                        else convertCosts[n] += mass * Cost(n, lane.Pawn, lane.Back);
                        s = s with { Lanes = Replace(s.Lanes, li, lane with { Position = Origin(lane.Pawn),
                            Available = clock + lane.Back, Back = 0, LastTarget = -1 }) };
                    }
                }
                if (s.Recruited == allRecruited && s.Lanes.All(l => l.Chat is null && l.Back == 0))
                    s = initial with { Recruited = allRecruited, Inspiration = null, Expires = 0 };
                scheduled.Add(s, mass);
            }
            states = scheduled;
            var next = (hour + 1) * (double)GameTime.TicksPerHour;
            foreach (var (s, _) in states.Rows)
            {
                foreach (var lane in s.Lanes)
                {
                    if (lane.Chat is { } chat) next = Math.Min(next, chat.Attempted ? chat.End : chat.Attempt);
                    else if (lane.Available > clock) next = Math.Min(next, lane.Available);
                }
                for (var n = 0; n < targets.Count; n++)
                    if (!Held(s, n) && s.Progress[n].Next > clock) next = Math.Min(next, s.Progress[n].Next);
            }
            if (next <= clock) throw new DomainException("prison joint clock did not advance");
            clock = next;
        }
        return [.. targets.Select((_, n) => new Expected(days[n])
            { PrunedProbability = pruned[n], WorkCost = costs[n], ConversionCost = convertCosts[n] })];
    }

    private static double Squared(Cell a, Cell b) => ((double)a.X - b.X) * ((double)a.X - b.X) + ((double)a.Z - b.Z) * ((double)a.Z - b.Z);
}
