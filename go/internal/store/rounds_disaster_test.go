package store

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoundsDisasterDurableManualAndContext(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{{ID: "1", Definition: "ColdSnap"}})
	r.Facts.RecoveryBuildings = domain.Known([]policy.RecoveryBuilding{{ID: "wall", UsesHitPoints: domain.Known(true), HitPoints: domain.Known(int64(50)), MaxHitPoints: domain.Known(int64(100)), Broken: domain.Known(false), Forbidden: domain.Known(false), Burning: domain.Known(false), Refuelable: domain.Known(false)}})
	out := reviewRounds(t, s, &r)
	if out.Review.Disaster == nil || len(out.Review.Disaster.Damaged) != 1 {
		t.Fatal(out.Review.Disaster)
	}
	first := out.Review.Disaster
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Disaster, first) {
		t.Fatal(loaded.Disaster, err)
	}
	r.Enabled = false
	out = reviewRounds(t, s, &r)
	if !reflect.DeepEqual(out.Review.Disaster, first) || out.Review.VetoIncident(roundsIncident(t, out, policy.RecoverDisasterServices).Incident) != "control paused" {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	if _, err = s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
	r.Enabled = true
	r.Current.Native++
	r.Facts.DisasterConditions = domain.Unknown[[]policy.DisasterCondition]()
	out = reviewRounds(t, s, &r)
	if out.Review.Disaster.Phase != policy.DisasterUnknown || roundsIncidentNeed(t, out, policy.RecoverDisasterServices) != domain.SituationUnclear {
		t.Fatal(out)
	}
	r.Current.Load = "replacement"
	out = reviewRounds(t, s, &r)
	if out.Review.Disaster != nil {
		t.Fatal("new world retained disaster")
	}
	if _, err = s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
}
