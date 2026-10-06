package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// The starving tribal colony (#2141) replayed through the hunt channel (#2158):
// the recording holds no hunt row because its native gates refused the wild
// deer. With a bow hunter the Go gate offers them (reach 25.9), and the plan
// opens a lone Hunt; a group worth a squad is a formation that Holds with
// needs_gunners while the colony has no gunners.
func TestStarvingTribalHuntsWithBowsAndHoldsFormations(t *testing.T) {
	r, err := snapshot.Load("../snapshot/testdata/food-starving-tribal-no-hunt-row.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	deer := func(id string, x, z int32) policy.AcquisitionSource {
		return policy.AcquisitionSource{ID: id, Definition: "Deer", Resource: "Corpse_Deer", Token: "t", Hunt: true, Food: true, Yield: 1, NutritionYield: 20, RevengeChance: 0.05, HerdSize: 1,
			WeaponRange: 25.9, Cell: domain.Cell{X: x, Z: z}, Products: []policy.SourceProduct{{Def: "Leather_Plain", Amount: 40}}}
	}
	plan := func(rows ...policy.AcquisitionSource) policy.FoodPlan {
		p := *r.Projection
		known, _ := p.Acquisition.Value()
		p.Acquisition = domain.Known(append(append([]policy.AcquisitionSource(nil), known...), rows...))
		out, ok := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), nil, nil, foodTrade{}).Value()
		if !ok {
			t.Fatal("food plan unknown")
		}
		return out
	}
	hunts := func(p policy.FoodPlan) (rows []policy.FoodPlanEntry) {
		for _, e := range p.Portfolio {
			if e.Channel.Kind == policy.CandidateHunt {
				rows = append(rows, e)
			}
		}
		return rows
	}
	if got := hunts(plan()); len(got) != 0 {
		t.Fatalf("the recording offers no hunt: %+v", got)
	}
	lone := hunts(plan(deer("deer", 10, 10)))
	if len(lone) != 1 || lone[0].Channel.Mode() != policy.HuntLone || lone[0].Decision != policy.FoodPlanOpen || len(lone[0].Channel.Products()) != 1 {
		t.Fatalf("a bow hunter's deer does not open a lone hunt: %+v", lone)
	}
	herd := hunts(plan(deer("a", 10, 10), deer("b", 12, 10), deer("c", 14, 10)))
	if len(herd) != 1 || herd[0].Channel.Mode() != policy.HuntFormation || herd[0].Decision != policy.FoodPlanHold {
		t.Fatalf("a herd without gunners must hold as a formation: %+v", herd)
	}
	needs := false
	for _, term := range herd[0].Channel.Terms {
		needs = needs || term.Name == "needs_gunners" && term.Value == policy.SquadHuntMinGunners
	}
	if !needs {
		t.Fatalf("held formation lacks needs_gunners: %+v", herd[0].Channel.Terms)
	}
}

// The same colony with a deer held only for want of a hunter's weapon (#2162):
// the plan prices the craft into the hunt and opens it, which is the arming the
// work planner reads; a deer held for another reason prices nothing.
func TestStarvingTribalOpensHunterWeaponPrerequisite(t *testing.T) {
	r, err := snapshot.Load("../snapshot/testdata/food-starving-tribal-no-hunt-row.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	deer := policy.AcquisitionSource{ID: "deer", Definition: "Deer", Resource: "Corpse_Deer", Token: "t", Hunt: true, Food: true, Yield: 1, NutritionYield: 20, RevengeChance: 0.05, HerdSize: 1,
		Cell: domain.Cell{X: 10, Z: 10}, Products: []policy.SourceProduct{{Def: "Leather_Plain", Amount: 40}}}
	plan := func(hold policy.HuntHold) domain.Fact[policy.FoodPlan] {
		p := *r.Projection
		p.HuntHolds = []policy.HuntHold{hold}
		return reviewFoodPlan(p, policy.DefaultRoundsPolicy(), nil, nil, foodTrade{})
	}
	unarmed := plan(policy.HuntHold{ID: "deer", Reason: policy.HuntHoldNoHunter, Detail: []string{"p1 hunting_inactive", "p2 no_hunting_weapon"}, Source: deer})
	if !policy.HuntArming(unarmed) {
		got, _ := unarmed.Value()
		t.Fatalf("the plan did not open the hunter-weapon prerequisite: %+v", got.Portfolio)
	}
	if policy.HuntArming(plan(policy.HuntHold{ID: "deer", Reason: policy.HuntHoldNoHunter, Detail: []string{"p1 too_far"}, Source: deer})) {
		t.Fatal("a hold that is not about the weapon opened the prerequisite")
	}
}
