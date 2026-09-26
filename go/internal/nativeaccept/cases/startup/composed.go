package startup

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startuplabor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// composedBound is the game-tick bound each composed variant must meet
// its outcome inside: one game day from the first review. Provisional
// (#655): eight builders raise eight beds and a ~30-cell ring in well
// under a day at the bunks case's pace; the first nightly records the
// measured ticks in the report (outcome_ticks) to tighten it from.
const composedBound domain.Tick = 60000

func init() {
	for _, c := range []struct{ variant, scope string }{
		{"wood_sufficient", "sufficient wood: the colony ends housed -- a roofed native room holding a bed per colonist -- while an upkeep goal completes work before shelter recovers, and a restart mid-construction keeps one shell and one bed rung with no lost method"},
		{"bed_blocked", "an admitted bed rung held on forbidden wood: a shell action completes while a bed action is still open, and upkeep completes work while shelter is active"},
		{"wood_shortage", "a measured wood shortage: MaintainWood completes acquisition while shelter is active and the shell progresses after it"},
	} {
		cases.Register(composedCase(c.variant, c.scope))
	}
}

func composedName(variant string) string {
	return "startup/composed-" + strings.ReplaceAll(variant, "_", "-")
}

func composedCase(variant, scope string) cases.Case {
	return cases.Case{
		Name: composedName(variant),
		Scope: "Issue #655: the #639 eight-colonist fixture under the production review/planner/admission/Hands path in automatic admission, every routine family on; " +
			scope + ". Idle distinct workers beside runnable ready work never persist past the continuation budget without an admission or an enforced constraint.",
		Start: cases.Fixture{
			Op:   "test/startup_labor_setup",
			Args: map[string]any{"variant": variant},
			On:   cases.Save{Name: sustained.BaselineSave},
		},
		RequiredOps: []string{"test/startup_labor_setup", "test/startup_labor_read"},
		Keep:        []string{string(na.LiveNeeds)},
		Serve:       &cases.ServeSpec{NativeTimeout: 30 * time.Second, Prefix: "composed", Extra: []string{"--routine-project-limit", "auto"}},
		Budget:      25 * time.Minute,
		Reason:      "one unstaged composed window of up to a game day: eight tribal builders raise beds and a ring while upkeep families compete, so no rung can be staged",
		Run:         func(ctx context.Context, s cases.Session) error { return runComposed(ctx, s, variant) },
	}
}

// composed is what the case has seen of the run so far. Ticks are the
// store's own progress ticks; unknown until observed.
type composed struct {
	variant string
	start   domain.Tick
	// shelterFrom is the first review tick the shelter goal held open work.
	shelterFrom domain.Fact[domain.Tick]
	recovery    domain.Fact[domain.Tick]
	enclosure   domain.Fact[domain.Tick]
	// upkeep is the earliest completion per non-shelter need.
	upkeep map[domain.GoalID]domain.Tick
	// shellDone is the earliest completed shell action, wallBesideBed the
	// first poll that saw a completed shell action beside an open bed one.
	shellDone     domain.Fact[domain.Tick]
	lastShellDone domain.Tick
	wallBesideBed domain.Fact[domain.Tick]
	samples       []startuplabor.CapacitySample
	limiting      map[policy.DevelopmentReason]int
}

