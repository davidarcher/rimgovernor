package buildingruntime

import (
	"context"
	"errors"
	"math"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineReviewer observes and journals needs under Player's existing gate.
// It neither acquires authority nor creates methods or game orders.
type RoutineReviewer struct {
	methods   domain.Fact[[]policy.GoalID]
	player    *Player
	native    observation.RoutineSource
	clock     observation.Clock
	policy    policy.RoutinePolicy
	maxAge    time.Duration
	rules     []policy.ResourceRule
	longitude domain.Fact[float64]
	// census retains the latest review reading for the planners of the same
	// tick; see routineCensus.
	census routineCensusStore
	// store receives each review's decoded sections (#354); the scheduler
	// that steps this reviewer sets it, a standalone reviewer files nowhere.
	store *facts.Store
	// buildTier is the last build tier logged (#604): the service log
	// records a change once, not every review.
	buildTier domain.Fact[policy.BuildTier]
}

// logBuildTier records the reading's build tier in the service log once per
// change: `[layout] build tier Masonry (Stonecutting)`. An unknown tier
// (no research census) is not a change.
func (r *RoutineReviewer) logBuildTier(ctx context.Context, projection observation.ColonyProjection) {
	tier, known := projection.BuildTier.Value()
	if !known {
		return
	}
	if last, logged := r.buildTier.Value(); logged && last == tier {
		return
	}
	r.buildTier = projection.BuildTier
	evidence := policy.BuildTierEvidence(observation.FinishedResearch(projection.Facts.Research), projection.PlayerTechLevel)
	message := "build tier " + tier.String()
	if evidence != "" {
		message += " (" + evidence + ")"
	}
	clockEvent(ctx, "layout", "build_tier", message, "tier", tier.String(), "evidence", evidence)
}

// seasonal is the configured policy with its food and wood targets widened
// by the calendar the reading carries (policy.RoutinePolicy.Seasonal): the
// same targets DetectRoutine measures the latches against, so a planner's
// deficit, field budget and butcher gate agree with the review.
func (r *RoutineReviewer) seasonal(facts policy.RoutineFacts) policy.RoutinePolicy {
	return r.policy.Seasonal(facts.Calendar, facts.DisasterConditions)
}

// routineStore is the review's section refresher (#360): the reviewer's
// facts store, with the section ages the policies bound tighter than the
// cadence. Only room temperature has one: while a temperature condition is
// active (policy.RoomTemperatureUrgent over the colony facts the last
// review held), the temperature planner plans against the step's own
// room census, so rooms are read every review until it ends.
func (r *RoutineReviewer) routineStore() observation.RoutineStore {
	out := observation.RoutineStore{Store: r.store}
	if colony, ok := facts.Get[observation.ColonyProjection](r.store, facts.Colony); ok && policy.RoomTemperatureUrgent(colony.Value.Facts.DisasterConditions) {
		out.MaxAge = map[facts.Section]int64{facts.Rooms: 0}
	}
	return out
}

// RoutineCapabilities is the runtime's complete configured method set. Omitting
// it leaves availability unspecified for callers that compose methods themselves.
//
// Longitude is the colony's home map-tile longitude (bridge.WorldRead.Longitude),
// the map-local-hour ingredient boundary.HourOfDay/ExpectedScheduleDef need to
// fence EnsureMood-* relief dispatch against a pawn's current timetable
// assignment. It is resolved once by the caller (the colony's map tile never
// moves within a session) rather than re-read on every Step, unlike every
// other RoutineSource fact which is re-derived fresh each tick because it can
// genuinely change; Longitude cannot, so caching it here avoids two wasted
// native round trips (map lookup, then world-tile lookup) every review.
type RoutineCapabilities struct {
	Methods   []policy.GoalID
	Longitude domain.Fact[float64]
}

func NewRoutineReviewer(player *Player, native observation.RoutineSource, clock observation.Clock, thresholds policy.RoutinePolicy, maxAge time.Duration, capabilities ...RoutineCapabilities) (*RoutineReviewer, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, ErrControl
	}
	rules := player.session.ResourceRules()
	if err := policy.ValidateResourceRules(rules); err != nil {
		return nil, err
	}
	methods := domain.Unknown[[]policy.GoalID]()
	longitude := domain.Unknown[float64]()
	if len(capabilities) > 1 {
		return nil, ErrControl
	}
	if len(capabilities) == 1 {
		methods = domain.Known(append([]policy.GoalID{}, capabilities[0].Methods...))
		if _, err := policy.DetectRoutine(policy.RoutineFacts{AvailableMethods: methods}, policy.RoutineLatches{}, thresholds); err != nil {
			return nil, err
		}
		if lon, known := capabilities[0].Longitude.Value(); known {
			if math.IsNaN(lon) || math.IsInf(lon, 0) || lon < -180 || lon > 180 {
				return nil, ErrControl
			}
			longitude = capabilities[0].Longitude
		}
	}
	reviewer := &RoutineReviewer{methods: methods, player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge, rules: append([]policy.ResourceRule(nil), rules...), longitude: longitude}
	if reviewer.roomsEnabled() {
		if _, ok := native.(observation.TemperatureSource); !ok {
			return nil, ErrControl
		}
	}
	return reviewer, nil
}

