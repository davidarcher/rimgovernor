using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

// The effect-source generator (#3224, #3217 Gate 1): from a forecast mood shortfall, a bounded deterministic backward
// search through the verified mechanic cards to coupled candidates no family hand-codes.
// - Shortfall: pawns whose no-change mood forecast (ForecastMoodTimeline) carries break risk or threshold exposure.
// - Effect match: a verified memory card with a positive mood stage; each colonist's marginal stack, from the card's
//   XML and the colonist's live summed offset, is the memory the candidate grants (none at the stack limit).
// - Backward chaining, at most MaxDepth steps per candidate: the site's action (the site -> action-kind table) on a
//   target whose guards hold; a fresh container (Building, through the layout slot it fills) when the card's
//   container is usable only fresh; disposal (Deconstruction) of an exhausted occupant whose slot and refund the
//   fresh one takes. Unknown guards never certify; a colonist's body and any container holding one are Burial's.
// Candidates carry their material inputs and refunds as task yields, so funding is the coupled forecast's, and the
// granted memories as ForecastCommitment.MoodMemories, so value comes only from the break-risk edge (#3221).

/// <summary>A layout slot for a container definition (a planned room's template piece), free or holding Occupant.</summary>
internal sealed record EffectSourceSlot(string Id, string Class, string Definition, string Stuff, Cell Location)
{
    /// <summary>The standing instance on the slot; empty when the slot is free.</summary>
    public string Occupant { get; init; } = "";

    /// <summary>What deconstructing the occupant returns (the catalog's deconstruct yield); unknown leaves it in place.</summary>
    public Fact<EquatableArray<Amount>> Refund { get; init; }
}

/// <summary>One pawn's work ticks on one step target (a slot to build, a thing to haul or deconstruct).</summary>
internal sealed record EffectSourceWork(string Pawn, string Target, Fact<double> Ticks);

/// <summary>The colony facts the generator reads; routes use ForecastLocations cell ids.</summary>
internal sealed record EffectSourceFacts
{
    public required NeedsForecastRead Needs { get; init; }
    public double Horizon { get; init; } = GameTime.TicksPerDay;
    public Fact<EquatableArray<MoodPawn>> Colonists { get; init; }
    public IReadOnlyList<WasteItem> Waste { get; init; } = [];
    public IReadOnlyList<EffectSourceSlot> Slots { get; init; } = [];

    /// <summary>Construction cost per definition and stuff (WorkAdmissionObservation.Buildings).</summary>
    public Fact<EquatableArray<WorkBuildingCost>> Costs { get; init; }

    public IReadOnlyList<EffectSourceWork> Work { get; init; } = [];
    public IReadOnlyList<ForecastActivityRoute> Routes { get; init; } = [];

    /// <summary>The node budget: candidate chains expanded before the search reports truncation.</summary>
    public int Budget { get; init; } = 32;
}

/// <summary>How a task executes: the existing action kind, its target, the slot it fills and the actor constraint.</summary>
internal sealed record EffectSourceStep(string Task, ActionKind Action, string Target, string Slot, string Constraint);

/// <summary>The family's tasks and options, its root commitments (with their mood memories) and each task's step.</summary>
internal sealed record EffectSourceResult(CoordinatedMethodCandidates Family, EquatableArray<ForecastCommitment> Roots, EquatableArray<EffectSourceStep> Steps);

internal static class EffectSourceCandidates
{
    internal const int MaxDepth = 3;

