// Package dialog holds the dialog/pause case (the former dialogpauseaccept),
// issue #156 end to end against a live game under the real Go player
// service: a force-pausing choice dialog the game opens by itself
// (Verse.Dialog_NodeTree, the shape of a finished research project's
// completion dialog or a caravan demand) no longer strands the native
// clock. Two dialogs are staged through the DialogFixture before the
// service attaches: one already open when the service acquires authority
// (the clock cannot start at all until it is answered), and one scheduled
// to open on a later game tick while a supervised window is running (the
// STOP_REASON_DIALOG_PAUSE hold, acknowledged like a letter pause). For
// each, the routine review raises AnswerDialog, the dialog planner picks the
// policy-preferred option ("OK" over "Research screen"), the executor
// activates it through Operations.AnswerDialog, and the clock runs again.
package dialog

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
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

const (
	baselineSave = "RimGovernor-tribal8-baseline"
	fixtureTool  = "test/open_choice_dialog"
	// scheduledDelay is how many ticks after the case's setup the second
	// dialog opens: far enough that the service has answered the first one
	// and admitted a running window, short enough for a Normal-speed run.
	scheduledDelay = 600
	// ceiling bounds each dialog's wait to be answered and the clock to run
	// again; the stall budget ends it earlier when nothing moves.
	ceiling = 8 * time.Minute
)

func init() {
	cases.Register(cases.Case{
		Name: "dialog/pause",
		Scope: "Issue #156: a force-pausing Dialog_NodeTree the game opens by itself is read as the colony " +
			"facts dialog section, answered with the policy-preferred option through Operations.AnswerDialog under the " +
			"AnswerDialog routine goal, its STOP_REASON_DIALOG_PAUSE hold acknowledged like a letter pause, and the " +
			"native clock runs again afterwards; both an already-open dialog at acquire and one opening mid-window.",
		Start: cases.Save{Name: baselineSave},
		// The building families keep supervised windows running so the
		// scheduled dialog opens mid-window.
		Serve: &cases.ServeSpec{
			Families: []string{"dialog", "supply", "shelter", "sleeping"}, NativeTimeout: 15 * time.Second, Prefix: "dialog-pause",
		},
		Budget: 12 * time.Minute,
		// A dialog answer took 68s to show in the trace once (#353).
		Stall: 2 * time.Minute,
		Run:   run,
	})
}

// stagedDialogs records what the fixture opened before the service took
// the game: the immediate dialog's window ID and the scheduled one's due
// tick. Both offer "Research screen" first and "OK" second, so the
// policy-preferred answer is provably not the first option.
type stagedDialogs struct {
	immediateWindow int32
	dueTick         int64
	setupTick       int64
}

