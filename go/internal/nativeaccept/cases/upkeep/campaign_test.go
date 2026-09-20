package upkeep

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type campaignRestoreSession struct {
	cases.Session
	staged, resumed *na.Checkpoint
}

func TestCampaignRebindsGuardsOnlyToLoadedReview(t *testing.T) {
	old := closedGoal{Stage: "kitchen", Need: policy.MaintainCleanFacilities, Goal: "old-kitchen", Epoch: 7}
	review := store.RoutineReview{Goals: []store.RoutineGoal{{Need: old.Need, Goal: "loaded-kitchen"}}}
	for _, status := range []domain.GoalStatus{domain.GoalActive, domain.GoalCancelled, domain.GoalInvalidated} {
		closed := []closedGoal{old}
		err := rebindClosedGoals(context.Background(), closed, review, func(_ context.Context, id domain.GoalID) (store.GoalState, error) {
			if id != "loaded-kitchen" {
				t.Fatalf("loaded %s instead of current binding", id)
			}
			return store.GoalState{Goal: domain.Goal{ID: id, Epoch: 2, Status: status}}, nil
		})
		if status == domain.GoalActive {
			if err != nil || closed[0].Goal != "loaded-kitchen" || closed[0].Epoch != 2 || closed[0].Stage != old.Stage {
				t.Fatalf("loaded guard = %+v, %v", closed, err)
			}
		} else if err == nil {
			t.Fatalf("accepted %s goal", status)
		}
	}
	if err := rebindClosedGoals(context.Background(), []closedGoal{old}, store.RoutineReview{}, nil); err == nil {
		t.Fatal("accepted a missing loaded binding")
	}
}

func (s campaignRestoreSession) Staged() (na.Checkpoint, bool) {
	if s.staged != nil {
		return *s.staged, true
	}
	return na.Checkpoint{}, false
}

func (s campaignRestoreSession) Resumed() (na.Checkpoint, bool) {
	if s.resumed != nil {
		return *s.resumed, true
	}
	return na.Checkpoint{}, false
}

func TestCampaignMedicineStateRestoresGuardIdentities(t *testing.T) {
	want := campaignState{Timeline: []map[string]any{{"stage": "medicine", "tick_after": float64(12345)}}}
	for i, st := range campaignStages()[:3] {
		want.Closed = append(want.Closed, closedGoal{Stage: st.name, Need: st.needs[0], Goal: domain.GoalID("goal-" + st.name), Epoch: uint64(i + 7)})
	}
	data, err := json.Marshal(want)
	if err != nil {
		t.Fatal(err)
	}
	var wire any
	if err := json.Unmarshal(data, &wire); err != nil {
		t.Fatal(err)
	}
	entry := &na.Checkpoint{Stage: "medicine", State: map[string]any{campaignStateKey: wire}}
	for _, s := range []campaignRestoreSession{{staged: entry}, {resumed: entry}} {
		got, err := restoreCampaignState(s)
		if err != nil || !reflect.DeepEqual(got, want) {
			t.Fatalf("restored medicine state = %#v, %v; want %#v", got, err, want)
		}
	}
	fresh, err := restoreCampaignState(campaignRestoreSession{})
	if err != nil || len(fresh.Closed) != 0 || len(fresh.Timeline) != 0 {
		t.Fatalf("fresh state = %#v, %v", fresh, err)
	}
}

func TestCampaignDeclaresStages(t *testing.T) {
	c, ok := cases.Lookup("upkeep/campaign")
	if !ok {
		t.Fatal("campaign missing from registry")
	}
	if err := c.Validate(); err != nil {
		t.Fatal(err)
	}
	if want := []string{"kitchen", "feed", "medicine", "cold"}; !reflect.DeepEqual(c.Stages, want) {
		t.Fatalf("stages = %v; want %v", c.Stages, want)
	}
}

func TestCampaignKeepsNamingAvailableAcrossStageRestarts(t *testing.T) {
	stages := campaignStages()
	for n := 1; n <= len(stages); n++ {
		families := cumulativeFamilies(stages, n)
		seen := map[string]bool{}
		for _, family := range families {
			if seen[family] {
				t.Fatalf("stage %s repeats family %s", stages[n-1].name, family)
			}
			seen[family] = true
		}
		if !seen["naming"] {
			t.Fatalf("stage %s cannot resolve a delayed naming modal", stages[n-1].name)
		}
		for _, st := range stages[:n] {
			for _, family := range st.families {
				if !seen[family] {
					t.Fatalf("stage %s dropped earlier family %s", stages[n-1].name, family)
				}
			}
		}
	}
}
