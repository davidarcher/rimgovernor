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
	for _, skill := range []string{"shooting", "melee"} {
		skill := skill
		cases.Register(cases.Case{
			Name: "lab/training-" + skill,
			Scope: "Native training job (#2610), " + skill + ": test/training_prepare builds one range lane (stand, dummy, partitions, back wall) and " +
				"leaves a low-skill colonist whose only work is the RimGovernorTraining work type; with no order from Go the vanilla work scan " +
				"offers the lane, the colonist drills, direct Learn raises the skill without touching xpSinceMidnight, the colonist's own weapon " +
				"is back in hand with no Bow_Training left anywhere, and a sentinel wall off the lane is unharmed. A Go snapshot test cannot see " +
				"the work giver, the job driver, the weapon swap or the arrows.",
			Start:       cases.Lab{Colonists: 3},
			RequiredOps: []string{"test/training_prepare", "test/training_inspect"},
			QuietWorld:  true,
			Budget:      cases.LabBudget,
			Crew:        cases.Crew{Size: 3},
			Run:         func(ctx context.Context, s cases.Session) error { return runTraining(ctx, s, skill) },
		})
	}
}

func runTraining(ctx context.Context, s cases.Session, skill string) error {
	h := s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	prepared, err := h.Call(ctx, "training-prepare", "test/training_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "skill": skill,
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("training_prepare: %#v", prepared)
	}
	args := map[string]any{
		"pawnId": prepared["pawn"], "dummyId": prepared["dummy"],
		"partitions": prepared["partitions"], "backWall": prepared["backWall"], "sentinels": prepared["sentinels"],
	}
	level, xp, midnight := skill+"Level", skill+"Xp", skill+"MidnightXp"
	real := na.AsString(prepared["weapon"])
	startLevel := 0
	if skill == "melee" {
		startLevel = 5
	}
	var last map[string]any
	if _, err := na.RunUntil(ctx, h, "training", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "training-inspect", "test/training_inspect", args)
		if err != nil {
			return "", false, err
		}
		last = got
		gained := trainingGained(startLevel, int(na.AsNumber(got[level])), na.AsNumber(got[xp]))
		idle := !strings.HasPrefix(na.AsString(got["job"]), "RimGovernor_Train")
		return fmt.Sprint(gained, got["job"]), gained > 0 && idle, nil
	}); err != nil {
		return err
	}
	s.Report()["training"] = last
	if got := trainingGained(startLevel, int(na.AsNumber(last[level])), na.AsNumber(last[xp])); got <= 0 || got > trainingMaxXp {
		return fmt.Errorf("%s xp gained %v outside (0, %d]: %#v", skill, got, trainingMaxXp, last)
	}
	if na.AsNumber(last[midnight]) != 0 {
		return fmt.Errorf("direct Learn counted toward the daily soft cap: %#v", last)
	}
	if skill == "shooting" {
		if na.AsNumber(last["shotsFired"]) < 1 {
			return fmt.Errorf("no arrow was fired: %#v", last)
		}
		// Every arrow hits the dummy, a partition or the back wall: some lane piece is damaged.
		if na.AsNumber(last["dummyHp"]) >= na.AsNumber(prepared["dummyMax"]) &&
			na.AsNumber(last["partitionHp"]) >= na.AsNumber(prepared["partitionMax"]) && na.AsNumber(last["backWallHp"]) >= na.AsNumber(prepared["backWallMax"]) {
			return fmt.Errorf("no arrow hit the lane: %#v", last)
		}
	}
	if na.AsString(last["primary"]) != real {
		return fmt.Errorf("real weapon %s not restored to hand: %#v", real, last)
	}
	if na.AsNumber(last["bowsOnMap"]) != 0 {
		return fmt.Errorf("training bow left on the map: %#v", last)
	}
	for _, def := range na.AsSlice(last["held"]) {
		if na.AsString(def) == "Bow_Training" {
			return fmt.Errorf("training bow still held: %#v", last)
		}
	}
	if na.AsNumber(last["sentinelHp"]) != na.AsNumber(last["sentinelMax"]) {
		return fmt.Errorf("damage outside the lane: %#v", last)
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
