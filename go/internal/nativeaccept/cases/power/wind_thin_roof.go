package power

// power/wind-thin-roof (#1873, epic #1764): EnsureBasicPower on a lab that
// is solid granite under thin mountain roof except a 30x30 pocket holding
// one unpowered lamp. The layout plan's turbine pair stands on rock beside
// the pocket, so the one plan the family admits digs the rock of the
// turbine's wind path and removes the roof over it in dig waves holding no
// building (#1896); the ordinary plan then places a WindTurbine once the
// site reads clear, and a follow-up connects it. Confirmed natively: the turbine stands on a
// planned site with no roofed or wind-blocking cell on its wind path and is
// on the lamp's network.
//
// Native, not a snapshot test: the dig, remove_roof and over-rock preview
// are native operations and the roof, the wind path and the generation are
// vanilla physics.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func init() {
	cases.Register(cases.Case{
		Name: "power/wind-thin-roof",
		Scope: "Issue #1873: on a lab of granite under thin mountain roof with one unpowered lamp in a pocket, the power family " +
			"digs and unroofs the layout's turbine site and wind path in building-free waves, places a WindTurbine on the planned site, " +
			"and connects it; an independent native read finds no roofed or wind-blocking cell on its wind path and the lamp " +
			"powered while the wind blows. Native: the dig, roof removal and over-rock preview are native operations and the " +
			"roof and wind are vanilla physics, so a snapshot test over recorded facts cannot cover it.",
		Start:   cases.Fixture{Op: "test/power_prepare", Args: map[string]any{"scenario": "mountain"}, On: cases.LabStart()},
		Service: true,
		Budget:  30 * time.Minute,
		Crew:    cases.Crew{Size: 3}, Reason: "three colonists mine, unroof and build a 7x16 turbine path out of solid granite",
		Run: runWindThinRoof,
	})
}

