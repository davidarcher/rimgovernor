package main

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// combatPawn mirrors test_native_combat_acceptance.py's pawn() fixture: a healthy,
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

func TestAttackRequestPreservesExactCASAndExplicitGuards(t *testing.T) {
	target := map[string]any{"pawn": map[string]any{"id": "Hare1", "snapshot": map[string]any{"token": "target"}}}
	request := attackRequest(map[string]any{"mapId": 0.0}, map[string]any{"context": map[string]any{"nativeGeneration": "4"}, "leaseId": "lease"}, combatPawn(), target, 3, "ATTACK_MODE_MELEE")
	operation, _ := na.AsMap(request["operation"])
	command, _ := na.AsMap(operation["attackTarget"])
	want := map[string]any{
		"pawn":                map[string]any{"entityId": "Human1", "expectedSnapshotToken": "attacker"},
		"target":              map[string]any{"entityId": "Hare1", "expectedSnapshotToken": "target"},
		"mode":                "ATTACK_MODE_MELEE",
		"requireHostile":      true,
		"requireStanding":     true,
		"requireCombatHealth": true,
	}
	if !na.DeepEqual(command, want) {
		t.Fatalf("unexpected attackTarget command: %#v", command)
	}
}

// combatFixture mirrors test_native_combat_acceptance.py's combat() fixture: a
// terminal, causally-verified melee kill.
func combatFixture() (receipt, progress, victim map[string]any) {
	effect := map[string]any{
		"pawnId": "Human1", "jobId": 58.0, "jobDef": "AttackMelee", "targetA": map[string]any{"thingId": "Hare1"},
		"verified": true, "verifiedReason": "Native positive damage by this exact attacker/job caused the observed target downing.",
	}
	receipt = map[string]any{"applied": map[string]any{"observed": map[string]any{"job": copyAny(effect)}}}
	progress = map[string]any{"completeInspection": true, "completed": map[string]any{"evidence": map[string]any{"job": effect}}}
	victim = map[string]any{"pawn": map[string]any{"id": "Hare1"}, "dead": false, "downed": true}
	return
}

func copyAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		if inner, ok := v.(map[string]any); ok {
			out[k] = copyAny(inner)
			continue
		}
		out[k] = v
	}
	return out
}

func TestTerminalRequiresTheExactRangedJob(t *testing.T) {
	receipt, progress, victim := combatFixture()
	if err := terminal(progress, receipt, victim, "Hare1", true); err == nil {
		t.Fatal("expected an error: fixture is a melee kill, not a ranged one")
	}
	applied, _ := na.AsMap(receipt["applied"])
	observed, _ := na.AsMap(applied["observed"])
	job, _ := na.AsMap(observed["job"])
	job["jobDef"] = "AttackStatic"
	completed, _ := na.AsMap(progress["completed"])
	evidence, _ := na.AsMap(completed["evidence"])
	evidenceJob, _ := na.AsMap(evidence["job"])
	evidenceJob["jobDef"] = "AttackStatic"
	if err := terminal(progress, receipt, victim, "Hare1", true); err != nil {
		t.Fatalf("unexpected error once both jobs report AttackStatic: %v", err)
	}
	victim["downed"] = false
	if err := terminal(progress, receipt, victim, "Hare1", true); err == nil {
		t.Fatal("expected an error once the victim is neither dead nor downed")
	}
}

func TestTerminalRequiresAttributedJobAndObservedNativeOutcome(t *testing.T) {
	for _, bad := range []string{"unrelated_job", "unrelated_attacker", "unrelated_target", "not_terminal", "pending", "no_causal_reason", "incomplete"} {
		t.Run(bad, func(t *testing.T) {
			receipt, progress, victim := combatFixture()
			if err := terminal(progress, receipt, victim, "Hare1", false); err != nil {
				t.Fatalf("fixture must pass before mutation: %v", err)
			}
			completed, _ := na.AsMap(progress["completed"])
			evidence, _ := na.AsMap(completed["evidence"])
			effect, _ := na.AsMap(evidence["job"])
			switch bad {
			case "unrelated_job":
				effect["jobId"] = 59.0
			case "unrelated_attacker":
				effect["pawnId"] = "Human2"
			case "unrelated_target":
				targetA, _ := na.AsMap(effect["targetA"])
				targetA["thingId"] = "Hare2"
			case "not_terminal":
				victim["downed"] = false
			case "pending":
				progress["pending"] = progress["completed"]
				delete(progress, "completed")
			case "no_causal_reason":
				effect["verifiedReason"] = "The target is dead."
			default:
				progress["completeInspection"] = false
			}
			if err := terminal(progress, receipt, victim, "Hare1", false); err == nil {
				t.Fatalf("expected an error for mutation %q", bad)
			}
		})
	}
}

func TestOverriddenAttackRequiresLostClaimNewSnapshotAndActualPlayerJob(t *testing.T) {
	for _, bad := range []string{"", "claim_retained", "same_snapshot", "wrong_job", "not_interrupted"} {
		t.Run(bad, func(t *testing.T) {
			before := map[string]any{"pawn": map[string]any{"id": "Human1", "snapshot": map[string]any{"token": "before"}}}
			after := map[string]any{
				"pawn": map[string]any{"id": "Human1", "snapshot": map[string]any{"token": "after"}}, "drafted": true,
				"draftClaim": map[string]any{"unowned": map[string]any{}},
				"job":        map[string]any{"loadId": "60", "defName": "Wait_Combat"},
			}
			external := map[string]any{"success": true, "accepted": true, "jobId": 60.0, "jobDef": "Wait_Combat"}
			progress := map[string]any{"completeInspection": true, "unsuccessful": map[string]any{"reason": "UNSUCCESSFUL_REASON_INTERRUPTED"}}
			if bad == "" {
				if err := overriddenAttack(progress, before, after, external); err != nil {
					t.Fatalf("expected the unmutated fixture to pass: %v", err)
				}
				return
			}
			if err := overriddenAttack(progress, before, after, external); err != nil {
				t.Fatalf("fixture must pass before mutation: %v", err)
			}
			switch bad {
			case "claim_retained":
				after["draftClaim"] = map[string]any{"owned": map[string]any{"claimId": "old"}}
			case "same_snapshot":
				pawn, _ := na.AsMap(after["pawn"])
				snapshot, _ := na.AsMap(pawn["snapshot"])
				snapshot["token"] = "before"
			case "wrong_job":
				job, _ := na.AsMap(after["job"])
				job["loadId"] = "61"
			default:
				unsuccessful, _ := na.AsMap(progress["unsuccessful"])
				unsuccessful["reason"] = "UNSUCCESSFUL_REASON_TARGET_DEAD"
			}
			if err := overriddenAttack(progress, before, after, external); err == nil {
				t.Fatalf("expected an error for mutation %q", bad)
			}
		})
	}
}
