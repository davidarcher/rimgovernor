// Package startup is the startup-labor reproduction (#639, epic #638):
// the eight-colonist fixture and the bounded diagnosis that makes startup
// starvation visible. The case is diagnostic infrastructure, not the
// scheduling fix -- it asserts the fixture's preconditions and that the
// diagnosis classified what it saw, and a run that still starves passes
// and says so in its artifact.
package startup

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strconv"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startuplabor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func init() {
	cases.Register(laborCase())
}

const (
	// VariantEnv selects the fixture variant: wood_sufficient (the
	// default), wood_shortage or bed_blocked.
	VariantEnv = "RIMGOVERNOR_ACCEPT_STARTUP_LABOR_VARIANT"
	// TicksEnv overrides the observation window in game ticks.
	TicksEnv = "RIMGOVERNOR_ACCEPT_STARTUP_LABOR_TICKS"
)

// windowTicks is the observed stretch: a third of a game day,
// long enough for several reviews and the first construction rung.
const windowTicks domain.Tick = 20000

// expectedPawns is what the fixture establishes and the case validates.
const expectedPawns = 8

func laborCase() cases.Case {
	return cases.Case{
		Name: "startup/labor",
		Scope: "Issue #639: the eight-colonist startup fixture (no shelter, mixed construction skill, reachable wood, an animal-feed deficit and scattered supplies) " +
			"comes up on its declared preconditions, and the startup-labor diagnosis classifies every reviewed goal from evidence the run already holds.",
		Start: cases.Fixture{
			Op:   "test/startup_labor_setup",
			Args: map[string]any{"variant": variant()},
			On:   cases.Save{Name: sustained.BaselineSave},
		},
		RequiredOps: []string{"test/startup_labor_setup", "test/startup_labor_read"},
		Keep:        []string{string(na.LiveNeeds)},
		Serve:       &cases.ServeSpec{NativeTimeout: 30 * time.Second, Prefix: "startuplabor"},
		Budget:      15 * time.Minute,
		Reason:      "one window of a third of a game day over an eight-colonist fixture",
		Run:         run,
	}
}

func variant() string {
	switch v := os.Getenv(VariantEnv); v {
	case "wood_sufficient", "wood_shortage", "bed_blocked":
		return v
	default:
		return "wood_sufficient"
	}
}

func window() domain.Tick {
	if raw := os.Getenv(TicksEnv); raw != "" {
		if n, err := strconv.ParseInt(raw, 10, 64); err == nil && n > 0 {
			return domain.Tick(n)
		}
	}
	return windowTicks
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	v := variant()
	report["variant"], report["window_ticks"] = v, window()
	prepared, err := validateFixture(s.Prepared())
	if err != nil {
		return err
	}
	report["fixture"] = prepared
	obs, err := observe(ctx, s, v)
	if err != nil {
		return err
	}
	report["observation"] = obs.Row()
	return nil
}

// validateFixture checks the precondition the fixture reports: eight
// colonists with mixed construction skill, reachable wood, no player bed,
// a hungry animal with no feed and scattered supply stacks. A refusal or
// a missing precondition fails the case here, before any play.
func validateFixture(prepared map[string]any) (map[string]any, error) {
	if prepared == nil {
		return nil, errors.New("the startup-labor fixture returned no reply")
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return nil, fmt.Errorf("startup-labor fixture refused: %v", prepared["reason"])
	}
	row := map[string]any{
		"variant": na.AsString(prepared["variant"]), "seed": na.AsString(prepared["seed"]),
		"tick": na.AsNumber(prepared["tick"]), "wood": na.AsNumber(prepared["wood"]),
		"wood_forbidden": prepared["woodForbidden"], "beds": na.AsNumber(prepared["beds"]),
		"feed_stacks": na.AsNumber(prepared["feedStacks"]), "pet_food": na.AsNumber(prepared["petFood"]),
		"scattered": prepared["scattered"], "colonists": prepared["colonists"],
		"wood_reachable_by": na.AsNumber(prepared["woodReachableBy"]),
	}
	colonists := na.AsSlice(prepared["colonists"])
	if len(colonists) != expectedPawns {
		return row, fmt.Errorf("fixture reports %d colonists, want %d", len(colonists), expectedPawns)
	}
	skills := map[float64]bool{}
	for _, raw := range colonists {
		c, _ := na.AsMap(raw)
		if na.AsString(c["id"]) == "" || na.AsString(c["name"]) == "" {
			return row, fmt.Errorf("a fixture colonist has no identity: %v", c)
		}
		skills[na.AsNumber(c["construction"])] = true
	}
	if len(skills) < 2 {
		return row, fmt.Errorf("construction skill is not mixed across the eight colonists: %v", colonists)
	}
	if na.AsNumber(prepared["beds"]) != 0 {
		return row, fmt.Errorf("the fixture left %v player bed(s) standing", prepared["beds"])
	}
	if na.AsNumber(prepared["feedStacks"]) != 0 || na.AsNumber(prepared["petFood"]) <= 0 {
		return row, fmt.Errorf("no animal-feed deficit: %v feed stack(s), pet food %v", prepared["feedStacks"], prepared["petFood"])
	}
	if len(na.AsSlice(prepared["scattered"])) == 0 {
		return row, errors.New("the fixture scattered no supplies")
	}
	if na.AsNumber(prepared["wood"]) <= 0 {
		return row, errors.New("the fixture placed no wood")
	}
	if reach := na.AsNumber(prepared["woodReachableBy"]); reach < expectedPawns {
		return row, fmt.Errorf("the wood pile is reachable by %v of %d colonists", reach, expectedPawns)
	}
	return row, nil
}

