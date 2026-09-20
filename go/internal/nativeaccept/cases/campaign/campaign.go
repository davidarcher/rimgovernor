// Package campaign is the proof of autonomy (#633, epic #613): an
// unassisted campaign on a declared fixture, seed and mod set, driven
// through the player control path with one dashboard viewer polling video,
// where the harness assists only during setup and every later hand is
// recorded as an intervention. Two campaigns (campaign/foothold,
// campaign/recovery) and six fault injections (campaign/fault-*) share
// one shape: launch rimgovernor serve over the tribal8 baseline, watch a
// tick-measured phase, stop, inject the next disturbance through a fixture
// op, relaunch. Every assertion is native end state or an advancing
// GoalProgress record (#629), never a plan count.
//
// The player control path is plain Ultrafast today; #627's player pacing
// mode replaces it through ControlModeEnv once it lands (see playerControl).
package campaign

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// The declared fixture: the committed eight-colonist tribal baseline
// (sustained.BaselineSave), Core only, the save's own seed. The report's
// manifest row names all three so a failure is reproducible.
const (
	fixtureSave = sustained.BaselineSave
	modSet      = "Core"
)

// ControlModeEnv selects the player control path: "ultrafast" (the
// default: --clock-speed Ultrafast with no test acceleration, the fastest
// speed a player can select) or, once #627 lands, "player" for its paced
// mode. An unknown value fails the case before the game is opened.
const ControlModeEnv = "RIMGOVERNOR_ACCEPT_CAMPAIGN_CONTROL"

// playerControl resolves ControlModeEnv to serve arguments.
func playerControl() (mode string, args []string, err error) {
	mode = os.Getenv(ControlModeEnv)
	if mode == "" {
		mode = "ultrafast"
	}
	switch mode {
	case "ultrafast":
		// Plain Ultrafast: no --clock-test-acceleration (the native dev
		// tick boost is not a speed a player can select).
		return mode, []string{"--clock-speed", "Ultrafast"}, nil
	case "player":
		// #627's player pacing mode: frame-time pacing, controller
		// backoff, visible effective speed. Not landed; the hook stays so
		// the family switches over by environment alone.
		return mode, nil, fmt.Errorf("%s=player: #627's player pacing mode is not landed yet", ControlModeEnv)
	}
	return mode, nil, fmt.Errorf("%s=%q: one of ultrafast, player", ControlModeEnv, mode)
}

// WindowTicksEnv overrides the foothold window (footholdTicks) in game
// ticks; the shorter recovery and fault windows scale with it.
const WindowTicksEnv = "RIMGOVERNOR_ACCEPT_CAMPAIGN_TICKS"

// footholdTicks is the foothold campaign's window: three game days, the
// stretch on which the tribal8 baseline's shelter and food goals settle
// under every family (sustained/colony samples five).
const footholdTicks uint64 = 3 * 60000

func footholdWindow() uint64 {
	if raw := os.Getenv(WindowTicksEnv); raw != "" {
		if n, err := strconv.ParseUint(raw, 10, 64); err == nil && n > 0 {
			return n
		}
	}
	return footholdTicks
}

// dayTicks is one game day.
const dayTicks uint64 = 60000

// serveSpec is the campaign's serve process: every routine family (nil
// Families; the supply family must be in, or ManageSupplySafety parks
// every goal after a raid, #620), the player control path's clock flags
// and the campaign's own request prefix.
func serveSpec(prefix string) (cases.ServeSpec, error) {
	_, args, err := playerControl()
	if err != nil {
		return cases.ServeSpec{}, err
	}
	return cases.ServeSpec{NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: prefix, Extra: args}, nil
}

// campaignCase is the shape every case in the family shares: the baseline
// save with live needs, quiet storyteller (the disturbances are staged, not
// drawn), a rendered profile so the viewer captures frames, and a serve
// process on the player control path.
func campaignCase(name, scope, reason string, budget time.Duration, run func(context.Context, cases.Session) error) cases.Case {
	spec, _ := serveSpec("campaign")
	return cases.Case{
		Name:        name,
		Scope:       scope,
		Start:       cases.Save{Name: fixtureSave},
		Keep:        []string{string(na.LiveNeeds)},
		Quiet:       na.QuietRequired,
		Serve:       &spec,
		Rendered:    true,
		RequiredOps: []string{"test/defense_setup", "test/hut_shell_fixture"},
		Reason:      reason,
		Budget:      budget,
		Run:         run,
	}
}