func runWindThinRoof(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	if !na.Contains(s.Names(), "test/power_observe") {
		return fmt.Errorf("missing test/power_observe in discovery; rebuild the native mod with -Fixture PowerFixture")
	}
	if rock := na.AsNumber(prepared["rockCells"]); rock < 5000 {
		return fmt.Errorf("fixture raised only %v rock cells: %#v", rock, prepared)
	}
	var ids, consumers []string
	for _, raw := range na.AsSlice(prepared["consumers"]) {
		consumers = append(consumers, fmt.Sprint(raw))
	}
	ids = append(ids, consumers...)
	observe := func(label string) (map[string]any, error) {
		reply, err := h.Call(ctx, label, "test/power_observe", map[string]any{"ids": strings.Join(ids, ",")})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(reply["success"]); !success {
			return nil, fmt.Errorf("%s: power_observe refused: %#v", label, reply)
		}
		return reply, nil
	}
	before, err := observe("power-before")
	if err != nil {
		return err
	}
	report["power_before"] = before
	rows := indexRows(before)
	if n := len(na.AsSlice(before["generators"])); n != 0 {
		return fmt.Errorf("power-before: the mountain fixture spawned %d generators: %#v", n, before["generators"])
	}
	for _, id := range consumers {
		if on, _ := na.AsBool(rows[id]["powerOn"]); on {
			return fmt.Errorf("power-before: consumer %s powered with no generator: %#v", id, rows[id])
		}
	}

	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []routinefamily.Family{routinefamily.Power, routinefamily.Work}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return err
	}
	report["service_state_attached"] = attached
	rootPlanID, err := service.Resume(prefix, identity, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	review, diagnostics, err := service.WaitRounds(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["rounds_review_first"] = json.RawMessage(reviewData)

	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	concernID, method, err := na.WaitMethod(methodCtx, journal, policy.EnsureBasicPower, nil)
	methodCancel()
	if err != nil {
		return fmt.Errorf("first power method: %w", err)
	}
	report["concern_id"] = string(concernID)
	sites, err := plannedTurbines(ctx, journal)
	if err != nil {
		return err
	}
	report["planned_turbines"] = sites
	// The sky dig and unroof waves come first (#1896), each its own plan
	// holding no building; the turbine is the ordinary plan placed once the
	// site reads clear. Walk the plans until the turbine's settles.
	seen := map[domain.PlanID]bool{}
	waves, renewals, sawDig, sawRoof := 0, 0, false, false
	for turbinePlaced := false; !turbinePlaced; {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		digs, roofs, turbine, err := checkSkyPlan(plan.Spec.Actions())
		if err != nil {
			return fmt.Errorf("power plan %s: %w", method.Plan, err)
		}
		sawDig, sawRoof = sawDig || digs > 0, sawRoof || roofs > 0
		doneCtx, doneCancel := context.WithTimeout(ctx, 20*time.Minute)
		_, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("power plan %s: %w", method.Plan, err)
		}
		seen[method.Plan] = true
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		if incidental {
			renewals++
			_, method, err = na.WaitMethodExcluding(renewCtx, journal, policy.EnsureBasicPower, seen)
			renewCancel()
			if err != nil {
				return fmt.Errorf("renewed power method after incidental cancellation #%d: %w", renewals, err)
			}
			continue
		}
		if turbine {
			turbinePlaced = true
			renewCancel()
			continue
		}
		waves++
		report[fmt.Sprintf("dig_wave_%d", waves)] = map[string]any{"plan": string(method.Plan), "excavations": digs, "roof_cells": roofs}
		_, method, err = na.WaitMethodExcluding(renewCtx, journal, policy.EnsureBasicPower, seen)
		renewCancel()
		if err != nil {
			return fmt.Errorf("power method after dig wave %d: %w", waves, err)
		}
	}
	if !sawDig || !sawRoof {
		return fmt.Errorf("the turbine was placed without digging (%v) and unroofing (%v) its site first", sawDig, sawRoof)
	}
	report["dig_waves"], report["incidental_renewals"], report["turbine_plan"] = waves, renewals, string(method.Plan)
	seen[method.Plan] = true
	if err := followUps(ctx, journal, seen, report); err != nil {
		return err
	}
	if err := na.AssertRoundsRunning(service.Get); err != nil {
		return err
	}

	journal.Close()
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := observe("power-after")
	if err != nil {
		return err
	}
	generators := na.AsSlice(after["generators"])
	for _, raw := range generators {
		if id := fmt.Sprint(raw); !na.Contains(ids, id) {
			ids = append(ids, id)
		}
	}
	if after, err = observe("power-after-built"); err != nil {
		return err
	}
	report["power_after"] = after
	rows = indexRows(after)
	if len(generators) != 1 {
		return fmt.Errorf("power-after: expected exactly the turbine the controller built, observed %d: %#v", len(generators), generators)
	}
	built := rows[fmt.Sprint(generators[0])]
	if def := na.AsString(built["defName"]); def != policy.WindTurbineDefinition {
		return fmt.Errorf("power-after: the controller's generator is a %s, expected %s: %#v", def, policy.WindTurbineDefinition, built)
	}
	if connected, _ := na.AsBool(built["connected"]); !connected {
		return fmt.Errorf("power-after: the turbine is not connected to a power net: %#v", built)
	}
	if !plannedAt(sites, built) {
		return fmt.Errorf("power-after: the turbine at (%v,%v) is on none of the planned sites %v", built["x"], built["z"], sites)
	}
	for _, field := range []string{"windBlockedCells", "windPathRoofed", "windPathBlockers"} {
		if n := na.AsNumber(built[field]); n != 0 {
			return fmt.Errorf("power-after: the turbine's wind path has %s=%v: %#v", field, n, built)
		}
	}
	if roofed, _ := na.AsBool(built["roofed"]); roofed {
		return fmt.Errorf("power-after: the turbine's footprint is still roofed: %#v", built)
	}
	// A turbine powers the lamp only while the wind blows: read again while
	// the game runs a few seconds at a time, and accept a calm read.
	for attempt := 1; attempt <= 12 && !allPowered(rows, consumers); attempt++ {
		label := fmt.Sprintf("after-%d", attempt)
		if _, err := h.Call(ctx, "resume-"+label, "rimgovernor/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Second):
		}
		if _, err := h.Call(ctx, "pause-"+label, "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		if after, err = observe("power-" + label); err != nil {
			return err
		}
		report["power_after"] = after
		rows = indexRows(after)
	}
	built = rows[fmt.Sprint(generators[0])]
	if na.AsNumber(built["powerOutputW"]) == 0 {
		report["wind_calm_at_read"] = true
	} else {
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-after: consumer %s unpowered while the turbine outputs %vW: %#v", id, built["powerOutputW"], rows[id])
			}
		}
	}
	return checkStartupLog(s)
}

