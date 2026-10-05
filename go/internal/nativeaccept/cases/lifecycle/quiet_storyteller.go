// lifecycle/quiet_storyteller is the native acceptance for the shipped
// RimGovernorQuiet StorytellerDef (#2023): a colony started through the
// new-colony op with that storyteller has no storyteller comps, keeps none
// after a save and reload (RimWorld rebuilds comps from the def by defName),
// and fires no incident over a day of ticks. Nothing here uses the
// test/quiet_storyteller apply path; its inspect action is only the read.
package lifecycle

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const quietStorytellerDef = "RimGovernorQuiet"

// quietDayTicks is one in-game day.
const quietDayTicks = 60000

func init() {
	cases.Register(cases.Case{
		Name: "lifecycle/quiet_storyteller",
		Scope: "RimGovernorQuiet storyteller def (#2023): a colony started with it via the new-colony op reports that storyteller with zero storytellerComps, " +
			"keeps both after a save and lifecycle_load, and a day of ticks leaves the letter stack empty (no incident). Residuals " +
			"(inspirations, social fights, map-gen strangers and hives) are not asserted.",
		Start:  cases.Owned{},
		Reason: "the start needs a fresh main menu process",
		NoKeep: true,
		Budget: 20 * time.Minute,
		Run:    runQuietStoryteller,
	})
}

func runQuietStoryteller(ctx context.Context, s cases.Session) error {
	cfg, report := s.Config(), s.Report()
	reuse, err := na.OpenReusableGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer func() {
		retireCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = reuse.Retire(retireCtx, "quiet storyteller case complete")
	}()
	h, err := reuse.Session(ctx)
	if err != nil {
		return err
	}
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	for _, tool := range []string{"rimgovernor/lifecycle_new_colony", "rimgovernor/lifecycle_save", "rimgovernor/lifecycle_load", na.QuietStorytellerTool} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}

	spec := newColonySpec()
	spec["storyteller"] = quietStorytellerDef
	spec["seed"] = "quietteller-accept"
	spec["saveName"] = "quietteller-accept"
	id := fmt.Sprintf("quietteller-%d", time.Now().UnixNano())
	if _, err := newColonyRun(ctx, h, id, spec, "FINISHING", "quiet"); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "quiet-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := quietInspect(ctx, h, "fresh", report); err != nil {
		return err
	}

	identity, err := na.ReadIdentity(ctx, h, "quiet-identity")
	if err != nil {
		return err
	}
	saveName := fmt.Sprintf("quietteller-%d", time.Now().UnixNano())
	saved, err := h.Wire(ctx, "quiet-save", "lifecycle_save", map[string]any{
		"player":   map[string]any{"identity": identity, "playerDirection": 1, "requestId": saveName},
		"saveName": saveName,
	})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(saved, "completed"); err != nil {
		return fmt.Errorf("save: %w", err)
	}
	loadID := saveName + "-load"
	loadReply, err := h.Wire(ctx, "quiet-load", "lifecycle_load", map[string]any{
		"requestId": loadID, "saveName": saveName, "readiness": "READINESS_MAP",
		"expectedPlayer":  map[string]any{"identity": identity, "playerDirection": 1, "requestId": loadID},
		"playerDirection": 1,
	})
	if err != nil {
		return err
	}
	if _, err := na.PollLoad(ctx, h, loadReply, loadID, "quiet-load-poll"); err != nil {
		return fmt.Errorf("reload: %w", err)
	}
	if err := quietInspect(ctx, h, "reloaded", report); err != nil {
		return err
	}

	// A day of ticks: any incident delivers a letter, and a pausing letter
	// fails RunUntil with a PauseCause.
	start, err := h.Tick(ctx)
	if err != nil {
		return err
	}
	advanced, err := na.RunUntil(ctx, h, "quiet-day", 2*quietDayTicks, na.Wait{}, func(ctx context.Context) (string, bool, error) {
		tick, err := h.Tick(ctx)
		if err != nil {
			return "", false, err
		}
		letters, err := na.ReadLetters(ctx, h.WireFunc(), "quiet-day", nil)
		if err != nil {
			return "", false, err
		}
		if len(letters) > 0 {
			return "", false, fmt.Errorf("tick %d: a quiet colony delivered letters: %v", tick, letters)
		}
		return "", tick >= start+quietDayTicks, nil
	})
	if err != nil {
		return err
	}
	report["quiet_day_ticks"] = advanced
	return nil
}

// quietInspect asserts the loaded game's storyteller is RimGovernorQuiet with
// no comps, via the fixture op's read-only inspect action.
func quietInspect(ctx context.Context, h *na.Harness, label string, report na.Report) error {
	reply, err := h.Call(ctx, "quiet-inspect-"+label, na.QuietStorytellerTool, map[string]any{"action": "inspect"})
	if err != nil {
		return err
	}
	if got := na.AsString(reply["storyteller"]); got != quietStorytellerDef {
		return fmt.Errorf("%s: storyteller %q, want %s", label, got, quietStorytellerDef)
	}
	if comps := int(na.AsNumber(reply["comps"])); comps != 0 {
		return fmt.Errorf("%s: storytellerComps has %d entries, want 0", label, comps)
	}
	report["quiet_"+label] = map[string]any{"storyteller": reply["storyteller"], "comps": reply["comps"]}
	return nil
}
