package store

import (
	"bytes"
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type RoutineGoal struct {
	Need domain.GoalID
	Goal domain.GoalID
}

// RoutineReview is the durable review cursor and hysteresis history. Goal
// bindings retain semantic needs while old executable plans keep their identity.
type RoutineReview struct {
	ClearanceHolds         []policy.ClearanceHold  `json:",omitempty"`
	Recovery               *RoutineRecovery        `json:",omitempty"`
	Disaster               *policy.DisasterHistory `json:",omitempty"`
	Mood                   *RoutineMood            `json:",omitempty"`
	MoodMethods            []RoutineMoodMethod     `json:",omitempty"`
	Sleeping               policy.SleepingHistory
	Revision               uint64
	WorkPreferenceRevision uint64
	Snapshot               domain.GenerationSnapshot
	Tick                   domain.Tick
	Enabled                bool
	Latches                policy.RoutineLatches
	MedicalCare            policy.MedicalCareHistory
	StartingSupplies       policy.StartingSupplies
	EventLoot              policy.EventLootHistory
	Comfort                policy.ComfortHistory
	Goals                  []RoutineGoal
	Development            RoutineDevelopment
	// AsOf is the tick each census section the review read described,
	// by facts.Section name (#354); absent before any review filed one.
	AsOf map[string]int64 `json:",omitempty"`
}

type RoutineReviewRequest struct {
	Revision               uint64
	WorkPreferenceRevision uint64
	Current                domain.GenerationSnapshot
	Tick                   domain.Tick
	Enabled                bool
	Policy                 policy.RoutinePolicy
	Facts                  policy.RoutineFacts
	// PartialPlanners: only the planners a wake named follow this review,
	// so the next review must not count an unrun planner's goal idle.
	PartialPlanners bool
	// AsOf is the tick each census section described (RoutineReview.AsOf).
	AsOf map[string]int64
}

type RoutineReviewResult struct {
	Review RoutineReview
	Needs  policy.RoutineNeeds
	Goals  []GoalState
	// Emergency names the assessed needs that suspended every priority>=2
	// goal in this review (a home fire, live hostiles, a critical patient);
	// empty when nothing did. The development rows only say "emergency", so
	// this is the log's answer to which need held the colony (#221).
	Emergency []policy.GoalID
}

func loadRoutine(ctx context.Context, tx *sql.Tx) (RoutineReview, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM routine_review WHERE singleton=1").Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return RoutineReview{}, nil
		}
		return RoutineReview{}, err
	}
	var r RoutineReview
	if len(data) > 1024*1024 {
		return r, ErrCapacity
	}
	if err := json.Unmarshal(data, &r); err != nil {
		return r, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Revision == 0 || r.Snapshot.Validate() != nil || r.Tick < 0 || len(r.Goals) > 304 {
		return RoutineReview{}, errors.New("invalid routine review history")
	}
	if err := r.MedicalCare.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if err := r.moodHistory().Validate(); err != nil {
		return RoutineReview{}, err
	}
	if err := r.Disaster.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if r.Disaster != nil && r.Disaster.Observed > r.Tick {
		return RoutineReview{}, errors.New("future disaster history")
	}
	if err := validateRoutineRecovery(r); err != nil {
		return RoutineReview{}, err
	}
	var proposals []RoutineMoodMethod
	if r.Enabled {
		proposals, err = moodProposals(r.moodHistory())
		if err != nil {
			return RoutineReview{}, err
		}
	}
	if len(proposals) != len(r.MoodMethods) {
		return RoutineReview{}, errors.New("invalid mood method review")
	}
	expectedProposals, _ := json.Marshal(proposals)
	actualProposals, _ := json.Marshal(r.MoodMethods)
	if !bytes.Equal(expectedProposals, actualProposals) {
		return RoutineReview{}, errors.New("stale mood method proposal")
	}
	if err := r.EventLoot.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if err := r.StartingSupplies.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if err := r.Latches.Animals.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if err := r.Sleeping.Validate(); err != nil {
		return RoutineReview{}, err
	}
	for _, use := range r.Sleeping.Uses {
		if use.Tick > r.Tick {
			return RoutineReview{}, errors.New("future sleeping use history")
		}
	}
	if err := r.Comfort.Validate(); err != nil {
		return RoutineReview{}, err
	}
	if r.Comfort.Dining.Tick > r.Tick || r.Comfort.Recreation.Tick > r.Tick {
		return RoutineReview{}, errors.New("future comfort use history")
	}
	if r.Enabled {
		if r.Development.Snapshot != r.Snapshot || r.Development.Tick != r.Tick || policy.ValidateDevelopmentState(r.Development.State()) != nil {
			return RoutineReview{}, errors.New("invalid routine development history")
		}
	} else if len(r.Development.Rows) > 0 || r.Development.Workers != nil {
		// A disabled review keeps the last ranking (waiting ages) but
		// selects nothing: methods cannot be committed without authority.
		if r.Development.Tick > r.Tick || policy.ValidateDevelopmentState(r.Development.State()) != nil {
			return RoutineReview{}, errors.New("invalid routine development history")
		}
		for _, row := range r.Development.Rows {
			if row.Selected {
				return RoutineReview{}, errors.New("disabled routine retains development selection")
			}
		}
	}
	known, _ := policy.DetectRoutine(policy.RoutineFacts{Mood: r.moodHistory(), Disaster: r.Disaster, DisasterTick: r.Tick}, policy.RoutineLatches{}, policy.DefaultRoutinePolicy())
	allowed := map[domain.GoalID]bool{}
	optional := map[domain.GoalID]bool{}
	for _, n := range known.Assessments {
		allowed[n.ID] = true
		optional[n.ID] = n.Priority >= 3 || n.ID == policy.RecoverDisasterServices
	}
	// Manual may retain historical bindings after a world change reset their
	// observation history. Enabled bindings must match the current pawn history.
	if !r.Enabled {
		for _, binding := range r.Goals {
			if policy.IsMoodGoal(binding.Need) || binding.Need == policy.RecoverDisasterServices {
				allowed[binding.Need] = true
			}
		}
	}
	for _, row := range r.Development.Rows {
		if !optional[row.Goal] {
			return RoutineReview{}, errors.New("unknown optional routine goal")
		}
	}
	if (r.Enabled || len(r.Goals) != 0) && len(r.Goals) != len(allowed) {
		return RoutineReview{}, errors.New("incomplete routine goal bindings")
	}
	seen := map[domain.GoalID]bool{}
	identities := map[domain.GoalID]bool{}
	for _, binding := range r.Goals {
		if !allowed[binding.Need] || seen[binding.Need] || identities[binding.Goal] {
			return RoutineReview{}, errors.New("duplicate routine need")
		}
		seen[binding.Need] = true
		identities[binding.Goal] = true
		g, err := loadGoal(ctx, tx, binding.Goal)
		if err != nil {
			return RoutineReview{}, err
		}
		if r.Recovery != nil && r.Recovery.Goal == g.Goal.ID && r.Recovery.Epoch > g.Goal.Epoch {
			return RoutineReview{}, errors.New("future recovery method epoch")
		}
		if g.Goal.Source != domain.AutopilotGoal || !strings.HasPrefix(string(binding.Goal), "routine-") || !strings.HasSuffix(string(binding.Goal), "-"+string(binding.Need)) {
			return RoutineReview{}, errors.New("routine goal ownership mismatch")
		}
	}
	return r, nil
}

