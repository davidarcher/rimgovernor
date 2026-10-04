package store

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func recoveryRequest() RoundsRequest {
	r := roundsRequest()
	k := domain.Known(false)
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{{ID: "fallout", Definition: "ToxicFallout"}})
	r.Facts.RecoverySafety = domain.Known(policy.RecoverySafety{Restrictions: []policy.RecoveryRestriction{{Pawn: "pawn", Area: domain.Known("player-area")}}})
	r.Facts.RecoveryWorkers = domain.Known([]policy.RecoveryWorker{{Pawn: "pawn", Dead: k, Downed: k, Drafted: k, Mental: k, PlayerForced: k}})
	known := domain.Known(true)
	r.Facts.RecoveryBuildings = domain.Known([]policy.RecoveryBuilding{{ID: "wall", UsesHitPoints: known, HitPoints: domain.Known(int64(50)), MaxHitPoints: domain.Known(int64(100)), Broken: k, Forbidden: k, Burning: k, Refuelable: k}})
	return r
}
func TestRoundsRecoveryProposalRestartManualAndCancellation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := recoveryRequest()
	out := reviewRounds(t, s, &r)
	first := out.Review.Recovery
	if first == nil || first.Selection.Reason != policy.RecoveryAdmissionRequired || len(first.Selection.Candidates) != 1 {
		t.Fatal(first)
	}
	if i := roundsIncident(t, out, policy.RecoverDisasterServices); roundsIncidentNeed(t, out, policy.RecoverDisasterServices) != domain.SituationActive || i.Incident.Priority != 2 {
		t.Fatal(i)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Recovery, first) {
		t.Fatal(loaded.Recovery, err)
	}
	r.Enabled = false
	out = reviewRounds(t, s, &r)
	if out.Review.Recovery != nil || out.Review.Disaster == nil {
		t.Fatal("Manual retained proposals or lost history")
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err = s.LoadRounds(ctx)
	if err != nil || loaded.Recovery != nil {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Current.Native++
	out = reviewRounds(t, s, &r)
	// A resume of the same world keeps the occurrence; the proposal binds
	// to it again.
	if out.Review.Recovery == nil || out.Review.Recovery.Incident != first.Incident {
		t.Fatal("resume replaced the recovery incident", out.Review.Recovery)
	}
	if _, err = s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
	r.Current.Load = "replacement"
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{})
	out = reviewRounds(t, s, &r)
	if out.Review.Disaster != nil || out.Review.Recovery != nil {
		t.Fatal("replacement world retained proposals")
	}
}
func TestRoundsRecoveryUnknownWorkerDoesNotBecomeAvailableAfterRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := recoveryRequest()
	workers, _ := r.Facts.RecoveryWorkers.Value()
	workers[0].Dead = domain.Unknown[bool]()
	r.Facts.RecoveryWorkers = domain.Known(workers)
	out := reviewRounds(t, s, &r)
	if out.Review.Recovery.Selection.Reason != policy.RecoveryFactsUnknown {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || loaded.Recovery.Selection.Reason != policy.RecoveryFactsUnknown || (*loaded.Recovery.Workers)[0].Dead != nil {
		t.Fatal(loaded, err)
	}
}
func TestRoundsRecoveryRejectsCorruptProposalInputs(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"target", "worker", "restriction", "incident", "disabled", "used"} {
		t.Run(name, func(t *testing.T) {
			s := open(t, memoryPath(t))
			defer s.Close()
			r := recoveryRequest()
			out := reviewRounds(t, s, &r)
			v := out.Review
			switch name {
			case "target":
				v.Recovery.Selection.Candidates[0].Building = "outside"
			case "worker":
				value := true
				(*v.Recovery.Workers)[0].Dead = &value
			case "restriction":
				value := "another"
				v.Recovery.Safety.Restrictions[0].Area = &value
			case "incident":
				v.Recovery.Incident = "other"
			case "disabled":
				v.Enabled = false
			case "used":
				v.Recovery.Used = append(v.Recovery.Used, v.Recovery.Selection.Candidates[0].ID)
			}
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE rounds SET payload=? WHERE singleton=1", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRounds(context.Background()); err == nil {
				t.Fatal("corrupt recovery accepted")
			}
		})
	}
}

func TestRoundsRecoverySkipsSharedMethodHistory(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := recoveryRequest()
	out := reviewRounds(t, s, &r)
	candidate := out.Review.Recovery.Selection.Candidates[0]
	i := roundsIncident(t, out, policy.RecoverDisasterServices)
	if _, err := s.CommitIncidentMethod(ctx, i.Incident.ID, candidate.ID, "", plan(t, "recovery-plan", "recovery-action")); err != nil {
		t.Fatal(err)
	}
	out = reviewRounds(t, s, &r)
	if out.Review.Recovery == nil || len(out.Review.Recovery.Used) != 1 || out.Review.Recovery.Used[0] != candidate.ID || out.Review.Recovery.Selection.Reason != policy.RecoveryMethodsSeen {
		t.Fatal("shared method was proposed twice", out.Review.Recovery)
	}
	if _, err := s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestRoundsRecoveryEmergencyVetoesCandidatesUntilObservedClearance(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := recoveryRequest()
	r.Facts.Hostiles = domain.Known(int64(1))
	out := reviewRounds(t, s, &r)
	if out.Review.VetoIncident(roundsIncident(t, out, policy.RecoverDisasterServices).Incident) == "" || out.Review.Recovery != nil {
		t.Fatal("emergency allowed recovery proposals", out.Review.Recovery)
	}
	r.Facts.Hostiles = domain.Known(int64(0))
	out = reviewRounds(t, s, &r)
	if out.Review.Recovery == nil || out.Review.Recovery.Selection.Reason != policy.RecoveryAdmissionRequired {
		t.Fatal("observed clearance did not restore planning")
	}
}

func TestRoundsRecoveryForcedEvidenceSurvivesRestart(t *testing.T) {
	t.Parallel()
	for _, forced := range []domain.Fact[bool]{domain.Known(true), domain.Unknown[bool]()} {
		path := memoryPath(t)
		s := open(t, path)
		r := recoveryRequest()
		workers, _ := r.Facts.RecoveryWorkers.Value()
		workers[0].PlayerForced = forced
		r.Facts.RecoveryWorkers = domain.Known(workers)
		out := reviewRounds(t, s, &r)
		if out.Review.Recovery.Selection.Reason != policy.RecoveryAdmissionRequired {
			t.Fatal("forced evidence blocked recovery")
		}
		s.Close()
		s = open(t, path)
		loaded, err := s.LoadRounds(context.Background())
		s.Close()
		if err != nil {
			t.Fatal(err)
		}
		if loaded.Recovery.Selection.Reason != policy.RecoveryAdmissionRequired {
			t.Fatal("restart blocked recovery")
		}
		got := (*loaded.Recovery.Workers)[0].PlayerForced
		value, known := forced.Value()
		if (got != nil) != known || got != nil && *got != value {
			t.Fatal("lost forced-job evidence", got)
		}
	}
}
