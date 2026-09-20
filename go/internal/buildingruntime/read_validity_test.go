package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
)

// settableClock is a wall clock a test moves by hand, so the scheduler
// measures the pace the test dictates.
type settableClock struct{ now time.Time }

func (c *settableClock) Now() time.Time { return c.now }

// At a 150x tick multiplier (9000 ticks/s) a migrated step refuses a stale
// proposal once, with the stale dependency named, admits a proposal the
// window's pace still covers (#345 is not recreated by a fixed tolerance),
// and reads its facts once per step: no re-read loop follows a refusal,
// and a refused proposal is not carried again (#624).
func TestClockSchedulerRefusesStaleProposalOnceAt150x(t *testing.T) {
	// Not parallel: the step keeps the process-wide shim drift in step
	// with its pace, which a parallel test's tick-exact bound would see.
	t.Cleanup(func() { domain.SetLiveDrift(0) })
	s, f := schedulerFixture(t)
	schedulerRoutine(t, s, f)
	clock := &settableClock{now: time.Unix(100, 0)}
	s.clock = clock
	s.catalog = nil
	first, err := s.Step(context.Background())
	if err != nil || first.Attempt == nil || first.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(first, err, f.writes)
	}
	v, ok := s.Validity()
	if !ok || v.Tick != domain.Tick(f.status.Context.GetTick()) || v.Pace != 0 || len(v.Versions) == 0 {
		t.Fatalf("first step fixes a validity at the stopped clock: %+v %v", v, ok)
	}
	// The window runs at 150x: one wall second later the game is 9000
	// ticks on. The step measures that pace from its status read.
	start := f.status.Context.GetTick()
	f.status.Context.Tick = proto.Int64(start + 9000)
	clock.now = clock.now.Add(time.Second)
	current := s.session.State().Snapshot
	committed := map[string]int{}
	late := func(id string, tick domain.Tick, versions map[string]uint64) *Proposal {
		p := &Proposal{ID: id, Planner: "lighting", Goal: domain.GoalID(id), Priority: plannerMaintenance, Snapshot: current, ValidTick: tick, Versions: versions}
		p.commit = func(context.Context) (domain.PlanID, RoutineBuildingReason, error) {
			committed[id]++
			return domain.PlanID("plan-" + id), BuildingMethodAdmitted, nil
		}
		return p
	}
	held := s.facts.store.Versions()
	// The buildings section moved since the version proposal was planned.
	s.facts.store.InvalidateFamily(bridge.FactColony)
	arbiter := newStepArbiter()
	arbiter.late = s.late
	arbiter.close()
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("paced", domain.Tick(start), nil)}, nil)
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("old", domain.Tick(start)-1000, nil)}, nil)
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("version", domain.Tick(start+9000), map[string]uint64{"buildings": held["buildings"]})}, nil)
	bundles := len(f.caches)
	second, err := s.Step(context.Background())
	if err != nil || !second.Running {
		t.Fatal(second, err)
	}
	if v, ok = s.Validity(); !ok || v.Pace != 9000 || v.Tick != domain.Tick(start+9000) || domain.LiveDrift() != 9000 {
		t.Fatalf("the step's validity carries the measured pace and keeps the shim in step: %+v drift %d", v, domain.LiveDrift())
	}
	if len(f.caches)-bundles != 1 {
		t.Fatalf("a step reads its bundle once, refusals or not: %d reads", len(f.caches)-bundles)
	}
	byID := map[string]ProposalOutcome{}
	for _, outcome := range second.Proposals {
		byID[outcome.Proposal] = outcome
	}
	if len(byID) != 3 {
		t.Fatalf("proposals %+v", second.Proposals)
	}
	if o := byID["paced"]; !o.Admitted || committed["paced"] != 1 || o.Stale != "" {
		t.Fatalf("a proposal 9000 ticks old is fresh under the inventory bound at 9000 ticks/s: %+v", o)
	}
	if o := byID["old"]; o.Admitted || committed["old"] != 0 || o.Reason != BuildingMethodExpired || !strings.Contains(o.Stale, "inventory bound 9250") {
		t.Fatalf("a proposal past the bound is refused with the tick named: %+v", o)
	}
	want := fmt.Sprintf("section buildings version %d, step holds %d", held["buildings"], held["buildings"]+1)
	if o := byID["version"]; o.Admitted || committed["version"] != 0 || o.Reason != BuildingMethodExpired || o.Stale != want {
		t.Fatalf("a proposal whose section moved is refused at any age with the section named: %+v (want %q)", o, want)
	}
	// Refused once: the next step carries nothing and commits nothing more.
	f.status.Context.Tick = proto.Int64(start + 18000)
	clock.now = clock.now.Add(time.Second)
	third, err := s.Step(context.Background())
	if err != nil || len(third.Proposals) != 0 || len(committed) != 1 || len(f.caches)-bundles != 2 {
		t.Fatal(third, err, committed, len(f.caches)-bundles)
	}
}

// The Worker carries the scheduler's validity on each dispatch, so a
// dispatch boundary judges its own reads by the dispatch class (one
// dispatch's reads at the window's pace) and the cached emergency census
// by the step's inventory bound, while the same ticks under the shim were
// one bound for both.
func TestReadValidityDispatchClassUnderPace(t *testing.T) {
	t.Parallel()
	v := domain.ReadValidity{Scope: domain.ReadScope{Colony: "c", Map: 1, Load: "l", Native: 1}, Tick: 100000, Pace: 9000, Wall: time.Second}
	ctx := domain.WithReadValidity(context.Background(), v)
	read := domain.Tick(100000)
	// A live re-read 2000 ticks after the inspection (within 250 ms at
	// 9000 ticks/s plus the tolerance) still describes it; 3000 does not.
	if !domain.FreshIn(ctx, domain.AgeDispatch, read+2000, read) || domain.FreshIn(ctx, domain.AgeDispatch, read+3000, read) {
		t.Fatal("dispatch bound is one dispatch's reads at the pace")
	}
	// The step's cached census may predate the inspection by the whole
	// step wall at the pace, but its freshness never certifies the re-read.
	if !domain.CoversIn(ctx, domain.AgeInventory, read-9000, read) || domain.CoversIn(ctx, domain.AgeInventory, read-9300, read) {
		t.Fatal("emergency bound is the step's wall at the pace")
	}
	if domain.FreshIn(ctx, domain.AgeDispatch, read+3000, read) && domain.CoversIn(ctx, domain.AgeInventory, read+3000, read) {
		t.Fatal("a fresh census must not certify a stale re-read")
	}
}
