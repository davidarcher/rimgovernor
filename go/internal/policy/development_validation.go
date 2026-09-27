package policy

import (
	"errors"
	"math"
)

func ValidateDevelopmentState(s DevelopmentState) error {
	if s.Snapshot.Validate() != nil || s.Tick < 0 || s.Capacity < 0 || s.Capacity > 8 {
		return errors.New("invalid development state")
	}
	if labor, known := s.Labor.Value(); known {
		for w, n := range labor {
			if !validResource(Resource(w)) || n < 0 || n > 4096 {
				return errors.New("invalid development labor")
			}
		}
	}
	for _, h := range s.Holds {
		if h.Goal != "" && !validResource(Resource(h.Goal)) || !validLabor(h.Labor) {
			return errors.New("invalid development hold")
		}
	}
	if census, known := s.Census.Value(); known {
		for _, w := range census {
			if w.ID == "" || !validLabor(LaborProfile(w.Work)) {
				return errors.New("invalid development census")
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
	for _, row := range s.Rows {
		deficit, k := row.Deficit.Value()
		if risk, rk := row.Risk.Value(); rk && (math.IsNaN(risk) || risk < 0 || risk > 1) {
			return errors.New("invalid development risk")
		}
		if since, k := row.LaborIdleSince.Value(); k && (since < 0 || since > s.Tick) {
			return errors.New("invalid development idle age")
		}
		if !validLaborEvidence(row.LaborEvidence) {
			return errors.New("invalid development labor evidence")
		}
		if !validResource(Resource(row.Goal)) || seen[row.Goal] || row.WaitingSince < 0 || row.WaitingSince > s.Tick || math.IsNaN(row.Score) || math.IsInf(row.Score, 0) || row.Score < 0 || k && (math.IsNaN(deficit) || math.IsInf(deficit, 0) || deficit < 0 || deficit > 1) || row.Committed != committed[row.Goal] {
			return errors.New("invalid development row")
		}
		seen[row.Goal] = true
		switch row.Reason {
		case "", DevelopmentCancelled, DevelopmentEmergency, DevelopmentStartup, DevelopmentBlocked, DevelopmentCommitted, DevelopmentLaborIdle, DevelopmentWorkersUnknown, DevelopmentNoWorkers, DevelopmentUnknown, DevelopmentCapacity, DevelopmentMethodUnavailable, DevelopmentRisk, DevelopmentDisabled, DevelopmentStage, DevelopmentOvercommitted:
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
			if row.Reason != "" || row.Committed || !k {
				return errors.New("invalid development selection")
			}
		}
	}
	return nil
}
