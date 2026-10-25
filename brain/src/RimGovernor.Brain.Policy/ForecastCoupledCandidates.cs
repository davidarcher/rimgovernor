using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Unselected alternatives for ColonyForecast; the existing Method selector consumes these.</summary>
public sealed partial record ForecastCoupledCandidates(ForecastCoupledContext Context,
    EquatableArray<ForecastCommitment> Alternatives, EquatableArray<string> Findings, bool Truncated)
{
    public int CompositionExpansions { get; init; }
    /// <summary>Exactly observed current services eligible for retain/release comparison, not fresh orders.</summary>
    public EquatableArray<ForecastCommitment> Incumbents { get; init; }
}

public sealed partial record ForecastCoupledCandidates
{
    private static readonly System.Diagnostics.ActivitySource Spans = new("github.com/davidarcher/RimGovernor/policy");

    /// <summary>
    /// Per planned room, its role: the planned sites' (#3651), and a planned shelter's for a room that owes no planned site
    /// (a placed shell, #3537).
    /// </summary>
    private static Dictionary<int, PlannedRole> PlannedRoomRolesOf(ReviewFacts facts)
    {
        var roles = facts.PlannedSites is { } planned ? planned.Roles.ToDictionary(r => r.Room, r => r.Role) : new Dictionary<int, PlannedRole>();
        foreach (var room in facts.PlannedShelters) roles.TryAdd(room.Id, room.Role);
        return roles;
    }

    /// <summary>The layout plan's rooms by id (#3800): the planned shelters, a room id once.</summary>
    internal static Dictionary<int, PlannedRoom> LayoutRoomsOf(ReviewFacts facts) =>
        facts.PlannedShelters.Where(r => r.Id > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());

    public static ReviewFacts ReviewCoupledActivities(ReviewFacts facts) => facts with { CoupledAlternatives = GenerateCoupledActivities(facts) };