func (d *stagedDialogs) open(ctx context.Context, h *na.Harness, names []string, identity map[string]any, report na.Report) error {
	if !na.Contains(names, fixtureTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture DialogFixture", fixtureTool)
	}
	scheduled, err := h.Call(ctx, "schedule-dialog", fixtureTool, map[string]any{
		"action": "open", "title": "Research finished", "text": "Scheduled choice dialog (#156).", "options": "Research screen|OK", "delayTicks": scheduledDelay,
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(scheduled["scheduled"]); !ok {
		return fmt.Errorf("schedule-dialog: fixture did not schedule: %#v", scheduled)
	}
	d.dueTick, d.setupTick = int64(na.AsNumber(scheduled["dueTick"])), int64(na.AsNumber(scheduled["tick"]))
	report["scheduled_dialog"] = scheduled
	immediate, err := h.Call(ctx, "open-dialog", fixtureTool, map[string]any{
		"action": "open", "title": "Research finished", "text": "Immediate choice dialog (#156).", "options": "Research screen|OK", "delayTicks": 0,
	})
	if err != nil {
		return err
	}
	if open, _ := na.AsBool(immediate["windowOpen"]); !open {
		return fmt.Errorf("open-dialog: fixture dialog is not open: %#v", immediate)
	}
	if forced, _ := na.AsBool(immediate["forcePaused"]); !forced {
		return fmt.Errorf("open-dialog: the dialog does not force-pause the game: %#v", immediate)
	}
	d.immediateWindow = int32(na.AsNumber(immediate["windowId"]))
	report["immediate_dialog"] = immediate
	// The typed census must already expose the open dialog exactly as the
	// service's own review will read it.
	reply, err := h.Wire(ctx, "colony-facts-dialog", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	dialog, ok := na.AsMap(observed["dialog"])
	if !ok || int32(na.AsNumber(dialog["windowId"])) != d.immediateWindow {
		return fmt.Errorf("colony-facts-dialog: typed census lacks the open dialog: %#v", observed["dialog"])
	}
	options := na.AsSlice(dialog["options"])
	if len(options) != 2 {
		return fmt.Errorf("colony-facts-dialog: expected two options: %#v", dialog)
	}
	second, _ := na.AsMap(options[1])
	if na.AsString(second["label"]) != "OK" {
		return fmt.Errorf("colony-facts-dialog: unexpected option labels: %#v", dialog)
	}
	if selectable, _ := na.AsBool(second["selectable"]); !selectable {
		return fmt.Errorf("colony-facts-dialog: OK is not selectable: %#v", dialog)
	}
	report["colony_facts_dialog"] = dialog
	return nil
}

// answered is what the durable journal proves for one dialog: the
// AnswerDialog plan whose action targets the window, completed.
type answered struct {
	Goal   domain.GoalID `json:"goal"`
	Epoch  uint64        `json:"epoch"`
	Plan   domain.PlanID `json:"plan"`
	Option string        `json:"option"`
	Index  int32         `json:"index"`
	Stage  string        `json:"stage"`
}

// dialogAnswers lists every AnswerDialog plan the goal has ever admitted,
// across epochs (the goal recovers once a dialog closes and reopens for
// the next one), keyed by the targeted window.
func dialogAnswers(ctx context.Context, st *store.Store) (map[int32]answered, error) {
	review, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	out := map[int32]answered{}
	for _, binding := range review.Goals {
		if binding.Need != policy.AnswerDialog {
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
					answer, ok := action.DialogAnswer()
					if !ok {
						continue
					}
					out[answer.WindowID()] = answered{Goal: binding.Goal, Epoch: epoch, Plan: m.Plan, Option: answer.OptionLabel(), Index: answer.OptionIndex(), Stage: string(plan.Progress[i].View().Stage)}
				}
			}
		}
	}
	return out, nil
}

// clockTrace is what the durable clock inbox shows: every stop reason in
// order, the dialog-pause stops with their window, and the cursor of the
// latest epoch start.
type clockTrace struct {
	Stops        []string `json:"stops"`
	DialogPauses []int32  `json:"dialog_pause_windows"`
	DialogCursor int64    `json:"last_dialog_pause_cursor"`
	StartCursor  int64    `json:"last_start_cursor"`
	Starts       int      `json:"starts"`
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
				t.StartCursor = event.GetCursor()
			}
			if stop := event.GetStopped(); stop != nil {
				t.Stops = append(t.Stops, stop.GetReason().String())
				if stop.GetReason() == k.StopReason_STOP_REASON_DIALOG_PAUSE {
					t.DialogPauses = append(t.DialogPauses, stop.GetPause().GetDialog().GetWindowId())
					t.DialogCursor = event.GetCursor()
				}
			}
		}
	}
	return t, nil
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	// The baseline's naming dialog goes first, so the fixture's dialogs are
	// the only force-pausing windows the service meets.
	if _, err := na.ConfirmColonyNames(ctx, s.Harness(), report); err != nil {
		return err
	}
	staged := &stagedDialogs{}
	if err := staged.open(ctx, s.Harness(), s.Names(), s.Identity(), report); err != nil {
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
	waitFor := func(label string, ready func(map[int32]answered, clockTrace) (bool, error)) (map[int32]answered, clockTrace, error) {
		var answers map[int32]answered
		var trace clockTrace
		err := na.WaitProgress(ctx, na.Wait{Ceiling: ceiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			a, err := dialogAnswers(ctx, st)
			if err != nil {
				return "", false, err
			}
			t, err := traceClock(ctx, st, profile)
			if err != nil {
				return "", false, err
			}
			answers, trace = a, t
			done, err := ready(a, t)
			if err != nil {
				return "", false, err
			}
			return na.Signature(len(a), fmt.Sprint(a), len(t.Stops), t.Starts), done, nil
		})
		report[label+"_answers"] = answers
		report[label+"_clock"] = trace
		if err != nil {
			return answers, trace, fmt.Errorf("%s: %w", label, err)
		}
		return answers, trace, nil
	}

	// Phase 1: the dialog open at acquire is answered with "OK". The clock
	// could not start while it was up (an Unavailable start, retried), so
	// no epoch ever reports that window as its dialog pause.
	_, _, err = waitFor("phase1", func(a map[int32]answered, t clockTrace) (bool, error) {
		first, ok := a[staged.immediateWindow]
		if !ok {
			return false, nil
		}
		if first.Option != "OK" || first.Index != 1 {
			return false, fmt.Errorf("planner chose %q (index %d), not the policy-preferred OK", first.Option, first.Index)
		}
		if domain.Stage(first.Stage) == domain.Unsuccessful || domain.Stage(first.Stage) == domain.Cancelled {
			return false, fmt.Errorf("first dialog answer ended %s", first.Stage)
		}
		return domain.Stage(first.Stage) == domain.Completed, nil
	})
	if err != nil {
		return err
	}

	// Phase 2: the scheduled dialog opens while a window runs, stops the
	// epoch with STOP_REASON_DIALOG_PAUSE naming its window, is answered
	// under the re-raised goal, and a later epoch starts again.
	_, trace2, err := waitFor("phase2", func(a map[int32]answered, t clockTrace) (bool, error) {
		if len(t.DialogPauses) == 0 {
			return false, nil
		}
		window := t.DialogPauses[len(t.DialogPauses)-1]
		second, ok := a[window]
		if !ok {
			return false, nil
		}
		if second.Option != "OK" || second.Index != 1 {
			return false, fmt.Errorf("planner chose %q (index %d) for the paused dialog, not OK", second.Option, second.Index)
		}
		if domain.Stage(second.Stage) == domain.Unsuccessful || domain.Stage(second.Stage) == domain.Cancelled {
			return false, fmt.Errorf("second dialog answer ended %s", second.Stage)
		}
		return domain.Stage(second.Stage) == domain.Completed && t.StartCursor > t.DialogCursor, nil
	})
	if err != nil {
		return err
	}
	pausedWindow := trace2.DialogPauses[len(trace2.DialogPauses)-1]
	for _, window := range trace2.DialogPauses {
		if window == staged.immediateWindow {
			return fmt.Errorf("phase2: a running epoch reported the dialog open at acquire (window %d) as its pause: %+v", window, trace2)
		}
	}
	report["run_keepalive"] = service.Stop()
	if keep, ok := report["run_keepalive"].(map[string]any); ok {
		if acked := int(na.AsNumber(keep["acknowledged"])); acked == 0 {
			return fmt.Errorf("the dialog pause never surfaced as a clock hold for the player to acknowledge: %#v", keep)
		}
	}

	// Independent native read after the service releases the game slot:
	// the fixture's last dialog is closed, OK was the option that ran, no
	// force-pausing window remains and the game ticked past the due tick.
	h, err := s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := h.Call(ctx, "dialog-after", fixtureTool, map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	report["dialog_after"] = after
	if open, _ := na.AsBool(after["windowOpen"]); open {
		return fmt.Errorf("dialog-after: the scheduled dialog is still open: %#v", after)
	}
	if na.AsString(after["chosen"]) != "OK" {
		return fmt.Errorf("dialog-after: native recorded %q as the activated option, not OK: %#v", after["chosen"], after)
	}
	if int32(na.AsNumber(after["windowId"])) != pausedWindow {
		return fmt.Errorf("dialog-after: fixture window %v is not the paused window %d", after["windowId"], pausedWindow)
	}
	if windows := na.AsSlice(after["forcePausingWindows"]); len(windows) != 0 {
		return fmt.Errorf("dialog-after: force-pausing windows remain: %#v", windows)
	}
	if tick := int64(na.AsNumber(after["tick"])); tick <= staged.dueTick {
		return fmt.Errorf("dialog-after: game tick %d never passed the scheduled dialog's due tick %d", tick, staged.dueTick)
	}
	return nil
}
