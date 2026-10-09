package medical

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "medical/peg-training",
		Scope: "Peg-leg doctor training (#1236): with Medicine 8 doctors (below the floor of 10) and an unrecruitable " +
			"factionless prisoner missing a leg, MaintainSurgery queues InstallPegLeg on the prisoner, native doctors " +
			"install it, it queues RemoveBodyPart on the peg, and a doctor's Medicine level rises. " +
			"Native: vanilla surgery XP and the wood-part install and removal completing are no Go snapshot.",
		Start: cases.Fixture{Op: "test/medical_management_setup", On: cases.LabStart(),
			Args: map[string]any{"disease": false, "condition": "pegTraining"}},
		Service: true, Budget: 15 * time.Minute, Crew: cases.Crew{Size: 3}, Run: pegTraining,
	})
}

func pegTraining(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prisoner := na.AsString(s.Prepared()["prisonerId"])
	index, ok := s.Prepared()["conditionPart"].(float64)
	if prisoner == "" || !ok || index < 0 {
		return fmt.Errorf("medical_management_setup reply lacks prisonerId/conditionPart: %#v", s.Prepared())
	}
	leg := int(index)
	before, err := medicineXP(ctx, s, "peg-training-xp-before")
	if err != nil {
		return err
	}
	// A queued bill is clock work: one service queues the install,
	// runs it, queues the removal on the peg and runs that.
	run, err := startSurgeryRun(ctx, s, "peg-training")
	if err != nil {
		return err
	}
	defer run.stop()
	for _, recipe := range []string{"InstallPegLeg", "RemoveBodyPart"} {
		if err = run.until(ctx, 6*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
			_, ok := findSurgery(surgeries, prisoner, leg, recipe)
			return na.Signature(len(surgeries)), ok, nil
		}); err != nil {
			return fmt.Errorf("peg-training %s: %w", recipe, err)
		}
		report[recipe] = true
	}
	if err = run.served(ctx, s, "peg-training-remove", prisoner, func(health map[string]any) bool { return partMissing(health, leg) }); err != nil {
		return err
	}
	after, err := medicineXP(ctx, s, "peg-training-xp-after")
	if err != nil {
		return err
	}
	report["medicine_xp"] = map[string]any{"before": before, "after": after}
	if after <= before {
		return fmt.Errorf("doctors' Medicine did not rise over the cycle: %v -> %v", before, after)
	}
	return nil
}

// medicineXP sums the free colonists' Medicine levels. The read carries no
// XP, so the fixture starts each doctor 1000 XP short of level 9 with a
// major passion: the cycle's 5600 XP (x1.5) crosses it.
func medicineXP(ctx context.Context, s cases.Session, label string) (float64, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": s.Identity()},
		"filter":  map[string]any{"humanlike": true, "animal": false},
		"details": map[string]any{"health": false, "needs": false, "equipment": false, "biography": true, "settings": false, "social": false, "animals": false},
	})
	if err != nil {
		return 0, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return 0, err
	}
	total, seen := 0.0, false
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		if free, _ := row["freeColonist"].(bool); !free {
			continue
		}
		bio, _ := na.AsMap(row["biography"])
		for _, rawSkill := range na.AsSlice(bio["skills"]) {
			skill, _ := na.AsMap(rawSkill)
			def, _ := na.AsMap(skill["definition"])
			if level, ok := skill["level"].(float64); ok && na.AsString(def["defName"]) == "Medicine" {
				total, seen = total+level, true
			}
		}
	}
	if !seen {
		return 0, fmt.Errorf("%s: no Medicine skill in the pawn read", label)
	}
	return total, nil
}
