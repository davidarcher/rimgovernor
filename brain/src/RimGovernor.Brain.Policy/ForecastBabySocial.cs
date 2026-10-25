using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One nursery baby's two conditional sleeping/mood scenarios, owned by the nursery calculation.</summary>
internal sealed record NurseryBabyScenario(BabyRead Baby, ForecastMoodResponse Response,
    double WithShift, double WithoutShift, double WithAwake, double WithoutAwake);

/// <summary>Conditional mean-field social feedback on the existing nursery timeline. Opinion has no independent value:
/// only native interaction mood memories, their later fit consequences and the shared mood/break ledger count.</summary>
internal static class ForecastBabySocial
{
    internal sealed record Side(IReadOnlyDictionary<string, ForecastMoodResponse> Responses,
        IReadOnlyDictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range> Heard);
    internal sealed record Result(Side With, Side Without);

    internal static Result Of(IReadOnlyList<NurseryBabyScenario> babies, ForecastCoupledContext context,
        ForecastCoupledOutcome outcome, ForecastNurseryExposure exposure, double horizon, ISet<string> findings)
    {
        var read = context.Needs.Babies.Social;
        if (!read.Pawns.Any(p => p.Pairs.Any(b => b.Cry is not null || b.Giggle is not null)))
            findings.Add("nursery_no_opinion_hearer");
        else
        {
            findings.Add("nursery_opinion_mean_field_feedback");
            findings.Add("nursery_opinion_poisson_cohorts");
            findings.Add("nursery_opinion_observed_nonjob_eligibility");
            findings.Add("nursery_opinion_incoming_interaction_cooldowns_unprojected");
        }
        return new(Timeline(true), Timeline(false));

        Side Timeline(bool nursery)
        {
            var adults = read.Pawns.ToDictionary(p => p.Pawn, p => new Actor(p, read));
            var canonical = context.Needs.Pawns.Where(p => read.Pawns.Any(a => a.Pawn == p.Pawn) || babies.Any(b => b.Baby.Pawn == p.Pawn))
                .ToDictionary(p => p.Pawn, p => p.Mood?.Memories ?? throw new RequiredFactException("social pawn mood:" + p.Pawn));
            foreach (var recipient in context.Needs.Babies.OtherRecipients.Where(p => read.Pawns.Any(a => a.Pawn == p.Pawn)))
                canonical.Add(recipient.Pawn, EquatableArray.From(recipient.Mood.Memories.SelectMany(g => g.Members)));
            var socialDefs = context.Needs.Babies.Fits.Select(f => f.OpinionThought).Where(d => d.Length != 0).ToHashSet(StringComparer.Ordinal);
            var moodDefs = read.MoodDefs.ToHashSet(StringComparer.Ordinal);
            var social = read.Pawns.ToDictionary(p => p.Pawn, p => new ForecastSocialMemories(ForecastSocialMemories.Observed(canonical[p.Pawn], socialDefs)));
            var admittedMood = babies.ToDictionary(b => b.Baby.Pawn, b => SupportedMood(b.Baby.Pawn));
            var mood = babies.ToDictionary(b => b.Baby.Pawn, b => new ForecastSocialMemories(ForecastSocialMemories.Observed(canonical[b.Baby.Pawn], admittedMood[b.Baby.Pawn])));
            HashSet<string> SupportedMood(string baby)
            {
                var definitions = ForecastSocialMemories.Observed(canonical[baby], moodDefs).Select(m => m.Memory)
                    .Concat(read.Pawns.SelectMany(p => p.Pairs.Where(pair => pair.Baby == baby)).SelectMany(pair => pair.Interactions).SelectMany(i => i.Moods));
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in definitions.GroupBy(d => d.Def))
                {
                    var mechanics = group.Select(d => (d.Label, d.Offset, d.DurationTicks, d.StackLimit, d.SamePawnLimit, d.Multiplier, d.FadeStarts)).Distinct().ToArray();
                    if (mechanics.Length != 1 || group.Any(d => d.FadeStarts != 1 || d.Unprojected.Length != 0))
                    { findings.Add("nursery_opinion_mood_group_unprojected:" + baby + "/" + group.Key); continue; }
                    result.Add(group.Key);
                }
                return result;
            }
            var actual = babies.ToDictionary(b => b.Baby.Pawn, b => b.Response.Initial);
            var targets = babies.ToDictionary(b => b.Baby.Pawn, _ => new List<ForecastMoodTarget>());
            var heardRates = new Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range>();
            var sleepBaseline = new Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range>();
            var maximumRate = new Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), double>();
            var totalRate = new Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), double>();
            var babyIds = babies.Select(b => b.Baby.Pawn).ToHashSet(StringComparer.Ordinal);
            var thoughtDefs = context.Needs.Babies.Fits.ToDictionary(f => f.Def, f => f.OpinionThought);
            var excluded = context.Needs.Babies.Fits.Where(f => f.OpinionThought.Length != 0).Select(f => f.OpinionThought).ToHashSet(StringComparer.Ordinal);
            var cuts = new SortedSet<double> { 0, horizon };
            cuts.UnionWith(exposure.Cuts);
            for (double at = context.Needs.Babies.CheckIntervalTicks; at < horizon; at += context.Needs.Babies.CheckIntervalTicks) cuts.Add(at);
            foreach (var b in babies) foreach (var target in b.Response.Targets) { cuts.Add(target.Start); cuts.Add(Math.Min(horizon, target.End)); }
            foreach (var allocation in outcome.Plan.Allocations)
            {
                cuts.Add(allocation.Arrive); cuts.Add(allocation.Start); cuts.Add(Math.Min(horizon, allocation.End));
                if (!adults.TryGetValue(allocation.Pawn, out var actor)) continue;
                AddChecks(actor.Read.CheckAfterTicks, read.CheckIntervalTicks, allocation);
                AddChecks(actor.Read.RetryAfterTicks, read.RetryIntervalTicks, allocation);
            }
            foreach (var actor in adults.Values)
            {
                for (var t = actor.Read.CheckAfterTicks; t < horizon; t += read.CheckIntervalTicks) if (t >= 0) cuts.Add(t);
                for (var t = actor.Read.RetryAfterTicks; t < horizon; t += read.RetryIntervalTicks) if (t >= 0) cuts.Add(t);
            }
            void AddChecks(double first, double interval, ForecastActivityAllocation a)
            {
                var from = Math.Max(a.Arrive, a.Start);
                var next = first + Math.Max(0, Math.Ceiling((from - first) / interval)) * interval;
                for (var at = next; at < Math.Min(horizon, a.End); at += interval) cuts.Add(at);
            }
            var times = cuts.Where(t => t >= 0 && t <= horizon).ToArray();
            for (var i = 0; i + 1 < times.Length; i++)
            {
                var at = times[i]; var end = times[i + 1]; var ticks = end - at;
                if (ticks <= 0) continue;
                // Hazards see the state at the beginning of this interval. Gains affect the subsequent mood/fit
                // intervals; no fixed point or substitution of mean opinion for an observed fact is claimed exact.
                foreach (var b in babies)
                {
                    var id = b.Baby.Pawn;
                    var source = Target(b.Response, at);
                    var shift = nursery ? b.WithShift : b.WithoutShift;
                    var extra = mood[id].MoodChange(at) / 100;
                    var prior = ForecastMood.ChaseTarget(actual[id], Interpolate(source, at) + shift + extra,
                        Interpolate(source, end) + shift + extra, ticks, b.Response.Rise, b.Response.Fall, source.Frozen);
                    var next = ForecastNursery.FitRates(context.Needs.Babies, b.Baby.Fits, prior.Mean, nursery ? b.WithAwake : b.WithoutAwake);
                    foreach (var (fit, rate) in next)
                    {
                        foreach (var hearer in b.Baby.Hearers)
                        {
                            var duration = context.Needs.Babies.Fits.Single(f => f.Def == fit).DurationTicks;
                            var heard = exposure.HeardDuringFit(b.Baby, hearer.Pawn, hearer.Hearing, at, duration, nursery);
                            Accumulate((id, hearer.Pawn, fit, false), heard, rate * ticks);
                            if (b.Baby.SleepHearers.Any(s => s.Pawn == hearer.Pawn && s.Memory.Fit == fit))
                            {
                                Accumulate((id, hearer.Pawn, fit, true), exposure.HeardAt(b.Baby, hearer.Pawn, hearer.Hearing, at, nursery, true), rate * ticks);
                                var key = (id, hearer.Pawn, fit, true);
                                var baseline = exposure.HeardAt(b.Baby, hearer.Pawn, hearer.Hearing, at, nursery, true, false);
                                var priorBaseline = sleepBaseline.GetValueOrDefault(key);
                                sleepBaseline[key] = new(priorBaseline.Lower + baseline.Lower * rate * ticks, priorBaseline.Upper + baseline.Upper * rate * ticks);
                                maximumRate[key] = Math.Max(maximumRate.GetValueOrDefault(key), rate);
                                totalRate[key] = totalRate.GetValueOrDefault(key) + rate * ticks;
                            }
                            if (!social.TryGetValue(hearer.Pawn, out var pool)) continue;
                            var pair = adults[hearer.Pawn].Read.Pairs.FirstOrDefault(p => p.Baby == id)
                                ?? throw new RequiredFactException("baby social pair:" + hearer.Pawn + "/" + id);
                            var memory = pair.Cry?.Def == thoughtDefs[fit] ? pair.Cry : pair.Giggle?.Def == thoughtDefs[fit] ? pair.Giggle : null;
                            if (memory is not null) pool.Gain(id, memory, at, end, rate * (nursery == (memory.Offset < 0) ? heard.Upper : heard.Lower));
                        }
                    }
                }
                foreach (var (pawn, actor) in adults)
                {
                    actor.Release(at);
                    var presence = exposure.At(pawn, at, nursery);
                    if (actor.Read.Unprojected.Length != 0) { findings.Add(actor.Read.Unprojected); actor.Bounded = true; }
                    if (presence.Count != 1 || presence[0].Share != 1 || presence[0].LowerShare != 1 || presence[0].Position is ServicePositionRead.Unprojected)
                    { findings.Add("nursery_opinion_presence_bounded:" + pawn); actor.Bounded = true; }
                    if (actor.Bounded) { Envelope(pawn, actor, presence); continue; }
                    var own = presence[0];
                    if (own.Social is not ForecastSocialService.Verified { Mode: var mode })
                    { findings.Add("nursery_opinion_service_mode_unprojected:" + own.Activity); actor.Bounded = true; Envelope(pawn, actor, presence); continue; }
                    mode = (ActivitySocialMode)Math.Min((int)mode, (int)actor.Read.MaximumMode);
                    if (mode is ActivitySocialMode.Off or ActivitySocialMode.Quiet) actor.ClearWant();
                    if (mode == ActivitySocialMode.Off) continue;
                    var recipients = new List<string>(); var located = true;
                    foreach (var recipient in actor.Read.Recipients)
                    {
                        IReadOnlyList<ForecastNurseryExposure.Presence> other;
                        if (babyIds.Contains(recipient))
                            other = exposure.BabyAt(babies.Single(b => b.Baby.Pawn == recipient).Baby, at, nursery);
                        else if (context.Needs.Pawns.Any(p => p.Pawn == recipient)) other = exposure.At(recipient, at, nursery);
                        else { located = false; break; }
                        var probability = 0d;
                        foreach (var position in other.Where(p => !p.Asleep))
                        {
                            var spatial = exposure.Social(own.Position, position.Position, nursery);
                            if (spatial.Lower != spatial.Upper || position.LowerShare != position.Share) { located = false; break; }
                            probability += position.Share * spatial.Lower;
                        }
                        if (!located || probability is > 0 and < 1) { located = false; break; }
                        if (probability == 1) recipients.Add(recipient);
                    }
                    if (!located) { findings.Add("nursery_opinion_recipient_exposure_unprojected:" + pawn); actor.Bounded = true; Envelope(pawn, actor, presence); continue; }
                    var events = actor.Events(at, mode, recipients.Count != 0);
                    if (events == 0 || recipients.Count == 0) continue;
                    foreach (var pair in actor.Read.Pairs.Where(p => p.Eligible && recipients.Contains(p.Baby) && mood.ContainsKey(p.Baby)))
                    {
                        var raw = ForecastPrison.Opinion.ObservedRaw(pair.Opinion, canonical[pawn], pair.Baby, at, excluded) + social[pawn].Offset(pair.Baby, at, true);
                        var opinion = ForecastPrison.Opinion.Finish(pair.Opinion, raw);
                        var factor = LayoutFilthRules.EvaluateCurve([.. read.OpinionFactor.Select(p => new CurvePoint(p.X, p.Y))], opinion);
                        var weights = pair.Interactions.Select(p => p.Weight * (p.NegativeOpinionWeight ? factor : 1)).ToArray();
                        var total = weights.Sum();
                        if (total <= 0) throw new RequiredFactException("social recipient positive selection weight:" + pawn + "/" + pair.Baby);
                        for (var n = 0; n < weights.Length; n++)
                            foreach (var memory in pair.Interactions[n].Moods)
                                if (admittedMood[pair.Baby].Contains(memory.Def))
                                    mood[pair.Baby].Gain(pawn, memory, at, end, events / ticks / recipients.Count * weights[n] / total);
                    }

                    void Envelope(string id, Actor bounded, IReadOnlyList<ForecastNurseryExposure.Presence> positions)
                    {
                        findings.Add("nursery_opinion_adverse_exposure_envelope:" + id);
                        if (bounded.Read.MaximumMode == ActivitySocialMode.Off || positions.All(p => p.Asleep ||
                            p.Social is ForecastSocialService.Verified { Mode: ActivitySocialMode.Off })) return;
                        var upper = bounded.MaximumEvents(at);
                        if (upper == 0) return;
                        // No distribution over uncertain recipient membership is asserted. Every eligible competing
                        // recipient remains possible; one baby's selection probability is enclosed by [0,1]. Take
                        // adverse memory endpoints on both conditional sides instead of dropping an uncertain harm
                        // or using a smaller, invented recipient denominator. Tracker uncertainty persists thereafter.
                        foreach (var pair in bounded.Read.Pairs.Where(p => p.Eligible && bounded.Read.Recipients.Contains(p.Baby) && mood.ContainsKey(p.Baby)))
                            foreach (var memory in pair.Interactions.SelectMany(p => p.Moods).DistinctBy(m => m.Def))
                                if (admittedMood[pair.Baby].Contains(memory.Def) && nursery == (memory.Offset < 0))
                                    mood[pair.Baby].Gain(id, memory, at, end, upper / ticks);
                    }
                }
                foreach (var b in babies)
                {
                    var id = b.Baby.Pawn; var source = Target(b.Response, at);
                    var shift = nursery ? b.WithShift : b.WithoutShift;
                    var from = Interpolate(source, at) + shift + mood[id].MoodChange(at) / 100;
                    var to = Interpolate(source, end) + shift + mood[id].MoodChange(end) / 100;
                    targets[id].Add(new(at, end, from, to, source.Frozen));
                    actual[id] = ForecastMood.ChaseTarget(actual[id], from, to, ticks, b.Response.Rise, b.Response.Fall, source.Frozen).Final;
                }
            }
            foreach (var key in heardRates.Keys.ToArray())
            {
                var value = heardRates[key];
                if (key.Sleep)
                {
                    // An arbitrary placement of a finite care budget changes this continuous arrival dose by no
                    // more than its duration times the largest rate. Alternatives share one budget; no uniform
                    // occupancy or independence from cries is needed for this geometric enclosure.
                    var baseline = sleepBaseline[key];
                    var dose = maximumRate[key] * exposure.CareBudgetTicks(key.Baby, key.Pawn, nursery);
                    value = new(Math.Max(value.Lower, Math.Max(0, baseline.Lower - dose)),
                        Math.Min(value.Upper, Math.Min(totalRate[key], baseline.Upper + dose)));
                }
                heardRates[key] = new(value.Lower / horizon, value.Upper / horizon);
            }
            return new(babies.ToDictionary(b => b.Baby.Pawn, b => b.Response with { Targets = [.. targets[b.Baby.Pawn]] }), heardRates);

            void Accumulate((string Baby, string Pawn, string Fit, bool Sleep) key, ForecastNurseryExposure.Range share, double weight)
            {
                var prior = heardRates.GetValueOrDefault(key);
                heardRates[key] = new(prior.Lower + weight * share.Lower, prior.Upper + weight * share.Upper);
            }
        }
    }

    private static ForecastMoodTarget Target(ForecastMoodResponse response, double at) => response.Targets.FirstOrDefault(t => t.Start <= at && at < t.End)
        ?? throw new RequiredFactException("social baby target span");
    private static double Interpolate(ForecastMoodTarget target, double at) => target.From + (target.To - target.From) * (at - target.Start) / (target.End - target.Start);

    /// <summary>Mean-field native tracker mass. Successful random interactions cool until the first hash check strictly
    /// past320 ticks; failed attempts become pending91-tick retries. Incoming interactions are explicitly unprojected.</summary>
    private sealed class Actor(BabySocialPawnRead read, BabySocialRead mechanics)
    {
        internal BabySocialPawnRead Read => read;
        internal bool Bounded { get; set; }
        internal double MaximumEvents(double at) =>
            (Check(at, read.RetryAfterTicks, mechanics.RetryIntervalTicks) && at >= read.AbsoluteCooldownTicks ? 1 : 0)
            + (Check(at, read.CheckAfterTicks, mechanics.CheckIntervalTicks) && at >= read.CooldownTicks ? 1 : 0);
        private double ready = read.WantsInteraction ? 0 : 1;
        private double wants = read.WantsInteraction ? 1 : 0;
        private readonly List<(double At, double Mass)> cooling = [];
        internal void Release(double at)
        { foreach (var item in cooling.Where(c => c.At <= at).ToArray()) { ready += item.Mass; cooling.Remove(item); } }
        internal void ClearWant() { ready += wants; wants = 0; }
        internal double Events(double at, ActivitySocialMode mode, bool recipient)
        {
            double success = 0;
            if (Check(at, read.RetryAfterTicks, mechanics.RetryIntervalTicks) && at >= read.AbsoluteCooldownTicks && wants > 0)
            { if (recipient) { success += wants; wants = 0; } }
            if (Check(at, read.CheckAfterTicks, mechanics.CheckIntervalTicks) && at >= read.CooldownTicks)
            {
                double mtb;
                if (mode == ActivitySocialMode.Quiet) mtb = mechanics.QuietMtbTicks;
                else if (mode == ActivitySocialMode.Normal) mtb = mechanics.NormalMtbTicks;
                else if (mode == ActivitySocialMode.SuperActive) mtb = mechanics.SuperActiveMtbTicks;
                else throw new RequiredFactException("social event mode");
                if (!(mtb > 0)) throw new RequiredFactException("social interaction MTB");
                var attempt = ready * Math.Min(1, mechanics.CheckIntervalTicks / mtb);
                ready -= attempt;
                if (recipient) success += attempt; else wants += attempt;
            }
            if (success > 0)
            {
                var after = at + mechanics.MinInteractionTicks;
                var release = read.CheckAfterTicks + (Math.Floor((after - read.CheckAfterTicks) / mechanics.CheckIntervalTicks) + 1) * mechanics.CheckIntervalTicks;
                cooling.Add((release, success));
            }
            return success;
        }
        private static bool Check(double at, double first, double interval) => at >= first && Math.Abs((at - first) % interval) < 1e-8;
    }
}
