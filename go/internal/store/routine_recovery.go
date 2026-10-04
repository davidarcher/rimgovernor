package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recovery retains the exact inputs for reproducible proposal readback. Nullable
// values are storage boundaries, not known zero/false domain observations.
type RoutineRecovery struct {
	// Incident is the RecoverDisasterServices occurrence the selection
	// serves (#1078).
	Incident  domain.IncidentID
	Used      []domain.MethodID
	Safety    *RoutineRecoverySafety
	Workers   *[]RoutineRecoveryWorker
	Buildings *[]RoutineRecoveryBuilding
	Selection policy.RecoverySelection
}
type RoutineRecoverySafety struct {
	Restrictions []RoutineRecoveryRestriction
}
type RoutineRecoveryRestriction struct {
	Pawn policy.PawnID
	Area *string
}
type RoutineRecoveryWorker struct {
	Pawn                                        policy.PawnID
	Dead, Downed, Drafted, Mental, PlayerForced *bool
}
type RoutineRecoveryBuilding struct {
	ID                                                    string
	UsesHitPoints, Broken, Forbidden, Burning, Refuelable *bool
	HitPoints, MaxHitPoints                               *int64
	Fuel, FuelTarget                                      *float64
}

func recoveryRecord(p policy.RecoveryPlanning) *RoutineRecovery {
	r := &RoutineRecovery{}
	if s, k := p.Safety.Value(); k {
		r.Safety = &RoutineRecoverySafety{}
		for _, v := range s.Restrictions {
			r.Safety.Restrictions = append(r.Safety.Restrictions, RoutineRecoveryRestriction{v.Pawn, moodValue(v.Area)})
		}
	}
	if workers, k := p.Workers.Value(); k {
		rows := make([]RoutineRecoveryWorker, 0, len(workers))
		for _, v := range workers {
			rows = append(rows, RoutineRecoveryWorker{v.Pawn, moodValue(v.Dead), moodValue(v.Downed), moodValue(v.Drafted), moodValue(v.Mental), moodValue(v.PlayerForced)})
		}
		r.Workers = &rows
	}
	if buildings, k := p.Buildings.Value(); k {
		rows := make([]RoutineRecoveryBuilding, 0, len(buildings))
		for _, v := range buildings {
			rows = append(rows, RoutineRecoveryBuilding{v.ID, moodValue(v.UsesHitPoints), moodValue(v.Broken), moodValue(v.Forbidden), moodValue(v.Burning), moodValue(v.Refuelable), moodValue(v.HitPoints), moodValue(v.MaxHitPoints), moodValue(v.Fuel), moodValue(v.FuelTarget)})
		}
		r.Buildings = &rows
	}
	return r
}
func (r *RoutineRecovery) planning() policy.RecoveryPlanning {
	p := policy.RecoveryPlanning{}
	if r.Safety != nil {
		s := policy.RecoverySafety{}
		for _, v := range r.Safety.Restrictions {
			s.Restrictions = append(s.Restrictions, policy.RecoveryRestriction{Pawn: v.Pawn, Area: moodFact(v.Area)})
		}
		p.Safety = domain.Known(s)
	}
	if r.Workers != nil {
		rows := make([]policy.RecoveryWorker, 0, len(*r.Workers))
		for _, v := range *r.Workers {
			rows = append(rows, policy.RecoveryWorker{Pawn: v.Pawn, Dead: moodFact(v.Dead), Downed: moodFact(v.Downed), Drafted: moodFact(v.Drafted), Mental: moodFact(v.Mental), PlayerForced: moodFact(v.PlayerForced)})
		}
		p.Workers = domain.Known(rows)
	}
	if r.Buildings != nil {
		rows := make([]policy.RecoveryBuilding, 0, len(*r.Buildings))
		for _, v := range *r.Buildings {
			rows = append(rows, policy.RecoveryBuilding{ID: v.ID, UsesHitPoints: moodFact(v.UsesHitPoints), Broken: moodFact(v.Broken), Forbidden: moodFact(v.Forbidden), Burning: moodFact(v.Burning), Refuelable: moodFact(v.Refuelable), HitPoints: moodFact(v.HitPoints), MaxHitPoints: moodFact(v.MaxHitPoints), Fuel: moodFact(v.Fuel), FuelTarget: moodFact(v.FuelTarget)})
		}
		p.Buildings = domain.Known(rows)
	}
	return p
}
func validateRoutineRecovery(r Rounds) error {
	if r.Recovery == nil {
		return nil
	}
	v := r.Recovery
	if !r.Enabled || r.Disaster == nil || v.Selection.Tick != r.Tick || v.Selection.Validate() != nil {
		return errors.New("invalid routine recovery selection")
	}
	if b, ok := r.incidentBinding(v.Incident); !ok || b.Kind != policy.RecoverDisasterServices {
		return errors.New("recovery selection lost incident ownership")
	}
	selection, err := policy.SelectRecoveryMethods(v.planning(), r.Disaster, v.Used, r.Tick)
	if err != nil {
		return err
	}
	actual, _ := json.Marshal(v.Selection)
	expected, _ := json.Marshal(selection)
	if !bytes.Equal(actual, expected) {
		return errors.New("recovery proposal differs from observed inputs")
	}
	return nil
}

func routineRecovery(ctx context.Context, tx *sql.Tx, f policy.RoutineFacts, h *policy.DisasterHistory, review Rounds, tick domain.Tick) (*RoutineRecovery, error) {
	if h == nil {
		return nil, nil
	}
	binding, ok := review.Incident(policy.RecoverDisasterServices)
	if !ok {
		return nil, nil
	}
	state, err := loadIncident(ctx, tx, binding.Incident)
	if err != nil {
		return nil, err
	}
	if review.VetoIncident(state.Incident) != "" {
		return nil, nil
	}
	r := recoveryRecord(policy.RecoveryPlanning{Safety: f.RecoverySafety, Workers: f.RecoveryWorkers, Buildings: f.RecoveryBuildings})
	r.Incident = binding.Incident
	rows, err := tx.QueryContext(ctx, "SELECT method_id FROM incident_methods WHERE incident_id=? ORDER BY method_id LIMIT 257", r.Incident)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var id domain.MethodID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		r.Used = append(r.Used, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	r.Selection, err = policy.SelectRecoveryMethods(r.planning(), h, r.Used, tick)
	return r, err
}