// campaign is one run's bookkeeping: the phases played, the milestones
// reached (name and tick), the harness's interventions (every fixture op
// or authority hand after setup) and the injections (the disturbances the
// scenario stages, which are the campaign's subject, not assistance).
type campaign struct {
	s             cases.Session
	report        na.Report
	setup         bool
	phases        []map[string]any
	milestones    []map[string]any
	interventions []map[string]any
	injections    []map[string]any
	launches      int
}

func newCampaign(s cases.Session) (*campaign, error) {
	mode, _, err := playerControl()
	if err != nil {
		return nil, err
	}
	c := &campaign{s: s, report: s.Report()}
	c.report["manifest"] = map[string]any{"fixture": fixtureSave, "mods": []string{modSet}, "seed": na.AsString(s.Identity()["seed"]), "identity": s.Identity(), "control": mode, "viewer": "one dashboard video tile (na.ViewerClient)"}
	c.report["interventions"] = c.interventions
	c.report["injections"] = c.injections
	c.report["milestones"] = c.milestones
	c.report["phases"] = c.phases
	c.report["intervention_count"] = 0
	for _, op := range []string{"test/defense_setup", "test/hut_shell_fixture"} {
		if !na.Contains(s.Names(), op) {
			return nil, fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture DefenseFixture,HutShellFixture", op)
		}
	}
	return c, nil
}

// setupDone marks the end of setup: everything the harness does to the
// colony from here is an intervention.
func (c *campaign) setupDone() { c.setup = true }

// intervene records a harness hand on the colony under label; after setup
// it counts against the campaign.
func (c *campaign) intervene(label string, detail map[string]any) {
	c.record(label, c.setup, detail)
}

// record files an intervention row, counted when afterSetup.
func (c *campaign) record(label string, afterSetup bool, detail map[string]any) {
	row := map[string]any{"label": label, "after_setup": afterSetup, "at": time.Now().UTC().Format(time.RFC3339)}
	for k, v := range detail {
		row[k] = v
	}
	c.interventions = append(c.interventions, row)
	c.report["interventions"] = c.interventions
	c.report["intervention_count"] = c.interventionCount()
}

// interventionCount is the number of interventions after setup.
func (c *campaign) interventionCount() int {
	n := 0
	for _, row := range c.interventions {
		if after, _ := row["after_setup"].(bool); after {
			n++
		}
	}
	return n
}

// inject records a staged disturbance (the scenario's own subject).
func (c *campaign) inject(label string, detail map[string]any) {
	row := map[string]any{"label": label, "at": time.Now().UTC().Format(time.RFC3339)}
	for k, v := range detail {
		row[k] = v
	}
	c.injections = append(c.injections, row)
	c.report["injections"] = c.injections
}

// milestone records a named campaign milestone at a game tick.
func (c *campaign) milestone(name string, tick uint64) {
	c.milestones = append(c.milestones, map[string]any{"name": name, "tick": tick})
	c.report["milestones"] = c.milestones
	fmt.Printf("MILESTONE %s tick=%d\n", name, tick)
}

// phase is one watched stretch of play under a launched service.
type phase struct {
	Label    string
	Timeline []map[string]any
	Window   *sustainedfood.TickWindow
	// Progress is every goal's progress record when the phase ended.
	Progress []progressRecord
	// Mode is /api/state's mode when the phase ended.
	Mode string
	// Viewer is the viewer's summary, nil when the phase ran none.
	Viewer map[string]any
	// Flight is the service's flight recording; Stderr its log's path.
	Flight []na.FlightRow
	Stderr string
	// Keep is the keep-alive's counters (report authority_reacquisitions).
	Keep map[string]any
	// Err is the watch's own error (a fail-fast verdict, a stall).
	Err error
}

// lastTick is the last sampled game tick.
func (p *phase) lastTick() uint64 {
	for i := len(p.Timeline) - 1; i >= 0; i-- {
		if tick, ok := p.Timeline[i]["tick"].(uint64); ok {
			return tick
		}
	}
	return 0
}

// advanced is how far the game tick moved during the phase.
func (p *phase) advanced() uint64 {
	if p.Window == nil || !p.Window.Sampled {
		return 0
	}
	return p.Window.LastTick - p.Window.FirstTick
}