    /// <summary>
    /// The search over precomputed facts (the Methods stage calls no policy function): <paramref name="shortfall"/> holds
    /// the pawns whose no-change mood forecast carries break risk or exposure, and <paramref name="evaluations"/> each card
    /// site's guard evaluation keyed by (def, site, target, instance), where instance "" is a planned fresh container.
    /// PolicyFunctions.GenerateEffectSources computes both.
    /// </summary>
    internal static EffectSourceResult Generate(EffectSourceFacts facts, IEnumerable<MechanicCard> cards, IReadOnlySet<string> shortfall,
        IReadOnlyDictionary<(string Def, string Site, string Target, string Instance), CardEvaluation> evaluations)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var roots = new List<ForecastCommitment>(); var steps = new List<EffectSourceStep>();
        var truncated = false; var budget = facts.Budget;
        foreach (var card in cards.Where(_ => shortfall.Count > 0).OrderBy(c => c.Def, StringComparer.Ordinal))
        {
            if (!card.Live)
            {
                findings.Add((card.Status == MechanicCardStatus.Unreachable ? "effect_source_card_unreachable:" : "effect_source_card_unverified:") + card.Def);
                continue;
            }
            var memories = Memories(card, facts, findings);
            if (!memories.Any(m => shortfall.Contains(m.Pawn))) continue;
            foreach (var site in card.Sites.OrderBy(s => s.Site, StringComparer.Ordinal))
            {
                var owner = site.Site[..Math.Max(site.Site.LastIndexOf('.'), 0)];
                if (MechanicCardGuards.SiteActions.FirstOrDefault(a => a.Site == site.Site) is not { } action)
                { findings.Add("effect_source_site_unreached:" + site.Site); continue; }
                foreach (var target in facts.Waste.Where(w => w.State != WasteState.Buried && w.Eligible && !Owned(w)).OrderBy(w => w.Id, StringComparer.Ordinal))
                {
                    var fresh = Evaluation(card, site, target.Id, "");
                    foreach (var slot in facts.Slots.Where(s => s.Class == owner || s.Class.EndsWith("." + owner, StringComparison.Ordinal))
                        .OrderBy(s => s.Id, StringComparer.Ordinal))
                    {
                        if (budget-- == 0) { truncated = true; findings.Add("effect_source_generation_truncated"); goto done; }
                        var chain = new List<(ActionKind Action, string Target, string Constraint)>();
                        if (slot.Occupant.Length == 0)
                        {
                            if (fresh?.Usable != true) continue;
                            chain.Add((ActionKind.Building, slot.Id, ""));
                        }
                        else if (Evaluation(card, site, target.Id, slot.Occupant) is var used && used?.Usable != true)
                        {
                            // An exhausted occupant whose guard fails only for its own state: dispose of it, build fresh in its slot.
                            if (fresh?.Usable != true || used is null || !used.Exhausted || used.Guards.Any(g => g.Verdict == CardGuardVerdict.Unknown)
                                || !slot.Refund.IsKnown || facts.Waste.Any(w => w.State == WasteState.Buried && w.Grave == slot.Occupant && Owned(w))) continue;
                            chain.Add((ActionKind.Deconstruction, slot.Occupant, ""));
                            chain.Add((ActionKind.Building, slot.Id, ""));
                        }
                        chain.Add((action.Action, target.Id, action.Constraint));
                        if (chain.Count > MaxDepth) { findings.Add("effect_source_depth_exceeded:" + card.Def); continue; }
                        Emit(card, slot, target, chain, memories);
                    }
                }
            }
        }
    done:
        var pawns = options.Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        var horizon = Known(facts.Horizon);
        var problem = activities.Count == 0 ? null : new ForecastActivityProblem
        {
            Activities = activities.ToArray(), Options = options.ToArray(),
            Routes = ForecastLocations.Merge(facts.Routes.Where(r => pawns.Contains(r.Pawn))
                .OrderBy(r => r.Pawn, StringComparer.Ordinal).ThenBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.To, StringComparer.Ordinal)),
            Deadline = horizon, AlternativeLaborCost = horizon
        };
        return new(new(problem, [], new Dictionary<(string, string), ActivityTaskCandidate>(), truncated) { Findings = EquatableArray.From(findings) },
            EquatableArray.From(roots), EquatableArray.From(steps));

        void Emit(MechanicCard card, EffectSourceSlot slot, WasteItem target, List<(ActionKind Action, string Target, string Constraint)> chain,
            EquatableArray<ForecastMoodMemory> memories)
        {
            var id = "effect/" + card.Def + "/" + target.Id + "/" + slot.Id;
            var tasks = new List<ForecastActivity>(); var rows = new List<ForecastActivityOption>(); var bound = new List<EffectSourceStep>();
            var at = ForecastLocations.Of(slot.Location);
            string[] prior = [];
            foreach (var (kind, thing, constraint) in chain)
            {
                var task = id + "/" + kind.ToString().ToLowerInvariant();
                var activity = new ForecastActivity(task, at, at, prior);
                if (kind == ActionKind.Building)
                {
                    if (!facts.Costs.TryGetValue(out var costs) || costs.FirstOrDefault(c => c.Definition == slot.Definition && c.Stuff == slot.Stuff) is not { } cost
                        || !cost.Materials.TryGetValue(out var materials))
                    { findings.Add("effect_source_cost_unknown:" + slot.Definition + "/" + slot.Stuff); return; }
                    activity = activity with { Inputs = EquatableArray.From(materials.Select(m => new ForecastActivityYield(m.Resource, Known((double)m.Count)))) };
                }
                else if (kind == ActionKind.Deconstruction)
                    activity = activity with { Outputs = EquatableArray.From(slot.Refund.Value.Select(m => new ForecastActivityYield(m.Resource, Known((double)m.Count)))) };
                else activity = activity with { Location = ForecastLocations.Of(target.Cell) };
                var actors = Actors(constraint);
                var workers = facts.Work.Where(w => w.Target == thing && w.Ticks.IsKnown && (actors is null || actors.Contains(w.Pawn)))
                    .GroupBy(w => w.Pawn).Select(g => g.First()).OrderBy(w => w.Pawn, StringComparer.Ordinal).ToArray();
                if (workers.Length == 0) { findings.Add("effect_source_no_worker:" + kind + "/" + thing); return; }
                tasks.Add(activity);
                rows.AddRange(workers.Select(w => new ForecastActivityOption(task, w.Pawn, w.Ticks, Known(0.0))));
                bound.Add(new(task, kind, thing, slot.Id, constraint));
                prior = [task];
            }
            activities.AddRange(tasks); options.AddRange(rows); steps.AddRange(bound);
            roots.Add(new(id, []) { ActivityCompletions = [prior[0]], ExclusiveOutcome = "effect/" + card.Def, MoodMemories = memories });
        }

        // A missing evaluation certifies nothing; unknown guards are reported.
        CardEvaluation? Evaluation(MechanicCard card, MechanicCardSite site, string target, string instance)
        {
            if (!evaluations.TryGetValue((card.Def, site.Site, target, instance), out var evaluation)) return null;
            foreach (var unknown in evaluation.Guards.Where(g => g.Verdict == CardGuardVerdict.Unknown))
                findings.Add("effect_source_guard_unknown:" + card.Def + ":" + unknown.Reason);
            return evaluation;
        }

        // The actor constraints of the site -> action-kind table; null is no constraint.
        HashSet<string>? Actors(string constraint) => constraint switch
        {
            "" => null,
            "colonist" => facts.Colonists.TryGetValue(out var colonists) ? colonists.Select(c => c.Id.Value).ToHashSet(StringComparer.Ordinal) : [],
            _ => [],
        };
    }

    /// <summary>A colonist's body, and a container holding one, belong to colonist burial (the admin/burial Need), not this search.</summary>
    private static bool Owned(WasteItem item) => item.CorpseOf == CorpseOf.Colonist;

    /// <summary>
    /// The card's memory for each colonist with a mood: its marginal stack, BaseMoodEffect * StackedEffectMultiplier^live,
    /// none at StackLimit. Live stacks are read from the colonist's summed offset of the def. Unread thoughts give none.
    /// </summary>
    private static EquatableArray<ForecastMoodMemory> Memories(MechanicCard card, EffectSourceFacts facts, SortedSet<string> findings)
    {
        if (card.Kind != "memory" || card.Xml.Stages is not [{ BaseMoodEffect: double effect and > 0 }]) return [];
        if (card.Xml.StackLimit is not { } limit || limit > 1 && card.Xml.StackedEffectMultiplier is null
            || !facts.Colonists.TryGetValue(out var colonists) || colonists.Any(c => !c.Thoughts.IsKnown))
        { findings.Add("effect_source_stacks_unknown:" + card.Def); return []; }
        var multiplier = card.Xml.StackedEffectMultiplier ?? 1;
        var duration = card.Xml.DurationDays is { } days ? Known(days * GameTime.TicksPerDay) : Unknown<double>();
        var rows = new List<ForecastMoodMemory>();
        foreach (var colonist in colonists.Where(c => c.Mood.IsKnown).OrderBy(c => c.Id.Value, StringComparer.Ordinal))
        {
            var offset = colonist.Thoughts.Value.Where(t => t.Def == card.Def).Sum(t => t.Offset);
            var live = 0; double total = 0;
            while (live < limit && offset >= (total += effect * Math.Pow(multiplier, live)) - .01) live++;
            if (live < limit) rows.Add(new(colonist.Id.Value, new NeedMemory(card.Def, effect * Math.Pow(multiplier, live), duration, false)));
        }
        return EquatableArray.From(rows);
    }
}
