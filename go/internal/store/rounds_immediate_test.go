package store

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestImmediateRoundsPreserveOrdinaryStateAndTimestamps(t *testing.T) {
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := recoveryRequest()
	before := reviewRounds(t, s, &r)
	g := roundsGoal(t, before, policy.MaintainResource)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood", plan(t, "wood-plan", "wood-action")); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil {
		t.Fatal(err)
	}
	p, err := s.LoadPlan(ctx, "wood-plan")
	if err != nil {
		t.Fatal(err)
	}
	r.Immediate, r.Tick = true, 200
	r.Facts = policy.RoundsFacts{Hostiles: domain.Known(int64(2)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), SafeAreaOwed: domain.Known(false), Upkeep: policy.UpkeepObservation{Fires: domain.Known([]policy.UpkeepFire{})}}
	after := reviewRounds(t, s, &r)
	if !after.Review.Immediate || after.Review.Tick != 200 || after.Review.OrdinaryTick != before.Review.Tick {
		t.Fatalf("scope clock: %+v", after.Review)
	}
	if after.Detection == nil || after.Detection.Facts.DisasterTick != before.Review.OrdinaryTick {
		t.Fatal("carried disaster evidence acquired an urgent observation tick")
	}
	if _, ok := after.Review.Incident(policy.ActiveCombat); !ok {
		t.Fatal("urgent combat was not opened")
	}
	got, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil || !reflect.DeepEqual(got, g) {
		t.Fatalf("ordinary standard changed: %+v %v", got, err)
	}
	gotPlan, err := s.LoadPlan(ctx, p.Spec.ID())
	if err != nil || !reflect.DeepEqual(gotPlan, p) {
		t.Fatal("ordinary method/progress changed", err)
	}
	ordinary := func(v Rounds) Rounds {
		v.Revision, v.Tick, v.Snapshot, v.Immediate = 0, 0, domain.GenerationSnapshot{}, false
		v.Standards, v.Projects, v.Incidents, v.Emergency, v.NoOps = nil, nil, nil, nil, nil
		v.Latches.Upkeep.Fire = false
		return v
	}
	if !reflect.DeepEqual(ordinary(before.Review), ordinary(after.Review)) {
		t.Fatal("ordinary derived history changed during urgent inspection")
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded, after.Review) {
		t.Fatal("immediate review did not reload", err)
	}
	r.Immediate = false
	r.Facts = roundsRequest().Facts
	full := reviewRounds(t, s, &r)
	if full.Review.Immediate || full.Review.OrdinaryTick != full.Review.Tick {
		t.Fatal("full inspection clock not advanced")
	}
}

func TestImmediateRoundsPreserveHuntWithoutFoodEvidence(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	rows := []policy.AcquisitionSource{{ID: "a", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}, {ID: "b", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}, {ID: "c", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}}
	squads := policy.HuntCandidates(rows, policy.SquadHuntMinGunners, domain.Fact[float64]{})
	r.Facts.FoodPlan = domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: squads[0], Decision: policy.FoodPlanOpen}}})
	before := reviewRounds(t, s, &r)
	hunt := roundsIncident(t, before, policy.ActiveCombat)
	r.Immediate = true
	r.Facts.FoodPlan = domain.Unknown[policy.FoodPlan]()
	for i := 0; i < 3; i++ {
		after := reviewRounds(t, s, &r)
		if got := roundsIncident(t, after, policy.ActiveCombat); !reflect.DeepEqual(got, hunt) {
			t.Fatal("hunt was refreshed or closed without food evidence")
		}
		if _, err := s.LoadRounds(ctx); err != nil {
			t.Fatal(err)
		}
	}
	r.Facts.FoodPlan = domain.Known(policy.FoodPlan{})
	after := reviewRounds(t, s, &r)
	if _, ok := after.Review.Incident(policy.ActiveCombat); ok {
		t.Fatal("observed ended hunt remained open")
	}
}

func TestImmediateRoundsKeepWorldRewindAndPauseInvariants(t *testing.T) {
	for _, mode := range []string{"load", "rewind", "pause"} {
		t.Run(mode, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, memoryPath(t))
			r := roundsRequest()
			before := reviewRounds(t, s, &r)
			g := roundsGoal(t, before, policy.MaintainResource)
			if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood", plan(t, "wood-plan", "wood-action")); err != nil {
				t.Fatal(err)
			}
			r.Immediate = true
			switch mode {
			case "load":
				r.Current.Load = "new-world"
			case "rewind":
				r.Tick = 5
			case "pause":
				r.Enabled = false
			}
			after := reviewRounds(t, s, &r)
			if _, err := s.LoadRounds(ctx); err != nil {
				t.Fatal(err)
			}
			got, err := s.LoadPlan(ctx, "wood-plan")
			if err != nil {
				t.Fatal(err)
			}
			if mode == "pause" {
				if after.Review.Enabled || !PlanOpen(got) || after.Review.OrdinaryTick != before.Review.Tick {
					t.Fatal("pause discarded pending work or ordinary clock")
				}
			} else if PlanOpen(got) || after.Review.OrdinaryTick != 0 || after.Review.Stage != nil || after.Review.ReadyWork != nil {
				t.Fatal("world replacement retained work/history")
			}
		})
	}
}

// An immediate review that follows a disabled one keeps that record's mood
// history, so it files the mood proposals the disabled record left empty; the
// stored record must load back, or every later review fails on it.
func TestImmediateRoundsAfterDisabledReviewFileMoodMethods(t *testing.T) {
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	defer func() { s.Close() }()
	r := roundsRequest()
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{moodPerson()})
	reviewRounds(t, s, &r)
	r.Enabled, r.Tick = false, 20
	reviewRounds(t, s, &r)
	r.Enabled, r.Immediate, r.Tick = true, true, 30
	after := reviewRounds(t, s, &r)
	if len(after.Review.MoodMethods) != len(after.Review.Mood.States) || len(after.Review.MoodMethods) == 0 {
		t.Fatalf("mood methods %d for %d states", len(after.Review.MoodMethods), len(after.Review.Mood.States))
	}
	s.Close()
	s = open(t, path)
	if _, err := s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
}
