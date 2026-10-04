package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineGoal binds a Standard need to the goal row the review filed for it.
type RoutineGoal struct {
	Need domain.ConcernID
	Goal domain.ConcernID
}

// RoutineProject binds a Project need to the Project row the review filed for
// it (#1927): Projects are never goal rows.
type RoutineProject struct {
	Need    domain.ConcernID
	Project domain.ProjectID
}

// ReserveSupply binds identity and access direction. Its current position is
// resolved by the supply census and checked by Hands before a write.
type ReserveSupply struct {
	Thing, Definition string
	Forbid            bool
}

// Rounds is the durable review cursor and hysteresis history. Goal
// bindings retain semantic needs while old executable plans keep their identity.
type Rounds struct {
	// Emergency names the assessed needs policy.EmergencyNeed found in this
	// enabled review; the EmergencySafeguard vetoes other work from them (#1017).
	Emergency []policy.ConcernID `json:",omitempty"`
	// Unsafe lists the loose things the last known safety census reported
	// unsafe to haul; policy.UnsafeLootSafeguard refuses allowing them at
	// dispatch (#1018).
	Unsafe          []string                `json:",omitempty"`
	BrewingFinished bool                    `json:",omitempty"`
	ResourceRunways []ResourceRunwayRecord  `json:",omitempty"`
	ReserveSupplies []ReserveSupply         `json:",omitempty"`
	LarderSupplies  []policy.StartingSupply `json:",omitempty"`
	ClearanceHolds  []policy.ClearanceHold  `json:",omitempty"`
	// SalvageTarget is the one remote ruin this review admitted by reach and
	// demand; the clearance planner executes it against a fresh native census.
	SalvageTarget string              `json:",omitempty"`
	ShrineHolds   []policy.ShrineHold `json:",omitempty"`
	// ShrineStep is the shrine planner's last step (#680): the shrine it
	// held on and why, and the candidates it passed over. A world change
	// clears it; otherwise a review keeps the last one.
	ShrineStep     *RoutineShrineStep      `json:",omitempty"`
	Recovery       *RoutineRecovery        `json:",omitempty"`
	Disaster       *policy.DisasterHistory `json:",omitempty"`
	Mood           *RoutineMood            `json:",omitempty"`
	MoodMethods    []RoutineMoodMethod     `json:",omitempty"`
	Sleeping       policy.SleepingHistory
	Revision       uint64
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	Enabled        bool
	Latches        policy.RoutineLatches
	MedicalCare    policy.MedicalCareHistory
	MedicineTarget int64 `json:",omitempty"`
	// DependencyNeeds are the MaintainResource floors this review's live
	// shortfall edges raised (#728), so the resource planner stocks them.
	DependencyNeeds map[policy.Resource]int64 `json:",omitempty"`
	// WoodFloor is the wood latch's WoodLog floor (policy.RoundsFindings).
	WoodFloor        int64 `json:",omitempty"`
	StartingSupplies policy.StartingSupplies
	EventLoot        policy.EventLootHistory
	Comfort          policy.ComfortHistory
	// Goals binds the Standards this review assessed; Projects binds the
	// Projects.
	Goals    []RoutineGoal
	Projects []RoutineProject `json:",omitempty"`
	// Incidents binds the Responses this review assessed to their open
	// occurrences (#1020); those needs have no entry in Goals.
	Incidents   []RoutineIncident `json:",omitempty"`
	Development RoutineDevelopment
	// Roster is the roster planner's last recorded report (#448): coverage,
	// decaying skills and pawn profiles as of its Tick. A disabled review
	// keeps the last one; absent until an enabled review planned work.
	Roster *policy.WorkRosterReport `json:",omitempty"`
	// Layout is the TidyLayout review this enabled review measured (#611):
	// the standing proposal with its explanation, or why none stands.
	Layout *policy.TidyReview `json:",omitempty"`
	// Progress is every active goal's progress record (#629), keyed by
	// need: method, expected observable, last progress tick, next review
	// tick, blocked reason and the bounded cooldowns its rotations keyed.
	Progress []policy.GoalProgress `json:",omitempty"`
	// Stage is the colony stage (#630) the review derived from its facts
	// and progress records, with the first unmet condition of the next
	// stage; a disabled review keeps the last one. Absent before any
	// enabled review filed one.
	Stage *policy.ColonyStageRecord `json:",omitempty"`
	// ReadyWork is the shadow ready-work projection (#645) of this
	// enabled review's plans and unserved goals, bounded by
	// policy.DefaultReadyBounds. Diagnostics only: no admission reads it.
	ReadyWork *policy.ReadyWorkReport `json:",omitempty"`
	// ShadowRank is the shadow project ranker (#1913): candidate goals scored
	// by projected shortfall per open action, and where that order disagrees
	// with Development. Diagnostics only: no admission reads it.
	ShadowRank *policy.ShadowRank `json:",omitempty"`
	// Built is every live building action standing built, by geometry, in the
	// last complete construction census (builtActions, #1355), sorted; a review without a
	// complete census keeps the last one. Admission reads it for building
	// prerequisites (checkDependencies, #937).
	Built []domain.ActionID `json:",omitempty"`
	// WallCells is, for every cell a live plan's wall removal names, whether
	// the last complete construction census still had a colony Wall on it,
	// sorted by cell; a review without a complete census keeps the last one.
	// Admission reads it for wall-removal prerequisites (checkDependencies,
	// #989): an applied removal is only designated, so a dependent (the
	// replacement wall on the same cell) waits for the wall to be gone.
	WallCells []WallCell `json:",omitempty"`
	// Dependencies are the live shortfall edges (#651) planners recorded
	// (RecordDependency); each review drops the settled or stale ones.
	Dependencies []DependencyRecord `json:",omitempty"`
	// NoOps is every inspection that raised nothing in the last enabled
	// review, with its typed reason (#1909); a disabled review keeps the last.
	NoOps []policy.NoOpRecord `json:",omitempty"`
}