// observe plays one window and derives the
// window's diagnosis: the per-review classification, the idle accounting
// off the reviews' own pawn census, and the two shelter milestones.
func observe(ctx context.Context, s cases.Session, variant string) (startuplabor.Observation, error) {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return startuplabor.Observation{}, err
	}
	if _, err = service.Acquire(); err != nil {
		service.Stop()
		return startuplabor.Observation{}, err
	}
	service.KeepAuthority(ctx)
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		service.Stop()
		return startuplabor.Observation{}, err
	}
	defer st.Close()
	obs := startuplabor.Observation{
		Variant: variant, WindowTicks: window(),
		FirstEnclosure: domain.Unknown[domain.Tick](), ShelterRecovery: domain.Unknown[domain.Tick](),
	}
	w := na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second, Terminal: service.Exited}
	var (
		diagnoses []startuplabor.Diagnosis
		seen      = map[string]bool{}
		start     domain.Tick
	)
	err = na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		if review.Tick == 0 {
			return "no-review-yet", false, nil
		}
		if start == 0 {
			start = review.Tick
		}
		rows, err := reviewDiagnoses(ctx, st, review)
		if err != nil {
			return "", false, err
		}
		for _, d := range rows {
			key := fmt.Sprintf("%d/%s/%s", d.ReviewTick, d.Goal, d.Action)
			if seen[key] {
				continue
			}
			seen[key] = true
			diagnoses = append(diagnoses, d)
		}
		if obs.ShelterRecovery, err = shelterRecovery(ctx, st, review, obs.ShelterRecovery); err != nil {
			return "", false, err
		}
		if obs.FirstEnclosure, err = firstEnclosure(ctx, st, obs.FirstEnclosure); err != nil {
			return "", false, err
		}
		done := review.Tick-start >= window()
		return na.Signature(review.Tick, len(diagnoses)), done, nil
	})
	if err != nil {
		// A window that never advances is itself the observation this
		// case exists to record, not a harness failure to hide: the
		// error carries the reviews seen so far.
		service.Stop()
		return obs, fmt.Errorf("observed %d diagnoses before the window closed: %w", len(diagnoses), err)
	}
	service.Stop()
	obs.Blocked = startuplabor.BlockedTicks(diagnoses, window(), 0)
	obs.Idle = idleAccount(service.FlightPath)
	obs.Revision = na.AsString(s.Report()["revision"])
	rows := make([]map[string]any, 0, len(diagnoses))
	for _, d := range diagnoses {
		rows = append(rows, d.Row())
	}
	s.Report()["diagnoses"] = rows
	return obs, nil
}

// reviewDiagnoses builds one subject per ranked goal of the review: the
// ranking row, the goal's open action (when it bound one) and whether
// shelter is waiting on its beds rung.
func reviewDiagnoses(ctx context.Context, st *store.Store, review store.Rounds) ([]startuplabor.Diagnosis, error) {
	world := startuplabor.World{Colony: string(review.Snapshot.Colony), Load: string(review.Snapshot.Load), Map: int(review.Snapshot.Map)}
	var out []startuplabor.Diagnosis
	for _, row := range review.Development.Rows {
		slot := startuplabor.Slot{
			Reason: row.Reason, Bottleneck: row.Bottleneck, Score: row.Score,
			Selected: row.Selected, Committed: row.Committed, Idle: row.Idle,
		}
		subject := startuplabor.Subject{
			World: world, ReviewTick: review.Development.Tick, Goal: row.Goal, Slot: &slot,
			ShelterBeds: domain.Unknown[bool](),
		}
		if methods, ok, err := ownerMethods(ctx, st, review, row.Goal); err != nil {
			return nil, err
		} else if ok {
			method, action, progress, beds, err := openWork(ctx, st, methods)
			if err != nil {
				return nil, err
			}
			subject.Method, subject.Action, subject.Progress = method, action, progress
			if row.Goal == policy.MaintainHousing {
				subject.ShelterBeds = domain.Known(beds)
			}
		}
		out = append(out, startuplabor.Diagnose(subject))
	}
	return out, nil
}

