package lab

import (
	"context"
	"fmt"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// trainingMaxXp is the native daily budget (RangeTraining.DailyXpBudget): one
// session never pays more than a day's worth.
const trainingMaxXp = 3000

func init() {
	cases.Register(cases.Case{
		Name: "lab/training-shooting",
		Scope: "Native training job (#2610, open air since #2686): test/training_prepare stands one range column in open air (a stand, a dummy " +
			"8 cells across, no walls or partitions) and leaves a low-skill colonist whose only work is the RimGovernorTraining work type; with no " +
			"order from Go the vanilla work scan offers the stand, the colonist fires the unlocked practice weapon at the dummy, direct Learn raises " +
			"Shooting without touching xpSinceMidnight, the colonist's own weapon is back in hand with no practice weapon left anywhere, and a " +
			"sentinel wall off the line is unharmed. A Go snapshot test cannot see the work giver, the job driver, the weapon swap or the shots.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{"test/training_prepare", "test/training_inspect"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3},
		Run:         runTraining,
	})
}

func runTraining(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	prepared, err := h.Call(ctx, "training-prepare", "test/training_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])),
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("training_prepare: %#v", prepared)
	}
	args := map[string]any{"pawnId": prepared["pawn"], "dummyId": prepared["dummy"], "sentinels": prepared["sentinels"]}
	real := na.AsString(prepared["weapon"])
	var last map[string]any
	if _, err := na.RunUntil(ctx, h, "training", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "training-inspect", "test/training_inspect", args)
		if err != nil {
			return "", false, err
		}
		last = got
		gained := trainingGained(0, int(na.AsNumber(got["shootingLevel"])), na.AsNumber(got["shootingXp"]))
		idle := !strings.HasPrefix(na.AsString(got["job"]), "RimGovernor_Train")
		return fmt.Sprint(gained, got["job"]), gained > 0 && idle, nil
	}); err != nil {
		return err
	}
	s.Report()["training"] = last
	if got := trainingGained(0, int(na.AsNumber(last["shootingLevel"])), na.AsNumber(last["shootingXp"])); got <= 0 || got > trainingMaxXp {
		return fmt.Errorf("shooting xp gained %v outside (0, %d]: %#v", got, trainingMaxXp, last)
	}
	if na.AsNumber(last["shootingMidnightXp"]) != 0 {
		return fmt.Errorf("direct Learn counted toward the daily soft cap: %#v", last)
	}
	if na.AsNumber(last["shotsFired"]) < 1 {
		return fmt.Errorf("no shot was fired: %#v", last)
	}
	// The shooter mends the dummy at the end of the session (#2687): it is whole
	// again, not destroyed or worn.
	if hp := na.AsNumber(last["dummyHp"]); hp <= 0 || hp != na.AsNumber(prepared["dummyMax"]) {
		return fmt.Errorf("drilled dummy not mended to full: %#v", last)
	}
	// Nobody else drilled within chat range (#2710): no "trained with" thought.
	if worn, _ := na.AsBool(last["trainedWith"]); worn {
		return fmt.Errorf("lone shooter got the trained-with thought: %#v", last)
	}
	if na.AsString(last["primary"]) != real {
		return fmt.Errorf("real weapon %s not restored to hand: %#v", real, last)
	}
	if na.AsNumber(last["practiceOnMap"]) != 0 {
		return fmt.Errorf("practice weapon left on the map: %#v", last)
	}
	for _, def := range na.AsSlice(last["held"]) {
		if name := na.AsString(def); name == "Bow_Training" || strings.HasPrefix(name, "Gun_Practice") {
			return fmt.Errorf("practice weapon still held: %#v", last)
		}
	}
	if na.AsNumber(last["sentinelHp"]) != na.AsNumber(last["sentinelMax"]) {
		return fmt.Errorf("damage outside the range: %#v", last)
	}
	return nil
}

// trainingGained is the XP a skill gained since startLevel at zero XP, on
// vanilla's curve below level 9 (a level costs 1000 x (level + 1)).
func trainingGained(startLevel, level int, xp float64) float64 {
	total := xp
	for i := startLevel; i < level; i++ {
		total += 1000 * float64(i+1)
	}
	return total
}