// plannedTurbines waits for the layout plan the first review recorded and
// returns its wind turbine sites as "x,z" centres.
func plannedTurbines(ctx context.Context, journal *store.Store) ([]string, error) {
	deadline := time.Now().Add(3 * time.Minute)
	for {
		review, err := journal.LoadRounds(ctx)
		if err != nil {
			return nil, err
		}
		layout, laid, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
		if err != nil {
			return nil, err
		}
		if laid {
			var out []string
			for _, site := range policy.PlannedPowerSites(layout.Plan, policy.WindTurbineDefinition) {
				out = append(out, fmt.Sprintf("%d,%d", site.Cell.X, site.Cell.Z))
			}
			if len(out) == 0 {
				return nil, errors.New("the layout plan reserves no wind turbine site")
			}
			return out, nil
		}
		if time.Now().After(deadline) {
			return nil, errors.New("no layout plan recorded within 3 minutes of the first power method")
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
}

func plannedAt(sites []string, row map[string]any) bool {
	return na.Contains(sites, fmt.Sprintf("%d,%d", int(na.AsNumber(row["x"])), int(na.AsNumber(row["z"]))))
}

// checkSkyPlan classifies a plan of the sky method (#1896): a dig wave of excavations and a
// roof removal, or the one WindTurbine build alone. It returns
// the excavation count and the cells to unroof.
func checkSkyPlan(actions []domain.Action) (excavations, roofCells int, turbine bool, err error) {
	turbines := 0
	for _, a := range actions {
		if _, ok := a.Excavation(); ok {
			excavations++
			continue
		}
		if r, ok := a.RemoveRoof(); ok {
			roofCells += len(r.Cells())
			continue
		}
		b, ok := a.Building()
		if !ok || b.Definition() != policy.WindTurbineDefinition {
			return 0, 0, false, fmt.Errorf("unexpected action in the sky plan: %#v", a)
		}
		turbines++
	}
	if turbines > 1 || turbines == 1 && (excavations > 0 || roofCells > 0) {
		return 0, 0, false, fmt.Errorf("a turbine plan holds %d turbines, %d digs, %d roof cells: the dig and the build are separate plans", turbines, excavations, roofCells)
	}
	if turbines == 0 && excavations == 0 && roofCells == 0 {
		return 0, 0, false, errors.New("an empty plan")
	}
	return excavations, roofCells, turbines == 1, nil
}

// followUpPlanBound bounds the connect follow-ups. The family lays at most 8
// conduit cells per plan (connectLiveBeforeUpgrade), and the lab's lamp lies
// about 42 route cells from the turbine site, so the connection takes six plans
// (#2334: a bound of four stopped the case 5 cells short of the lamp).
const followUpPlanBound = 8

// followUps lets the family connect the new turbine: a bounded number of
// conduit (or bank) plans, none of which may raise another generator.
func followUps(ctx context.Context, journal *store.Store, seen map[domain.PlanID]bool, report na.Report) error {
	for n := 1; n <= followUpPlanBound; n++ {
		waitCtx, waitCancel := context.WithTimeout(ctx, 2*time.Minute)
		_, next, err := na.WaitMethodExcluding(waitCtx, journal, policy.EnsureBasicPower, seen)
		waitCancel()
		if err != nil {
			// A satisfied goal admits nothing more: the bounded wait running
			// out or the journal going quiet ends the follow-ups.
			var stalled *na.WaitError
			if ctx.Err() == nil && (errors.Is(err, context.DeadlineExceeded) || errors.As(err, &stalled)) {
				report["follow_up_wait_ended"] = err.Error()
				return nil
			}
			return fmt.Errorf("follow-up power method #%d: %w", n, err)
		}
		seen[next.Plan] = true
		plan, err := journal.LoadPlan(ctx, next.Plan)
		if err != nil {
			return err
		}
		for _, action := range plan.Spec.Actions() {
			b, ok := action.Building()
			if !ok || b.Definition() != "HiddenConduit" && b.Definition() != policy.BatteryDefinition {
				return fmt.Errorf("power family committed an unexpected action after the turbine: %#v", action)
			}
		}
		doneCtx, doneCancel := context.WithTimeout(ctx, 5*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, next.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("follow-up power plan %s: %w", next.Plan, err)
		}
		report[fmt.Sprintf("follow_up_plan_%d", n)] = map[string]any{
			"plan": string(next.Plan), "actions": len(plan.Spec.Actions()), "incidental": incidental,
			"completed_tick": int64(state.Progress[0].View().Tick),
		}
	}
	return nil
}