// playOptions shape one phase: the watch (window, goal, early exit), the
// viewer, edits to the serve spec (a fault injection's environment, chat
// flags) and hooks that run against the live service before the watch
// ends (probe) and after it (audit, service still running).
type playOptions struct {
	Watch  sustainedfood.WatchConfig
	Viewer *na.ViewerOptions
	Spec   func(*na.ServeSpec)
	// During runs beside the watch with the live service, for a case that
	// acts on the service mid-phase (a viewer that disconnects, a chat
	// request against a dead endpoint); it returns what it observed.
	During func(ctx context.Context, service *na.ServiceProcess, viewer *na.ViewerClient) (map[string]any, error)
	// FailFast is on by default; a phase that expects the service to stop
	// (an injected authority loss) disables it.
	NoFailFast bool
}

// play launches the service, starts the viewer, watches the phase, then
// stops both and reattaches the harness. Every launch after the first is
// the scenario's restart around an injection, not assistance; the
// authority hand at launch is recorded either way.
func (c *campaign) play(ctx context.Context, label string, opts playOptions) (*phase, error) {
	c.launches++
	spec, err := serveSpec("campaign-" + label)
	if err != nil {
		return nil, err
	}
	if opts.Spec != nil {
		opts.Spec(&spec)
	}
	service, err := c.s.Serve(ctx, spec)
	if err != nil {
		return nil, fmt.Errorf("%s: serve: %w", label, err)
	}
	p := &phase{Label: label}
	var viewer *na.ViewerClient
	viewerOpts := na.ViewerOptions{ID: "campaign-viewer"}
	if opts.Viewer != nil {
		viewerOpts = *opts.Viewer
	}
	viewer = na.StartViewerWith(ctx, service, service.Token, viewerOpts)
	stop := func() {
		if viewer != nil {
			p.Viewer = viewer.Stop()
			viewer = nil
		}
		if keep := service.Stop(); keep != nil {
			p.Keep = keep
			c.report["authority_reacquisitions"] = keep
		}
	}
	defer stop()
	// The authority grant at launch establishes control (setup, or the
	// scenario's restart around an injection); the keep-alive's
	// re-acquisitions during the phase are hands on the colony.
	c.record("acquire-"+label, false, map[string]any{"kind": "authority", "launch": c.launches, "restart": c.launches > 1})
	cfg := opts.Watch
	if opts.NoFailFast {
		cfg.FailFast = sustainedfood.FailFast{Disabled: true}
	}
	if cfg.Goal == "" {
		cfg.Goal = policy.EnsureFoodSupply
	}
	if cfg.Poll <= 0 {
		cfg.Poll = 10 * time.Second
	}
	var during map[string]any
	var duringErr error
	done := make(chan struct{})
	if opts.During != nil {
		go func() {
			defer close(done)
			during, duringErr = opts.During(ctx, service, viewer)
		}()
	} else {
		close(done)
	}
	report := na.Report{}
	p.Timeline, p.Err = sustainedfood.Watch(ctx, c.s.Config(), service, cfg, report)
	<-done
	p.Window, _ = report["window"].(*sustainedfood.TickWindow)
	if service.Exited() == nil {
		p.Progress, _ = readProgress(service)
		if st, _, err := service.API("GET", "/api/state", nil, ""); err == nil {
			p.Mode = na.AsString(st["mode"])
		}
	}
	stop()
	if reacquired := int(na.AsNumber(p.Keep["reacquired"])); reacquired > 0 {
		c.intervene("keepalive-"+label, map[string]any{"kind": "authority_reacquired", "count": reacquired})
	}
	p.Flight, _ = na.ReadFlight(service.FlightPath)
	p.Stderr = service.StderrPath()
	row := map[string]any{"label": label, "launch": c.launches, "samples": len(p.Timeline), "window": p.Window, "mode": p.Mode, "viewer": p.Viewer,
		"metrics": sustainedfood.DeriveMetrics(p.Timeline), "colony": sustainedfood.DeriveColonyOutcome(p.Timeline), "progress": p.Progress, "keepalive": p.Keep,
		"clock": service.Entry()["clock"], "during": during, "events": report["events"]}
	if p.Err != nil {
		row["error"] = p.Err.Error()
	}
	if duringErr != nil {
		row["during_error"] = duringErr.Error()
	}
	c.phases = append(c.phases, row)
	c.report["phases"] = c.phases
	c.report["timeline"] = p.Timeline
	if _, err := c.s.Reattach(ctx); err != nil {
		return p, fmt.Errorf("%s: reattach: %w", label, err)
	}
	if duringErr != nil {
		return p, fmt.Errorf("%s: %w", label, duringErr)
	}
	return p, nil
}