func (s *Store) LoadRoutineReview(ctx context.Context) (RoutineReview, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return RoutineReview{}, err
	}
	defer tx.Rollback()
	r, err := loadRoutine(ctx, tx)
	if err != nil {
		return RoutineReview{}, err
	}
	return r, tx.Commit()
}

// ReviewRoutine commits all need assessments and latch history atomically.
// It selects no methods and grants no execution authority. A disabled review
// of the same world suspends routine goals and leaves their in-flight work
// open for the next enabled review to resume; only world replacement or a
// tick rewind invalidates linked work through the same cancellation journal
// as Hands.
func (s *Store) ReviewRoutine(ctx context.Context, request RoutineReviewRequest) (RoutineReviewResult, error) {
	if request.Current.Validate() != nil || request.Tick < 0 {
		return RoutineReviewResult{}, errors.New("invalid routine scope")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RoutineReviewResult{}, err
	}
	defer tx.Rollback()
	result, err := reviewRoutineTx(ctx, tx, request)
	if err != nil {
		return RoutineReviewResult{}, err
	}
	if err = tx.Commit(); err != nil {
		return RoutineReviewResult{}, err
	}
	return result, nil
}

func reviewRoutineTx(ctx context.Context, tx *sql.Tx, request RoutineReviewRequest) (RoutineReviewResult, error) {
	if request.Enabled {
		preferences, err := loadWorkPreferences(ctx, tx, request.Current.Plan)
		if err != nil && !errors.Is(err, ErrNotFound) {
			return RoutineReviewResult{}, err
		}
		if preferences.Revision != request.WorkPreferenceRevision {
			return RoutineReviewResult{}, ErrConflict
		}
	}
	previous, err := loadRoutine(ctx, tx)
	if err != nil {
		return RoutineReviewResult{}, err
	}
	if previous.Revision != request.Revision {
		return RoutineReviewResult{}, ErrConflict
	}
	if previous.Revision == ^uint64(0) {
		return RoutineReviewResult{}, ErrCapacity
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
	needs := policy.RoutineNeeds{}
	// Stopping routine work must not depend on a successful native observation.
	if request.Enabled {
		request.Facts.OwnedStockpiles, err = stockpileClaims(ctx, tx, request.Current, request.Tick)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		request.Facts.ConstructionClaims, err = constructionClaims(ctx, tx, request.Current, request.Tick)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		request.Facts.UpkeepIssued, err = routineUpkeepIssued(ctx, tx, request.Current)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		sleepingReview, sleepingErr := policy.ReviewSleeping(request.Facts.Sleeping, sleeping, request.Tick)
		if sleepingErr != nil {
			return RoutineReviewResult{}, sleepingErr
		}
		sleeping = sleepingReview.History
		request.Facts.SleepingRecovered = sleepingReview.Recovered()
		comfortReview, comfortErr := policy.ReviewComfort(request.Facts.Comfort, comfort, request.Tick)
		if comfortErr != nil {
			return RoutineReviewResult{}, comfortErr
		}
		comfort = comfortReview.History
		request.Facts.ComfortRecovered, request.Facts.ComfortDeficit = comfortReview.Recovered(), comfortReview.Deficit()
		supplies, request.Facts.ForbiddenSupplies, err = policy.ReviewStartingSupplies(request.Facts.StartingSupplies, supplies)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		loot, request.Facts.EventLootPending, err = policy.ReviewEventLoot(request.Facts.EventLoot, loot)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		medical, err = policy.ReviewMedicalCare(request.Facts.MedicalPawns, medical)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		request.Facts.MedicalCareRecovered = medical.Recovered()
		mood, err = policy.ReviewMood(request.Facts.MoodPawns, mood)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		request.Facts.Mood = mood
		request.Facts.Disaster, request.Facts.DisasterTick = disaster, request.Tick
		needs, err = policy.DetectRoutine(request.Facts, latches, request.Policy)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		disaster = needs.Disaster
		if needs.Latches.Refrigeration {
			needs.Latches.RefrigerationSince = request.Tick
			if latches.Refrigeration && latches.RefrigerationSince > 0 {
				needs.Latches.RefrigerationSince = latches.RefrigerationSince
			}
		}
	}
	old := map[domain.GoalID]GoalState{}
	assessed := map[domain.GoalID]bool{}
	for _, n := range needs.Assessments {
		assessed[n.ID] = true
	}
	if request.Enabled {
		if err = retireRoutinePlans(ctx, tx, request.Current, request.Tick); err != nil {
			return RoutineReviewResult{}, err
		}
	}
	for _, binding := range previous.Goals {
		g, err := loadGoal(ctx, tx, binding.Goal)
		if err != nil {
			return RoutineReviewResult{}, err
		}
		if changed || (request.Enabled && !assessed[binding.Need]) {
			if g.Goal.Status != domain.GoalCancelled && g.Goal.Status != domain.GoalInvalidated {
				if err = cancelGoalMethods(ctx, tx, g); err != nil {
					return RoutineReviewResult{}, err
				}
				next := g.Goal
				next.Status = domain.GoalInvalidated
				g, err = saveGoal(ctx, tx, g, next)
				if err != nil {
					return RoutineReviewResult{}, err
				}
			}
		} else if !request.Enabled && g.Goal.Status == domain.GoalActive {
			// Paused control admits no new work; native designations already
			// issued keep progressing and the resumed goal adopts the result.
			next := g.Goal
			next.Status = domain.GoalSuspended
			g, err = saveGoal(ctx, tx, g, next)
			if err != nil {
				return RoutineReviewResult{}, err
			}
		}
		old[binding.Need] = g
	}
	// Keep disabled bindings available for review. An enabled review replaces
	// invalidated bindings, so their clean history can leave active capacity.
	retained := map[domain.GoalID]bool{}
	for _, g := range old {
		if !request.Enabled || g.Goal.Status != domain.GoalInvalidated {
			retained[g.Goal.ID] = true
		}
	}
	if err = retireRoutineGoals(ctx, tx, retained); err != nil {
		return RoutineReviewResult{}, err
	}
	r := RoutineReview{Revision: previous.Revision + 1, WorkPreferenceRevision: request.WorkPreferenceRevision, Snapshot: b, Tick: request.Tick, Enabled: request.Enabled, Latches: needs.Latches}
	r.MedicalCare = medical
	r.StartingSupplies = supplies
	r.EventLoot = loot
	if request.Enabled && len(request.AsOf) > 0 {
		r.AsOf = request.AsOf
	}
	r.Comfort = comfort
	r.Sleeping = sleeping
	r.Mood = moodRecord(mood)
	r.Disaster = disaster
	if request.Enabled {
		r.MoodMethods, err = moodProposals(mood)
		if err != nil {
			return RoutineReviewResult{}, err
		}
	}
	result := RoutineReviewResult{Needs: needs}
	if !request.Enabled {
		r.WorkPreferenceRevision = previous.WorkPreferenceRevision
		r.Goals = previous.Goals
		r.Latches = latches
		// Manual mode, a player interruption or a restart's first review
		// before authority returns does not rank; the last ranking stays
		// so waiting ages survive it. Only a world change or tick rewind
		// resets ranking history.
		r.Development = previous.Development
		r.Development.Rows = append([]RoutineDevelopmentRow(nil), previous.Development.Rows...)
		for i := range r.Development.Rows {
			if row := &r.Development.Rows[i]; row.Selected || row.Reason == "" {
				row.Selected, row.Reason = false, policy.DevelopmentDisabled
			}
		}
		for _, binding := range r.Goals {
			result.Goals = append(result.Goals, old[binding.Need])
		}
	} else {
		// A mental break (a priority-1 mood goal) is not an emergency: it
		// clears only as ticks pass, so suspending every other goal would
		// leave the clock with no work and never let the break end.
		emergency := false
		for _, n := range needs.Assessments {
			if n.Priority < 2 && n.ID != policy.ConfirmColonyNames && n.ID != policy.AnswerDialog && !policy.IsMoodGoal(n.ID) && n.Need != domain.NeedRecovered && !n.MethodUnavailable {
				emergency = true
				result.Emergency = append(result.Emergency, n.ID)
			}
		}
		for _, n := range needs.Assessments {
			g, exists := old[n.ID]
			if !exists || g.Goal.Status == domain.GoalInvalidated {
				// Include the durable review revision so tick rewinds cannot reuse
				// invalidated identities, even in an otherwise identical world.
				digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d/%d", b.Colony, b.Load, b.Map, r.Revision)))
				id := domain.GoalID(fmt.Sprintf("routine-%x-%s", digest[:8], n.ID))
				goal, err := domain.NewGoal(id, domain.AutopilotGoal, n.Priority, b, request.Tick)
				if err != nil {
					return RoutineReviewResult{}, err
				}
				if err = createGoal(ctx, tx, goal); err != nil {
					return RoutineReviewResult{}, err
				}
				g = GoalState{Goal: goal}
			}
			if n.Need == domain.NeedRecovered {
				if err = cancelUndispatchedGoalMethods(ctx, tx, g); err != nil {
					return RoutineReviewResult{}, err
				}
			}
			open, err := goalOpenWork(ctx, tx, g)
			if err != nil {
				return RoutineReviewResult{}, err
			}
			next := g.Goal
			next.Priority = n.Priority
			next, err = domain.ReviewGoal(next, b, request.Tick, n.Need, emergency, open)
			if err != nil {
				return RoutineReviewResult{}, err
			}
			g, err = saveGoal(ctx, tx, g, next)
			if err != nil {
				return RoutineReviewResult{}, err
			}
			r.Goals = append(r.Goals, RoutineGoal{n.ID, g.Goal.ID})
			result.Goals = append(result.Goals, g)
		}
	}
	if request.Enabled {
		var development policy.DevelopmentState
		development, err = rankRoutineDevelopment(ctx, tx, request, needs, result.Goals, previous.Development.State())
		if err != nil {
			return RoutineReviewResult{}, err
		}
		r.Development = developmentRecord(development)
		r.Recovery, err = routineRecovery(ctx, tx, request.Facts, disaster, r.Goals, result.Goals, request.Tick)
		if err != nil {
			return RoutineReviewResult{}, err
		}
	}
	if rows, known := request.Facts.Upkeep.Clearance.Value(); known {
		r.ClearanceHolds = policy.SelectHomeClearance(rows, domain.Cell{}).Holds
	} else {
		r.ClearanceHolds = previous.ClearanceHolds
	}
	data, err := json.Marshal(r)
	if err != nil {
		return RoutineReviewResult{}, err
	}
	if len(data) > 1024*1024 {
		return RoutineReviewResult{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoutineReviewResult{}, err
	}
	result.Review = r
	return result, nil
}
