// Package naming holds the naming/confirm case, issue #178 end to end
// against a live game under the real Go player service: the initial
// faction/settlement naming dialog (Dialog_NamePlayerFactionAndSettlement),
// which force-pauses the game until it is answered, is resolved by the
// ConfirmColonyNames routine family. The NamingFixture reopens the dialog on
// the baseline's settlement before the service attaches and the harness
// leaves it open (ServeSpec.KeepColonyNaming); the routine review raises
// ConfirmColonyNames, the naming planner commits a one-action
// NamingConfirmation plan for the exact observed window and suggestions,
// the store persists it, the executor confirms it through
// Operations.ConfirmColonyNames, and the clock starts afterwards.
package naming

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
)

const (
	baselineSave = "RimGovernor-tribal8-baseline"
	fixtureTool  = "test/open_colony_naming"
	// ceiling bounds the wait for the dialog to be confirmed and the clock
	// to start; the stall budget ends it earlier when nothing moves.
	ceiling = 8 * time.Minute
)

func init() {
	cases.Register(cases.Case{
		Name: "naming/confirm",
		Scope: "Issue #178: the force-pausing faction/settlement naming dialog is read as the colony facts naming " +
			"section, its exact observed suggestions confirmed through Operations.ConfirmColonyNames under the " +
			"ConfirmColonyNames routine goal (the plan persisted and dispatched by the store and worker), the names " +
			"applied natively and the native clock starting afterwards.",
		Start: cases.Save{Name: baselineSave},
		// The building families give the clock ordinary work to start on
		// once the dialog is gone.
		Serve: &cases.ServeSpec{
			Families: []string{"naming", "supply", "shelter", "sleeping"}, NativeTimeout: 15 * time.Second, Prefix: "naming-confirm",
			KeepColonyNaming: true,
		},
		Budget: 12 * time.Minute,
		Run:    run,
	})
}

// staged is what the fixture opened before the service took the game: the
// dialog's window ID and the generated suggestions the service must
// confirm verbatim.
type staged struct {
	window     int32
	faction    string
	settlement string
}

func (d *staged) open(ctx context.Context, h *na.Harness, names []string, identity map[string]any, report na.Report) error {
	if !na.Contains(names, fixtureTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture NamingFixture", fixtureTool)
	}
	opened, err := h.Call(ctx, "open-naming", fixtureTool, map[string]any{"action": "open"})
	if err != nil {
		return err
	}
	if open, _ := na.AsBool(opened["windowOpen"]); !open {
		return fmt.Errorf("open-naming: fixture dialog is not open: %#v", opened)
	}
	if forced, _ := na.AsBool(opened["forcePaused"]); !forced {
		return fmt.Errorf("open-naming: the dialog does not force-pause the game: %#v", opened)
	}
	d.window, d.faction, d.settlement = int32(na.AsNumber(opened["windowId"])), na.AsString(opened["factionName"]), na.AsString(opened["settlementName"])
	if d.faction == "" || d.settlement == "" {
		return fmt.Errorf("open-naming: the dialog carries no suggestions: %#v", opened)
	}
	if d.faction == na.AsString(opened["currentFactionName"]) && d.settlement == na.AsString(opened["currentSettlementName"]) {
		return fmt.Errorf("open-naming: the suggestions already are the live names, so confirming would prove nothing: %#v", opened)
	}
	report["naming_dialog"] = opened
	// The typed census must already expose the dialog exactly as the
	// service's own review and planner will read it.
	reply, err := h.Wire(ctx, "colony-facts-naming", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	naming, ok := na.AsMap(observed["naming"])
	if !ok || int32(na.AsNumber(naming["windowId"])) != d.window {
		return fmt.Errorf("colony-facts-naming: typed census lacks the open naming dialog: %#v", observed["naming"])
	}
	if na.AsString(naming["factionName"]) != d.faction || na.AsString(naming["settlementName"]) != d.settlement {
		return fmt.Errorf("colony-facts-naming: census suggestions %#v differ from the fixture's %q/%q", naming, d.faction, d.settlement)
	}
	report["colony_facts_naming"] = naming
	return nil
}

// confirmed is what the durable journal proves: the ConfirmColonyNames
// plan whose action targets the window, with the suggestions it carries
// and its stage.
type confirmed struct {
	Goal       domain.GoalID `json:"goal"`
	Epoch      uint64        `json:"epoch"`
	Plan       domain.PlanID `json:"plan"`
	Faction    string        `json:"faction"`
	Settlement string        `json:"settlement"`
	Stage      string        `json:"stage"`
}

// confirmations lists every ConfirmColonyNames plan the goal has admitted,
// across epochs, keyed by the targeted window.
func confirmations(ctx context.Context, st *store.Store) (map[int32]confirmed, error) {
	review, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	out := map[int32]confirmed{}
	for _, binding := range review.Goals {
		if binding.Need != policy.ConfirmColonyNames {
			continue
		}
		g, err := st.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return nil, err
		}
		for epoch := uint64(0); epoch <= g.Goal.Epoch; epoch++ {
			methods, err := st.LoadGoalMethods(ctx, binding.Goal, epoch)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return nil, err
			}
			for _, m := range methods {
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil {
					return nil, err
				}
				for i, action := range plan.Spec.Actions() {
					value, ok := action.NamingConfirmation()
					if !ok {
						continue
					}
					out[value.WindowID()] = confirmed{Goal: binding.Goal, Epoch: epoch, Plan: m.Plan, Faction: value.FactionName(), Settlement: value.SettlementName(), Stage: string(plan.Progress[i].View().Stage)}
				}
			}
		}
	}
	return out, nil
}