// progressRecord is one goal's GoalProgress record as /api/routines
// renders it (#629).
type progressRecord struct {
	Goal         string `json:"goal"`
	Method       string `json:"method"`
	Expected     string `json:"expected"`
	LastProgress uint64 `json:"lastProgress"`
	NextReview   uint64 `json:"nextReview"`
	Blocked      string `json:"blocked"`
}

// readProgress reads the live service's goal progress records.
func readProgress(service *na.ServiceProcess) ([]progressRecord, error) {
	body, status, err := service.API("GET", "/api/routines", nil, "")
	if err != nil {
		return nil, err
	}
	if status != 200 {
		return nil, fmt.Errorf("/api/routines status %d", status)
	}
	raw, err := json.Marshal(body["progress"])
	if err != nil {
		return nil, err
	}
	var records []progressRecord
	if err := json.Unmarshal(raw, &records); err != nil {
		return nil, err
	}
	return records, nil
}

// assertProgress is the sustained-progress gate (#629): at least one goal
// record advanced on native evidence during the phase (LastProgress at or
// past its first tick), and no unblocked record sat past its NextReview
// deadline without progress or a named blocker (the contract rotates or
// cools such a method down, so a record left there is a stall the policy
// missed).
func (p *phase) assertProgress() error {
	if p.Window == nil || !p.Window.Sampled {
		return fmt.Errorf("%s: no sampled tick, progress cannot be judged", p.Label)
	}
	advanced, stalled := 0, []string{}
	for _, r := range p.Progress {
		if r.LastProgress >= p.Window.FirstTick {
			advanced++
		} else if r.Blocked == "" && r.NextReview > 0 && r.NextReview < p.Window.LastTick {
			stalled = append(stalled, fmt.Sprintf("%s/%s last=%d next=%d", r.Goal, r.Method, r.LastProgress, r.NextReview))
		}
	}
	if advanced == 0 {
		return fmt.Errorf("%s: no goal progress record advanced between ticks %d and %d (%d records)", p.Label, p.Window.FirstTick, p.Window.LastTick, len(p.Progress))
	}
	if len(stalled) > 0 {
		return fmt.Errorf("%s: %d goal(s) past their progress deadline with no blocker: %v", p.Label, len(stalled), stalled)
	}
	return nil
}

// assertPlaying is the "must keep playing" gate: the game tick advanced
// through the phase's window, the service stayed in automate and the
// watch raised no stall or fail-fast verdict.
func (p *phase) assertPlaying(window uint64) error {
	if p.Err != nil {
		return fmt.Errorf("%s: %w", p.Label, p.Err)
	}
	if p.Window == nil || !p.Window.Reached {
		return fmt.Errorf("%s: window of %d ticks not reached (advanced %d)", p.Label, window, p.advanced())
	}
	if p.Mode != "automate" {
		return fmt.Errorf("%s: service ended the phase in mode %q, want automate", p.Label, p.Mode)
	}
	return nil
}

// stoppedTicks bounds how far the tick may still move once play must
// stop: the window native lets run out after the last admitted one, plus
// the blind ticks a stop takes to land.
const stoppedTicks = dayTicks + 3000

// assertStopped is the "must stop" gate: the phase's window was not
// reached and the tick moved at most one running window past the fault.
func (p *phase) assertStopped(window uint64) error {
	if p.Window != nil && p.Window.Reached {
		return fmt.Errorf("%s: play continued through the whole %d-tick window (advanced %d)", p.Label, window, p.advanced())
	}
	if moved := p.advanced(); moved > stoppedTicks {
		return fmt.Errorf("%s: tick advanced %d past the fault, more than one window (%d)", p.Label, moved, stoppedTicks)
	}
	return nil
}

// colonyFacts reads the controller's decoded colony facts natively through
// the reattached harness: the food forecast, wood stock and sleeping
// capacity the end-state audits judge.
func colonyFacts(ctx context.Context, h *na.Harness, s cases.Session, label string) (*observation.ColonyProjection, error) {
	facts, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return nil, err
	}
	raw, err := json.Marshal(facts)
	if err != nil {
		return nil, err
	}
	reply := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(raw, reply); err != nil {
		return nil, err
	}
	v := reply.GetObserved()
	if v == nil || v.Context == nil || v.Context.Identity == nil {
		return nil, fmt.Errorf("%s: colony facts unavailable", label)
	}
	id := v.Context.Identity
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: domain.ColonyID(id.GetColonyId()), Map: domain.MapID(id.GetMapId()), Load: domain.LoadID(id.GetLoadToken()), Tick: domain.Tick(v.Context.GetTick())})
	if err != nil {
		return nil, err
	}
	return &projection, nil
}

