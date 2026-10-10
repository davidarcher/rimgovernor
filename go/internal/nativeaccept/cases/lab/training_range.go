package lab

import (
	"context"
	"fmt"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The open-air range's verification cases (#2688). Each stages the same fixture
// column as lab/training-shooting plus one option and asserts what only the
// native job, the verb and vanilla's social and fire rules can show. They run in
// the nightly bulk tier.

var trainingOps = []string{"test/training_prepare", "test/training_inspect", "test/training_act"}

func init() {
	cases.Register(cases.Case{
		Name: "lab/training-midlane",
		Scope: "A drafted colonist standing between the stand and the dummy (#2688) takes no injury from the drill and the shooter still " +
			"completes its session: the verb is cast with friendly fire prevented, so an open lane needs no partition. A Go snapshot test cannot see the shots.",
		Start: cases.Lab{Colonists: 3}, RequiredOps: trainingOps, QuietWorld: true, Budget: cases.LabBudget, Crew: cases.Crew{Size: 3},
		Run: func(ctx context.Context, s cases.Session) error {
			t, err := prepareTraining(ctx, s, map[string]any{"bystander": true})
			if err != nil {
				return err
			}
			last, err := t.untilTrained(ctx, 0)
			if err != nil {
				return err
			}
			if na.AsNumber(last["shotsFired"]) < 1 {
				return fmt.Errorf("mid-lane pawn stalled the shooter: no shot fired: %#v", last)
			}
			if na.AsNumber(last["bystanderHurt"]) != 0 || na.AsNumber(last["bystanderHealth"]) < 1 {
				return fmt.Errorf("mid-lane pawn was hit by a drill arrow: %#v", last)
			}
			return nil
		},
	})
	cases.Register(cases.Case{
		Name: "lab/training-chat",
		Scope: "Two colonists drilling at adjacent stands (#2688) sit inside vanilla's 6-cell line-of-sight chat rule and can chat while the " +
			"drill job runs: the drill does not bar social interaction.",
		Start: cases.Lab{Colonists: 3}, RequiredOps: trainingOps, QuietWorld: true, Budget: cases.LabBudget, Crew: cases.Crew{Size: 3},
		Run: func(ctx context.Context, s cases.Session) error {
			t, err := prepareTraining(ctx, s, map[string]any{"neighbour": true})
			if err != nil {
				return err
			}
			if _, err := na.RunUntil(ctx, t.h, "training-both", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
				got, err := t.inspect(ctx)
				if err != nil {
					return "", false, err
				}
				both := training(got["job"]) && training(got["otherJob"])
				return fmt.Sprint(got["job"], got["otherJob"]), both, nil
			}); err != nil {
				return err
			}
			chat, err := t.h.Call(ctx, "training-chat", "test/training_act", map[string]any{
				"action": "chat", "pawnId": t.prepared["pawn"], "otherId": t.prepared["neighbour"],
			})
			if err != nil {
				return err
			}
			s.Report()["chat"] = chat
			if good, _ := na.AsBool(chat["good"]); !good {
				return fmt.Errorf("adjacent drillers are outside the chat rule: %#v", chat)
			}
			if chatted, _ := na.AsBool(chat["chatted"]); !chatted {
				return fmt.Errorf("adjacent driller could not chat: %#v", chat)
			}
			return nil
		},
	})
	cases.Register(cases.Case{
		Name: "lab/training-tiers",
		Scope: "Tier ceilings end to end (#2688): a Shooting 8 and a Shooting 10 colonist are both idle at the bow's ceiling; finishing " +
			"Gunsmithing lets the 8 train past 8 with the practice rifle while the 10 stays idle; finishing ChargedShot lets the 10 train past 10 with the " +
			"practice pulse rifle (separate colonists because the 3,000 XP daily budget would stop one). The native tier read, ceiling and weapon swap cannot be seen from a Go snapshot.",
		Start: cases.Lab{Colonists: 3}, RequiredOps: trainingOps, QuietWorld: true, Budget: cases.LabBudget, Crew: cases.Crew{Size: 3},
		Run: func(ctx context.Context, s cases.Session) error {
			low, err := prepareTraining(ctx, s, map[string]any{"level": 8, "neighbour": true})
			if err != nil {
				return err
			}
			high := low.neighbour()
			if err := low.act(ctx, map[string]any{"action": "level", "pawnId": low.prepared["neighbour"], "level": 10}); err != nil {
				return err
			}
			if err := low.idleFor(ctx, 600); err != nil {
				return fmt.Errorf("level 8 at the bow ceiling: %w", err)
			}
			if err := high.idleFor(ctx, 600); err != nil {
				return fmt.Errorf("level 10 below Gunsmithing: %w", err)
			}
			for _, step := range []struct {
				who              *trainingRun
				research, weapon string
				level            int
			}{{low, "Gunsmithing", "Gun_PracticeRifle", 8}, {high, "ChargedShot", "Gun_PracticePulseRifle", 10}} {
				if err := step.who.act(ctx, map[string]any{"action": "research", "research": step.research}); err != nil {
					return err
				}
				var seen string
				last, err := step.who.untilTrainedWith(ctx, step.level, &seen)
				if err != nil {
					return fmt.Errorf("after %s: %w", step.research, err)
				}
				if seen != step.weapon {
					return fmt.Errorf("after %s trained with %q, want %s: %#v", step.research, seen, step.weapon, last)
				}
				if step.who == low {
					got, err := high.inspect(ctx)
					if err != nil {
						return err
					}
					if na.AsNumber(got["shotsFired"]) != 0 {
						return fmt.Errorf("level 10 trained under the rifle ceiling: %#v", got)
					}
				}
			}
			return nil
		},
	})
	for _, c := range []struct{ name, scope, action string }{
		{"lab/training-raid", "A raid drafts the shooter mid-drill (#2688): the finish action restores the real weapon and leaves no practice weapon anywhere.", "draft"},
		{"lab/training-fire", "A fire on the open-air dummy mid-drill (#2688): whatever ends the session, the real weapon is restored and no practice weapon leaks.", "fire"},
	} {
		c := c
		cases.Register(cases.Case{
			Name: c.name, Scope: c.scope,
			Start: cases.Lab{Colonists: 3}, RequiredOps: trainingOps, QuietWorld: true, Budget: cases.LabBudget, Crew: cases.Crew{Size: 3},
			Run: func(ctx context.Context, s cases.Session) error { return runTrainingInterrupt(ctx, s, c.action) },
		})
	}
}

