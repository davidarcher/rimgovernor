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
		Name: "takeover/drug-policy", Scope: "Under autonomous control SetDrugPolicy owns drug policies: a colonist on another policy is moved to the social policy, a drifted same-named policy is repaired in place rather than duplicated, the policy becomes the colony default, and the routine readback flags drift for replanning.",
		Start: cases.DebugStart{}, RequiredOps: []string{"test/drug_policy"}, Budget: 2 * time.Minute, Run: runDrugPolicy,
	})
}

func runDrugPolicy(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	fixture := func(label, action string) (map[string]any, error) {
		return h.Call(ctx, label, "test/drug_policy", map[string]any{"action": action})
	}
	// The first colonist sits on a foreign policy and a drifted policy already
	// carries the routine's name: both are the controller's to take over.
	seeded, err := fixture("drug-seed", "seed")
	if err != nil {
		return err
	}
	ids, _ := seeded["pawns"].([]any)
	if ok, _ := na.AsBool(seeded["success"]); !ok || len(ids) != 3 || seeded["routineCount"] != float64(1) {
		return fmt.Errorf("drug fixture did not seed three colonists and a drifted policy: %v", seeded)
	}
	first, second := na.AsString(ids[0]), na.AsString(ids[1])
	operation := func(pawn, token string) map[string]any {
		return map[string]any{"setDrugPolicy": map[string]any{"pawn": map[string]any{"entityId": pawn, "expectedSnapshotToken": token}, "name": policy.SocialDrugPolicyName}}
	}
	// read returns the routine's view of one pawn: its settings token and
	// whether the Go planner would assign the social policy.
	read := func(label, pawn string) (string, policy.WorkPawn, error) {
		rows, err := readPawns(ctx, s, label, []string{pawn})
		if err != nil {
			return "", policy.WorkPawn{}, err
		}
		token, _ := rows[0].SnapshotToken.Value()
		return token, rows[0], nil
	}
	_, row, err := read("drug-first-read", first)
	if err != nil {
		return err
	}
	if row.DrugPolicyName != "" || !policy.DrugPolicyChange(row) {
		return fmt.Errorf("colonist on a foreign policy not planned for assignment: %+v", row)
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
	check := func(stage string, after map[string]any, assigned int) error {
		labels, _ := after["labels"].([]any)
		tea, _ := na.AsBool(after["routineTea"])
		beer, _ := na.AsBool(after["routineBeer"])
		if len(labels) != 3 || after["defaultLabel"] != policy.SocialDrugPolicyName || after["routineCount"] != float64(1) || after["routineId"] != seeded["routineId"] || tea || !beer {
			return fmt.Errorf("%s: policy not owned by the controller: %v", stage, after)
		}
		for i := 0; i <= assigned; i++ {
			if labels[i] != policy.SocialDrugPolicyName {
				return fmt.Errorf("%s: colonist %d not on the social policy: %v", stage, i, after)
			}
		}
		return nil
	}
	after, err := execute("drug-assign-first", first)
	if err != nil {
		return err
	}
	if err = check("first", after, 0); err != nil {
		return err
	}
	_, row, err = read("drug-assigned-read", first)
	if err != nil {
		return err
	}
	if row.DrugPolicyName != policy.SocialDrugPolicyName || policy.DrugPolicyChange(row) {
		return fmt.Errorf("assigned colonist still planned: %+v", row)
	}
	again, err := execute("drug-assign-second", second)
	if err != nil {
		return err
	}
	if err = check("second", again, 1); err != nil {
		return err
	}
	// Drift after assignment is repaired: the readback hides the name, the
	// planner reassigns, and execution rewrites the same policy in place.
	if _, err = fixture("drug-drift", "drift"); err != nil {
		return err
	}
	_, row, err = read("drug-drift-read", first)
	if err != nil {
		return err
	}
	if row.DrugPolicyName != "" || !policy.DrugPolicyChange(row) {
		return fmt.Errorf("drifted policy not planned for repair: %+v", row)
	}
	repaired, err := execute("drug-repair", first)
	if err != nil {
		return err
	}
	if err = check("repair", repaired, 1); err != nil {
		return err
	}
	s.Report()["drug_policy"] = map[string]any{"seeded": seeded, "first": after, "second": again, "repaired": repaired}
	return nil
}
