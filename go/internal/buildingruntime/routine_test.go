package buildingruntime

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type routineNative struct {
	reply     *o.ColonyFactsReply
	onRead    func(context.Context)
	reads     int
	planning  bool
	pawnReply *o.ListPawnsReply
}

func (n *routineNative) ReadRoutinePawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	if n.pawnReply != nil {
		return n.pawnReply, bridge.Result{}, ctx.Err()
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(0), Returned: proto.Uint64(0), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) ReadRoutinePopulation(ctx context.Context, _ *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	return bridge.PrisonerCensus{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Prisoners: domain.Known([]policy.PrisonerFacts{})}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}, bridge.Result{}, ctx.Err()
}
func (n *routineNative) ReadColonyFacts(ctx context.Context, _ *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	n.reads++
	n.planning = planning
	if n.onRead != nil {
		n.onRead(ctx)
	}
	if len(definitions) > 0 {
		reply := proto.Clone(n.reply).(*o.ColonyFactsReply)
		p := reply.GetObserved().Planning.GetObserved()
		p.Definitions = nil
		for _, name := range definitions {
			row := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}}
			for _, existing := range n.reply.GetObserved().Planning.GetObserved().Definitions {
				if existing.Definition.GetDefName() == name {
					row = proto.Clone(existing).(*o.PlanningDefinition)
				}
			}
			p.Definitions = append(p.Definitions, row)
		}
		p.Completeness.Matched = proto.Uint64(uint64(len(definitions)))
		p.Completeness.Returned = proto.Uint64(uint64(len(definitions)))
		return reply, bridge.Result{}, nil
	}
	return n.reply, bridge.Result{}, nil // A late transport may ignore cancellation.
}

