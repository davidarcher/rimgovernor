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
	Comfort                policy.ComfortHistory
	Goals                  []RoutineGoal
	Development            RoutineDevelopment
}

type RoutineReviewRequest struct {
	Revision               uint64
	WorkPreferenceRevision uint64
	Current                domain.GenerationSnapshot
	Tick                   domain.Tick
	Enabled                bool
	Policy                 policy.RoutinePolicy
	Facts                  policy.RoutineFacts
}

type RoutineReviewResult struct {
	Review RoutineReview
	Needs  policy.RoutineNeeds
	Goals  []GoalState
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
	if len(data) > 512*1024 {
		return r, ErrCapacity
	}
	if err := json.Unmarshal(data, &r); err != nil {
		return r, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Revision == 0 || r.Snapshot.Validate() != nil || r.Tick < 0 || len(r.Goals) > 295 {
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
	} else {
		actual, _ := json.Marshal(r.Development)
		empty, _ := json.Marshal(RoutineDevelopment{})
		if !bytes.Equal(actual, empty) {
			return RoutineReview{}, errors.New("disabled routine retains development selection")
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
	sleeping := previous.Sleeping
	comfort := previous.Comfort
	mood := previous.moodHistory()
	disaster := previous.Disaster
	if reset {
		latches = policy.RoutineLatches{}
		medical = policy.MedicalCareHistory{}
		supplies = policy.StartingSupplies{}
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
		supplies, request.Facts.ForbiddenSupplies, err = policy.ReviewStartingSupplies(request.Facts.StartingSupplyCells, supplies)
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
		for _, binding := range r.Goals {
			result.Goals = append(result.Goals, old[binding.Need])
		}
	} else {
		emergency := false
		for _, n := range needs.Assessments {
			if n.Priority < 2 && n.ID != policy.ConfirmColonyNames && n.Need != domain.NeedRecovered {
				emergency = true
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
	data, err := json.Marshal(r)
	if err != nil {
		return RoutineReviewResult{}, err
	}
	if len(data) > 512*1024 {
		return RoutineReviewResult{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoutineReviewResult{}, err
	}
	result.Review = r
	return result, nil
}