    public static ForecastCoupledCandidates? GenerateCoupledActivities(ReviewFacts facts, double horizon = GameTime.TicksPerDay)
    {
        using var span = Spans.StartActivity("coupled.generate");
        if (facts.ForecastObserved.Coupled is not { } read || facts.ForecastObserved.Needs is not { } needs
            || facts.WorkView is not { } resources) return null;
        if (read.Tick != facts.Tick.Value || needs.Tick.Value != read.Tick || resources.Evidence.Tick.Value != read.Tick)
            return null; // A mixed frame cannot certify a consequence; caller retains absence explicitly.
        // A gate is keyed by its owner, a migrated family's NeedId (#3250).
        bool NeedEnabled(NeedId need) => facts.AvailableNeeds.Contains(need);
        if (!NeedEnabled(AdminFamily.WorkAssignments)) return null;
        read = read with
        {
            Food = read.Food with { Options = EquatableArray.From(read.Food.Options.Where(o =>
                !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Target))) },
            FoodConversions = EquatableArray.From(read.FoodConversions.Where(o => !resources.Claims.Pawns.Contains(o.Pawn)
                && !resources.Claims.Targets.Contains(o.Source) && !resources.Claims.Targets.Contains(o.Bench)
                && !resources.Claims.Targets.Contains(o.Bill)))
        };
        if (!NeedEnabled(FoodRules.FoodSupplyNeed) || OutdoorHazardRules.OutdoorAcquisitionHeld(facts))
            read = read with { Food = read.Food with { Options = [] }, FoodConversions = [] };
        // An unplaced planned shelter's cells and planned heater come from the round's planned thermal read (#3323).
        read = read with { Thermal = PlannedRules.WithPlannedThermal(read.Thermal, facts.PlannedThermal) };
        var shelterEnabled = NeedEnabled(ShelterFamily.Sleeping)
            || NeedEnabled(ShelterFamily.SafeArea) || NeedEnabled(ShelterFamily.Temperature);
        if (!shelterEnabled)
            read = read with { Construction = read.Construction with { Options = [] },
                Thermal = read.Thermal with { Cells = EquatableArray.From(read.Thermal.Cells.Select(c => c with { BuildRoof = false })) } };
        // An urgent build's review (#3537) compares its placed sites alone of the construction: the planned sites, every other
        // placed site and the standing removals are routine, vanilla's. Its shelters are the planned rooms it rings, and all
        // of its sites are compared, so a shell's whole ring can close.
        var urgent = facts.UrgentSites.Select(b => (b.Definition, b.Cell)).ToHashSet();
        if (urgent.Count != 0)
            read = read with { Construction = read.Construction with { Removals = [],
                Options = EquatableArray.From(read.Construction.Options.Where(o => urgent.Contains((o.Definition, o.Location)))) } };
        var bindings = new Dictionary<(string, string), Methods.ActivityTaskCandidate>();
        var deadline = checked(read.Tick + (long)horizon);
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new Dictionary<(string, string, string), ForecastActivityRoute>();
        // Planned rooms are construction (#3276): the same gate as placed sites, from the round's planned-site facts.
        var plannedFindings = new List<string>();
        var plannedFacts = shelterEnabled ? facts.PlannedSites : null;
        var allPlannedSites = plannedFacts is null ? [] : plannedFacts.Sites;
        var plannedSites = urgent.Count == 0 ? allPlannedSites : [];
        if (shelterEnabled && plannedFacts is null) plannedFindings.Add("planned_sites_unknown");
        var plannedAlternatives = PlannedRules.PlannedRoomAlternatives(plannedSites);
        plannedFindings.AddRange(plannedAlternatives.Findings);
        var planned = Methods.Generation.PlannedRoomCandidates.Generate(plannedSites, plannedAlternatives.Alternatives,
            plannedFacts is null ? [] : plannedFacts.Routes, read.Thermal, resources,
            needs.Pawns.Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal), horizon, deadline);
        var prerequisiteSites = EquatableArray.From(facts.RoomPrerequisites.Values.SelectMany(s => s).DistinctBy(s => s.Id));
        var prerequisiteWork = Methods.Generation.PlannedRoomCandidates.Generate(prerequisiteSites,
            PlannedRules.PlannedRoomAlternatives(prerequisiteSites).Alternatives, [], read.Thermal, resources,
            needs.Pawns.Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal), horizon, deadline, schedulable: false);
        ForecastMaterialLot Lot(string lot, string location, string resource, double count) => new(lot, location, new(resource), count);

        var lotGroups = read.FoodSources.Select(o => Lot(o.Target, Location(o.Location), o.Resource, o.Count) with { Freshness = o.FreshTicks })
    .Concat(read.Construction.Options.Where(o => o.Input.Length != 0).Select(o => Lot(o.Input, Location(o.Origin), o.Resource, o.AvailableInput)))
    .Concat(read.Clothing.Options.SelectMany(o => o.Inputs).Select(i => Lot(i.Thing, Location(i.Location), i.Resource, i.AvailableCount)))
    .Concat(read.Support.Deliveries.Select(d => Lot(d.Target, Location(d.Source), d.Resource, d.Available)))
    .Concat(read.Support.Workers.Select(w => w.Medicine).OfType<SupportMedicine>()
        .Select(m => Lot(m.Id, Location(m.Location), m.Resource, m.Available)))
    .Concat(needs.Pawns.SelectMany(p => p.Targets).Where(t => t.Kind == "food")
        .Select(t => t.Stock is NeedStock.Stacked stacked
            ? Lot(t.Id, Location(t.Location), t.Resource, stacked.Count) with { Freshness = stacked.Freshness }
            : null).OfType<ForecastMaterialLot>())
    .GroupBy(l => (l.Lot, l.Location, l.Resource)).ToList();
        // Sources that count one lot differently leave it out: no schedule draws from a lot whose count is not read, and the
        // finding names it.
        var conflicting = lotGroups.Where(g => g.Select(l => l.Count).Distinct().Count() != 1).Select(g => g.Key.Lot).ToArray();
        var lots = lotGroups.Where(g => g.Select(l => l.Count).Distinct().Count() == 1).Select(g => g.First()).ToList();
        // The frame is the forecast's start state (#3881): the planned-site read comes from a later native call, so a stack both
        // name can have moved in between. The frame's count wins; planned lots add only the stacks the frame does not name.
        var framed = lotGroups.Select(g => g.Key).ToHashSet();
        lots.AddRange(planned.Lots.Where(l => framed.Add((l.Lot, l.Location, l.Resource))));
        var needTasks = new List<ForecastNeedTask>(); var alternatives = new List<ForecastCommitment>(facts.StockArrival.Requests.Select(r => r.Commitment()).Concat(facts.StockArrival.Missions.Select(m => m.Commitment())));
        var findings = new SortedSet<string>(plannedFindings, StringComparer.Ordinal); findings.UnionWith(planned.Findings);
        foreach (var lot in conflicting) findings.Add("lot_count_conflict:" + lot);
        foreach (var pawn in needs.Pawns) foreach (var blocker in pawn.VoluntaryControlBlockers)
            findings.Add("unsupported_voluntary_state:" + pawn.Pawn + ":" + blocker);
        if (facts.CoupledWorkActive) findings.Add("protected_current_method_remaining_time_unassessed");
        var truncated = planned.Truncated; var expanded = 0;
        string Location(Cell c) => "cell/" + c.X + "/" + c.Z;
        bool Space(int count)
        {
            if (++expanded > 256 || tasks.Count + count > 256) { truncated = true; return false; }
            return true;
        }
        void Route(string pawn, string from, string to, RouteTicks ticks)
            => routes.TryAdd((pawn, from, to), new(pawn, from, to, ticks));
        void Option(string id, string pawn, double ticks, string? equipment = null)
            => options.Add(new(id, pawn, ticks, 0) { Equipment = equipment is null ? [] : [equipment] });
        foreach (var route in read.Routes) Route(route.Pawn, route.FromStart ? "start" : Location(route.From), Location(route.To), route.Ticks);
        var food = Methods.Generation.FoodMethodCandidates.GenerateCoupled(read, needs, deadline);
        var construction = Methods.Generation.ConstructionMethodCandidates.Generate(read.Construction, deadline, resources,
            FlooringRules.DefaultFlooringPolicy().Floors.ToHashSet(StringComparer.Ordinal),
            urgent.Count == 0 ? Methods.Generation.ConstructionMethodCandidates.SiteBound : int.MaxValue);
        var shelterRooms = (shelterEnabled ? facts.PlannedShelters : [])
            .Where(r => urgent.Count == 0 || PlannedRules.RectCells(PlannedRoom.RoomWalls(r)).Any(c => urgent.Any(u => u.Cell == c)));
        var physicalRooms = urgent.Count != 0
            ? facts.Upkeep.Rooms with { Rooms = [] } : facts.Upkeep.Rooms;
        var shelter = Methods.Generation.CoupledShelterCandidates.Generate(shelterRooms.OrderBy(r => r.Id).Select(r =>
            new ForecastShelterGeometry(new(ForecastShelterSource.Planned, r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), EquatableArray.From(r.Region.Cells()), r.Region.Bounds(), r.Door, r.Outdoor) { Unbounded = r.Role == PlannedRole.Freezer }),
            physicalRooms, read.Thermal, construction.Operations, construction.Bindings, plannedSites, planned.Sites, deadline, horizon);
        findings.UnionWith(shelter.Findings); truncated |= shelter.Truncated;
        foreach (var outcome in construction.Outcomes.Where(o => !construction.Bindings.Values.Any(b => b.Id == o.Completion)))
            findings.Add("construction_outcome_unbound:" + outcome.Id);
        var constructionRoots = construction.Outcomes.Where(o => construction.Bindings.Values.Any(b => b.Id == o.Completion))
            .Select(outcome => new ForecastCommitment(outcome.Id, [])
        {
            ActivityCompletions = [outcome.Completion], Outcome = outcome,
            ExclusiveOutcome = ForecastCoupledSelection.ConstructionOutcome + construction.Bindings.Values.First(b => b.Id == outcome.Completion).Target
        });
        var templates = food.Bindings.Concat(construction.Bindings).Concat(shelter.Bindings).Concat(planned.Bindings).ToDictionary(b => b.Key, b => b.Value);
        var shelterRoots = shelter.Roots.Keys.Select(id => new ForecastCommitment(id, []) { ExclusiveOutcome = id });
        var support = Methods.Generation.SupportMethodCandidates.Generate(read.Support, food.Operations.Concat(shelter.Operations).Concat(planned.Operations),
            food.Options.Concat(construction.Problem?.Options ?? []).Concat(shelter.Options).Concat(planned.Options), templates, resources, read.Tick, deadline);
        var composed = ForecastComposedCandidates.ComposeCoupledCandidates(support.Operations, food.Observed.Concat(construction.Observed).Concat(shelter.Observed).Concat(planned.Observed).Concat(support.Observed),
            food.Alternatives.Concat(constructionRoots).Concat(shelterRoots).Concat(planned.Roots).Concat(support.Roots), support.Options,
            support.Bindings, food.Needs, horizon, shelter.Roots);
        // The realizations of each planned site; nothing binds them until placement (#3278), except a Deconstruct clearing,
        // whose building stands now: a bound realization is startable work, not an unplaced activity (#3557).
        var bound = composed.Bindings.Keys.Select(k => k.Task).ToHashSet(StringComparer.Ordinal);
        var plannedRealized = EquatableArray.From(planned.Sites.Select(s => new ForecastPlannedSite(s.Site, s.Room, s.Stuff,
            EquatableArray.From(s.Operations.SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []).Where(id => !bound.Contains(id))
                .Distinct().Order(StringComparer.Ordinal)),
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
        // The worn outfit per pawn with a loadout model: the same options the effect-source facts carry (#3546).
        var gear = facts.Gear;
        var outfits = gear.Pawns.Where(p => p.Model is not null).GroupBy(p => p.Pawn.Value, StringComparer.Ordinal)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Model!.Worn, StringComparer.Ordinal);
        if (NeedEnabled(MilitaryFamily.Equipment))
        {
            // The effect-source family (#3285): labor-free (tainted filter, insulation floor) candidates per pawn that verified cards price (GearChoices, #3329).
            var effects = EffectSourcesRules.GenerateEffectSources(EffectSourcesRules.EffectSourceFactsOf(gear, facts.MedicalPawns, facts.Ideology is IdeologyMode.Installed));
            AppendFamily(Methods.Generation.ClothingMethodCandidates.Generate(read.Clothing, ClothingActivitiesRules.ClothingActivityRequirements(gear),
                checked(read.Tick + (long)horizon), resources), effects.Roots);
            alternatives.AddRange(effects.Roots); findings.UnionWith(effects.Findings); truncated |= effects.Truncated;
        }
        var practice = Methods.Generation.PracticeMethodCandidates.Generate(facts, read.Practice, horizon, CapabilityMaintenanceRules.AssignedCombatCapabilities(facts.Ideology),
            facts.Hostiles is HostileCensus.Quiet && facts.CriticalPatients == 0
            && !needs.Pawns.SelectMany(p => p.Needs).Any(n => n.Kind is "food" or "rest" && n.Level <= 0));
        tasks.AddRange(practice.Tasks); options.AddRange(practice.Options); alternatives.AddRange(practice.Roots);
        foreach (var binding in practice.Bindings) bindings.Add(binding.Key, binding.Value);
        foreach (var route in practice.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        findings.UnionWith(practice.Findings);
        // The research Concern owns which project is current; a disabled Concern gets no research block (#3527).
        if (NeedEnabled(EconomyFamily.Research))
        {
            var research = Methods.Generation.ResearchMethodCandidates.Generate(read.Research, horizon, resources.Claims, deadline);
            tasks.AddRange(research.Tasks); options.AddRange(research.Options); alternatives.AddRange(research.Roots);
            foreach (var binding in research.Bindings) bindings.Add(binding.Key, binding.Value);
            findings.UnionWith(research.Findings); truncated |= research.Truncated;
        }
        var sowing = Methods.Generation.SowingMethodCandidates.Generate(read.Sowing, read.Tick, horizon, resources.Claims);
        tasks.AddRange(sowing.Tasks); options.AddRange(sowing.Options); alternatives.AddRange(sowing.Roots);
        foreach (var binding in sowing.Bindings) bindings.Add(binding.Key, binding.Value);
        foreach (var route in sowing.Routes) Route(route.Pawn, route.From, route.To, route.Ticks);
        findings.UnionWith(sowing.Findings);
        // A spar is recreation: its joy at the native rate over the fight, and the memory a completed bout gives (#3858).
        foreach (var task in practice.Practice.Where(p => p.Worker.JoyGainPerTick > 0))
            needTasks.Add(new(task.Task, task.Worker.Pawn, "joy", task.Worker.Target, new(""), 0,
                task.Worker.JoyGainPerTick * Math.Max(0, task.Worker.DurationTicks - task.Worker.PreparationTicks), task.Worker.Thoughts)
                { ServiceDelay = task.Worker.PreparationTicks,
                    MaxPawns = needs.Pawns.SelectMany(p => p.Targets).FirstOrDefault(t => t.Id == task.Worker.Target)?.MaxPawns ?? 1 });
        foreach (var pawn in needs.Pawns)
        {
            var capacity = ForecastNeeds.NeedsWorkCapacity(resources, pawn.Pawn, horizon);
            // A claimed bed or pin is whole; a claimed food stack keeps the units and slots its owners leave (#3194).
            var targets = pawn.Targets.Select(t => WorkRules.ProposableNeedTarget(resources.Claims, t)).OfType<NeedTarget>().ToArray();
            var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn with { Targets = EquatableArray.From(targets) }, capacity, horizon, 32);
            findings.UnionWith(generated.Findings); truncated |= generated.Truncated;
            foreach (var choice in generated.Choices)
            {
                if (!Space(1)) break;
                var id = choice.Id;
                var target = targets.Single(t => t.Id == choice.Target);
                var location = Location(target.Location);
                tasks.Add(choice.Activity with
                {
                    NotBefore = choice.Start,
                    Location = location,
                    EndLocation = location,
                    Inputs = choice.Kind == "food" ? [new(new(choice.Resource), choice.Count) { Lot = target.Id }] : []
                });
                Route(pawn.Pawn, "start", location, new RouteTicks.Reachable(choice.Start - choice.Depart));
                if (target.Location is { } physical)
                    bindings[(id, pawn.Pawn)] = new(id, pawn.Pawn, [], choice.Kind == "food" ? PawnActivityKind.Eat : choice.Kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation,
                        target.Id, "", "", choice.Count, deadline, physical)
                    { Resource = choice.Resource, Source = physical,
                        SelectedInputs = choice.Kind == "food"
                            ? [new(choice.Resource, target.Id, choice.Count, physical, "observed need target") { Observed = true }] : [] };
                Option(id, pawn.Pawn, choice.End - choice.Start);
                needTasks.Add(new(id, pawn.Pawn, choice.Kind, choice.Target, new(choice.Resource), choice.Count, choice.Gain, choice.Thoughts)
                { Quantity = TargetQuantity.Of(target.Stock), MaxPawns = target.MaxPawns });
                alternatives.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "need/" + pawn.Pawn + "/" + choice.Kind
                    + (choice.Kind == "food" ? "/use/" + Math.Round(choice.Start, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "") });
            }
        }
        EquatableArray<EquatableArray<string>> Realized(EquatableArray<EquatableArray<string>> groups) => EquatableArray.From(groups.Select(group =>
            EquatableArray.From(group.SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []).Distinct().Order(StringComparer.Ordinal))));
        var shelters = shelter.Plans.Select(plan => plan with
        {
            AlternativePrerequisites = Realized(plan.AlternativePrerequisites), RoofCells = Realized(plan.RoofCells),
        }).ToArray();
        // Recovery can unlock productive work which the earliest-only schedule would otherwise try too soon.
        // Propose the dependency, rather than hoping weights invent a missing complementary combination.
        // Planned work has no action template to delay (#3278).
        var productiveRoots = alternatives.Where(c => c.ActivityCompletions.Count == 1 && !unplaced.Contains(c.ActivityCompletions[0])
            && needTasks.All(n => n.Task != c.ActivityCompletions[0])
            && practice.Practice.All(p => p.Task != c.ActivityCompletions[0])
            && sowing.Roots.All(r => r.Id != c.Id)).ToArray(); // Unvalued sowing would only spend the delayed-variant budget.
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
                    // Only the recovering pawn follows its own recovery: another pawn's work is not gated behind it (#3047). A plant
                    // cut gated behind another colonist's meal a day ahead held both colonists for the day (#3649).
                    foreach (var option in options.Where(o => o.Activity == original.Id && o.Pawn == recovery.Pawn).ToArray())
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
        // A bench in an unclosed planned room works faster once the room closes (#3339): closure-conditional variants.
        // A placed ring site's finishes are its FinishFrame tasks, any one of them (#3537).
        var placedFinishes = bindings.Values.Where(b => b.Kind == PawnActivityKind.Work && b.Job == "FinishFrame").GroupBy(b => b.Target, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => EquatableArray.From(g.Select(b => b.Id).Distinct().Order(StringComparer.Ordinal)), StringComparer.Ordinal);
        // The owed work (#3744, #3796): a planned site's, the least work of each of its work groups' options, summed; a room
        // item's, the least work of its finishes. A site with a group of no option with work has no row.
        double LeastWork(IEnumerable<string> activities)
        {
            var of = activities.ToHashSet(StringComparer.Ordinal);
            var least = double.PositiveInfinity;
            foreach (var (_, ticks) in Worked(options.Where(o => of.Contains(o.Activity))))
                least = Math.Min(least, ticks);
            return least;
        }
        var owedWork = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var site in planned.Sites.Where(s => s.WorkGroups.Length > 0))
        {
            var groups = site.WorkGroups.Select(g => LeastWork(g.SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []))).ToArray();
            if (groups.All(double.IsFinite)) owedWork[site.Site] = groups.Sum();
        }
        foreach (var site in prerequisiteWork.Sites.Where(s => s.WorkGroups.Length > 0))
        {
            var groups = site.WorkGroups.Select(g => Worked(prerequisiteWork.Options.Where(o => g.Contains(o.Activity)))
                .Select(w => w.Ticks).DefaultIfEmpty(double.PositiveInfinity).Min()).ToArray();
            if (groups.All(double.IsFinite)) owedWork[site.Site] = groups.Sum();
        }
        // The room items (#3796, epic #3650 P4): each generated placed site's finish and standing removal, by its site or
        // designation id, owed by the planned rooms whose interior or ring cells hold its cell or set cells.
        var footprints = (shelterEnabled ? facts.PlannedShelters : []).Where(r => r.Id > 0).GroupBy(r => r.Id)
            .Select(g => (Room: g.Key, Cells: PlannedRules.RoomFootprint(g.First()))).ToArray();
        var roomItems = construction.Bindings.Values.Where(b => b.Kind != PawnActivityKind.Supply).GroupBy(b => b.Target, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new ForecastRoomItem(g.Key,
                EquatableArray.From(footprints.Where(f => g.Any(b => f.Cells.Contains(b.Location) || b.Cells.Any(f.Cells.Contains))).Select(f => f.Room)),
                EquatableArray.From(g.Select(b => b.Id).Distinct().SelectMany(id => composed.Realizations.GetValueOrDefault(id) ?? []).Distinct().Order(StringComparer.Ordinal))))
            .Where(i => i.Rooms.Count != 0).ToArray();
        foreach (var item in roomItems)
            if (LeastWork(item.Finishes) is var work && double.IsFinite(work)) owedWork[item.Item] = work;
        foreach (var (site, finishes) in placedFinishes)
            if (!owedWork.ContainsKey(site) && LeastWork(finishes) is var work && double.IsFinite(work)) owedWork[site] = work;
        var builders = LayoutClusterRules.BuildersOf(facts.ForecastObserved.WorkPawns,
            pawn => needs.Pawns.Any(p => p.Pawn == pawn) && !resources.Claims.Pawns.Contains(pawn));
        var closures = shelterEnabled ? ForecastRoofConsequences.PlannedClosuresOf(allPlannedSites, facts.PlannedShelters, facts.PlannedRoomPredictions,
            facts.CurrentConstruction.Sites, placedFinishes) : [];
        ForecastRoofConsequences.AddClosedBenchVariants(closures, productiveRoots, plannedRealized, tasks, options, bindings, alternatives,
            planned.Sites.Where(s => s.Stuff.Length != 0).Select(s => (Id: s.Site, s.Room, s.Stuff))
                .Concat(planned.Rooms.Where(a => a.Stuff.Length != 0).Select(a => (a.Id, a.Room, a.Stuff)))
                .GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (g.First().Room, g.First().Stuff), StringComparer.Ordinal),
            PlannedRoomRolesOf(facts).Where(r => r.Value == PlannedRole.Workshop).Select(r => r.Key).ToHashSet());
        var equipment = options.SelectMany(o => o.Equipment).Distinct().Order(StringComparer.Ordinal)
            .Select(id => new ForecastEquipmentCapacity(id, tasks.Single(t => t.Id == options.First(o => o.Equipment.Contains(id)).Activity) is var equipmentTask && equipmentTask.EquipmentLocation.Length != 0 ? equipmentTask.EquipmentLocation : equipmentTask.Location, resources.Claims.Targets.Contains(id)
                ? [] : EquatableArray.From(new ForecastTimeWindow[] { new(0, horizon) }))).ToArray();
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
                Pawns = needs.Pawns.Select(p => ForecastNeeds.NeedsWorkCapacity(resources, p.Pawn, horizon).Capacity).ToArray(),
                Equipment = equipment,
                Deadline = horizon,
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
            PlannedRoomRoles = PlannedRoomRolesOf(facts),
            PrisonerPopulation = facts.Population,
            StoreStock = facts.PlannedStoreStock,
            Connectors = PlannedRoomCredits.ConnectorsOf(facts.PlannedShelters, facts.PlannedLinks),
            RoomPrerequisites = facts.RoomPrerequisites,
            FreezerStock = facts.PlannedShelters.Any(r => r.Role == PlannedRole.Freezer) ? facts.CombinedFoodSupply.Stocks.Concat(facts.CombinedFoodSupply.Barred).ToArray() : [],
            RotRates = facts.RotRates,
            Closures = closures,
            PlannedBuilders = builders,
            OwedWork = owedWork,
            OptionWork = Worked(options).GroupBy(w => (w.Option.Activity, w.Option.Pawn)).ToDictionary(g => g.Key, g => g.Min(w => w.Ticks)),
            LayoutRooms = LayoutRoomsOf(facts),
            Filth = facts.Filth,
            ResearchBenchSpeeds = facts.ResearchBenchSpeeds,
            WorkBenchSpeeds = facts.WorkBenchSpeeds,
            ProductionOrders = facts.ProductionOrders,
            ProductionRecipes = facts.ProductionRecipes,
            LaboratoryBench = facts.LaboratoryBench,
            PlannedBeds = ForecastHousing.PlannedBedsOf(allPlannedSites, facts.SleepingBeds),
            NurserySites = EquatableArray.From(allPlannedSites.Where(s => s.Op == OpKind.Build && facts.PlannedShelters.Any(r => r.Id == s.Room && r.Role == PlannedRole.Nursery))),
            ComfortCurve = facts.ComfortCurve,
            HospitalBed = facts.HospitalBed,
            Stock = facts.StockArrival,
            Worship = facts.Worship,
            PenHerds = facts.PlannedPens,
            Roamers = facts.AnimalUpkeep.Roamers,
            Sparring = practice.Sparring, Range = practice.Range,
            InitialLots = EquatableArray.From(lots),
            FoodUses = read.FoodUses,
            Bindings = bindings,
            CapacityPenalties = facts.CapacityPenalties,
            Value = ForecastValuePolicy.For(facts.Preferences),
            Harm = HarmInputs.Of(facts),
            Outfits = outfits,
            ApparelShifts = ForecastApparel.BreakShifts(needs, outfits, alternatives),
            GoalValues = facts.GoalValues,
            CookedNutritionRatio = ForecastValue.CookedNutritionRatios(read.FoodConversions, facts.Items.Nutrition, findings),
            MoodBreaks = facts.MoodBreak,
            NeedTasks = EquatableArray.From(needTasks),
            Findings = EquatableArray.From(findings),
            // Temperature mood (#3327): the gear census's card facts and the mood census's observed thoughts.
            CardPawns = EffectSourcesRules.EffectSourceFactsOf(gear, facts.MedicalPawns, facts.Ideology is IdeologyMode.Installed).Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
                    .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Card, StringComparer.Ordinal),
            // The colonists' thoughts read whole (#3805). Two rows for one pawn are contradictory evidence: no row.
            // An unread census gives no pawn a row.
            ColonistThoughts = facts.MoodPawns.GroupBy(m => m.Id.Value, StringComparer.Ordinal)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Thoughts, StringComparer.Ordinal),
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
        // The options with their work in ticks.
        static IEnumerable<(ForecastActivityOption Option, double Ticks)> Worked(IEnumerable<ForecastActivityOption> rows)
            => rows.Select(option => (option, option.WorkTicks));

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

        void AppendFamily(Methods.CoordinatedMethodCandidates family, EquatableArray<ForecastCommitment> swaps)
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
                // A garment's armor (#3621) and apparel thoughts (#3816), from its delivery's completion or a retained bill's;
                // a crafted one's warmth too (#3330), timed by the delivery. A swap that changes the garment's slot cannot hold with it.
                var clothing = family.Garments.GetValueOrDefault(outcome.Id);
                alternatives.Add(new(outcome.Id, [])
                {
                    ActivityCompletions = outcome.Completion.Length == 0 ? [] : [outcome.Completion],
                    ExclusiveOutcome = exclusive,
                    Outcome = clothing is null ? outcome : outcome with { Excludes = EquatableArray.From(outcome.Excludes
                        .Concat(ClothingActivitiesRules.SwapsInSlot(clothing, outfits.TryGetValue(clothing.Wearer, out var wearing) ? wearing : null, swaps))) },
                    ComfortShifts = clothing?.Comfort is { } comfort && outcome.Completion.Length != 0 ? [comfort] : [],
                    OutfitChanges = clothing is null ? [] : [new(clothing.Wearer, [clothing.Garment]) { Overlay = true }]
                });
            }
        }
    }
}
