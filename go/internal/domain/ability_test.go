package domain

import (
	"strings"
	"testing"
)

func TestAbilityPermitSourceAndTargets(t *testing.T) {
	source, err := PermitSource("Empire", "CallMilitaryAidSmall")
	if err != nil || source.Kind() != AbilityPermit || source.Faction() != "Empire" || source.Def() != "CallMilitaryAidSmall" {
		t.Fatal(source, err)
	}
	if back, err := ParseAbilitySource(source.Key()); err != nil || back != source {
		t.Fatal(back, err)
	}
	cell, err := AbilityCellTarget(Cell{X: 3, Z: 4})
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range []AbilityTarget{NoAbilityTarget(), cell} {
		ability, err := NewAbility("pawn-1", source, target)
		if err != nil || ability.Pawn() != "pawn-1" || ability.Source() != source || ability.Target() != target {
			t.Fatal(ability, err)
		}
		action, err := NewAbilityAction("ability-1", ability)
		if err != nil || action.Kind() != AbilityAction {
			t.Fatal(action, err)
		}
		if got, ok := action.Ability(); !ok || got != ability {
			t.Fatal(got, ok)
		}
		if _, ok := action.QuestAccept(); ok {
			t.Fatal("ability exposed quest accept")
		}
	}
	plan, err := NewPlan("plan-1", 1, []Action{mustAbilityAction(t, source, cell)})
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := plan.Actions()[0].Ability(); !ok || got.Target() != cell {
		t.Fatal("plan did not keep the ability", got)
	}
}

func mustAbilityAction(t *testing.T, source AbilitySource, target AbilityTarget) Action {
	t.Helper()
	ability, err := NewAbility("pawn-1", source, target)
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewAbilityAction("ability-1", ability)
	if err != nil {
		t.Fatal(err)
	}
	return action
}

// A colon would break the stored source key, so def names never carry one.
func TestAbilityPermitSourceRefusesColon(t *testing.T) {
	if _, err := PermitSource("a:b", "Permit"); err == nil {
		t.Fatal("colon faction accepted")
	}
	if _, err := PermitSource("Empire", "a:b"); err == nil {
		t.Fatal("colon permit accepted")
	}
}

func TestAbilityRefusals(t *testing.T) {
	source, _ := PermitSource("Empire", "CallMilitaryAidSmall")
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257)} {
		if _, err := PermitSource(invalid, "Permit"); err == nil {
			t.Fatalf("faction %q accepted", invalid)
		}
		if _, err := PermitSource("Empire", invalid); err == nil {
			t.Fatalf("permit %q accepted", invalid)
		}
		if _, err := NewAbility(PawnID(invalid), source, NoAbilityTarget()); err == nil {
			t.Fatalf("pawn %q accepted", invalid)
		}
		if _, err := NewAbilityAction(ActionID(invalid), Ability{pawn: "pawn-1", source: source, target: NoAbilityTarget()}); err == nil {
			t.Fatalf("action id %q accepted", invalid)
		}
	}
	if _, err := NewAbility("pawn-1", AbilitySource{}, NoAbilityTarget()); err == nil {
		t.Fatal("empty source accepted")
	}
	if _, err := NewAbility("pawn-1", source, AbilityTarget{}); err == nil {
		t.Fatal("unspecified target accepted")
	}
	if _, err := AbilityCellTarget(Cell{X: -1, Z: 0}); err == nil {
		t.Fatal("negative cell accepted")
	}
	pawn, _ := AbilityPawnTarget("pawn-2")
	thing, _ := AbilityThingTarget("thing-1")
	for _, target := range []AbilityTarget{pawn, thing} {
		if _, err := NewAbility("pawn-1", source, target); err == nil {
			t.Fatalf("permit accepted a %s target", target.Kind())
		}
	}
	for _, key := range []string{"", "permit", "permit:Empire", "psycast", "psycast:", "psycast:Skip:Extra", "permit:Empire:A:B"} {
		if _, err := ParseAbilitySource(key); err == nil {
			t.Fatalf("source key %q accepted", key)
		}
	}
}

// The psycast source (#1610) is keyed "psycast:<abilityDef>" and takes every
// target arm; native decides which arm one ability needs.
func TestAbilityPsycastSource(t *testing.T) {
	source, err := PsycastSource("Skip")
	if err != nil || source.Kind() != AbilityPsycast || source.Def() != "Skip" || source.Faction() != "" || source.Key() != "psycast:Skip" {
		t.Fatal(source, err)
	}
	if back, err := ParseAbilitySource(source.Key()); err != nil || back != source {
		t.Fatal(back, err)
	}
	cell, _ := AbilityCellTarget(Cell{X: 3, Z: 4})
	pawn, _ := AbilityPawnTarget("pawn-2")
	thing, _ := AbilityThingTarget("thing-1")
	for _, target := range []AbilityTarget{NoAbilityTarget(), cell, pawn, thing} {
		action := mustAbilityAction(t, source, target)
		if got, ok := action.Ability(); !ok || got.Source() != source || got.Target() != target {
			t.Fatal(target.Kind(), got)
		}
	}
	for _, invalid := range []string{"", " ", "a:b", "x\x00y", strings.Repeat("x", 257)} {
		if _, err := PsycastSource(invalid); err == nil {
			t.Fatalf("psycast def %q accepted", invalid)
		}
	}
}