func (r *RoutineReviewer) Step(ctx context.Context) (store.RoutineReviewResult, error) {
	call, epoch, done, err := r.player.enter(ctx, false)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter(), false)
}

// step is also usable by a scheduler already holding the player gate;
// partial says only a wake's planners follow the review.
func (r *RoutineReviewer) step(ctx, epoch context.Context, arbiter *stepArbiter, partial bool) (store.RoutineReviewResult, error) {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		clockSchedulerLog("routine.step: state not enabled -> stopRoutine")
		return p.stopRoutine(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		clockSchedulerLog("routine.step: ErrControl observationKnown=%v snapshotValidate=%v native=%d", state.ObservationKnown, state.Snapshot.Validate(), state.Snapshot.Native)
		return store.RoutineReviewResult{}, ErrControl
	}
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		clockSchedulerLog("routine.step: LoadRoutineReview err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	expected, err := stepScope(ctx, r.native)
	if err != nil {
		clockSchedulerLog("routine.step: stepScope err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	native, known := expected.NativeGeneration.Value()
	if expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !known || native != state.Snapshot.Native {
		clockSchedulerLog("routine.step: ErrControl identity mismatch expectedColony=%v stateColony=%v expectedLoad=%v stateLoad=%v expectedMap=%v stateMap=%v known=%v native=%d stateNative=%d",
			expected.Colony, state.Snapshot.Colony, expected.Load, state.Snapshot.Load, expected.Map, state.Snapshot.Map, known, native, state.Snapshot.Native)
		return store.RoutineReviewResult{}, ErrControl
	}
	plans, err := p.journal.LoadPlans(ctx, 256)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(ctx, playerWorld(state.Snapshot))
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	readDefinitions := definitions
	if r.methodEnabled(policy.EnsureCooking) {
		readDefinitions = append(append([]string(nil), definitions...), "NutrientPasteDispenser", "Hopper")
	}
	preferences, err := p.journal.LoadWorkPreferences(ctx, state.Snapshot.Plan)
	if errors.Is(err, store.ErrNotFound) {
		// Directly created plans have no player submission or saved overrides.
		preferences = store.WorkPreferences{Plan: state.Snapshot.Plan, World: store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}, Overrides: []policy.WorkOverride{}}
		err = nil
	}
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if preferences.World != (store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}) {
		return store.RoutineReviewResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		clockSchedulerLog("routine.step: ConstructionClaims err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	observe := observation.ObserveRoutineOwned
	if r.roomsEnabled() {
		observe = observation.ObserveRoutineRooms
	}
	reading, err := observe(observation.WithRoutineStore(ctx, r.routineStore()), r.native, r.clock, expected, r.maxAge, claims, readDefinitions...)
	if err != nil {
		clockSchedulerLog("routine.step: observe err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if err = releaseBreakWork(ctx, p.journal, state.Snapshot, reading.Emergency, plans); err != nil {
		return store.RoutineReviewResult{}, err
	}
	r.logBuildTier(ctx, reading.Projection)
	reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
	reading.Projection.Facts.ConstructionClaims = claims
	reading.Projection.Facts.OwnedStockpiles, err = p.journal.StockpileClaims(ctx, state.Snapshot, reading.Projection.Identity.Tick)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Sections.Colony.Value.Facts.ConstructionClaims = reading.Projection.Facts.ConstructionClaims
	reading.Sections.Colony.Value.Facts.OwnedStockpiles = reading.Projection.Facts.OwnedStockpiles
	if err = r.reviewColonyGrid(ctx, state.Snapshot, &reading.Projection); err != nil {
		clockSchedulerLog("routine.step: colony grid err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Sections.Colony.Value.ColonyGrid = reading.Projection.ColonyGrid
	reading.Projection.Facts.ResourceSurfaceOre = r.resourceSurfaceOre(ctx, state.Snapshot)
	r.reviewMeals(&reading.Projection)
	r.reviewReserve(&reading.Projection)
	if plan, known := reading.Projection.Facts.FoodPlan.Value(); known {
		reading.Projection.Facts.AnimalUpkeep.Forecast = domain.Known(plan.Forecast)
	}
	reading.Sections.Colony.Value.Facts.FoodPlan = reading.Projection.Facts.FoodPlan
	reading.Sections.Colony.Value.Facts.FoodReserve = reading.Projection.Facts.FoodReserve
	r.census.retain(reading, r.roomsEnabled(), claims)
	reading.Sections.File(r.store, facts.Scope{Load: string(expected.Load), Generation: uint64(native)})
	// A served native section may have gained a fresh derived food review.
	if r.store != nil && reading.Sections.Colony.Source != "" {
		facts.Put(r.store, facts.Scope{Load: string(expected.Load), Generation: uint64(native)}, facts.Colony, reading.Sections.Colony)
	}
	asOf := entitySectionsAsOf(r.store, reading.Sections.AsOf())
	asOfMin, asOfSpread := facts.Spread(asOf)
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		clockSchedulerLog("routine.step: NewEmergencySnapshot err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	// Owned drafts belong to this persistent controller's shared journal.
	// Use the same complete catalog and cleanup predicate as the release sweep.
	cleanup := false
	for _, plan := range plans {
		cleanup = cleanup || idleDraftWorkOpen(plan)
		for _, progress := range plan.Progress {
			cleanup = cleanup || draftOutstanding(progress)
		}
	}
	idleDrafts, err := r.idleDrafts(ctx, state, expected.Tick, reading.Emergency, plans)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.CleanupPawns = domain.Known(cleanup || len(idleDrafts) > 0)
	reading.Projection.ApplyFieldBudget(r.seasonal(reading.Projection.Facts).FoodTargetDays)
	// The workshop ladder's recorded research rung is the derived
	// EnsureResearch target; it is journal evidence, not a native read, so
	// a review costs no extra call for it.
	needs, err := routineResearchNeeds(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadProductionLadder err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	needs = r.fishingResearchNeeds(needs)
	reading.Projection.Facts.ResearchNeeds = needs
	// The player's population policy is the capacity a joiner offer is
	// admitted against; journal evidence too, unset until the player declares one.
	reading.Projection.Facts.PopulationCapacity, err = routinePopulationCapacity(ctx, p.journal, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: CurrentPopulationPolicy err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.DefenseTiers, err = routineJoinerDefenseTiers(ctx, p.journal, state.Snapshot)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.DefensiveLayoutStanding, reading.Projection.Facts.ResourceNeeds, err = routineDefensiveLayoutStanding(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadDefenseLayout err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.ResourceNeeds = policy.ResourceGoalTargets(reading.Projection.Facts.ResourceNeeds, policy.SocialDrugTargets(reading.Projection.Facts.Research))
	medicine, err := policy.ReviewMedicalReserve(reading.Projection.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, r.policy.MedicalReserve)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.ResourceNeeds = policy.MedicineResourceNeeds(reading.Projection.Facts.ResourceNeeds, r.policy.MedicineReserveTarget(reading.Projection.Facts.Colonists, medicine.Active))
	resourceTargets, err := r.policy.EffectiveResourceTargets(reading.Projection.Facts.Resources, reading.Projection.Facts.ResourceNeeds)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Workers = policy.RoutineWorkers(pawns)
		reading.Projection.Facts.Labor = policy.RoutineLabor(pawns)
		reading.Projection.Facts.LaborUse = policy.RoutineLaborUse(pawns)
		reading.Projection.Facts.WorkProfiles = domain.Known(policy.Profiles(pawns))
		required, known := routineProjectWork(definitions, reading.Projection.Definitions).Value()
		if known {
			// Bench work (open bills, a deficit's standing benches) counts
			// toward coverage here so the work goal assesses a deficit the
			// planner then covers; a failed census leaves coverage unknown.
			benches, _ := r.native.(RoutineWorkBenchSource)
			recovered, _ := policy.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources)
			deficit, deficitKnown := recovered.Value()
			targets := routineDeficitTargets(resourceTargets, deficitKnown && !deficit, reading.Projection.Facts.Gear)
			benchWork, err := routineBenchWork(ctx, benches, state.Snapshot, plans, playerPlans, targets, len(targets) > 0)
			if err != nil {
				clockSchedulerLog("routine.step: bench work err=%v", err)
			}
			var rows []policy.WorkRequirement
			rows, known = benchWork.Value()
			required = mergeWorkRequirements(required, rows)
			required = mergeWorkRequirements(required, routineResearchWork(policy.ArmorResearchPolicy(r.policy, previous.Latches.Soldiers || policy.GearSoldierPresent(reading.Projection.Facts.Gear)), needs, reading.Projection.Facts.Research))
			required = mergeWorkRequirements(required, fishingWork(reading.Projection))
		}
		if known {
			demand, err := routineDiseaseDemand(reading.Projection, len(definitions) > 0, previous, state.Snapshot)
			if err != nil {
				return store.RoutineReviewResult{}, err
			}
			work, err := policy.PlanWork(pawns, required, preferences.Overrides, demand)
			if err == nil {
				reading.Projection.Facts.WorkCoverage = work.Matches
				for _, pawn := range pawns {
					if len(policy.FoodPolicyChanges(pawn)) > 0 || policy.DrugPolicyChange(pawn) {
						reading.Projection.Facts.WorkCoverage = domain.Known(false)
					}
				}
				// A timetable behind its role template is a work
				// deficit the same review corrects (#417).
				if matches, ok := work.Matches.Value(); ok && matches {
					for _, row := range policy.PlanSchedules(pawns).Schedules {
						if !row.Matches {
							reading.Projection.Facts.WorkCoverage = domain.Known(false)
						}
					}
				}
				if _, ok := work.Capacity.Value(); ok {
					reading.Projection.Facts.WorkRoster = domain.Known(work.Coverage)
					reading.Projection.Facts.WorkDecaying = domain.Known(work.Decaying)
				}
			}
		}
	}
	reading.Projection.Facts.Upkeep.Rooms = reading.Projection.Rooms
	reading.Projection.Facts.Upkeep.ShrinePolicy = r.policy.Shrine
	reading.Projection.Facts.CleaningContext(reading.Projection.Identity.Tick)
	if err = p.current(ctx, epoch); err != nil {
		clockSchedulerLog("routine.step: p.current err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if p.session.State() != state {
		clockSchedulerLog("routine.step: ErrControl state changed under us")
		return store.RoutineReviewResult{}, ErrControl
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	if r.methodEnabled(policy.ClearHomeObstructions) {
		source, ok := r.native.(observation.ClearanceSource)
		if !ok {
			return store.RoutineReviewResult{}, ErrControl
		}
		clearance, err := observation.ObserveClearanceCensus(ctx, source, expected)
		if err != nil {
			return store.RoutineReviewResult{}, err
		}
		if census, known := clearance.Value(); known {
			reading.Projection.Facts.Upkeep.Clearance = domain.Known(census.Targets)
			reading.Projection.Facts.Upkeep.Chunks = domain.Known(census.Chunks)
		}
	}
	if r.methodEnabled(policy.ClearAncientShrine) {
		source, ok := r.native.(observation.ShrineSource)
		if !ok {
			return store.RoutineReviewResult{}, ErrControl
		}
		if reading.Projection.Facts.Upkeep.Shrines, err = observation.ObserveShrines(ctx, source, expected); err != nil {
			return store.RoutineReviewResult{}, err
		}
		// The breach judgement (#457) is journalled beside the review so the
		// hold reason and the chosen wall are readable; the planner re-reads
		// before drafting anyone. The reads only happen for a sealed shrine
		// with a breach wall.
		if reading.Projection.Facts.ShrineHolds, err = routineShrineHolds(ctx, r.native, state.Snapshot, reading.Projection, r.policy.Shrine); err != nil {
			return store.RoutineReviewResult{}, err
		}
	}
	reading.Projection.Facts.AvailableMethods = r.methods
	result, err := p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, WorkPreferenceRevision: preferences.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts, PartialPlanners: partial, AsOf: routineAsOf(asOf)})
	if err != nil {
		clockSchedulerLog("routine.step: ReviewRoutine err=%v", err)
	} else {
		// as_of is the tick each census section described; as_of_spread
		// (max - min) is zero while every section comes from one bundle
		// and becomes visible the moment a section takes another source.
		clockEvent(ctx, "routine", "routine_review", "routine reviewed", append([]any{"revision", result.Review.Revision, "previous_revision", previous.Revision, "tick", int64(reading.Projection.Identity.Tick), "goals", len(result.Goals), "emergency", routineEmergencyNames(result.Emergency), "as_of", routineAsOf(asOf), "as_of_min", asOfMin, "as_of_spread", asOfSpread}, routineFoodAttrs(reading.Projection.Facts, r.seasonal(reading.Projection.Facts))...)...)
	}
	return result, err
}

// routineAsOf keys the sections' ticks by name for the journal.
func routineAsOf(asOf map[facts.Section]int64) map[string]int64 {
	out := make(map[string]int64, len(asOf))
	for section, tick := range asOf {
		out[string(section)] = tick
	}
	return out
}

// routineFoodAttrs are the review row's stored-food attrs: the food runway
// the review read and the seasonal thresholds it held it to, with the
// calendar they came from (#229). An unknown fact leaves its attr out, so
// a sustained run's samples are exactly the reviews that had one.
func routineFoodAttrs(f policy.RoutineFacts, seasonal policy.RoutinePolicy) []any {
	attrs := []any{"food_min_days", seasonal.FoodMinDays, "food_target_days", seasonal.FoodTargetDays}
	if reserve, known := f.FoodReserve.Value(); known {
		attrs = append(attrs, "food_reserve_days", seasonal.FoodReserveDays, "food_reserve_target", reserve.TargetNutrition,
			"food_reserve_stock", reserve.StockNutrition, "food_reserve_deficit", reserve.DeficitNutrition, "food_reserve_emergency", reserve.Emergency)
	}
	if plan, known := f.FoodPlan.Value(); known {
		attrs = append(attrs, "food_plan", plan.Explain(), "food_gap_per_day", plan.GapPerDay)
	}
	if days, known := f.FoodDays.Value(); known {
		attrs = append(attrs, "food_days", days)
	}
	if c, known := f.Calendar.Value(); known {
		attrs = append(attrs, "season", c.Season, "day_of_year", c.DayOfYear, "growing_days_remaining", c.GrowingDaysRemaining, "growing_days_until", c.GrowingDaysUntil, "non_growing_days", c.NonGrowingDays)
	}
	return attrs
}

// Caller holds the player gate. Invalidating existing work needs no native read,
// including when a stale browser request stops a different observed world.
// routineEmergencyNames renders the needs that suspended the review's
// goals for an event attr.
func routineEmergencyNames(ids []policy.GoalID) []string {
	out := make([]string, 0, len(ids))
	for _, id := range ids {
		out = append(out, string(id))
	}
	return out
}

func (p *Player) stopRoutine(ctx context.Context) (store.RoutineReviewResult, error) {
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if previous.Revision == 0 || !previous.Enabled {
		return store.RoutineReviewResult{Review: previous}, nil
	}
	return p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, Current: previous.Snapshot, Tick: previous.Tick, Enabled: false})
}

// routineJoinerDefenseTiers counts only nonempty firing/turret tiers observed
// built in this load. A record from another load awaits native re-verification.
func routineJoinerDefenseTiers(ctx context.Context, journal *store.Store, snapshot domain.GenerationSnapshot) (domain.Fact[int], error) {
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	record, stored, err := journal.LoadDefenseLayout(ctx, world)
	if err != nil {
		return domain.Unknown[int](), err
	}
	if stored && record.World != world {
		return domain.Unknown[int](), nil
	}
	tiers := 0
	for _, tier := range record.Tiers {
		if tier.Built && len(tier.Buildings) > 0 && (tier.Name == policy.TierFiringLine || tier.Name == policy.TierTurrets) {
			tiers++
		}
	}
	return domain.Known(tiers), nil
}
