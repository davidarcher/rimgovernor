package combat

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// combatPawn is the fixture pawn: a healthy,
// undrafted, violence-capable colonist.
func combatPawn() map[string]any {
	return map[string]any{
		"pawn": map[string]any{"id": "Human1", "snapshot": map[string]any{"token": "attacker"}},
		"dead": false, "downed": false, "drafted": false,
		"health":    map[string]any{"summaryFraction": 1.0},
		"biography": map[string]any{"disabledWorkTags": []any{}},
	}
}

func TestHealthyCandidatesRequiresConsciousHealthAndViolence(t *testing.T) {
	row := combatPawn()
	got, err := healthyCandidates([]map[string]any{row}, false)
	if err != nil || len(got) != 1 {
		t.Fatalf("expected the fixture pawn to qualify, got %#v, %v", got, err)
	}

	violent := combatPawn()
	biography, _ := na.AsMap(violent["biography"])
	biography["disabledWorkTags"] = []any{"Violent"}
	got, err = healthyCandidates([]map[string]any{violent}, false)
	if err != nil || len(got) != 0 {
		t.Fatalf("expected a violence-disabled pawn to be filtered out, not erred: %#v, %v", got, err)
	}

	unconscious := combatPawn()
	health, _ := na.AsMap(unconscious["health"])
	health["summaryFraction"] = .5005
	if _, err := healthyCandidates([]map[string]any{unconscious}, false); err == nil {
		t.Fatal("expected an error for a row at/below the consciousness threshold")
	}

	downed := combatPawn()
	downed["downed"] = true
	if _, err := healthyCandidates([]map[string]any{downed}, false); err == nil {
		t.Fatal("expected an error for a downed row")
	}
}

func TestHealthyCandidatesRangedRequiresNativeShootingCapability(t *testing.T) {
	row := combatPawn()
	biography, _ := na.AsMap(row["biography"])
	biography["disabledWorkTags"] = []any{"Shooting"}
	got, err := healthyCandidates([]map[string]any{row}, false)
	if err != nil || len(got) != 1 {
		t.Fatalf("expected shooting-disabled pawn to still qualify for melee: %#v, %v", got, err)
	}
	got, err = healthyCandidates([]map[string]any{row}, true)
	if err != nil || len(got) != 0 {
		t.Fatalf("expected shooting-disabled pawn to be excluded from a ranged wait: %#v, %v", got, err)
	}
}

func TestCheckAttackResultRequiresTheAppliedJob(t *testing.T) {
	ok := []map[string]any{{"index": 0.0, "pawnId": "Human1", "applied": true, "jobDef": "AttackMelee"}}
	if err := checkAttackResult(ok, "Human1", "AttackMelee"); err != nil {
		t.Fatal(err)
	}
	for _, bad := range [][]map[string]any{
		nil,
		{{"pawnId": "Human1", "applied": false, "refusal": "not_drafted"}},
		{{"pawnId": "Human1", "applied": true, "jobDef": "AttackStatic"}},
		{{"pawnId": "Human2", "applied": true, "jobDef": "AttackMelee"}},
	} {
		if checkAttackResult(bad, "Human1", "AttackMelee") == nil {
			t.Fatalf("accepted %#v", bad)
		}
	}
}
