package workers

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/drug-policy", Scope: "SetDrugPolicy leaves a colonist on a player-chosen policy untouched, refuses while a customized same-named policy exists, assigns default-policy colonists the social policy without rewriting it or the colony default, and refuses once the player customizes it.",
		Start: cases.DebugStart{}, RequiredOps: []string{"test/drug_policy"}, Budget: 2 * time.Minute, Run: runDrugPolicy,
	})
}

func runDrugPolicy(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	fixture := func(label, action string) (map[string]any, error) {
		return h.Call(ctx, label, "test/drug_policy", map[string]any{"action": action})
	}
	seeded, err := fixture("drug-custom", "custom")
	if err != nil {
		return err
	}
	ids, _ := seeded["pawns"].([]any)
	if ok, _ := na.AsBool(seeded["success"]); !ok || len(ids) != 3 {
		return fmt.Errorf("drug fixture did not seed three colonists: %v", seeded)
	}
	player, first, second := na.AsString(ids[0]), na.AsString(ids[1]), na.AsString(ids[2])
	operation := func(pawn, token string) map[string]any {
		return map[string]any{"setDrugPolicy": map[string]any{"pawn": map[string]any{"entityId": pawn, "expectedSnapshotToken": token}, "name": policy.SocialDrugPolicyName}}
	}
	// read returns the routine's view of one pawn: its settings token and
	// whether the Go guard would assign the social policy.
	read := func(label, pawn string) (string, policy.WorkPawn, error) {
		rows, err := readPawns(ctx, s, label, []string{pawn})
		if err != nil {
			return "", policy.WorkPawn{}, err
		}
		token, _ := rows[0].SnapshotToken.Value()
		return token, rows[0], nil
	}
	preview := func(label, pawn string) (bool, error) {
		token, _, err := read(label+"-read", pawn)
		if err != nil {
			return false, err
		}
		reply, err := h.Wire(ctx, label, "operations_preview", map[string]any{"identity": identity, "operation": operation(pawn, token)})
		if err != nil {
			return false, err
		}
		_, evaluation, err := na.Outcome(reply, "evaluated")
		if err != nil {
			return false, nil
		}
		accepted, _ := na.AsBool(evaluation["accepted"])
		return accepted, nil
	}
	_, row, err := read("drug-player-read", player)
	if err != nil {
		return err
	}
	if onDefault, known := row.DrugPolicyDefault.Value(); !known || onDefault || row.DrugPolicyName != "test-player-drugs" || policy.DrugPolicyChange(row) {
		return fmt.Errorf("player policy read as assignable: %+v", row)
	}
	if accepted, err := preview("drug-player-preview", player); err != nil || accepted {
		return fmt.Errorf("player-set policy previewed writable (%v): %v", accepted, err)
	}
	_, row, err = read("drug-default-read", first)
	if err != nil {
		return err
	}
	if !policy.DrugPolicyChange(row) {
		return fmt.Errorf("default-policy colonist not assignable: %+v", row)
	}
	if accepted, err := preview("drug-default-preview", first); err != nil || !accepted {
		return fmt.Errorf("default-policy colonist refused (%v): %v", accepted, err)
	}
	if _, err = fixture("drug-conflict", "conflict"); err != nil {
		return err
	}
	if accepted, err := preview("drug-conflict-preview", first); err != nil || accepted {
		return fmt.Errorf("customized same-named policy previewed writable (%v): %v", accepted, err)
	}
	if resolved, err := fixture("drug-resolve", "resolve"); err != nil || resolved["routineCount"] != float64(0) {
		return fmt.Errorf("conflict policy not removed: %v %v", resolved, err)
	}
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "drug-auto", identity); err != nil {
		return err
	}
	execute := func(label, pawn string) (map[string]any, error) {
		token, _, err := read(label+"-read", pawn)
		if err != nil {
			return nil, err
		}
		status, err := h.Wire(ctx, label+"-authority", "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, st, err := na.Outcome(status, "status")
		if err != nil {
			return nil, err
		}
		c, _ := na.AsMap(st["context"])
		reply, err := h.Wire(ctx, label, "operations_execute", map[string]any{"precondition": map[string]any{"identity": identity, "expectedGeneration": c["nativeGeneration"], "attempt": map[string]any{"controllerSessionId": owner, "actionId": label, "attemptId": "1"}}, "operation": operation(pawn, token)})
		if err != nil {
			return nil, err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return nil, err
		}
		if _, ok := na.AsMap(receipt["applied"]); !ok {
			return nil, fmt.Errorf("%s not applied: %v", label, reply)
		}
		return fixture(label+"-after", "read")
	}
	after, err := execute("drug-assign-first", first)
	if err != nil {
		return err
	}
	labels, _ := after["labels"].([]any)
	playerTea, _ := na.AsBool(after["playerTea"])
	routineTea, _ := na.AsBool(after["routineTea"])
	routineBeer, _ := na.AsBool(after["routineBeer"])
	if len(labels) != 3 || labels[0] != "test-player-drugs" || labels[1] != policy.SocialDrugPolicyName || after["defaultLabel"] != seeded["defaultLabel"] ||
		!playerTea || routineTea || !routineBeer || after["routineCount"] != float64(1) {
		return fmt.Errorf("first assignment disturbed policies: %v", after)
	}
	_, row, err = read("drug-assigned-read", first)
	if err != nil {
		return err
	}
	if onDefault, _ := row.DrugPolicyDefault.Value(); onDefault || row.DrugPolicyName != policy.SocialDrugPolicyName || policy.DrugPolicyChange(row) {
		return fmt.Errorf("assigned colonist read back as still assignable: %+v", row)
	}
	// The second assignment reuses the existing policy rather than creating
	// or rewriting one.
	again, err := execute("drug-assign-second", second)
	if err != nil {
		return err
	}
	labels, _ = again["labels"].([]any)
	if len(labels) != 3 || labels[0] != "test-player-drugs" || labels[2] != policy.SocialDrugPolicyName || again["defaultLabel"] != seeded["defaultLabel"] || again["routineCount"] != float64(1) || again["routineId"] != after["routineId"] {
		return fmt.Errorf("second assignment did not reuse the policy: %v", again)
	}
	// A player edit to the routine's own policy is preserved: pawns on it
	// still read the name (no replanning) and a default-policy pawn is refused
	// rather than rewriting the policy.
	if _, err = fixture("drug-customize", "customize"); err != nil {
		return err
	}
	_, row, err = read("drug-customized-read", first)
	if err != nil {
		return err
	}
	if row.DrugPolicyName != policy.SocialDrugPolicyName || policy.DrugPolicyChange(row) {
		return fmt.Errorf("customized routine policy would be replanned: %+v", row)
	}
	if _, err = fixture("drug-reset-second", "reset"); err != nil {
		return err
	}
	if accepted, err := preview("drug-customized-preview", second); err != nil || accepted {
		return fmt.Errorf("customized policy previewed writable (%v): %v", accepted, err)
	}
	final, err := fixture("drug-final", "read")
	if err != nil {
		return err
	}
	if tea, _ := na.AsBool(final["routineTea"]); !tea {
		return fmt.Errorf("player customization of the routine policy lost: %v", final)
	}
	s.Report()["drug_policy"] = map[string]any{"seeded": seeded, "first": after, "second": again, "final": final}
	return nil
}
