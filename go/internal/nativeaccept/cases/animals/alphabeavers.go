package animals

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// alphabeaverFamilies serve the pest hunt (acquisition) and the emergency
// responders: a wounded beaver has a 50% chance of turning manhunter, and
// the whole pack is a herd, so a hunt can turn into a small raid the
// defense family has to answer before the hunt census offers the survivors
// again. The hunter is the fixture's (a bow and Hunting active), not the
// equip and work families': the baseline's bows start in the drop pile.
const alphabeaverFamilies = "acquisition,supply,defense,tend,rescue"

// alphabeaverWindow is how long ClearPests gets to plan and dispatch a hunt
// of every beaver: two hunts outstanding at a time, each a walk across the
// map, with re-plans when a beaver wanders off its planned cell before the
// hunter reaches it. Three beavers took two in-game days on a loaded host
// (runs 6 and 7 of #247), so the pack is two.
const alphabeaverWindow = 10 * time.Minute

// alphabeaverCount is the pack size the fixture stages, the incident's
// usual two or three.
const alphabeaverCount = 2

// settleStepTicks and settleSteps bound the settling the audit drives
// itself once the watch ends: a beaver the goal already designated is the
// hunter's to finish natively (the designation outlives the service), and
// the case is about the goal answering the pack, not the hunter's walk.
const (
	settleStepTicks = 2500
	settleSteps     = 6
)

func init() {
	cases.Register(cases.Case{
		Name:  "animals/alphabeavers",
		Scope: "ClearPests hunts a wild alphabeaver pack (factionless, never hostile, ignored by the emergency census) through the food acquisition's hunt method until none remain on the map, observed natively (#247).",
		Start: cases.Fixture{Op: "test/pest_prepare", Args: map[string]any{"count": alphabeaverCount}, On: cases.Save{Name: sustained.BaselineSave}},
		// A hunter with pinned needs never leaves the hut for a beaver
		// across the map; hunger and rest stay live, like the food case's.
		Keep:   []string{string(na.NeedFood), string(na.NeedRest)},
		Serve:  &cases.ServeSpec{Families: []string{alphabeaverFamilies}, NativeTimeout: 15 * time.Second, Prefix: "animals"},
		Budget: alphabeaverWindow + 5*time.Minute,
		Reason: "hunts across a 250x250 map with bows, at most two outstanding at a time, each re-planned when its beaver wanders off the planned cell, then a bounded native settle for a beaver already designated",
		Run: func(ctx context.Context, s cases.Session) error {
			var staged []string
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: alphabeaverWindow, Poll: 5 * time.Second, Goal: policy.ClearPests,
					Extra: []policy.GoalID{policy.EnsureFoodSupply},
					Until: pestsCleared},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					prepared := s.Prepared()
					report["fixture"] = prepared
					for _, raw := range na.AsSlice(prepared["pests"]) {
						row, _ := na.AsMap(raw)
						if spawned, _ := na.AsBool(row["spawned"]); !spawned {
							return fmt.Errorf("fixture staged an unspawned beaver: %#v", row)
						}
						if hostile, _ := na.AsBool(row["hostile"]); hostile {
							return fmt.Errorf("fixture staged a hostile beaver; the emergency census would answer it: %#v", row)
						}
						staged = append(staged, na.AsString(row["id"]))
					}
					if len(staged) != alphabeaverCount {
						return fmt.Errorf("fixture staged %d beavers, wanted %d: %#v", len(staged), alphabeaverCount, prepared)
					}
					if len(na.AsSlice(prepared["hunters"])) == 0 {
						return fmt.Errorf("no colonist holds a ranged weapon with Hunting active; nothing can hunt: %#v", prepared["colonists"])
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					return auditAlphabeavers(ctx, s, h, report, staged)
				},
			})
			return err
		},
	})
}

// pestsCleared is the goal's own recovery: the wild-animal census counts
// no recognised pest.
func pestsCleared(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}

// auditAlphabeavers checks the goal's recovery against native: every staged
// beaver is dead or gone from the map, no wild alphabeaver remains, and no
// colonist died to the pack. A beaver still alive but designated when the
// watch ended is given a bounded settle: the goal did its part, the hunter
// finishes it under native ticks alone.
func auditAlphabeavers(ctx context.Context, s cases.Session, h *na.Harness, report na.Report, staged []string) error {
	readInspect := func() (map[string]any, error) {
		inspect, err := h.Call(ctx, "inspect-pests", "test/pest_inspect", map[string]any{})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(inspect["success"]); !success {
			return nil, fmt.Errorf("pest_inspect refused: %#v", inspect)
		}
		report["pest_inspect"] = inspect
		return inspect, nil
	}
	designatedAlive := func(inspect map[string]any) bool {
		for _, raw := range na.AsSlice(inspect["pests"]) {
			row, _ := na.AsMap(raw)
			dead, _ := na.AsBool(row["dead"])
			spawned, _ := na.AsBool(row["spawned"])
			designated, _ := na.AsBool(row["huntDesignated"])
			if !dead && spawned && designated {
				return true
			}
		}
		return false
	}
	inspect, err := readInspect()
	if err != nil {
		return err
	}
	steps := 0
	for ; designatedAlive(inspect) && steps < settleSteps; steps++ {
		if _, err = s.Advance(ctx, settleStepTicks); err != nil {
			return err
		}
		if inspect, err = readInspect(); err != nil {
			return err
		}
	}
	report["settle_steps"] = steps
	seen := map[string]bool{}
	for _, raw := range na.AsSlice(inspect["pests"]) {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["id"])
		seen[id] = true
		dead, _ := na.AsBool(row["dead"])
		spawned, _ := na.AsBool(row["spawned"])
		if !dead && spawned {
			return fmt.Errorf("beaver %s is still alive on the map: %#v", id, row)
		}
	}
	for _, id := range staged {
		if !seen[id] {
			return fmt.Errorf("staged beaver %s was forgotten by the fixture (world reloaded?)", id)
		}
	}
	if remaining := na.AsNumber(inspect["remaining"]); remaining != 0 {
		return fmt.Errorf("%v wild alphabeavers remain on the map: %v", remaining, inspect["remainingIds"])
	}
	for _, raw := range na.AsSlice(inspect["colonists"]) {
		row, _ := na.AsMap(raw)
		if dead, _ := na.AsBool(row["dead"]); dead {
			return fmt.Errorf("colonist %s died during the hunt", na.AsString(row["id"]))
		}
	}
	return nil
}
