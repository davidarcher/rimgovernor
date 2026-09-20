package buildingruntime

import (
	"context"
	"sort"
	"strings"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// haulPairNative is one planner's view of the shared colony-core fixture:
// the generic colony read, one known hauler for the emergency and tend
// pawn reads, and a gate the test closes to hold this planner's tend read
// until its peer has finished, so the wave's completion order is chosen by
// the test rather than by goroutine scheduling.
type haulPairNative struct {
	*routineNative
	gate <-chan struct{}
}

func (n *haulPairNative) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	if n.gate != nil {
		select {
		case <-n.gate:
		case <-ctx.Done():
			return nil, bridge.Result{}, ctx.Err()
		}
	}
	return n.routineNative.ReadRoutinePawns(ctx, identity, ids)
}
func (n *haulPairNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "hauler", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "hauler2", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "hauler3", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
	}
	return v, receipt, err
}
func (n *haulPairNative) PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, bridge.ErrUnavailable
}
func (n *haulPairNative) PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	return bridge.BuildingPreview{}, bridge.Result{}, bridge.ErrUnavailable
}

// haulPairFixture reviews a colony where SecureSupplies (a deteriorating
// stack in the open) and MaintainStorage (an ordinary stack outside
// storage) are both selected and every hauler is eligible, so policy picks
// the same (alphabetically first) pawn for both planners in one step.
func haulPairFixture(t *testing.T) (*RoutineReviewer, *routineNative) {
	t.Helper()
	reviewer, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(3), proto.Uint32(3)
	item := func(id, def string, deterioration float64) *o.UpkeepItem {
		return &o.UpkeepItem{Item: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)}},
			Roofed: proto.Bool(false), InStorage: proto.Bool(false), Forbidden: proto.Bool(false), BaseDeteriorationRate: proto.Float64(deterioration), Medicine: proto.Bool(false), Count: proto.Int64(10)}
	}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{
		Items:        []*o.UpkeepItem{item("supply-1", "MealSimple", 2), item("stack-1", "Steel", 0)},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
		Comfort:      &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}},
	}}}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := func(id string, hauls bool) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false),
			Job:       &o.JobEvidence{DefName: proto.String("Wait"), PlayerForced: proto.Bool(false)},
			Health:    &o.PawnHealth{NeedsTend: proto.Bool(false), Bleeding: proto.Bool(false)},
			Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{},
			Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Hauling"), Priority: proto.Int32(1), Disabled: proto.Bool(!hauls)}}},
			Issues:   []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	rows := []*o.PawnState{row("hauler", true), row("hauler2", true), row("hauler3", true)}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(3), Returned: proto.Uint64(3), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	// The fixture already holds one player Wall plan in flight; two more
	// slots let both haul goals be selected in one review.
	reviewer.policy.MaxDevelopmentProjects = 3
	reviewer.native = &haulPairNative{routineNative: native}
	got, err := reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	selected := map[policy.GoalID]bool{}
	for _, row := range got.Review.Development.Rows {
		selected[row.Goal] = row.Selected
	}
	if !selected[policy.SecureSupplies] || !selected[policy.MaintainStorage] {
		t.Fatal("both haul goals must be selected", got.Review.Development.Rows)
	}
	return reviewer, native
}

// haulPairWave runs the secure-supplies and haul planners as one clock-step
// wave over the fixture, with the named planner's tend read held until its
// peer has returned, and reports the plans the wave admitted.
func haulPairWave(t *testing.T, held string) []domain.PlanID {
	t.Helper()
	reviewer, native := haulPairFixture(t)
	secureGate, haulGate := make(chan struct{}), make(chan struct{})
	// Each planner reads through its own copy of the fake: the shared
	// reply is only read, but the fake counts its reads.
	secureCopy, haulCopy := *native, *native
	secureNative := &haulPairNative{routineNative: &secureCopy}
	haulNative := &haulPairNative{routineNative: &haulCopy}
	var releaseSecure, releaseHaul sync.Once
	switch held {
	case "secureSupplies":
		secureNative.gate = secureGate
		releaseHaul.Do(func() { close(haulGate) })
	case "haul":
		haulNative.gate = haulGate
		releaseSecure.Do(func() { close(secureGate) })
	}
	secure, err := NewRoutineSecureSuppliesPlanner(reviewer, secureNative)
	if err != nil {
		t.Fatal(err)
	}
	haul, err := NewRoutineHaulPlanner(reviewer, haulNative)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	call, epoch, done, err := reviewer.player.enter(ctx, false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	arbiter := newStepArbiter()
	g := newPlannerGroup(call, plannerWidth)
	var secureResult, haulResult ProposalOutcome
	g.Go(plannerFoothold, func() error {
		defer releaseHaul.Do(func() { close(haulGate) })
		result, err := secure.propose(call, epoch)
		if err != nil {
			return err
		}
		arbiter.propose("secureSupplies", result, func(outcome ProposalOutcome) { secureResult = outcome })
		return nil
	})
	g.Go(plannerMaintenance, func() error {
		defer releaseSecure.Do(func() { close(secureGate) })
		result, err := haul.propose(call, epoch)
		if err != nil {
			return err
		}
		arbiter.propose("haul", result, func(outcome ProposalOutcome) { haulResult = outcome })
		return nil
	})
	if err := g.Wait(); err != nil {
		t.Fatal(err)
	}
	if failures := g.Failures(); len(failures) > 0 {
		t.Fatal(failures)
	}
	outcomes, failures := arbiter.coordinate(call, nil)
	if len(failures) > 0 {
		t.Fatal(failures)
	}
	if len(outcomes) != 2 || outcomes[0].Planner != "secureSupplies" || outcomes[1].Planner != "haul" {
		t.Fatalf("both planners must propose, secure supplies ranked first: %+v", outcomes)
	}
	// The loser is reported waiting on the hauler the winner holds, and
	// its planner result carries the same reason: never a failure.
	if !secureResult.Admitted || haulResult.Admitted || haulResult.Reason != BuildingMethodWaiting || !strings.HasPrefix(haulResult.Waiting, "pawn:hauler held by secureSupplies/") {
		t.Fatalf("secure supplies must win the hauler and haul wait on it: %+v / %+v", secureResult, haulResult)
	}
	var admitted []domain.PlanID
	for _, outcome := range outcomes {
		if outcome.Admitted {
			admitted = append(admitted, outcome.Plan)
		}
	}
	sort.Slice(admitted, func(i, j int) bool { return admitted[i] < admitted[j] })
	return admitted
}

// The same eligible proposals over one snapshot admit the same plan set
// whichever planner's native read returns first (#622): the higher
// priority SecureSupplies haul wins the only hauler both ways, and the
// MaintainStorage haul waits.
func TestHaulPairAdmissionIsCompletionOrderIndependent(t *testing.T) {
	t.Parallel()
	secureFirst := haulPairWave(t, "haul")
	haulFirst := haulPairWave(t, "secureSupplies")
	if len(secureFirst) != 1 || len(haulFirst) != 1 || secureFirst[0] != haulFirst[0] {
		t.Fatalf("admitted plans depend on completion order: secure first %v, haul first %v", secureFirst, haulFirst)
	}
}