func TestRoutineReviewerUsesConfiguredFieldReserve(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := routineFixture(t)
	v := n.reply.GetObserved()
	v.Issues = v.Issues[1:] // Complete native farm census replaces its unavailable issue.
	v.Farms = []*o.FarmFacts{{ZoneId: proto.String("farm"), Crop: proto.String("Plant_Rice"), EdibleCrop: proto.Bool(true), GrowingCells: proto.Uint32(73), PlantedCells: proto.Uint32(73), UsableCells: proto.Uint32(73)}}
	d := v.Planning.GetObserved().Definitions[0]
	d.Definition.DefName = proto.String("Plant_Rice")
	d.GrowDays, d.HarvestNutrition, d.NutritionDemandPerDay = proto.Float64(3), proto.Float64(1), proto.Float64(5)
	for _, reserve := range []float64{7, 14, 7} {
		r.policy.FoodTargetDays = reserve
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if !n.planning {
			t.Fatal("routine did not request crop definitions")
		}
		want := domain.NeedUnknown // Stored-food forecast remains unknown.
		if reserve == 14 {
			want = domain.NeedDeficit
		}
		for _, binding := range out.Review.Goals {
			if binding.Need != policy.EnsureFoodSupply {
				continue
			}
			g, err := db.LoadGoal(context.Background(), binding.Goal)
			if err != nil || g.Goal.Need != want {
				t.Fatal("field budget did not reach durable food need", reserve, g, err)
			}
		}
	}
}
func routineFixture(t *testing.T) (*RoutineReviewer, *store.Store, *playerFakeSession, store.ControlRequest, *routineNative) {
	t.Helper()
	p, db, session, _ := playerFixture(t)
	request := playerAcquire(t, p)
	if _, err := p.Acquire(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &routineNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	r, err := NewRoutineReviewer(p, n, testkit.NewManualClock(time.Now()), policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	return r, db, session, request, n
}

func TestRoutineReviewerPersistsNeedsAndManualInvalidatesWithoutRead(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := routineFixture(t)
	got, err := r.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Goals) != 32 || got.Review.Revision != 1 || !got.Review.Enabled {
		t.Fatal(got)
	}
	for _, binding := range got.Review.Goals {
		if binding.Need == domain.GoalID(policy.EnsureFoodSupply) {
			g, err := db.LoadGoal(context.Background(), binding.Goal)
			if err != nil || g.Goal.Need != domain.NeedUnknown {
				t.Fatal("raw food became recovery", g, err)
			}
		}
	}
	request.Kind = store.ManualControl
	request.Plan, request.Revision = "", 0
	request.RequestID = "manual-routine"
	request.World.Load = "stale-browser"
	if _, err = r.player.Manual(context.Background(), request); err == nil {
		t.Fatal("stale browser accepted")
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Enabled || stored.Revision != 2 || n.reads != 1 || session.State().Enabled {
		t.Fatal(stored, err)
	}
	for _, binding := range stored.Goals {
		g, err := db.LoadGoal(context.Background(), binding.Goal)
		if err != nil || g.Goal.Status != domain.GoalInvalidated {
			t.Fatal(g, err)
		}
	}
}

func TestRoutineReviewerRejectsAuthorityChangesDuringRead(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"direction", "disabled", "native", "load"} {
		t.Run(change, func(t *testing.T) {
			r, db, session, _, n := routineFixture(t)
			n.onRead = func(context.Context) {
				session.mu.Lock()
				defer session.mu.Unlock()
				switch change {
				case "direction":
					session.state.Snapshot.Direction++
				case "disabled":
					session.state.Enabled = false
				case "native":
					session.state.Snapshot.Native++
				case "load":
					session.state.Snapshot.Load = "new"
				}
			}
			if _, err := r.Step(context.Background()); err == nil {
				t.Fatal("late facts committed")
			}
			stored, err := db.LoadRoutineReview(context.Background())
			if err != nil || stored.Revision != 0 {
				t.Fatal(stored, err)
			}
		})
	}
}

func TestRoutineReviewerManualCancelsBlockedNativeRead(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := routineFixture(t)
	entered := make(chan struct{})
	n.onRead = func(ctx context.Context) { close(entered); <-ctx.Done() }
	reviewDone := make(chan error, 1)
	go func() { _, err := r.Step(context.Background()); reviewDone <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("read not entered")
	}
	request.Kind, request.RequestID = store.ManualControl, "manual-blocked"
	request.Plan, request.Revision = "", 0
	if _, err := r.player.Manual(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if err := <-reviewDone; err == nil {
		t.Fatal("cancelled review succeeded")
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Revision != 0 || session.State().Enabled {
		t.Fatal(stored, err)
	}
}

func TestRoutineReviewerDisabledStepRetiresReviewWithoutReacquiring(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	if _, err := r.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := session.Disable(); err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(context.Background())
	if err != nil || got.Review.Enabled || got.Review.Revision != 2 {
		t.Fatal(got, err)
	}
	got, err = r.Step(context.Background())
	if err != nil || got.Review.Revision != 2 || n.reads != 1 || session.acquires.Load() != 1 {
		t.Fatal(got, err)
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Enabled {
		t.Fatal(stored, err)
	}
}

type routineMedicalNative struct {
	*routineNative
	stale   bool
	unknown bool
}

func (n *routineMedicalNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, id)
	if n.stale {
		v.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
	}
	pawn := policy.EmergencyPawn{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(true)}
	if n.unknown {
		pawn.NeedsTend = domain.Unknown[bool]()
	}
	v.Facts.Colonists = []policy.EmergencyPawn{pawn}
	return v, receipt, err
}
func TestRoutineReviewerUsesSameTickMedicalCensus(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"needs_tend", "unknown", "stale"} {
		t.Run(kind, func(t *testing.T) {
			r, db, _, _, n := routineFixture(t)
			r.native = &routineMedicalNative{routineNative: n, stale: kind == "stale", unknown: kind == "unknown"}
			got, err := r.Step(context.Background())
			if kind == "stale" {
				if err == nil {
					t.Fatal("mixed ticks committed")
				}
				stored, e := db.LoadRoutineReview(context.Background())
				if e != nil || stored.Revision != 0 {
					t.Fatal(stored, e)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			for _, binding := range got.Review.Goals {
				if binding.Need != domain.GoalID(policy.CriticalMedicine) {
					continue
				}
				g, e := db.LoadGoal(context.Background(), binding.Goal)
				want := domain.NeedDeficit
				if kind == "unknown" {
					want = domain.NeedUnknown
				}
				if e != nil || g.Goal.Need != want {
					t.Fatal(g, e)
				}
				return
			}
			t.Fatal("medical goal missing")
		})
	}
}
