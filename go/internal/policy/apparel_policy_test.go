package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoleApparelPolicies(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}, {Name: "Armor", Armor: true, Adult: true}, {Name: "KidShirt", Child: true}, {Name: "Hat", Adult: true, Child: true}}}
	for _, tt := range []struct {
		role GearRole
		want []string
	}{{GearWorker, []string{"Hat", "Shirt"}}, {GearSoldier, []string{"Armor", "Hat", "Shirt"}}, {GearHunter, []string{"Hat", "Shirt"}}, {GearIndoor, []string{"Hat", "Shirt"}}, {GearChild, []string{"Hat", "KidShirt"}}, {GearSlave, []string{"Hat", "Shirt"}}, {GearNonCombatant, []string{"Hat", "Shirt"}}} {
		t.Run(string(tt.role), func(t *testing.T) {
			v, ok := RoleApparelPolicy("pawn", tt.role, state)
			s := v.Spec()
			if !ok || s.Name != "Ann" || !reflect.DeepEqual(s.Definitions, tt.want) || s.MinHP != .51 || s.MaxHP != 1 || s.MinQuality != 0 || s.MaxQuality != 6 {
				t.Fatalf("policy: %+v", s)
			}
		})
	}
}

func TestRoleApparelPolicyNeedsTheShortName(t *testing.T) {
	if _, ok := RoleApparelPolicy("pawn", GearWorker, ApparelPolicyState{Token: "cas", Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}}}); ok {
		t.Fatal("an unnamed pawn got an outfit")
	}
}

// Title, ideoligion role and precept apparel is allowed whatever the role
// filter says (here combat armor for a worker).
func TestRoleApparelPolicyAllowsRequiredApparel(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", Required: []string{"Prestige"}, Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}, {Name: "Prestige", Armor: true, Adult: true, CoversBody: true}}}
	v, ok := RoleApparelPolicy("pawn", GearWorker, state)
	if !ok || !reflect.DeepEqual(v.Spec().Definitions, []string{"Prestige", "Shirt"}) {
		t.Fatalf("policy: %+v", v.Spec())
	}
}

// A nude pawn (nudist or mandatory nudity) gets nothing covering torso or
// legs unless required.
func TestRoleApparelPolicyNude(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", Nude: true, Required: []string{"Robe"}, Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true, CoversBody: true}, {Name: "Hat", Adult: true}, {Name: "Robe", Adult: true, CoversBody: true}}}
	v, ok := RoleApparelPolicy("pawn", GearWorker, state)
	if !ok || !reflect.DeepEqual(v.Spec().Definitions, []string{"Hat", "Robe"}) {
		t.Fatalf("policy: %+v", v.Spec())
	}
}

// Body type and genes reach the policy as the per-pawn definition list:
// what native does not list is never allowed.
func TestRoleApparelPolicyOnlyListedDefinitions(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", Required: []string{"Crown"}, Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}}}
	v, ok := RoleApparelPolicy("pawn", GearWorker, state)
	if !ok || !reflect.DeepEqual(v.Spec().Definitions, []string{"Shirt"}) {
		t.Fatalf("policy: %+v", v.Spec())
	}
}

func TestApparelQualityFloorRisesWithStock(t *testing.T) {
	shirt := func(q int, src GearSource) GearOption {
		return GearOption{Definition: "Shirt", Slot: GearSkinTorso, Quality: q, Condition: 1, Source: src}
	}
	allowed := []string{"Shirt"}
	for _, tt := range []struct {
		name  string
		model GearLoadoutInput
		want  int32
	}{
		{"nothing worn", GearLoadoutInput{}, 0},
		{"awful only", GearLoadoutInput{Worn: []GearOption{shirt(0, GearWorn)}}, 0},
		{"normal stored", GearLoadoutInput{Worn: []GearOption{shirt(0, GearWorn)}, Options: []GearOption{shirt(2, GearStored)}}, 2},
		{"good worn", GearLoadoutInput{Worn: []GearOption{shirt(3, GearWorn)}}, 3},
		{"bill is not stock", GearLoadoutInput{Worn: []GearOption{shirt(0, GearWorn)}, Options: []GearOption{shirt(4, GearBillSource)}}, 0},
		{"tattered is not stock", GearLoadoutInput{Worn: []GearOption{shirt(0, GearWorn)}, Options: []GearOption{{Definition: "Shirt", Slot: GearSkinTorso, Quality: 4, Condition: .3, Source: GearStored}}}, 0},
	} {
		t.Run(tt.name, func(t *testing.T) {
			if got := apparelQualityFloor(tt.model, allowed); got != tt.want {
				t.Fatalf("floor %d, want %d", got, tt.want)
			}
		})
	}
}

func TestApparelPoliciesOverridePlayerChoiceAndPreserveMatchingPolicy(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", PolicyID: "Outfit_1", Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}}}
	p := GearPawn{Pawn: "p", Policy: domain.Known(state)}
	v, ok := DesiredApparelPolicy(p)
	if !ok {
		t.Fatal("player policy not replaced")
	}
	if _, settled := OutfitKeep([]GearPawn{p}); settled {
		t.Fatal("outfits pruned before the pawn is on its own")
	}
	state.Current = v.Spec()
	state.Current.MinHP = float64(float32(.51))
	state.ExcludesTainted = true
	p.Policy = domain.Known(state)
	if _, ok := DesiredApparelPolicy(p); ok {
		t.Fatal("matching native policy repeated")
	}
	if keep, settled := OutfitKeep([]GearPawn{p}); !settled || !reflect.DeepEqual(keep, map[string]bool{"Outfit_1": true}) {
		t.Fatalf("keep %v %v", keep, settled)
	}
	state.ExcludesTainted = false
	p.Policy = domain.Known(state)
	if _, ok := DesiredApparelPolicy(p); !ok {
		t.Fatal("tainted allowed was not repaired")
	}
	p.Blocked = true
	if _, ok := DesiredApparelPolicy(p); ok {
		t.Fatal("unavailable pawn was assigned")
	}
	if _, settled := OutfitKeep([]GearPawn{p}); settled {
		t.Fatal("outfits pruned with a pawn blocked")
	}
}

func TestOutfitsToPruneOnceEveryPawnIsOnItsOwn(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", PawnName: "Ann", PolicyID: "Outfit_1", ExcludesTainted: true, Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}}}
	v, _ := RoleApparelPolicy("p", GearWorker, state)
	state.Current = v.Spec()
	obs := GearObservation{Pawns: []GearPawn{{Pawn: "p", Policy: domain.Known(state)}}, Outfits: domain.Known([]string{"Outfit_0", "Outfit_1"})}
	if got := obs.OutfitsToPrune(); !reflect.DeepEqual(got, []string{"Outfit_0"}) {
		t.Fatalf("prune %v", got)
	}
	obs.Outfits = domain.Unknown[[]string]()
	if got := obs.OutfitsToPrune(); got != nil {
		t.Fatalf("pruned with the outfit database unknown: %v", got)
	}
}