func runComposed(ctx context.Context, s cases.Session, variant string) error {
	report := s.Report()
	report["variant"], report["bound_ticks"] = variant, composedBound
	prepared, err := validateFixture(s.Prepared())
	if err != nil {
		return err
	}
	report["fixture"] = prepared
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		service.Stop()
		return err
	}
	c := &composed{variant: variant, upkeep: map[domain.GoalID]domain.Tick{}, limiting: map[policy.DevelopmentReason]int{},
		shelterFrom: domain.Unknown[domain.Tick](), recovery: domain.Unknown[domain.Tick](), enclosure: domain.Unknown[domain.Tick](),
		shellDone: domain.Unknown[domain.Tick](), wallBesideBed: domain.Unknown[domain.Tick]()}
	restartDue := variant == "wood_sufficient"
	var lastTick domain.Tick
	wait := func(service *na.ServiceProcess, until func() bool) error {
		w := na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second, Terminal: service.Exited}
		return na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
			tick, err := c.poll(ctx, st)
			if err != nil {
				return "", false, err
			}
			lastTick = tick
			if c.start > 0 && tick-c.start > composedBound {
				return "", false, fmt.Errorf("%s: outcome not met within %d ticks of the first review: %v", variant, composedBound, c.row())
			}
			return na.Signature(tick, len(c.upkeep), c.shellDone, c.recovery), until(), nil
		})
	}
	if restartDue {
		// Restart while shell and bed work and their claims are open.
		if err := wait(service, func() bool { _, ok := c.shellDone.Value(); return ok }); err != nil {
			service.Stop()
			st.Close()
			return err
		}
		before, err := shelterMethods(ctx, st)
		if err != nil {
			service.Stop()
			st.Close()
			return err
		}
		report["before_restart"] = before
		service.Stop()
		st.Close()
		if service, err = restart(ctx, service); err != nil {
			return err
		}
		if st, err = na.OpenStoreWithRetry(ctx, service.StatePath); err != nil {
			service.Stop()
			return err
		}
		if err := checkRestart(ctx, st, before, report); err != nil {
			service.Stop()
			st.Close()
			return err
		}
	}
	err = wait(service, c.met)
	report["outcome_ticks"], report["last_tick"] = c.row(), lastTick
	report["limiting_reasons"] = c.limiting
	if err != nil {
		service.Stop()
		st.Close()
		return err
	}
	st.Close()
	report["keepalive"] = service.Stop()
	report["idle"] = idleAccount(service.FlightPath).Row()
	var stranded []map[string]any
	for _, x := range startuplabor.Strandings(c.samples, 0) {
		stranded = append(stranded, x.Row())
	}
	if len(stranded) > 0 {
		report["stranded"] = stranded
		return fmt.Errorf("unused workers stood beside runnable ready work past the %d-tick continuation budget with no admission or enforced constraint: %v", startuplabor.ContinuationBudget, stranded)
	}
	if variant != "wood_sufficient" {
		return nil
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	return housed(ctx, h, s.Identity(), report)
}

// met is the variant's outcome: native-backed shelter progress and
// upkeep work completed while shelter was still active.
func (c *composed) met() bool {
	if !c.concurrentUpkeep() {
		return false
	}
	switch c.variant {
	case "bed_blocked":
		_, ok := c.wallBesideBed.Value()
		return ok
	case "wood_shortage":
		wood, ok := c.upkeep[policy.MaintainWood]
		from, active := c.shelterFrom.Value()
		return ok && active && wood >= from && c.lastShellDone > wood
	default:
		_, recovered := c.recovery.Value()
		_, enclosed := c.enclosure.Value()
		return recovered && enclosed
	}
}

// concurrentUpkeep is an upkeep completion inside the active shelter
// stretch: after shelter held open work and before it recovered.
func (c *composed) concurrentUpkeep() bool {
	from, ok := c.shelterFrom.Value()
	if !ok {
		return false
	}
	until, recovered := c.recovery.Value()
	for _, tick := range c.upkeep {
		if tick >= from && (!recovered || tick <= until) {
			return true
		}
	}
	return false
}

func (c *composed) row() map[string]any {
	fact := func(f domain.Fact[domain.Tick]) any {
		if v, ok := f.Value(); ok {
			return v - c.start
		}
		return nil
	}
	upkeep := map[string]domain.Tick{}
	for need, tick := range c.upkeep {
		upkeep[string(need)] = tick - c.start
	}
	return map[string]any{"start": c.start, "shelter_active": fact(c.shelterFrom), "first_shell_action": fact(c.shellDone),
		"wall_beside_open_bed": fact(c.wallBesideBed), "enclosure": fact(c.enclosure), "shelter_recovery": fact(c.recovery),
		"upkeep_completed": upkeep, "reviews": len(c.samples)}
}

