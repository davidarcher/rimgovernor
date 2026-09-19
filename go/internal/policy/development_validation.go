package policy

import (
	"errors"
	"math"
)

func ValidateDevelopmentState(s DevelopmentState) error {
	if s.Snapshot.Validate() != nil || s.Tick < 0 || s.Capacity < 0 || s.Capacity > 8 || len(s.Rows) > 256 || len(s.Committed) > 4096 {
		return errors.New("invalid development state")
	}
	if labor, known := s.Labor.Value(); known {
		if len(labor) > 256 {
			return errors.New("invalid development labor")
		}
		for w, n := range labor {
			if !validResource(Resource(w)) || n < 0 || n > 4096 {
				return errors.New("invalid development labor")
			}
		}
	}
	workers, known := s.Workers.Value()
	if known && (workers < 0 || workers > 4096 || s.Capacity > workers) || !known && s.Capacity != 0 {
		return errors.New("invalid development worker capacity")
	}
	committed := map[GoalID]bool{}
	for _, id := range s.Committed {
		if !validResource(Resource(id)) || committed[id] {
			return errors.New("invalid development commitments")
		}
		committed[id] = true
	}
	seen := map[GoalID]bool{}
	selected := 0
	for _, row := range s.Rows {
		deficit, k := row.Deficit.Value()
		if risk, rk := row.Risk.Value(); rk && (math.IsNaN(risk) || risk < 0 || risk > 1) {
			return errors.New("invalid development risk")
		}
		if since, k := row.LaborIdleSince.Value(); k && (since < 0 || since > s.Tick) {
			return errors.New("invalid development idle age")
		}
		if !validResource(Resource(row.Goal)) || seen[row.Goal] || row.WaitingSince < 0 || row.WaitingSince > s.Tick || math.IsNaN(row.Score) || math.IsInf(row.Score, 0) || row.Score < 0 || k && (math.IsNaN(deficit) || math.IsInf(deficit, 0) || deficit < 0 || deficit > 1) || row.Committed != committed[row.Goal] {
			return errors.New("invalid development row")
		}
		seen[row.Goal] = true
		switch row.Reason {
		case "", DevelopmentCancelled, DevelopmentAdviser, DevelopmentEmergency, DevelopmentStartup, DevelopmentBlocked, DevelopmentCommitted, DevelopmentLaborIdle, DevelopmentWorkersUnknown, DevelopmentNoWorkers, DevelopmentUnknown, DevelopmentCapacity, DevelopmentMethodUnavailable, DevelopmentRisk, DevelopmentDisabled:
			if row.Bottleneck != "" {
				return errors.New("invalid development bottleneck")
			}
		case DevelopmentLabor:
			if !validResource(Resource(row.Bottleneck)) {
				return errors.New("invalid development bottleneck")
			}
		default:
			return errors.New("invalid development reason")
		}
		if row.Selected {
			selected++
			if row.Reason != "" || row.Committed || !k {
				return errors.New("invalid development selection")
			}
		} else if row.Granted {
			return errors.New("invalid development grant")
		}
	}
	if selected > max(0, s.Capacity-len(s.Committed)) {
		return errors.New("development capacity exceeded")
	}
	return nil
}
