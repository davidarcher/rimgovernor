package buildingruntime

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func tierTestPreview(t *testing.T, id string) policy.Preview {
	t.Helper()
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(id), b)
	if err != nil {
		t.Fatal(err)
	}
	return policy.Preview{Action: a}
}

// A funnel call stating a tier per preview stamps each building action; an
// unknown tier leaves it untiered, and a missing statement is refused.
func TestTierPreviewsStampsEachActionAndRefusesAMissingStatement(t *testing.T) {
	selected := []policy.Preview{tierTestPreview(t, "a"), tierTestPreview(t, "b"), tierTestPreview(t, "c")}
	tiers := []domain.Fact[domain.ConstructionTier]{domain.Known(domain.TierSurvive), domain.Known(domain.TierComfort), domain.Unknown[domain.ConstructionTier]()}
	out, err := tierPreviews(selected, tiers)
	if err != nil {
		t.Fatal(err)
	}
	for i, want := range []domain.Fact[domain.ConstructionTier]{domain.Known(domain.TierSurvive), domain.Known(domain.TierComfort), domain.Unknown[domain.ConstructionTier]()} {
		if got := out[i].Action.Tier(); got != want {
			t.Errorf("action %d tier = %v, want %v", i, got, want)
		}
	}
	if _, known := selected[0].Action.Tier().Value(); known {
		t.Fatal("the caller's previews were modified")
	}
	if _, err := tierPreviews(selected, tiers[:2]); !errors.Is(err, ErrControl) {
		t.Fatalf("tierPreviews with a missing tier = %v, want ErrControl", err)
	}
}

// The planners state the ladder's tiers: the shell ring of each room kind
// takes its role's tier and a loose planner takes its concern's.
func TestFunnelsStateTheLadderTiers(t *testing.T) {
	for _, c := range []struct {
		name string
		got  domain.Fact[domain.ConstructionTier]
		want domain.ConstructionTier
	}{
		{"starter shelter ring", domain.Known(policy.RoomTier(policy.PlannedShelter)), domain.TierSurvive},
		{"cooking campfire", policy.PlannerTier(policy.EnsureCooking, ""), domain.TierSurvive},
		{"private bedroom", domain.Known(policy.RoomTier(policy.PlannedBedroom)), domain.TierSustain},
		{"freezer", policy.PlannerTier(policy.MaintainRefrigeration, ""), domain.TierSustain},
		{"dining room", domain.Known(policy.RoomTier(policy.PlannedDining)), domain.TierComfort},
		{"workshop", domain.Known(policy.RoomTier(policy.PlannedWorkshop)), domain.TierProduce},
		{"graveyard", domain.Known(policy.RoomTier(policy.PlannedGraveyard)), domain.TierExpand},
	} {
		if tier, known := c.got.Value(); !known || tier != c.want {
			t.Errorf("%s tier = %d, %v; want %d", c.name, tier, known, c.want)
		}
	}
}