// ownerMethods is the open methods of the goal or Project the review binds to
// need; ok is false when it binds none or the row is gone.
func ownerMethods(ctx context.Context, st *store.Store, review store.Rounds, need domain.ConcernID) ([]domain.Method, bool, error) {
	var owner store.WorkOwner
	var err error
	if id, ok := review.ProjectFor(need); ok {
		owner, err = st.LoadProject(ctx, id)
	} else {
		for _, b := range review.Goals {
			if b.Need == need {
				owner, err = st.LoadStandard(ctx, b.Goal)
			}
		}
	}
	if owner == nil || errors.Is(err, store.ErrNotFound) {
		return nil, false, nil
	}
	if err != nil {
		return nil, false, err
	}
	return owner.OwnerMethods(), true, nil
}

// openWork is the first open action among methods: its method, action id, the
// progress view and whether the open rung is the shelter-beds one.
func openWork(ctx context.Context, st *store.Store, methods []domain.Method) (domain.MethodID, domain.ActionID, *domain.ProgressView, bool, error) {
	for _, m := range methods {
		plan, err := st.LoadPlan(ctx, m.Plan)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				continue
			}
			return "", "", nil, false, err
		}
		for _, p := range plan.Progress {
			v := p.View()
			if v.Stage == domain.Completed || v.Stage == domain.Cancelled {
				continue
			}
			return m.Method, v.Action, &v, m.Method == buildingruntime.ShelterBedsMethod(), nil
		}
	}
	return "", "", nil, false, nil
}

// shelterRecovery is the first tick at which the shelter goal's beds rung
// is completed for every colonist; once known it is never moved.
func shelterRecovery(ctx context.Context, st *store.Store, review store.Rounds, known domain.Fact[domain.Tick]) (domain.Fact[domain.Tick], error) {
	if _, ok := known.Value(); ok {
		return known, nil
	}
	for _, b := range review.Goals {
		if b.Need != policy.MaintainHousing {
			continue
		}
		state, err := st.LoadStandard(ctx, b.Goal)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				return known, nil
			}
			return known, err
		}
		for _, m := range state.Methods {
			if m.Method != buildingruntime.ShelterBedsMethod() {
				continue
			}
			plan, err := st.LoadPlan(ctx, m.Plan)
			if err != nil {
				if errors.Is(err, store.ErrNotFound) {
					continue
				}
				return known, err
			}
			done, last := len(plan.Progress) > 0, domain.Tick(0)
			for _, p := range plan.Progress {
				v := p.View()
				done = done && v.Stage == domain.Completed
				if v.Tick > last {
					last = v.Tick
				}
			}
			if done {
				return domain.Known(last), nil
			}
		}
	}
	return known, nil
}

// firstEnclosure is the first tick a shell plan completed every one of
// its actions: the enclosure the comparison reports.
func firstEnclosure(ctx context.Context, st *store.Store, known domain.Fact[domain.Tick]) (domain.Fact[domain.Tick], error) {
	if _, ok := known.Value(); ok {
		return known, nil
	}
	plans, err := st.LoadPlans(ctx, 256)
	if err != nil {
		return known, err
	}
	for _, plan := range plans {
		if !buildingruntime.IsShellMethod(plan.Method) || len(plan.Progress) == 0 {
			continue
		}
		done, last := true, domain.Tick(0)
		for _, p := range plan.Progress {
			v := p.View()
			done = done && v.Stage == domain.Completed
			if v.Tick > last {
				last = v.Tick
			}
		}
		if done {
			return domain.Known(last), nil
		}
	}
	return known, nil
}

// idleAccount derives the window's idle accounting from the pawn censuses
// the service's own reviews already took, as its flight recorder holds
// them. No sample is taken for the diagnosis itself.
func idleAccount(flightPath string) startuplabor.Account {
	rows, err := na.ReadFlight(flightPath)
	if err != nil {
		return startuplabor.Account{}
	}
	var samples []startuplabor.PawnSample
	for _, row := range rows {
		if row.Kind != "native_response" || na.AsString(row.Payload["tool"]) != "rimgovernor/observations_list_pawns" {
			continue
		}
		if !row.HasTick {
			continue
		}
		reply, ok := bridge.RecordedReply(row.Payload)
		if !ok {
			continue
		}
		samples = append(samples, startuplabor.SamplesFromPawnSnapshot(domain.Tick(row.Tick), reply, false)...)
	}
	return startuplabor.Accumulate(samples, 0)
}
