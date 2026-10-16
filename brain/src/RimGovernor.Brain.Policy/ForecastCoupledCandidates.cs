using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Unselected alternatives for ColonyForecast; the existing Method selector consumes these.</summary>
public sealed record ForecastCoupledCandidates(ForecastCoupledContext Context,
    EquatableArray<ForecastCommitment> Alternatives, EquatableArray<string> Findings, bool Truncated)
{
    public int CompositionExpansions { get; init; }
    /// <summary>Exactly observed current services eligible for retain/release comparison, not fresh orders.</summary>
    public EquatableArray<ForecastCommitment> Incumbents { get; init; }
}

public static partial class PolicyFunctions
{
    public static RoundsFacts ReviewCoupledActivities(RoundsFacts facts) => facts with { CoupledAlternatives = GenerateCoupledActivities(facts) };

    public static ForecastCoupledCandidates? GenerateCoupledActivities(RoundsFacts facts, double horizon = GameTime.TicksPerDay)
    {
        if (facts.ForecastObserved.Coupled is not { } read || facts.ForecastObserved.Needs is not { } needs
            || facts.WorkView is not { } resources) return null;
        if (read.Tick != facts.Tick.Value || needs.Tick != read.Tick || resources.Evidence.Tick.Value != read.Tick)
            return null; // A mixed frame cannot certify a consequence; caller retains absence explicitly.
        // A gate is keyed by its owner: a Concern id, or a migrated family's NeedId under its FilingKey (#3250).
        bool Enabled(ConcernId concern) => !facts.AvailableMethods.TryGetValue(out var available) || available.Contains(concern);
        bool NeedEnabled(NeedId need) => !facts.AvailableNeeds.TryGetValue(out var available) || available.Contains(need);
        if (!Enabled(EnsureWorkAssignments)) return null;
        read = read with
        {
            Food = read.Food with { Options = EquatableArray.From(read.Food.Options.Where(o =>
                !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Target))) },
            FoodConversions = EquatableArray.From(read.FoodConversions.Where(o => !resources.Claims.Pawns.Contains(o.Pawn)
                && !resources.Claims.Targets.Contains(o.Source) && !resources.Claims.Targets.Contains(o.Bench)
                && !resources.Claims.Targets.Contains(o.Bill)))
        };
        if (!NeedEnabled(FoodSupplyNeed) || OutdoorAcquisitionHeld(facts))
            read = read with { Food = read.Food with { Options = [] }, FoodConversions = [] };
        var shelterEnabled = Enabled(MaintainHousing) || Enabled(MaintainShelter) || Enabled(EnsureTemperatureSafety);
        if (!shelterEnabled)
            read = read with { Construction = read.Construction with { Options = [] },
                Thermal = read.Thermal is { } thermal ? thermal with { Cells = EquatableArray.From(thermal.Cells.Select(c => c with { BuildRoof = false })) } : null };
        var bindings = new Dictionary<(string, string), Methods.ActivityTaskCandidate>();
        var deadline = checked(read.Tick + (long)horizon);
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new Dictionary<(string, string, string), ForecastActivityRoute>();
        // Planned rooms are construction (#3276): the same gate as placed sites, from the round's planned-site facts.
        var plannedFindings = new List<string>();
        var plannedSites = shelterEnabled && facts.PlannedSites.TryGetValue(out var plannedFacts) ? plannedFacts.Sites : [];
        if (shelterEnabled && !facts.PlannedSites.IsKnown) plannedFindings.Add("planned_sites_unknown");
        var plannedAlternatives = PlannedRoomAlternatives(plannedSites);
        plannedFindings.AddRange(plannedAlternatives.Findings);
        var planned = Methods.Generation.PlannedRoomCandidates.Generate(plannedSites, plannedAlternatives.Alternatives, read.Thermal, resources,
            needs.Pawns.Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal), horizon);
        var lots = read.FoodSources.Select(o => new ForecastMaterialLot(o.Target, Location(o.Location), new(o.Resource), Known((double)o.Count)) { Perishable = true, FreshTicks = o.FreshTicks })
    .Concat(read.Construction.Options.Where(o => o.Input.Length != 0).Select(o => new ForecastMaterialLot(o.Input, Location(o.Origin), new(o.Resource), o.AvailableInput)))
    .Concat(read.Clothing.Options.SelectMany(o => o.Inputs).Select(i => new ForecastMaterialLot(i.Thing, Location(i.Location), new(i.Resource), Known((double)i.AvailableCount))))
    .Concat(read.Support.Deliveries.Select(d => new ForecastMaterialLot(d.Target, Location(d.Source), new(d.Resource), Known((double)d.Available))))
    .Concat(read.Support.Workers.Where(w => w.Medicine.Length != 0 && w.MedicineLocation is not null)
        .Select(w => new ForecastMaterialLot(w.Medicine, Location(w.MedicineLocation!.Value), new(w.MedicineResource), Known((double)w.MedicineAvailable))))
    .Concat(needs.Pawns.SelectMany(p => p.Targets).Where(t => t.Kind == "food" && t.Location is not null)
        .Select(t => new ForecastMaterialLot(t.Id, Location(t.Location!.Value), new(t.Resource), t.StockCount) { Perishable = true, FreshTicks = t.FreshTicks }))
    .Concat(planned.Lots)
    .GroupBy(l => (l.Lot, l.Location, l.Resource)).Select(g => g.Select(l => l.Count).Distinct().Count() == 1 ? g.First() : g.First() with { Count = default }).ToArray();
        var needTasks = new List<ForecastNeedTask>(); var alternatives = new List<ForecastCommitment>();
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        findings.UnionWith(plannedFindings); findings.UnionWith(planned.Findings);
        foreach (var pawn in needs.Pawns) foreach (var blocker in pawn.VoluntaryControlBlockers)
            findings.Add("unsupported_voluntary_state:" + pawn.Pawn + ":" + blocker);
        if (facts.CoupledWorkActive) findings.Add("protected_current_method_remaining_time_unassessed");
        var truncated = read.Truncated || planned.Truncated; var expanded = 0;
        string Location(Cell c) => "cell/" + c.X + "/" + c.Z;
        bool Space(int count)
        {
            if (++expanded > 256 || tasks.Count + count > 256) { truncated = true; return false; }
            return true;
        }
        void Route(string pawn, string from, string to, Fact<double> ticks)
            => routes.TryAdd((pawn, from, to), new(pawn, from, to, ticks));
        void Option(string id, string pawn, Fact<double> ticks, string? equipment = null)
            => options.Add(new(id, pawn, ticks, Known(0.0)) { Equipment = equipment is null ? [] : [equipment] });
        foreach (var route in read.Routes) Route(route.Pawn, route.FromStart ? "start" : Location(route.From), Location(route.To), route.Ticks);
        var food = Methods.Generation.FoodMethodCandidates.GenerateCoupled(read, needs, deadline);
        var construction = Methods.Generation.ConstructionMethodCandidates.Generate(read.Construction, deadline, resources);
        var shelter = Methods.Generation.CoupledShelterCandidates.Generate((shelterEnabled ? facts.PlannedShelters : []).OrderBy(r => r.Id).Select(r =>
            new ForecastShelterGeometry(new(ForecastShelterSource.Planned, r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), EquatableArray.From(r.Region.Cells()), r.Region.Bounds(), r.Door, r.Outdoor)),
            facts.Upkeep.Rooms, read.Thermal, construction.Operations, construction.Bindings, deadline, horizon);
        findings.UnionWith(shelter.Findings); truncated |= shelter.Truncated;
        foreach (var outcome in construction.Outcomes.Where(o => !construction.Bindings.Values.Any(b => b.Id == o.Completion)))
            findings.Add("construction_outcome_unbound:" + outcome.Id);
        var constructionRoots = construction.Outcomes.Where(o => construction.Bindings.Values.Any(b => b.Id == o.Completion))
            .Select(outcome => new ForecastCommitment(outcome.Id, [])
        {
            ActivityCompletions = [outcome.Completion], Outcome = outcome,
            ExclusiveOutcome = "construction/" + construction.Bindings.Values.First(b => b.Id == outcome.Completion).Target
        });
        var templates = food.Bindings.Concat(construction.Bindings).Concat(shelter.Bindings).ToDictionary(b => b.Key, b => b.Value);
        var shelterRoots = shelter.Roots.Keys.Select(id => new ForecastCommitment(id, []) { ExclusiveOutcome = id });
        var support = Methods.Generation.SupportMethodCandidates.Generate(read.Support, food.Operations.Concat(shelter.Operations).Concat(planned.Operations),
            food.Options.Concat(construction.Problem?.Options ?? []).Concat(shelter.Options).Concat(planned.Options), templates, resources, read.Tick, deadline);
        var composed = ComposeCoupledCandidates(support.Operations, food.Observed.Concat(construction.Observed).Concat(shelter.Observed).Concat(planned.Observed).Concat(support.Observed),
            food.Alternatives.Concat(constructionRoots).Concat(shelterRoots).Concat(planned.Roots).Concat(support.Roots), support.Options,
            support.Bindings, food.Needs, horizon, shelter.Roots);
        // The realizations of each planned site; nothing binds them until placement (#3278).
        var plannedRealized = EquatableArray.From(planned.Sites.Select(s => new ForecastPlannedSite(s.Site, s.Room, s.Stuff,
            EquatableArray.From(s.Operations.SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []).Distinct().Order(StringComparer.Ordinal)),
            EquatableArray.From((composed.Realizations.GetValueOrDefault(s.Finish) ?? []).Order(StringComparer.Ordinal)))));
        var unplaced = plannedRealized.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        tasks.AddRange(composed.Tasks); options.AddRange(composed.Options); needTasks.AddRange(composed.Needs);
        alternatives.AddRange(PlannedExclusions(composed.Alternatives, planned.Sites, planned.Rooms));
        foreach (var binding in composed.Bindings) bindings.Add(binding.Key, binding.Value);
        foreach (var route in food.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        foreach (var route in construction.Problem?.Routes ?? []) Route(route.Pawn, route.From, route.To, route.Ticks);
        foreach (var route in shelter.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        foreach (var route in planned.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        foreach (var route in support.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        findings.UnionWith(food.Findings); findings.UnionWith(construction.Findings); findings.UnionWith(composed.Findings); findings.UnionWith(support.Findings);
        truncated |= food.Truncated || construction.Truncated || composed.Truncated || support.Truncated;
        if (NeedEnabled(MilitaryFamily.Equipment) && facts.Gear.TryGetValue(out var gear))
        {
            AppendFamily(Methods.Generation.ClothingMethodCandidates.Generate(read.Clothing, ClothingActivityRequirements(gear).Alternatives,
                checked(read.Tick + (long)horizon), resources));
            // The effect-source family (#3285): one labor-free apparel-filter candidate per pawn a verified card prices (GearFilterChoices).
            var effects = GenerateEffectSources(EffectSourceFactsOf(gear, facts.MedicalPawns));
            alternatives.AddRange(effects.Roots); findings.UnionWith(effects.Findings); truncated |= effects.Truncated;
        }
        else findings.Add("unknown_clothing_requirements");
        var practice = Methods.Generation.PracticeMethodCandidates.Generate(facts, read.Practice, horizon, AssignedCombatCapabilities(facts.Ideology),
            facts.Hostiles.IsKnown && facts.Hostiles.Value == 0 && facts.CriticalPatients.IsKnown && facts.CriticalPatients.Value == 0
            && !needs.Pawns.SelectMany(p => p.Needs).Any(n => n.Kind is "food" or "rest" && n.Level.IsKnown && n.Level.Value <= 0));
        tasks.AddRange(practice.Tasks); options.AddRange(practice.Options); alternatives.AddRange(practice.Roots);
        foreach (var binding in practice.Bindings) bindings.Add(binding.Key, binding.Value);
        foreach (var route in practice.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        findings.UnionWith(practice.Findings);
        foreach (var task in practice.Practice.Where(p => p.Worker.JoyGainPerTick.IsKnown && p.Worker.JoyGainPerTick.Value > 0))
            needTasks.Add(new(task.Task, task.Worker.Pawn, "joy", task.Worker.Target, new(""), 0,
                Known(task.Worker.JoyGainPerTick.Value * Math.Max(0, task.Worker.DurationTicks.Value - task.Worker.PreparationTicks)), [])
                { ThoughtsComplete = false, ServiceDelay = task.Worker.PreparationTicks,
                    MaxPawns = needs.Pawns.SelectMany(p => p.Targets).FirstOrDefault(t => t.Id == task.Worker.Target)?.MaxPawns ?? default });
        foreach (var pawn in needs.Pawns)
        {
            var capacity = NeedsWorkCapacity(resources, pawn.Pawn, horizon);
            var generated = GenerateNeedsAlternatives(pawn with { Targets = EquatableArray.From(pawn.Targets.Where(t => !resources.Claims.Targets.Contains(t.Id))) },
                capacity, horizon, needs.Truncated, 32);
            findings.UnionWith(generated.Findings); truncated |= generated.Truncated;
            foreach (var choice in generated.Choices)
            {
                if (!Space(1)) break;
                var id = choice.Id;
                var target = pawn.Targets.Single(t => t.Id == choice.Target);
                var location = target.Location is { } cell ? Location(cell) : choice.Target;
                if (target.Location is null) findings.Add("unknown_need_target_location:" + target.Id);
                tasks.Add(choice.Activity with
                {
                    NotBefore = choice.Start,
                    Location = location,
                    EndLocation = location,
                    Inputs = choice.Kind == "food" ? [new(new(choice.Resource), Known((double)choice.Count)) { Lot = target.Id }] : []
                });
                Route(pawn.Pawn, "start", location, Known(choice.Start - choice.Depart));
                if (target.Location is { } physical)
                    bindings[(id, pawn.Pawn)] = new(id, pawn.Pawn, [], choice.Kind == "food" ? PawnActivityKind.Eat : choice.Kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation,
                        target.Id, "", "", choice.Count, deadline, physical)
                    { Resource = choice.Resource, Source = physical,
                        SelectedInputs = choice.Kind == "food"
                            ? [new(choice.Resource, target.Id, choice.Count, physical, "observed need target") { Observed = true }] : [] };
                Option(id, pawn.Pawn, Known(choice.End - choice.Start));
                needTasks.Add(new(id, pawn.Pawn, choice.Kind, choice.Target, new(choice.Resource), choice.Count, Known(choice.Gain), choice.Thoughts)
                { AvailableCount = target.StockCount, FreshTicks = target.FreshTicks, MaxPawns = target.MaxPawns });
                alternatives.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "need/" + pawn.Pawn + "/" + choice.Kind
                    + (choice.Kind == "food" ? "/use/" + Math.Round(choice.Start, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "") });
            }
        }
        var shelters = shelter.Plans.Select(plan => plan with
        {
            AlternativePrerequisites = EquatableArray.From(plan.AlternativePrerequisites.Select(group => EquatableArray.From(
                group.SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []).Distinct().Order(StringComparer.Ordinal))))
        }).ToArray();
        // Recovery can unlock productive work which the earliest-only schedule would otherwise try too soon.
        // Propose the dependency, rather than hoping weights invent a missing complementary combination.
        // Planned work has no action template to delay (#3278).
        var productiveRoots = alternatives.Where(c => c.ActivityCompletions.Count == 1 && !unplaced.Contains(c.ActivityCompletions[0])
            && needTasks.All(n => n.Task != c.ActivityCompletions[0])
            && practice.Practice.All(p => p.Task != c.ActivityCompletions[0])).ToArray();
        var delayed = 0; var delayedTasks = new HashSet<string>(StringComparer.Ordinal); var delayedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in productiveRoots)
            foreach (var recovery in needTasks.ToArray())
            {
                if (delayed >= 64) { truncated = true; break; }
                var original = tasks.Single(t => t.Id == root.ActivityCompletions[0]);
                if (!options.Any(o => o.Activity == original.Id && o.Pawn == recovery.Pawn)
                    || Ancestor(recovery.Task, original.Id)) continue;
                var id = original.Id + "/after/" + recovery.Task;
                // Distinct roots can share one completion task: build the delayed task once, and keep each root's own alternative.
                var first = delayedTasks.Add(id);
                if (first)
                {
                    tasks.Add(original with { Id = id, Prerequisites = [.. original.Prerequisites, recovery.Task] });
                    foreach (var option in options.Where(o => o.Activity == original.Id).ToArray())
                    {
                        options.Add(option with { Activity = id });
                        if (bindings.TryGetValue((original.Id, option.Pawn), out var binding))
                            bindings[(id, option.Pawn)] = binding with { Id = id, Prerequisites = EquatableArray.From(original.Prerequisites.Append(recovery.Task)) };
                    }
                }
                var alternativeId = id;
                for (var attempt = 0; !delayedRoots.Add(alternativeId); attempt++)
                    alternativeId = root.Id + "/after/" + recovery.Task + (attempt == 0 ? "" : "/" + attempt);
                alternatives.Add(root with { Id = alternativeId, ActivityCompletions = [id] }); delayed++;
            }
        bool Ancestor(string task, string ancestor)
        {
            if (task == ancestor) return true;
            return tasks.Single(t => t.Id == task).Prerequisites.Any(p => Ancestor(p, ancestor));
        }
        var equipment = options.SelectMany(o => o.Equipment).Distinct().Order(StringComparer.Ordinal)
            .Select(id => new ForecastEquipmentCapacity(id, tasks.Single(t => t.Id == options.First(o => o.Equipment.Contains(id)).Activity) is var equipmentTask && equipmentTask.EquipmentLocation.Length != 0 ? equipmentTask.EquipmentLocation : equipmentTask.Location, resources.Claims.Targets.Contains(id)
                ? Known<EquatableArray<ForecastTimeWindow>>([]) : Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)]))).ToArray();
        // A task reached by two generation paths is one task; two different tasks under one id cannot be scheduled.
        var distinctTasks = new List<ForecastActivity>();
        foreach (var group in tasks.GroupBy(t => t.Id, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Skip(1).Any(other => !other.Equals(first)))
                throw new DomainException("coupled generation produced different activities under one id: " + group.Key);
            distinctTasks.Add(first);
        }
        if (truncated) findings.Add("coupled_generation_truncated");
        return new(new()
        {
            Activities = new()
            {
                Activities = distinctTasks.ToArray(),
                Options = options.Distinct().ToArray(),
                Routes = routes.Values.ToArray(),
                Pawns = needs.Pawns.Select(p => p.VoluntaryControlBlockers.Count == 0
                    ? NeedsWorkCapacity(resources, p.Pawn, horizon).Capacity
                    : new ForecastPawnCapacity(p.Pawn, "start", Known<EquatableArray<ForecastTimeWindow>>([]), Known(1d))).ToArray(),
                Equipment = equipment,
                Deadline = Known(horizon),
                AlternativeLaborCost = Known(horizon * Math.Max(1, needs.Pawns.Count)),
                ExpansionBudget = 4096
            },
            Truncated = truncated,
            Needs = needs,
            Resources = resources,
            Construction = read.Construction,
            MaintenanceRequirements = practice.Requirements, PracticeTasks = practice.Practice,
            Support = read.Support,
            Thermal = read.Thermal,
            Shelters = EquatableArray.From(shelters),
            PlannedSites = plannedRealized,
            PlannedRooms = EquatableArray.From(planned.Rooms),
            InitialLots = EquatableArray.From(lots),
            Bindings = bindings,
            NeedTasks = EquatableArray.From(needTasks),
            Findings = EquatableArray.From(findings)
        },
            EquatableArray.From(DistinctAlternatives(alternatives)), EquatableArray.From(findings), truncated)
        { CompositionExpansions = composed.Expansions };

        // Ring sites in one stuff exclude the room's ring sites and alternatives in every other stuff (#3276). Composition
        // renames each root to its realizations, so the exclusion names those, not the generated root ids.
        static IEnumerable<ForecastCommitment> PlannedExclusions(IEnumerable<ForecastCommitment> roots,
            IEnumerable<Methods.Generation.PlannedSiteOperations> sites, IEnumerable<PlannedRoomAlternative> rooms)
        {
            var rows = roots.ToArray();
            var stuffOf = sites.Where(s => s.Stuff.Length != 0).Select(s => (Id: s.Site, s.Room, s.Stuff))
                .Concat(rooms.Where(a => a.Stuff.Length != 0).Select(a => (a.Id, a.Room, a.Stuff)))
                .ToDictionary(s => s.Id, s => (s.Room, s.Stuff), StringComparer.Ordinal);
            static string Base(string id) => id.IndexOf("/composition/", StringComparison.Ordinal) is var i and >= 0 ? id[..i] : id;
            var realized = rows.Where(c => stuffOf.ContainsKey(Base(c.Id))).Select(c => (c.Id, Of: stuffOf[Base(c.Id)])).ToArray();
            return rows.Select(c => c.Outcome is { } outcome && stuffOf.TryGetValue(Base(c.Id), out var own)
                ? c with { Outcome = outcome with { Excludes = EquatableArray.From(outcome.Excludes.Concat(realized
                    .Where(r => r.Of.Room == own.Room && r.Of.Stuff != own.Stuff).Select(r => r.Id)).Distinct().Order(StringComparer.Ordinal)) } }
                : c);
        }

        // The same root reached by two generation paths is one alternative; different roots cannot share an id.
        static List<ForecastCommitment> DistinctAlternatives(List<ForecastCommitment> roots)
        {
            var distinct = new List<ForecastCommitment>();
            foreach (var group in roots.GroupBy(r => r.Id, StringComparer.Ordinal))
            {
                var first = group.First();
                if (group.Skip(1).Any(other => !other.Equals(first)))
                    throw new DomainException("coupled generation produced different alternatives under one id: " + group.Key);
                distinct.Add(first);
            }
            return distinct;
        }

        void AppendFamily(Methods.CoordinatedMethodCandidates family)
        {
            findings.UnionWith(family.Findings); truncated |= family.Truncated;
            if (family.Problem is null) return;
            foreach (var task in family.Problem.Activities) tasks.Add(task);
            options.AddRange(family.Problem.Options);
            foreach (var binding in family.Bindings) bindings.Add(binding.Key, binding.Value);
            foreach (var route in family.Problem.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
            foreach (var outcome in family.Outcomes)
            {
                var exclusive = outcome.Need;
                if (outcome.Need == "construction-method")
                {
                    // A site whose finish work has no known pawn option has no binding to execute, so it is not an alternative.
                    var finish = family.Bindings.Values.FirstOrDefault(b => b.Id == outcome.Completion);
                    if (finish is null) { findings.Add("construction_outcome_unbound:" + outcome.Id); continue; }
                    exclusive = "construction/" + finish.Target;
                }
                alternatives.Add(new(outcome.Id, [])
                {
                    ActivityCompletions = outcome.Completion.Length == 0 ? [] : [outcome.Completion],
                    ExclusiveOutcome = exclusive,
                    Outcome = outcome
                });
            }
        }
    }
}
