// Package sustained holds issue #1's EnsureFoodSupply diagnostics: the
// goal-state timeline against the tribal8 baseline save (sustained/food)
// and the same window across the save variants of manifests/issue-1-matrix.json
// (sustained/matrix-*). They are diagnostics rather than pass/fail gates:
// a case fails only when the harness itself could not complete (load,
// service, authority, a starved step, the startup log), never over a poor
// food outcome, which is exactly the evidence it exists to surface.
// sustained/winter is the one pass/fail case here: the seasonal food
// reserve held across the tile's first non-growing day (#251).
package sustained

import (
	"context"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
)

// BaselineSave is the hand-prepared eight-colonist tribal start every
// serve-driven case loads unless it names another save.
const BaselineSave = "RimGovernor-tribal8-baseline"

// DefaultWindow is the sample window: wall-clock minutes at the serve clock
// speed, not ticks (#133); the regression-gate length. WindowEnv lengthens
// it for a diagnostic run (the binaries' 20m).
const DefaultWindow = 8 * time.Minute

// WindowEnv names the environment variable that overrides DefaultWindow
// with a Go duration (e.g. 20m). Every food observation reports the window
// it ran under as window_ms.
const WindowEnv = "RIMGOVERNOR_ACCEPT_WINDOW"

// Window is the sample window this process observes with: WindowEnv when
// set and valid, else DefaultWindow.
func Window() time.Duration {
	if raw := os.Getenv(WindowEnv); raw != "" {
		if d, err := time.ParseDuration(raw); err == nil && d > 0 {
			return d
		}
	}
	return DefaultWindow
}

// Spec is the serve spec every food-pipeline observation launches:
// EnsureFoodSupply's own families only (field growing, food storage,
// harvest/wood acquisition, cooking bills, starting supplies; the
// production-policy family only so the executor's capability is wired), so
// the food outcome under diagnosis is not confounded by other families and
// every family shares one step budget (#103). StepStall fails fast when no
// scheduler step admits a clock window.
func Spec(prefix string) cases.ServeSpec {
	return cases.ServeSpec{
		Families:      []string{"field,food-storage,acquisition,cooking,supply,production-policy"},
		NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: prefix,
	}
}

// MatrixWindowTicks is the matrix variants' sample window in game ticks
// (one in-game hour, #133): a variant's watch ends once the live tick has
// advanced this far, and Window() only caps a game that stops advancing.
// The diagnostic sustained/food watches the whole wall-clock window.
const MatrixWindowTicks = 2500

func init() {
	cases.Register(food("sustained/food", BaselineSave, "sustained-food", 0))
}

// food is one EnsureFoodSupply timeline on save; ticks > 0 ends the watch
// once the game advanced that far (report "window").
func food(name, save, prefix string, ticks uint64) cases.Case {
	return cases.Case{
		Name: name,
		Scope: "Diagnostic: EnsureFoodSupply goal-state timeline against the " + save +
			" save under the live routine reviewer/field planner, evidence for issue #1's eight-colonist crop labor / interim food deficit. Not a pass/fail acceptance gate.",
		Start: cases.Save{Name: save},
		Keep:  []string{string(na.NeedFood)},
		Serve: ptr(Spec(prefix)),
		// Load, acquire, stop and reattach fit in the margin over the window.
		Budget: Window() + 7*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: Window(), Window: ticks},
			})
			return err
		},
	}
}

func ptr(spec cases.ServeSpec) *cases.ServeSpec { return &spec }
