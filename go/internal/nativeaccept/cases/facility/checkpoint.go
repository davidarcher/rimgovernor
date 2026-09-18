package facility

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// startupCheckpoint is the committed save the comfort case resumes from: the
// tribal8 baseline played under comfortFamilies until RankDevelopment first
// admits EnsureComfort, so every startup-survival goal that ranks ahead of
// it (shelter, campfire, storage, fields, work assignments) is served or
// recovered (issue #201: from the raw baseline the ladder alone outlasts a
// 12-minute watch). tools/facility-checkpoint writes it under
// cases.CommittedSavesDir; regenerate it after fixture, ladder or
// save-format changes.
const startupCheckpoint = "RimGovernor-facility-startup"

func init() {
	cases.Register(cases.Case{
		Name:   "tools/facility-checkpoint",
		Scope:  "Checkpoint generation: the tribal8 baseline runs the comfort case's families until EnsureComfort is first admitted past the startup ladder, then the game is saved as the committed " + startupCheckpoint + " checkpoint facility/comfort resumes from (issue #201).",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   []string{string(na.NeedFood), string(na.NeedJoy)},
		Serve:  debugSpec("facility-checkpoint", comfortFamilies),
		Budget: 40 * time.Minute,
		Reason: "the startup ladder from the raw baseline takes over 15 minutes on a shared box; this run replaces it with a save so facility/comfort stays within budget",
		Run: func(ctx context.Context, s cases.Session) error {
			report := s.Report()
			checkpointed := func(map[string]any) bool {
				_, ok := report["checkpoint"]
				return ok
			}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: 36 * time.Minute, Poll: 5 * time.Second, Goal: policy.EnsureComfort,
					Extra:      []policy.GoalID{policy.EnsureInitialShelter, policy.EnsureCooking, policy.EnsureFoodStorage, policy.EnsureFoodSupply},
					Until:      checkpointed,
					Checkpoint: &sustainedfood.Checkpoint{Name: startupCheckpoint, When: comfortAdmitted},
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					if !checkpointed(nil) {
						// The rooms and colony facts say which startup goal
						// held the ladder (a hut with no free floor for the
						// food stockpile, an unroofed shell...).
						if rooms, err := h.Call(ctx, "audit-rooms", "home/list_rooms", map[string]any{"cells": true}); err == nil {
							report["native_rooms"] = rooms["rooms"]
						}
						if facts, err := h.Call(ctx, "audit-colony-facts", "home/colony_facts", map[string]any{}); err == nil {
							report["colony_facts_upkeep"] = facts["upkeep"]
						}
						return fmt.Errorf("EnsureComfort was never admitted within the watch window")
					}
					return commitCheckpoint(s.Config().Root, cases.CommittedSaves(), report)
				},
			})
			return err
		},
	})
}

// debugSpec is spec with the scheduler's step trace on: the checkpoint run
// is where a startup planner that never produces a method is diagnosed.
func debugSpec(prefix, families string) *cases.ServeSpec {
	s := spec(prefix, families)
	s.Env = []string{"RIMGOVERNOR_CLOCK_DEBUG=1"}
	return s
}

// comfortAdmitted reports a sample whose EnsureComfort goal holds a
// development slot or a method: RankDevelopment only grants either once no
// startup-survival goal is unserved.
func comfortAdmitted(sample map[string]any) bool {
	if count, _ := sample["method_count"].(int); count > 0 {
		return true
	}
	development, _ := sample["development"].(map[string]any)
	selected, _ := development["selected"].(bool)
	return selected
}

// commitCheckpoint copies the checkpoint save the watch staged under
// root/profile/Saves into the committed directory.
func commitCheckpoint(root, committed string, report na.Report) error {
	staged := filepath.Join(root, "profile", "Saves", startupCheckpoint+".rws")
	if info, err := os.Stat(staged); err != nil || info.Size() == 0 {
		return fmt.Errorf("checkpoint save %s is missing or empty", staged)
	}
	if err := os.MkdirAll(committed, 0755); err != nil {
		return err
	}
	target := filepath.Join(committed, startupCheckpoint+".rws")
	if err := na.CopyFile(staged, target); err != nil {
		return err
	}
	report["committed_checkpoint"] = target
	return nil
}