type RoundsRequest struct {
	Revision uint64
	Current  domain.GenerationSnapshot
	Tick     domain.Tick
	Enabled  bool
	Policy   policy.RoutinePolicy
	Facts    policy.RoutineFacts
	// PartialPlanners: only the planners a wake named follow this review,
	// so the next review must not count an unrun planner's goal idle.
	PartialPlanners bool
}

type RoundsResult struct {
	Review Rounds
	Needs  policy.RoundsFindings
	Goals  []StandardState
	// Projects are the Project rows Review.Projects binds.
	Projects []ProjectState
	// Incidents are the occurrences Review.Incidents binds (#1020).
	Incidents []IncidentState
	// Emergency names the assessed needs whose EmergencySafeguard vetoes every
	// priority>=2 proposal in this review (a home fire, live hostiles, a critical patient);
	// empty when nothing did. The development rows only say "emergency", so
	// this is the log's answer to which need held the colony (#221).
	Emergency []policy.ConcernID
	// Detection is what an enabled review passed policy.DetectRoutine: the
	// journal-enriched facts, the prior latches and the staged policy. Not
	// journalled; a colony snapshot (internal/snapshot) records it.
	Detection *RoutineDetection
}

// RoutineDetection is one DetectRoutine call's input.
type RoutineDetection struct {
	Facts   policy.RoutineFacts
	Latches policy.RoutineLatches
	Policy  policy.RoutinePolicy
}