// poll reads the latest review and folds its evidence into c, returning
// the review tick.
func (c *composed) poll(ctx context.Context, st *store.Store) (domain.Tick, error) {
	review, err := st.LoadRoutineReview(ctx)
	if err != nil || review.Tick == 0 {
		return 0, nil
	}
	if c.start == 0 {
		c.start = review.Tick
	}
	if d := review.Development; d.Auto && (len(c.samples) == 0 || c.samples[len(c.samples)-1].Tick != d.Tick) {
		sample := startuplabor.CapacitySample{Tick: d.Tick, Unused: domain.Unknown[int](), Committed: len(d.Committed), Limiting: d.Limiting}
		if d.Unused != nil {
			sample.Unused = domain.Known(*d.Unused)
		}
		if review.ReadyWork != nil {
			for _, r := range review.ReadyWork.Candidates {
				if r.State == policy.ReadyRunnable {
					sample.Runnable++
				}
			}
		}
		c.samples = append(c.samples, sample)
		c.limiting[d.Limiting]++
	}
	bedOpen, shellDoneNow := false, false
	for _, b := range review.Goals {
		views, err := goalProgress(ctx, st, b.Goal)
		if err != nil {
			return 0, err
		}
		for _, v := range views {
			done := v.view.Stage == domain.Completed
			open := !done && v.view.Stage != domain.Cancelled
			if b.Need != policy.EnsureInitialShelter {
				if done {
					if prior, seen := c.upkeep[b.Need]; !seen || v.view.Tick < prior {
						c.upkeep[b.Need] = v.view.Tick
					}
				}
				continue
			}
			if open {
				if _, ok := c.shelterFrom.Value(); !ok {
					c.shelterFrom = domain.Known(review.Tick)
				}
			}
			switch {
			case v.method == buildingruntime.ShelterBedsMethod():
				bedOpen = bedOpen || open
			case strings.HasPrefix(string(v.view.Plan), "routine-shell-"):
				if done {
					shellDoneNow = true
					if first, ok := c.shellDone.Value(); !ok || v.view.Tick < first {
						c.shellDone = domain.Known(v.view.Tick)
					}
					c.lastShellDone = max(c.lastShellDone, v.view.Tick)
				}
			}
		}
	}
	if _, ok := c.wallBesideBed.Value(); !ok && shellDoneNow && bedOpen {
		c.wallBesideBed = domain.Known(review.Tick)
	}
	if c.recovery, err = shelterRecovery(ctx, st, review, c.recovery); err != nil {
		return 0, err
	}
	if c.enclosure, err = firstEnclosure(ctx, st, c.enclosure); err != nil {
		return 0, err
	}
	return review.Tick, nil
}

type methodView struct {
	method domain.MethodID
	view   domain.ProgressView
}

// goalProgress is every action progress view of the goal's methods.
func goalProgress(ctx context.Context, st *store.Store, goal domain.GoalID) ([]methodView, error) {
	state, err := st.LoadGoal(ctx, goal)
	if errors.Is(err, store.ErrNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var out []methodView
	for _, m := range state.Methods {
		plan, err := st.LoadPlan(ctx, m.Plan)
		if errors.Is(err, store.ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, err
		}
		for _, p := range plan.Progress {
			out = append(out, methodView{method: m.Method, view: p.View()})
		}
	}
	return out, nil
}

// shelterMethods is the shelter goal's method -> plan binding.
func shelterMethods(ctx context.Context, st *store.Store) (map[string]string, error) {
	review, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	out := map[string]string{}
	for _, b := range review.Goals {
		if b.Need != policy.EnsureInitialShelter {
			continue
		}
		state, err := st.LoadGoal(ctx, b.Goal)
		if err != nil {
			return nil, err
		}
		for _, m := range state.Methods {
			out[string(m.Method)] = string(m.Plan)
		}
	}
	return out, nil
}

// restart relaunches the stopped service on the same state, retrying
// while its GABS subprocess releases the slot.
func restart(ctx context.Context, stopped *na.ServiceProcess) (*na.ServiceProcess, error) {
	deadline := time.Now().Add(90 * time.Second)
	for {
		next, err := stopped.Restart(ctx)
		if err == nil {
			return next, nil
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("restarted controller never attached: %w", err)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
}

// checkRestart waits for the restarted controller's first review and
// holds it to the pre-restart ownership: every shelter method keeps its
// plan (no lost site or claim, no duplicate rung under a new plan).
func checkRestart(ctx context.Context, st *store.Store, before map[string]string, report na.Report) error {
	first, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	w := na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second}
	var after map[string]string
	err = na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRoutineReview(ctx)
		if err != nil || review.Revision <= first.Revision {
			return "waiting", false, nil
		}
		after, err = shelterMethods(ctx, st)
		return na.Signature(review.Revision), err == nil, err
	})
	report["after_restart"] = after
	if err != nil {
		return fmt.Errorf("restarted controller never reviewed: %w", err)
	}
	for method, plan := range before {
		if got, ok := after[method]; ok && got != plan {
			return fmt.Errorf("restart rebound shelter method %s from plan %s to %s", method, plan, got)
		}
	}
	return nil
}

// housed reads the native rooms: one proper roofed room holds a bed per
// colonist.
func housed(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) error {
	reply, err := h.Wire(ctx, "rooms-after", "observations_list_rooms", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	best := 0
	for _, raw := range na.AsSlice(observed["rooms"]) {
		row, _ := na.AsMap(raw)
		if proper, _ := na.AsBool(row["properRoom"]); !proper {
			continue
		}
		if outdoors, _ := na.AsBool(row["outdoors"]); outdoors || na.AsNumber(row["openRoofCount"]) != 0 {
			continue
		}
		best = max(best, len(na.AsSlice(row["beds"])))
	}
	report["housed"] = best
	if best < expectedPawns {
		return fmt.Errorf("the best roofed room holds %d beds for %d colonists", best, expectedPawns)
	}
	return nil
}
