package sustained

import (
	"context"
	"os"
	"strconv"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ColonyWindowTicks is the colony diagnostics' sample window in game
// ticks: five game days by default (60000 ticks a day), the shortest
// stretch on which the foothold goals settle and the maintenance tier
// gets a turn. ColonyWindowTicksEnv overrides it; Window() stays the
// wall-clock ceiling for a game that stops advancing.
const ColonyWindowTicks uint64 = 5 * 60000

// ColonyWindowTicksEnv names the environment variable that overrides
// ColonyWindowTicks with a tick count (e.g. 900000 for fifteen days).
const ColonyWindowTicksEnv = "RIMGOVERNOR_ACCEPT_WINDOW_TICKS"

// ColonyWindow is ColonyWindowTicksEnv when set and valid, else
// ColonyWindowTicks.
func ColonyWindow() uint64 {
	if raw := os.Getenv(ColonyWindowTicksEnv); raw != "" {
		if n, err := strconv.ParseUint(raw, 10, 64); err == nil && n > 0 {
			return n
		}
	}
	return ColonyWindowTicks
}

// colonyGoals are the goals every colony sample reads beside
// EnsureFoodSupply: the foothold gates and the first maintenance-tier
// projects, so the timeline shows which one stalls, thrashes or starves the
// others (#99).
var colonyGoals = []policy.GoalID{
	policy.EnsureInitialShelter, policy.EnsureFoodStorage, policy.EnsureCooking,
	policy.EnsureTemperatureSafety, policy.MaintainSleeping, policy.MaintainWood,
	policy.EnsureWorkAssignments, policy.EnsureBasicDefense, policy.EnsureResearch,
	policy.EnsureComfort, policy.MaintainStorage, policy.MaintainEssentialRepairs,
}

func init() {
	cases.Register(colony("sustained/colony", na.QuietRequired,
		"Diagnostic: every routine family on the "+BaselineSave+" save under a quiet storyteller for "+
			"ColonyWindowTicks game ticks, sampling the foothold and first maintenance goals together. "+
			"Evidence for #99's sustained coverage: which goal stalls, thrashes or starves the others once "+
			"all families share one step budget. Not a pass/fail acceptance gate: it fails only when the harness "+
			"itself cannot complete."))
	cases.Register(colony("sustained/colony-loud", na.Loud,
		"Diagnostic: sustained/colony with the save's own storyteller, so raids, weather and events land on the "+
			"colony while every family runs. Not a pass/fail acceptance gate."))
}

func colony(name string, quiet na.QuietMode, scope string) cases.Case {
	return cases.Case{
		Name:  name,
		Scope: scope,
		Start: cases.Save{Name: BaselineSave},
		Keep:  []string{string(na.LiveNeeds)},
		Quiet: quiet,
		Serve: ptr(cases.ServeSpec{NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: "sustained-colony"}),
		// The whole point is a long window with every family live: the
		// checklist's minute-scale, single-family, frozen-needs defaults are
		// what this diagnostic exists to look past. Budget covers the wall
		// ceiling plus load, acquire, stop and reattach.
		Reason: "sustained multi-day diagnostic over every routine family with live needs" +
			map[bool]string{true: " and the save's storyteller", false: ""}[quiet == na.Loud],
		Budget: Window() + 7*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: Window(), Window: ColonyWindow(), Poll: 10 * time.Second,
					Goal: policy.EnsureFoodSupply, Extra: colonyGoals,
					// A diagnostic, not a gate: a refusal is part of what the
					// long window records, never a reason to cut it short.
					FailFast: sustainedfood.FailFast{Disabled: true},
				},
			})
			return err
		},
	}
}
