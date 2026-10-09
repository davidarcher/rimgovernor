package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A permit ability builds one AbilityIntent: the permit source arm and
// the target arm that matches the domain target.
func TestAbilityBuildsIntent(t *testing.T) {
	source, err := domain.PermitSource("Empire", "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	cell, err := domain.AbilityCellTarget(domain.Cell{X: 14, Z: 22})
	if err != nil {
		t.Fatal(err)
	}
	for name, target := range map[string]domain.AbilityTarget{"cell": cell, "none": domain.NoAbilityTarget()} {
		ability, err := domain.NewAbility("Human12", source, target)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewAbilityAction("ability1", ability)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("ability is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		intent := wire.GetAbility()
		permit := intent.GetPermit()
		if intent.GetPawnId() != "Human12" || permit.GetFactionDef() != "Empire" || permit.GetPermit() != "CallMilitaryAidSmall" {
			t.Fatalf("%s: %v", name, wire)
		}
		switch name {
		case "cell":
			if got := intent.GetCell(); got.GetX() != 14 || got.GetZ() != 22 || intent.GetNoTarget() != nil {
				t.Fatalf("cell target %v", wire)
			}
		case "none":
			if intent.GetNoTarget() == nil || intent.GetCell() != nil {
				t.Fatalf("no target %v", wire)
			}
		}
	}
}

// The bridge refuses an action that is not an ability, and the domain refuses
// shapes the permit source cannot take before they reach the wire.
func TestAbilityRefusals(t *testing.T) {
	if _, err := abilityAction(domain.Action{}); err == nil {
		t.Fatal("a non-ability action built an ability intent")
	}
	source, _ := domain.PermitSource("Empire", "CallMilitaryAidSmall")
	pawnTarget, _ := domain.AbilityPawnTarget("Human13")
	thingTarget, _ := domain.AbilityThingTarget("Thing9")
	for _, target := range []domain.AbilityTarget{pawnTarget, thingTarget} {
		if _, err := domain.NewAbility("Human12", source, target); err == nil {
			t.Fatalf("permit accepted a %s target", target.Kind())
		}
	}
}

// The royalty read decodes each held permit's cooldown, and an absent
// last use stays unknown rather than zero.
func TestDecodeRoyaltyPermitCooldowns(t *testing.T) {
	facts, err := decodeRoyalty(royaltyPawns())
	if err != nil {
		t.Fatal(err)
	}
	cd := facts.Holders[policy.PawnID("Human12")][0].Cooldowns["CallLaborerPack"]
	if last, ok := cd.LastUsedTick.Value(); !ok || last != 100 {
		t.Fatalf("last used %v %v", last, ok)
	}
	if left, ok := cd.RemainingTicks.Value(); !ok || left != 500 {
		t.Fatalf("remaining %v %v", left, ok)
	}
	if !facts.PermitUsedSince("Human12", "Empire", "CallLaborerPack", 100) || facts.PermitUsedSince("Human12", "Empire", "CallLaborerPack", 101) {
		t.Fatal("replay re-read did not compare the last use to the attempt tick")
	}
	if facts.PermitUsedSince("Human12", "Other", "CallLaborerPack", 0) || facts.PermitUsedSince("Human99", "Empire", "CallLaborerPack", 0) {
		t.Fatal("an unheld permit read as used")
	}
	pawns := royaltyPawns()
	pawns.Pawns[1].Royalty.Holdings[0].PermitCooldowns = []*o.PermitCooldown{{Permit: proto.String("CallLaborerPack")}}
	facts, err = decodeRoyalty(pawns)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := facts.Holders[policy.PawnID("Human12")][0].Cooldowns["CallLaborerPack"].LastUsedTick.Value(); ok {
		t.Fatal("never-used permit read a last use")
	}
	for name, bad := range map[string][]*o.PermitCooldown{
		"negative": {{Permit: proto.String("CallLaborerPack"), CooldownRemainingTicks: proto.Int32(-1)}},
		"unnamed":  {{CooldownRemainingTicks: proto.Int32(1)}},
		"repeated": {{Permit: proto.String("A")}, {Permit: proto.String("A")}},
	} {
		pawns := royaltyPawns()
		pawns.Pawns[1].Royalty.Holdings[0].PermitCooldowns = bad
		if _, err := decodeRoyalty(pawns); err == nil {
			t.Fatalf("%s cooldown accepted", name)
		}
	}
}

// A psycast ability builds the psycast source arm with every target arm.
func TestAbilityBuildsPsycastIntent(t *testing.T) {
	source, err := domain.PsycastSource("Skip")
	if err != nil {
		t.Fatal(err)
	}
	cell, _ := domain.AbilityCellTarget(domain.Cell{X: 5, Z: 6})
	pawn, _ := domain.AbilityPawnTarget("Human13")
	thing, _ := domain.AbilityThingTarget("Thing9")
	for name, target := range map[string]domain.AbilityTarget{"none": domain.NoAbilityTarget(), "cell": cell, "pawn": pawn, "thing": thing} {
		ability, err := domain.NewAbility("Human12", source, target)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewAbilityAction("ability1", ability)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		intent := wire.GetAbility()
		if intent.GetPawnId() != "Human12" || intent.GetPsycast().GetAbility() != "Skip" || intent.GetPermit() != nil {
			t.Fatalf("%s: %v", name, wire)
		}
		switch name {
		case "none":
			if intent.GetNoTarget() == nil {
				t.Fatalf("none %v", wire)
			}
		case "cell":
			if got := intent.GetCell(); got.GetX() != 5 || got.GetZ() != 6 {
				t.Fatalf("cell %v", wire)
			}
		case "pawn":
			if intent.GetPawn() != "Human13" {
				t.Fatalf("pawn %v", wire)
			}
		case "thing":
			if intent.GetThing() != "Thing9" {
				t.Fatalf("thing %v", wire)
			}
		}
	}
}