// clockTrace is what the durable clock inbox shows: every stop reason in
// order and how many epochs started.
type clockTrace struct {
	Stops  []string `json:"stops"`
	Starts int      `json:"starts"`
}

func traceClock(ctx context.Context, st *store.Store, profile string) (clockTrace, error) {
	inbox, err := st.LoadClockInbox(ctx, profile, clock.InboxCapacity)
	if errors.Is(err, store.ErrNotFound) {
		// The inbox binds on the service's first clock read; until then
		// there is nothing to trace.
		return clockTrace{}, nil
	}
	if err != nil {
		return clockTrace{}, err
	}
	var t clockTrace
	for _, page := range inbox.Pages {
		for _, event := range page.Page.GetEvents() {
			if event.GetStarted() != nil {
				t.Starts++
			}
			if stop := event.GetStopped(); stop != nil {
				t.Stops = append(t.Stops, stop.GetReason().String())
			}
		}
	}
	return t, nil
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	// Any naming dialog the baseline itself still holds goes first, so the
	// fixture's dialog is the one force-pausing window the service meets.
	if _, err := na.ConfirmColonyNames(ctx, s.Harness(), report); err != nil {
		return err
	}
	dialog := &staged{}
	if err := dialog.open(ctx, s.Harness(), s.Names(), s.Identity(), report); err != nil {
		return fmt.Errorf("before service: %w", err)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	// The service's clock inbox is keyed by its profile directory (na.Serve
	// puts it beside the state under the case's output).
	profile := s.Config().ServiceProfileDir()
	var found map[int32]confirmed
	var trace clockTrace
	// The dialog open at acquire is confirmed with the exact suggestions;
	// the clock could not start while it was up, so an epoch starting
	// afterwards proves the game was released.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: ceiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		a, err := confirmations(ctx, st)
		if err != nil {
			return "", false, err
		}
		t, err := traceClock(ctx, st, profile)
		if err != nil {
			return "", false, err
		}
		found, trace = a, t
		for window := range a {
			if window != dialog.window {
				return "", false, fmt.Errorf("planner targeted window %d, not the open naming dialog %d: %+v", window, dialog.window, a[window])
			}
		}
		c, ok := a[dialog.window]
		if !ok {
			return na.Signature(len(a), len(t.Stops), t.Starts), false, nil
		}
		if c.Faction != dialog.faction || c.Settlement != dialog.settlement {
			return "", false, fmt.Errorf("planner committed %q/%q, not the observed suggestions %q/%q", c.Faction, c.Settlement, dialog.faction, dialog.settlement)
		}
		if domain.Stage(c.Stage) == domain.Unsuccessful || domain.Stage(c.Stage) == domain.Cancelled {
			return "", false, fmt.Errorf("naming confirmation ended %s", c.Stage)
		}
		return na.Signature(len(a), fmt.Sprint(a), len(t.Stops), t.Starts), domain.Stage(c.Stage) == domain.Completed && t.Starts > 0, nil
	})
	report["confirmations"] = found
	report["clock"] = trace
	if err != nil {
		return err
	}
	report["run_keepalive"] = service.Stop()

	// Independent native read after the service releases the game slot:
	// the dialog is closed, the faction and settlement carry the confirmed
	// suggestions and no force-pausing window remains.
	h, err := s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := h.Call(ctx, "naming-after", fixtureTool, map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	report["naming_after"] = after
	if open, _ := na.AsBool(after["windowOpen"]); open {
		return fmt.Errorf("naming-after: the naming dialog is still open: %#v", after)
	}
	if got := na.AsString(after["currentFactionName"]); got != dialog.faction {
		return fmt.Errorf("naming-after: faction is named %q, not the confirmed %q", got, dialog.faction)
	}
	if got := na.AsString(after["currentSettlementName"]); got != dialog.settlement {
		return fmt.Errorf("naming-after: settlement is named %q, not the confirmed %q", got, dialog.settlement)
	}
	if windows := na.AsSlice(after["forcePausingWindows"]); len(windows) != 0 {
		return fmt.Errorf("naming-after: force-pausing windows remain: %#v", windows)
	}
	return nil
}