func loadRoutine(ctx context.Context, tx *sql.Tx) (Rounds, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM routine_review WHERE singleton=1").Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Rounds{}, nil
		}
		return Rounds{}, err
	}
	var r Rounds
	if len(data) > 1024*1024 {
		return r, ErrCapacity
	}
	if err := json.Unmarshal(data, &r); err != nil {
		return r, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Revision == 0 || r.Snapshot.Validate() != nil || r.Tick < 0 || len(r.Goals) > 306 || len(r.Projects) > 306 {
		return Rounds{}, errors.New("invalid rounds history")
	}
	if r.MedicineTarget < 0 || r.MedicineTarget > 10000 {
		return Rounds{}, errors.New("invalid medicine resource target")
	}
	if r.WoodFloor < 0 || r.WoodFloor > 1_000_000 {
		return Rounds{}, errors.New("invalid wood floor")
	}
	if len(r.DependencyNeeds) > maxDependencyRecords || policy.ValidateResourceTargets(r.DependencyNeeds) != nil {
		return Rounds{}, errors.New("invalid dependency resource needs")
	}
	if err := r.MedicalCare.Validate(); err != nil {
		return Rounds{}, err
	}
	if len(r.Dependencies) > maxDependencyRecords {
		return Rounds{}, errors.New("invalid routine dependencies")
	}
	if len(r.WallCells) > maxWallCells {
		return Rounds{}, errors.New("invalid routine wall cells")
	}
	for _, d := range r.Dependencies {
		if err := d.validate(); err != nil {
			return Rounds{}, err
		}
	}
	if err := r.moodHistory().Validate(); err != nil {
		return Rounds{}, err
	}
	if err := r.Disaster.Validate(); err != nil {
		return Rounds{}, err
	}
	if r.Disaster != nil && r.Disaster.Observed > r.Tick {
		return Rounds{}, errors.New("future disaster history")
	}
	if err := validateRoutineRecovery(r); err != nil {
		return Rounds{}, err
	}
	var proposals []RoutineMoodMethod
	if r.Enabled {
		proposals, err = moodProposals(r.moodHistory())
		if err != nil {
			return Rounds{}, err
		}
	}
	if len(proposals) != len(r.MoodMethods) {
		return Rounds{}, errors.New("invalid mood method review")
	}
	expectedProposals, _ := json.Marshal(proposals)
	actualProposals, _ := json.Marshal(r.MoodMethods)
	if !bytes.Equal(expectedProposals, actualProposals) {
		return Rounds{}, errors.New("stale mood method proposal")
	}
	if err := r.EventLoot.Validate(); err != nil {
		return Rounds{}, err
	}
	if err := r.StartingSupplies.Validate(); err != nil {
		return Rounds{}, err
	}
	if len(r.ReserveSupplies) > 4096 || !r.Enabled && len(r.ReserveSupplies) != 0 {
		return Rounds{}, errors.New("invalid reserve method history")
	}
	reserveIDs := map[string]bool{}
	for _, row := range r.ReserveSupplies {
		if !policy.ReserveFoodDefinition(policy.Resource(row.Definition)) || reserveIDs[row.Thing] {
			return Rounds{}, errors.New("invalid reserve supply")
		}
		if err := (policy.StartingSupply{Thing: row.Thing, Definition: row.Definition}).Validate(); err != nil {
			return Rounds{}, err
		}
		reserveIDs[row.Thing] = true
	}
	if len(r.LarderSupplies) > 1 || !r.Enabled && len(r.LarderSupplies) != 0 {
		return Rounds{}, errors.New("invalid larder method history")
	}
	for _, row := range r.LarderSupplies {
		if err := row.Validate(); err != nil {
			return Rounds{}, err
		}
	}
	if err := r.Sleeping.Validate(); err != nil {
		return Rounds{}, err
	}
	for _, use := range r.Sleeping.Uses {
		if use.Tick > r.Tick {
			return Rounds{}, errors.New("future sleeping use history")
		}
	}
	if err := r.Comfort.Validate(); err != nil {
		return Rounds{}, err
	}
	if r.Comfort.Dining.Tick > r.Tick || r.Comfort.Recreation.Tick > r.Tick {
		return Rounds{}, errors.New("future comfort use history")
	}
	if r.Roster != nil && (r.Roster.Tick > r.Tick || len(r.Roster.Coverage) > 256 || len(r.Roster.Decaying) > 4096 || len(r.Roster.Profiles) > 256 || r.Roster.Help != nil && (r.Roster.Help.Tick > r.Roster.Tick || len(r.Roster.Help.Idle) > 256 || len(r.Roster.Help.Helpers) > 256 || len(r.Roster.Help.Risky) > 256)) {
		return Rounds{}, errors.New("invalid routine roster history")
	}
	if r.ShrineStep != nil && r.ShrineStep.validate(r.Tick) != nil {
		return Rounds{}, errors.New("invalid routine shrine step")
	}
	if r.Layout != nil && len(r.Layout.Reason) > 256 {
		return Rounds{}, errors.New("invalid routine layout review")
	}
	if len(r.Progress) > 306 {
		return Rounds{}, errors.New("invalid routine progress history")
	}
	progressGoals := map[domain.ConcernID]bool{}
	for _, p := range r.Progress {
		if progressGoals[p.Goal] || policy.ValidateGoalProgress(p, r.Tick) != nil {
			return Rounds{}, errors.New("invalid routine progress history")
		}
		progressGoals[p.Goal] = true
	}
	if r.Stage != nil && policy.ValidateColonyStage(*r.Stage, r.Tick) != nil {
		return Rounds{}, errors.New("invalid routine stage history")
	}
	if r.Enabled {
		if r.Development.Snapshot != r.Snapshot || r.Development.Tick != r.Tick || policy.ValidateDevelopmentState(r.Development.State()) != nil {
			return Rounds{}, errors.New("invalid routine development history")
		}
	} else if len(r.Development.Rows) > 0 || r.Development.Workers != nil {
		// A disabled review keeps the last ranking (waiting ages) but
		// selects nothing: methods cannot be committed without authority.
		if r.Development.Tick > r.Tick || policy.ValidateDevelopmentState(r.Development.State()) != nil {
			return Rounds{}, errors.New("invalid routine development history")
		}
		for _, row := range r.Development.Rows {
			if row.Selected {
				return Rounds{}, errors.New("disabled routine retains development selection")
			}
		}
	}
	// The stored review is the fact: a binding is valid when it names a
	// routine goal the table knows, not when a second derivation from empty
	// facts would also have assessed it (#1763). Response kinds live in the
	// incidents table and Project kinds in the projects table; Goals binds
	// Standards only.
	for _, row := range r.Development.Rows {
		if c := policy.ConcernTypeOf(row.Goal); c != policy.StandardConcern && c != policy.ProjectConcern {
			return Rounds{}, errors.New("unknown optional routine goal")
		}
	}
	seen := map[domain.ConcernID]bool{}
	identities := map[domain.ConcernID]bool{}
	for _, binding := range r.Goals {
		if policy.ConcernTypeOf(binding.Need) != policy.StandardConcern || seen[binding.Need] || identities[binding.Goal] {
			return Rounds{}, errors.New("duplicate routine need")
		}
		seen[binding.Need] = true
		identities[binding.Goal] = true
		if _, err := loadGoal(ctx, tx, binding.Goal); err != nil {
			return Rounds{}, err
		}
		if !routineStandardOwns(binding.Goal, binding.Need) {
			return Rounds{}, errors.New("routine goal ownership mismatch")
		}
	}
	projects := map[domain.ProjectID]bool{}
	for _, binding := range r.Projects {
		if !policy.IsProjectKind(binding.Need) || seen[binding.Need] || projects[binding.Project] {
			return Rounds{}, errors.New("duplicate routine need")
		}
		seen[binding.Need] = true
		projects[binding.Project] = true
		p, err := loadProject(ctx, tx, binding.Project)
		if err != nil {
			return Rounds{}, err
		}
		if p.Project.Kind != binding.Need || !routineProjectOwns(binding.Project, binding.Need) {
			return Rounds{}, errors.New("routine project ownership mismatch")
		}
	}
	if err := validateRoutineIncidents(ctx, tx, r); err != nil {
		return Rounds{}, err
	}
	return r, nil
}