func runTrainingInterrupt(ctx context.Context, s cases.Session, action string) error {
	t, err := prepareTraining(ctx, s, nil)
	if err != nil {
		return err
	}
	if _, err := na.RunUntil(ctx, t.h, "training-start", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := t.inspect(ctx)
		if err != nil {
			return "", false, err
		}
		primary := na.AsString(got["primary"])
		armed := primary == "Bow_Training" || strings.HasPrefix(primary, "Gun_Practice")
		return fmt.Sprint(got["job"], primary), training(got["job"]) && armed, nil
	}); err != nil {
		return err
	}
	if err := t.act(ctx, map[string]any{"action": action, "pawnId": t.prepared["pawn"], "thingId": t.prepared["dummy"]}); err != nil {
		return err
	}
	// A drafted shooter ends the job at once; a burning dummy ends it by
	// destruction or when the session completes. Either way the hands are restored.
	var last map[string]any
	if _, err := na.RunUntil(ctx, t.h, "training-ended", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := t.inspect(ctx)
		if err != nil {
			return "", false, err
		}
		last = got
		return fmt.Sprint(got["job"]), !training(got["job"]), nil
	}); err != nil {
		return err
	}
	s.Report()["after"] = last
	if na.AsString(last["primary"]) != na.AsString(t.prepared["weapon"]) {
		return fmt.Errorf("real weapon not restored after %s: %#v", action, last)
	}
	if na.AsNumber(last["practiceOnMap"]) != 0 {
		return fmt.Errorf("practice weapon left on the map after %s: %#v", action, last)
	}
	for _, def := range na.AsSlice(last["held"]) {
		if name := na.AsString(def); name == "Bow_Training" || strings.HasPrefix(name, "Gun_Practice") {
			return fmt.Errorf("practice weapon still held after %s: %#v", action, last)
		}
	}
	return nil
}