// auditColony is the native end state every campaign phase must leave:
// no colonist lost, none past Malnutrition 0.3 (sustained.AuditNutrition),
// indoor sleeping capacity for every colonist (shelter) and a known food
// runway. It records the facts it read on report[label].
func auditColony(ctx context.Context, h *na.Harness, s cases.Session, report na.Report, label string, initial []string) error {
	var failures []error
	failures = append(failures, sustained.AuditNutrition(ctx, h, report))
	listed, err := h.Call(ctx, label+"-pawns", "home/list_pawns", map[string]any{"colonistsOnly": true, "includeDead": true})
	if err != nil {
		return errors.Join(append(failures, err)...)
	}
	alive := map[string]bool{}
	for _, raw := range na.AsSlice(listed["pawns"]) {
		row, _ := na.AsMap(raw)
		dead, known := na.AsBool(row["dead"])
		alive[na.AsString(row["thingId"])] = known && !dead
	}
	for _, id := range initial {
		if !alive[id] {
			failures = append(failures, fmt.Errorf("initial colonist %s dead or gone", id))
		}
	}
	projection, err := colonyFacts(ctx, h, s, label+"-facts")
	if err != nil {
		return errors.Join(append(failures, err)...)
	}
	colonists, ck := projection.Facts.Colonists.Value()
	indoor, ik := projection.Facts.IndoorCapacity.Value()
	days, dk := projection.Facts.FoodDays.Value()
	wood, wk := projection.Facts.Wood.Value()
	report[label] = map[string]any{"colonists": colonists, "indoor_capacity": indoor, "food_days": days, "food_days_known": dk, "wood": wood, "wood_known": wk, "alive": len(alive)}
	if !ck || !ik || indoor < colonists {
		failures = append(failures, fmt.Errorf("shelter: indoor sleeping capacity %d (known %v) for %d colonists (known %v)", indoor, ik, colonists, ck))
	}
	if !dk || days <= 0 {
		failures = append(failures, fmt.Errorf("food: runway %v days (known %v)", days, dk))
	}
	return errors.Join(failures...)
}

// initialColonists lists the colonists at setup, the roster every audit
// requires intact.
func initialColonists(ctx context.Context, h *na.Harness, report na.Report) ([]string, error) {
	listed, err := h.Call(ctx, "initial-colonists", "home/list_pawns", map[string]any{"colonistsOnly": true})
	if err != nil {
		return nil, err
	}
	var ids []string
	for _, raw := range na.AsSlice(listed["pawns"]) {
		row, _ := na.AsMap(raw)
		if id := na.AsString(row["thingId"]); id != "" {
			ids = append(ids, id)
		}
	}
	if len(ids) == 0 {
		return nil, fmt.Errorf("no initial colonists observed")
	}
	report["initial_colonists"] = ids
	return ids, nil
}

// fixture calls a test op on the paused game through the reattached
// harness, refusing a reply without success.
func (c *campaign) fixture(ctx context.Context, label, op string, args map[string]any) (map[string]any, error) {
	h := c.s.Harness()
	if _, err := h.Call(ctx, label+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	out, err := h.Call(ctx, label, op, args)
	if err != nil {
		return nil, err
	}
	if success, ok := na.AsBool(out["success"]); ok && !success {
		return nil, fmt.Errorf("%s %v refused: %#v", op, args, out)
	}
	return out, nil
}

// flightHeldBy lists the planners the clock_step rows named under held_by.
func flightHeldBy(rows []na.FlightRow) map[string]int {
	held := map[string]int{}
	for _, row := range rows {
		if row.Kind != "clock_step" {
			continue
		}
		for _, name := range na.AsSlice(row.Payload["held_by"]) {
			held[na.AsString(name)]++
		}
	}
	return held
}

// stepsAdmitted counts the clock_step rows that admitted a window (the
// rows carrying the window's ticks).
func stepsAdmitted(rows []na.FlightRow) int {
	n := 0
	for _, row := range rows {
		if row.Kind == "clock_step" && na.AsNumber(row.Payload["window_ticks"]) > 0 {
			n++
		}
	}
	return n
}

// logLines counts the service log's lines containing needle.
func logLines(path, needle string) int {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	return strings.Count(string(data), needle)
}