func (s *Store) LoadRounds(ctx context.Context) (Rounds, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return Rounds{}, err
	}
	defer tx.Rollback()
	r, err := loadRoutine(ctx, tx)
	if err != nil {
		return Rounds{}, err
	}
	return r, tx.Commit()
}

// ReviewRoutine commits all need assessments and latch history atomically.
// It selects no methods and grants no execution authority. A disabled review
// of the same world suspends routine goals and leaves their in-flight work
// open for the next enabled review to resume; only world replacement or a
// tick rewind invalidates linked work through the same cancellation journal
// as Hands.
func (s *Store) ReviewRoutine(ctx context.Context, request RoundsRequest) (RoundsResult, error) {
	if request.Current.Validate() != nil || request.Tick < 0 {
		return RoundsResult{}, errors.New("invalid routine scope")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RoundsResult{}, err
	}
	defer tx.Rollback()
	settled := map[retirementWorld]domain.Tick{}
	result, err := reviewRoutineTx(ctx, tx, request, settled)
	if err != nil {
		return RoundsResult{}, err
	}
	if err = tx.Commit(); err != nil {
		return RoundsResult{}, err
	}
	s.floors.raise(settled)
	s.notifyGoalsWritten()
	return result, nil
}

func reviewRoutineTx(ctx context.Context, tx *sql.Tx, request RoundsRequest, settled map[retirementWorld]domain.Tick) (RoundsResult, error) {
	previous, err := loadRoutine(ctx, tx)
	if err != nil {
		return RoundsResult{}, err
	}
	if previous.Revision != request.Revision {
		return RoundsResult{}, ErrConflict
	}
	if previous.Revision == ^uint64(0) {
		return RoundsResult{}, ErrCapacity
	}
	a, b := previous.Snapshot, request.Current
	changed := previous.Revision != 0 && (a.Colony != b.Colony || a.Load != b.Load || a.Map != b.Map || request.Tick < previous.Tick)
	// Player direction invalidates work, not an observed shortage's recovery
	// target. Only world replacement or time rewind discards latch history.
	reset := previous.Revision == 0 || a.Colony != b.Colony || a.Load != b.Load || a.Map != b.Map || request.Tick < previous.Tick
	latches := previous.Latches
	medical := previous.MedicalCare
	supplies := previous.StartingSupplies
	loot := previous.EventLoot
	sleeping := previous.Sleeping
	comfort := previous.Comfort
	mood := previous.moodHistory()
	disaster := previous.Disaster
	if reset {
		latches = policy.RoutineLatches{}
		medical = policy.MedicalCareHistory{}
		supplies = policy.StartingSupplies{}
		loot = policy.EventLootHistory{}
		comfort = policy.ComfortHistory{}
		sleeping = policy.SleepingHistory{}
		mood = policy.MoodHistory{}
		disaster = nil
	}
	// The stage the last review left sets this review's goal budgets (the
	// research ladder's pace, the stall deadline);
	// the stage this review derives is filed for the next.
	var previousStage policy.ColonyStageRecord
	if previous.Stage != nil && !reset {
		previousStage = *previous.Stage
	}
	request.Policy = policy.StageRoutinePolicy(request.Policy, previousStage.Stage)
	needs := policy.RoundsFindings{}
	var detection *RoutineDetection
	// Stopping routine work must not depend on a successful native observation.
	if request.Enabled {
		request.Facts.ResourceRunways, err = resourceRunways(ctx, tx, request)
		if err != nil {
			return RoundsResult{}, err
		}
		request.Facts.ResourceNeeds = policy.ResourceGoalTargets(request.Facts.ResourceNeeds, policy.ResourceRunwayTargets(request.Facts.ResourceRunways))
		request.Facts.ConstructionClaims, err = constructionClaims(ctx, tx, request.Current, request.Tick)
		if err != nil {
			return RoundsResult{}, err
		}
		request.Facts.UpkeepIssued, err = routineUpkeepIssued(ctx, tx, request.Current)
		if err != nil {
			return RoundsResult{}, err
		}
		sleepingReview, sleepingErr := policy.ReviewSleeping(request.Facts.Sleeping, sleeping, request.Tick)
		if sleepingErr != nil {
			return RoundsResult{}, sleepingErr
		}
		sleeping = sleepingReview.History
		request.Facts.SleepingRecovered = sleepingReview.Recovered()
		if owed, known := request.Facts.BedroomsOwed.Value(); known && owed {
			request.Facts.SleepingRecovered = domain.Known(false)
		}
		comfortReview, comfortErr := policy.ReviewComfort(request.Facts.Comfort, comfort, request.Tick)
		if comfortErr != nil {
			return RoundsResult{}, comfortErr
		}
		comfort = comfortReview.History
		request.Facts.ComfortRecovered, request.Facts.ComfortDeficit = comfortReview.Recovered(), comfortReview.Deficit()
		supplies, request.Facts.ForbiddenSupplies, err = policy.ReviewStartingSuppliesAt(request.Facts.StartingSupplies, supplies, request.Tick)
		if err != nil {
			return RoundsResult{}, err
		}
		lootCensus, held, err := lootReachFilter(request)
		if err != nil {
			return RoundsResult{}, err
		}
		loot, request.Facts.EventLootPending, err = policy.ReviewEventLoot(lootCensus, loot)
		if err != nil {
			return RoundsResult{}, err
		}
		loot.Held = held
		// The food reserve owns the forbid state of reserve food; drop its
		// stacks from both supply cohorts so the planners cannot flip them.
		supplies.Pending = policy.DropReserveHeld(supplies.Pending, request.Facts.FoodReserve)
		loot.Pending = policy.DropReserveHeld(loot.Pending, request.Facts.FoodReserve)
		if _, known := request.Facts.ForbiddenSupplies.Value(); known {
			request.Facts.ForbiddenSupplies = domain.Known(len(supplies.Pending) > 0)
		}
		if _, known := request.Facts.EventLootPending.Value(); known {
			request.Facts.EventLootPending = domain.Known(len(loot.Pending) > 0)
		}
		if rows, known := request.Facts.Upkeep.Clearance.Value(); known {
			remote, err := policy.SalvageContext(request.Policy, request.Facts)
			if err != nil {
				return RoundsResult{}, err
			}
			filtered, _, err := policy.FilterRemoteSalvage(rows, remote)
			if err != nil {
				return RoundsResult{}, err
			}
			request.Facts.Upkeep.Clearance = domain.Known(filtered)
		}
		medical, err = policy.ReviewMedicalCare(request.Facts.MedicalPawns, medical)
		if err != nil {
			return RoundsResult{}, err
		}
		request.Facts.MedicalCareRecovered = medical.Recovered()
		mood, err = policy.ReviewMood(request.Facts.MoodPawns, mood)
		if err != nil {
			return RoundsResult{}, err
		}
		request.Facts.Mood = mood
		request.Facts.Disaster, request.Facts.DisasterTick = disaster, request.Tick
		if !reset {
			if request.Facts.Dependencies, err = priorDependencies(ctx, tx, previous, request.Facts, request.Tick); err != nil {
				return RoundsResult{}, err
			}
		}
		detection = &RoutineDetection{Facts: request.Facts, Latches: latches, Policy: request.Policy}
		needs, err = policy.DetectRoutine(request.Facts, latches, request.Policy)
		if err != nil {
			return RoundsResult{}, err
		}
		disaster = needs.Disaster
		if needs.Latches.Refrigeration {
			needs.Latches.RefrigerationSince = request.Tick
			if latches.Refrigeration && latches.RefrigerationSince > 0 {
				needs.Latches.RefrigerationSince = latches.RefrigerationSince
			}
		}
	}
	old := map[domain.ConcernID]StandardState{}
	assessed := map[domain.ConcernID]bool{}
	for _, n := range needs.All() {
		assessed[n.ID] = true
	}
	if request.Enabled {
		if err = retireRoutinePlans(ctx, tx, request.Current, request.Tick, request.Facts.CurrentConstruction, settled); err != nil {
			return RoundsResult{}, err
		}
	}
	for _, binding := range previous.Goals {
		g, err := loadGoal(ctx, tx, binding.Goal)
		if err != nil {
			return RoundsResult{}, err
		}
		if changed || (request.Enabled && !assessed[binding.Need]) {
			if g.Standard.Status != domain.StandardVoided {
				if err = cancelMethods(ctx, tx, g); err != nil {
					return RoundsResult{}, err
				}
				next := g.Standard
				next.Status = domain.StandardVoided
				g, err = saveStandard(ctx, tx, g, next)
				if err != nil {
					return RoundsResult{}, err
				}
			}
		}
		// Paused control keeps its goals active: the PauseSafeguard vetoes new
		// work, native designations already issued keep progressing and the
		// resumed goal adopts the result.
		old[binding.Need] = g
	}
	oldProjects := map[domain.ConcernID]ProjectState{}
	for _, binding := range previous.Projects {
		p, err := loadProject(ctx, tx, binding.Project)
		if err != nil {
			return RoundsResult{}, err
		}
		if changed || (request.Enabled && !assessed[binding.Need]) {
			if p.Project.Status != domain.ProjectVoided {
				if err = cancelMethods(ctx, tx, p); err != nil {
					return RoundsResult{}, err
				}
				next := p.Project
				next.Status = domain.ProjectVoided
				if p, err = saveProject(ctx, tx, p, next); err != nil {
					return RoundsResult{}, err
				}
			}
		}
		oldProjects[binding.Need] = p
	}
	// A replaced or rewound world ends its occurrences and their work, as
	// it invalidates its goals.
	if changed {
		for _, binding := range previous.Incidents {
			if err = abandonIncident(ctx, tx, binding.Incident, previous.Tick); err != nil {
				return RoundsResult{}, err
			}
		}
	}
	// Keep disabled bindings available for review. An enabled review replaces
	// invalidated bindings, so their clean history can leave active capacity.
	retained := map[domain.ConcernID]bool{}
	for _, g := range old {
		if !request.Enabled || g.Standard.Status != domain.StandardVoided {
			retained[g.Standard.ID] = true
		}
	}
	if err = retireRoutineGoals(ctx, tx, retained); err != nil {
		return RoundsResult{}, err
	}
	retainedProjects := map[domain.ProjectID]bool{}
	for _, p := range oldProjects {
		if !request.Enabled || p.Project.Status != domain.ProjectVoided {
			retainedProjects[p.Project.ID] = true
		}
	}
	if err = retireProjects(ctx, tx, retainedProjects); err != nil {
		return RoundsResult{}, err
	}
	r := Rounds{Revision: previous.Revision + 1, Snapshot: b, Tick: request.Tick, Enabled: request.Enabled, Latches: needs.Latches}
	r.MedicalCare = medical
	r.BrewingFinished = policy.BrewingFinished(request.Facts.Research)
	r.MedicineTarget = request.Policy.MedicineReserveTarget(request.Facts.Colonists, needs.Latches.MedicalReserve)
	r.DependencyNeeds = policy.ConstructionResourceNeeds(policy.DependencyResourceNeeds(request.Facts.Dependencies), request.Facts.ConstructionDeficit, request.Facts.Resources)
	r.WoodFloor = needs.WoodFloor
	r.StartingSupplies = supplies
	r.EventLoot = loot
	if rows, known := request.Facts.EventLoot.Value(); known {
		r.Unsafe = policy.UnsafeLoot(rows)
	} else if !reset {
		r.Unsafe = previous.Unsafe
	}
	if !reset {
		r.Built = previous.Built
	}
	if !reset {
		r.WallCells = previous.WallCells
	}
	if census, known := request.Facts.CurrentConstruction.Value(); known && census.Colony {
		if r.WallCells, err = wallCells(ctx, tx, census); err != nil {
			return RoundsResult{}, err
		}
		if err = abandonStuckWallRemovals(ctx, tx, r.WallCells, request.Tick); err != nil {
			return RoundsResult{}, err
		}
	}
	if census, known := request.Facts.CurrentConstruction.Value(); known && census.Colony {
		if r.Built, err = builtActions(ctx, tx, census); err != nil {
			return RoundsResult{}, err
		}
	}
	if request.Enabled {
		if reserve, known := request.Facts.FoodReserve.Value(); known {
			wanted := map[string]bool{}
			for _, id := range reserve.Hold {
				wanted[id] = true
			}
			for _, id := range reserve.Release {
				wanted[id] = false
			}
			stocks, _ := request.Facts.FoodStorageUpkeep.Stocks.Value()
			for _, row := range stocks {
				if forbid, selected := wanted[row.Stock.ID]; selected && policy.ReserveFoodDefinition(row.Stock.DefName) {
					r.ReserveSupplies = append(r.ReserveSupplies, ReserveSupply{Thing: row.Stock.ID, Definition: string(row.Stock.DefName), Forbid: forbid})
				}
			}
		}
		choice, choiceErr := policy.SelectCorpseLarder(request.Facts.FoodStorageUpkeep)
		if choiceErr != nil {
			return RoundsResult{}, choiceErr
		}
		if choice.Kind == "allow" || choice.Kind == "forbid" {
			r.LarderSupplies = []policy.StartingSupply{{Thing: choice.Stock.ID, Definition: string(choice.Stock.DefName), Cell: choice.Handling.Cell, Forbid: choice.Kind == "forbid"}}
		}
	}
	r.Comfort = comfort
	r.Sleeping = sleeping
	r.Mood = moodRecord(mood)
	r.Roster = routineRoster(request, previous, reset)
	r.Layout = previous.Layout
	if !reset {
		r.ShrineStep = previous.ShrineStep
	}
	if tidy, known := request.Facts.LayoutTidy.Value(); request.Enabled && known {
		copied := tidy
		r.Layout = &copied
	}
	r.ResourceRunways = resourceRunwayRecords(request.Facts.ResourceRunways)
	if !request.Enabled && !reset {
		r.ResourceRunways = previous.ResourceRunways
	}
	r.Disaster = disaster
	if request.Enabled {
		r.MoodMethods, err = moodProposals(mood)
		if err != nil {
			return RoundsResult{}, err
		}
	}
	result := RoundsResult{Needs: needs, Detection: detection}
	// states is every assessment's row in assessment order, Projects as the
	// goal handles ranking and progress read.
	var states []WorkOwner
	if !request.Enabled {
		r.Goals = previous.Goals
		r.Projects = previous.Projects
		if !changed {
			r.Incidents = previous.Incidents
			for _, binding := range r.Incidents {
				state, err := loadIncident(ctx, tx, binding.Incident)
				if err != nil {
					return RoundsResult{}, err
				}
				result.Incidents = append(result.Incidents, state)
			}
		}
		r.Latches = latches
		// Manual mode, a player interruption or a restart's first review
		// before authority returns does not rank; the last ranking stays
		// so waiting ages survive it. Only a world change or tick rewind
		// resets ranking history.
		r.Development = previous.Development
		r.Development.Rows = append([]RoutineDevelopmentRow(nil), previous.Development.Rows...)
		if !reset {
			r.Progress = previous.Progress
			r.Stage = previous.Stage
			r.Dependencies = previous.Dependencies
			r.NoOps = previous.NoOps
		}
		for i := range r.Development.Rows {
			if row := &r.Development.Rows[i]; row.Selected || row.Reason == "" {
				row.Selected, row.Reason = false, policy.DevelopmentDisabled
			}
		}
		for _, binding := range r.Goals {
			result.Goals = append(result.Goals, old[binding.Need])
		}
		for _, binding := range r.Projects {
			result.Projects = append(result.Projects, oldProjects[binding.Need])
		}
	} else {
		// The review records the emergency needs; the EmergencySafeguard vetoes
		// other work from them at admission and dispatch (#1017).
		for _, n := range needs.All() {
			if policy.EmergencyNeed(n) {
				r.Emergency = append(r.Emergency, n.ID)
			}
		}
		result.Emergency = r.Emergency
		for _, n := range needs.NoOps {
			if err = n.Reason.Validate(); err != nil {
				return RoundsResult{}, err
			}
		}
		r.NoOps = needs.NoOps
		if r.Incidents, result.Incidents, err = reviewIncidents(ctx, tx, needs.Incidents, b, request.Tick); err != nil {
			return RoundsResult{}, err
		}
		for _, n := range needs.Assessments {
			if policy.IsProjectKind(n.ID) {
				p, err := reviewProject(ctx, tx, oldProjects, n, b, request.Tick)
				if err != nil {
					return RoundsResult{}, err
				}
				r.Projects = append(r.Projects, RoutineProject{n.ID, p.Project.ID})
				result.Projects = append(result.Projects, p)
				states = append(states, p)
				continue
			}
			g, exists := old[n.ID]
			if !exists || g.Standard.Status == domain.StandardVoided {
				id, err := mintRoutineStandardID(ctx, tx, World{b.Colony, b.Load, b.Map}, n.ID)
				if err != nil {
					return RoundsResult{}, err
				}
				goal, err := domain.NewStandard(id, n.Priority, b, request.Tick)
				if err != nil {
					return RoundsResult{}, err
				}
				if err = createStandard(ctx, tx, goal); err != nil {
					return RoundsResult{}, err
				}
				g = StandardState{Standard: goal}
			}
			if n.Need == domain.NeedRecovered {
				if err = cancelUndispatchedMethods(ctx, tx, g); err != nil {
					return RoundsResult{}, err
				}
			}
			open, err := goalOpenWork(ctx, tx, g)
			if err != nil {
				return RoundsResult{}, err
			}
			next := g.Standard
			next.Priority = n.Priority
			next, err = domain.ReviewStandard(next, b, request.Tick, n.Need, open)
			if err != nil {
				return RoundsResult{}, err
			}
			g, err = saveStandard(ctx, tx, g, next)
			if err != nil {
				return RoundsResult{}, err
			}
			r.Goals = append(r.Goals, RoutineGoal{n.ID, g.Standard.ID})
			result.Goals = append(result.Goals, g)
			states = append(states, g)
		}
	}
	if request.Enabled {
		r.Progress, err = routineProgress(ctx, tx, request, previous, reset, needs, states)
		if err != nil {
			return RoundsResult{}, err
		}
		stage := policy.ReviewColonyStage(previousStage, policy.StageColonyFacts(needs, request.Facts, request.Policy, r.Progress), request.Policy.Stages(), request.Tick)
		r.Stage = &stage
		var development policy.DevelopmentState
		var ready policy.ReadyWorkReport
		var shadow policy.ShadowRank
		var records []DependencyRecord
		if !reset {
			records = previous.Dependencies
		}
		development, ready, shadow, r.Dependencies, err = rankRoutineDevelopment(ctx, tx, request, needs, states, previous.Development.State(), policy.WithheldLabor(r.Progress), stage, records)
		if err != nil {
			return RoundsResult{}, err
		}
		unavailable := map[domain.ConcernID]bool{}
		for _, n := range needs.All() {
			unavailable[n.ID] = n.MethodUnavailable
		}
		r.Progress = policy.HoldProgress(r.Progress, development.Rows, policy.WithheldLabor(r.Progress), unavailable)
		r.Development = developmentRecord(development)
		r.ReadyWork = &ready
		r.ShadowRank = &shadow
		r.Recovery, err = routineRecovery(ctx, tx, request.Facts, disaster, r, request.Tick)
		if err != nil {
			return RoundsResult{}, err
		}
	}
	if rows, known := request.Facts.Upkeep.Clearance.Value(); known {
		var err error
		r.ClearanceHolds, r.SalvageTarget, err = policy.ReviewClearanceHolds(request.Policy, request.Facts, rows)
		if err != nil {
			return RoundsResult{}, err
		}
	} else {
		r.ClearanceHolds = previous.ClearanceHolds
	}
	if _, known := request.Facts.Upkeep.Shrines.Value(); known {
		r.ShrineHolds = slices.Clone(request.Facts.ShrineHolds)
	} else {
		r.ShrineHolds = previous.ShrineHolds
	}
	markShrineHolds(r.ShrineHolds, r.ShrineStep)
	data, err := json.Marshal(r)
	if err != nil {
		return RoundsResult{}, err
	}
	if len(data) > 1024*1024 {
		return RoundsResult{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoundsResult{}, err
	}
	result.Review = r
	return result, nil
}

// reviewProject files and reviews one Project need: it reuses the world's
// Project row, opens a new one when none stands or the finished one regressed
// with no work open (the finished row stays as its record, #1022), and reviews
// it against the assessment.
func reviewProject(ctx context.Context, tx *sql.Tx, old map[domain.ConcernID]ProjectState, n policy.RoutineAssessment, current domain.GenerationSnapshot, tick domain.Tick) (ProjectState, error) {
	p, exists := old[n.ID]
	if exists && domain.ProjectRegressed(p.Project, n.Need, false) {
		open, err := goalOpenWork(ctx, tx, p)
		if err != nil {
			return ProjectState{}, err
		}
		exists = open
	}
	if !exists || p.Project.Status == domain.ProjectVoided {
		id, err := mintRoutineProjectID(ctx, tx, World{current.Colony, current.Load, current.Map}, n.ID)
		if err != nil {
			return ProjectState{}, err
		}
		project, err := domain.NewProject(id, n.ID, n.Priority, current, tick)
		if err != nil {
			return ProjectState{}, err
		}
		if err = createProject(ctx, tx, project); err != nil {
			return ProjectState{}, err
		}
		p = ProjectState{Project: project}
	}
	if n.Need == domain.NeedRecovered {
		if err := cancelUndispatchedMethods(ctx, tx, p); err != nil {
			return ProjectState{}, err
		}
	}
	open, err := goalOpenWork(ctx, tx, p)
	if err != nil {
		return ProjectState{}, err
	}
	next := p.Project
	next.Priority = n.Priority
	if next, err = domain.ReviewProject(next, current, tick, n.Need, open); err != nil {
		return ProjectState{}, err
	}
	return saveProject(ctx, tx, p, next)
}

// routineRoster records the roster planner's report when this review planned
// work, keeps the previous report while it did not (a disabled review, an
// unknown census) and drops it with the rest of the history on a world
// change or tick rewind.
func routineRoster(request RoundsRequest, previous Rounds, reset bool) *policy.WorkRosterReport {
	coverage, known := request.Facts.WorkRoster.Value()
	if !request.Enabled || !known {
		if reset {
			return nil
		}
		return previous.Roster
	}
	report := &policy.WorkRosterReport{Tick: request.Tick, Coverage: append([]policy.WorkCoverage{}, coverage...)}
	if decaying, ok := request.Facts.WorkDecaying.Value(); ok {
		report.Decaying = append([]policy.DecayingSkill(nil), decaying...)
	}
	profiles, _ := request.Facts.WorkProfiles.Value()
	report.Profiles = append([]policy.PawnProfile{}, profiles...)
	if help := request.Facts.WorkHelp; help != nil && help.Tick <= request.Tick {
		copied := *help
		report.Help = &copied
	}
	return report
}