// trainingRun is a staged range column and the arguments that read it back.
type trainingRun struct {
	h        *na.Harness
	prepared map[string]any
	args     map[string]any
}

func prepareTraining(ctx context.Context, s cases.Session, extra map[string]any) (*trainingRun, error) {
	h := s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	args := map[string]any{"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"]))}
	for k, v := range extra {
		args[k] = v
	}
	prepared, err := h.Call(ctx, "training-prepare", "test/training_prepare", args)
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return nil, fmt.Errorf("training_prepare: %#v", prepared)
	}
	return &trainingRun{h: h, prepared: prepared, args: map[string]any{
		"pawnId": prepared["pawn"], "dummyId": prepared["dummy"], "sentinels": prepared["sentinels"],
		"otherId": prepared["neighbour"], "bystanderId": prepared["bystander"],
	}}, nil
}

// neighbour is the run read back through the second column's shooter and dummy.
func (t *trainingRun) neighbour() *trainingRun {
	args := map[string]any{}
	for k, v := range t.args {
		args[k] = v
	}
	args["pawnId"], args["dummyId"] = t.prepared["neighbour"], t.prepared["neighbourDummy"]
	return &trainingRun{h: t.h, prepared: t.prepared, args: args}
}

func training(job any) bool { return strings.HasPrefix(na.AsString(job), "RimGovernor_Train") }

func (t *trainingRun) inspect(ctx context.Context) (map[string]any, error) {
	return t.h.Call(ctx, "training-inspect", "test/training_inspect", t.args)
}

func (t *trainingRun) act(ctx context.Context, args map[string]any) error {
	got, err := t.h.Call(ctx, "training-act", "test/training_act", args)
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(got["success"]); !ok {
		return fmt.Errorf("training_act %v: %#v", args["action"], got)
	}
	return nil
}

// untilTrained runs until the shooter has gained XP above startLevel and is idle again.
func (t *trainingRun) untilTrained(ctx context.Context, startLevel int) (map[string]any, error) {
	return t.untilTrainedWith(ctx, startLevel, new(string))
}

// untilTrainedWith also records the practice weapon seen in hand while the job ran.
func (t *trainingRun) untilTrainedWith(ctx context.Context, startLevel int, weapon *string) (map[string]any, error) {
	var last map[string]any
	_, err := na.RunUntil(ctx, t.h, "training", 9000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := t.inspect(ctx)
		if err != nil {
			return "", false, err
		}
		last = got
		if p := na.AsString(got["primary"]); p == "Bow_Training" || strings.HasPrefix(p, "Gun_Practice") {
			*weapon = p
		}
		gained := trainingGained(startLevel, int(na.AsNumber(got["shootingLevel"])), na.AsNumber(got["shootingXp"]))
		return fmt.Sprint(gained, got["job"]), gained > 0 && !training(got["job"]), nil
	})
	return last, err
}

// idleFor runs ticks of game time and fails when the shooter drilled in them.
func (t *trainingRun) idleFor(ctx context.Context, ticks uint64) error {
	var first, shots float64
	var last map[string]any
	_, err := na.RunUntil(ctx, t.h, "training-idle", ticks+3000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := t.inspect(ctx)
		if err != nil {
			return "", false, err
		}
		last = got
		if training(got["job"]) {
			return "", false, fmt.Errorf("drilled at the ceiling: %#v", got)
		}
		if first == 0 {
			first, shots = na.AsNumber(got["tick"]), na.AsNumber(got["shotsFired"])
		}
		return fmt.Sprint(got["tick"]), na.AsNumber(got["tick"])-first >= float64(ticks), nil
	})
	if err != nil {
		return err
	}
	if na.AsNumber(last["shotsFired"]) != shots {
		return fmt.Errorf("trained at the ceiling: %#v", last)
	}
	return nil
}
