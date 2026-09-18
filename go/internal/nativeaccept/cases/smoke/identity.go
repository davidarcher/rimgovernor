// Package smoke holds the cases that prove the runner itself: open a game,
// read what the runner prepared, pass.
package smoke

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:  "smoke/identity",
		Scope: "Runner smoke: the shared runner opens a quiet debug game, freezes needs and hands the case a loaded identity.",
		Start: cases.DebugStart{},
		// Boot plus a handful of reads on a kept process.
		Budget: 5 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			identity := s.Identity()
			for _, key := range []string{"colonyId", "loadToken"} {
				if na.AsString(identity[key]) == "" {
					return fmt.Errorf("identity has no %s: %#v", key, identity)
				}
			}
			if !na.Contains(s.Names(), "rimgovernor/operations_execute") {
				return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
			}
			// A second read through the same session must agree with the
			// runner's: the game is loaded and stable under the case.
			reply, err := s.Harness().Wire(ctx, "identity-again", "lifecycle_read_identity", map[string]any{})
			if err != nil {
				return err
			}
			_, loaded, err := na.Outcome(reply, "loaded")
			if err != nil {
				return err
			}
			loadedContext, _ := na.AsMap(loaded["context"])
			again, _ := na.AsMap(loadedContext["identity"])
			if na.AsString(again["loadToken"]) != na.AsString(identity["loadToken"]) {
				return fmt.Errorf("loadToken changed under the case: %q then %q", identity["loadToken"], again["loadToken"])
			}
			s.Report()["identity_again"] = again
			return nil
		},
	})
}
