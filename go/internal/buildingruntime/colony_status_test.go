package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// colonyStatusNativeFake mirrors worldEvaluationNativeFake: clockCoreFake's
// Identity plus the colony census and home roster ColonyStatus.Read composes.
type colonyStatusNativeFake struct {
	*clockCoreFake
	colony      *o.ColonyFactsReply
	roster      *o.ListPawnsReply
	colonyErr   error
	rosterErr   error
	identityErr error
}

func (f *colonyStatusNativeFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	if f.identityErr != nil {
		return nil, bridge.Result{}, f.identityErr
	}
	return f.clockCoreFake.Identity(ctx)
}
func (f *colonyStatusNativeFake) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	if f.colonyErr != nil {
		return nil, bridge.Result{}, f.colonyErr
	}
	if planning {
		return nil, bridge.Result{}, errors.New("colony status must not request the planning census")
	}
	return f.colony, bridge.Result{}, ctx.Err()
}
func (f *colonyStatusNativeFake) ReadHomeColonists(ctx context.Context, id *c.Identity) (*o.ListPawnsReply, bridge.Result, error) {
	if f.rosterErr != nil {
		return nil, bridge.Result{}, f.rosterErr
	}
	return f.roster, bridge.Result{}, ctx.Err()
}

func colonyStatusFixture(t *testing.T) (*ColonyStatus, *colonyStatusNativeFake) {
	t.Helper()
	_, db, f, intent := clockCoreFixture(t)
	s, _, _ := newClockSessionTest(t, db, f)
	t.Cleanup(func() { _ = s.Close(context.Background()) })
	p, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, db, s, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = p.Close(context.Background()) })
	if _, err = s.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &colonyStatusNativeFake{clockCoreFake: f}
	ctx := native.status.Context
	native.colony = worldEvaluationColonyFixture(ctx, nil)
	native.roster = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(ctx).(*c.ObservationContext)}}}
	status, err := NewColonyStatus(p, native)
	if err != nil {
		t.Fatal(err)
	}
	return status, native
}

func TestColonyStatusReadProjectsCensusAndRoster(t *testing.T) {
	status, native := colonyStatusFixture(t)
	observed := native.colony.GetObserved()
	observed.ColonistCount, observed.WorkerCount = proto.Uint32(3), proto.Uint32(2)
	observed.FoodNutrition, observed.FoodRunwayDays = proto.Float64(12.5), proto.Float64(2.25)
	observed.FoodCorpses = []*o.FoodCorpse{{}, {}}
	native.roster.GetObserved().Pawns = []*o.PawnState{
		{Pawn: &o.EntityRef{Id: proto.String("p1"), Label: proto.String("Ann")}, Downed: proto.Bool(true), Needs: &o.PawnNeeds{Mood: proto.Float64(0.2), Food: proto.Float64(0.1)}},
		{Pawn: &o.EntityRef{Id: proto.String("p2"), Label: proto.String("Bob")}, Downed: proto.Bool(false)},
		{Pawn: &o.EntityRef{Id: proto.String("p3")}, Dead: proto.Bool(true)},
	}
	report, err := status.Read(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if colonists, _ := report.Colonists.Value(); colonists != 3 {
		t.Fatal(report)
	}
	if runway, known := report.FoodRunwayDays.Value(); !known || runway != 2.25 {
		t.Fatal(report)
	}
	if _, known := report.PendingFoodNutrition.Value(); known {
		t.Fatal("pending nutrition the census omitted must stay unknown")
	}
	if report.FoodCorpses != 2 || len(report.Pawns) != 2 {
		t.Fatal(report)
	}
	if downed, _ := report.Pawns[0].Downed.Value(); !downed || report.Pawns[0].Label != "Ann" {
		t.Fatal(report.Pawns)
	}
	if mood, known := report.Pawns[0].Mood.Value(); !known || mood != 0.2 {
		t.Fatal(report.Pawns)
	}
	if _, known := report.Pawns[1].Mood.Value(); known {
		t.Fatal("mood without a needs block must stay unknown")
	}
}

func TestColonyStatusReadPropagatesNativeErrors(t *testing.T) {
	sentinel := errors.New("native failure")
	t.Run("identity", func(t *testing.T) {
		status, native := colonyStatusFixture(t)
		native.identityErr = sentinel
		if _, err := status.Read(context.Background()); !errors.Is(err, sentinel) {
			t.Fatal(err)
		}
	})
	t.Run("colony", func(t *testing.T) {
		status, native := colonyStatusFixture(t)
		native.colonyErr = sentinel
		if _, err := status.Read(context.Background()); !errors.Is(err, sentinel) {
			t.Fatal(err)
		}
	})
	t.Run("roster", func(t *testing.T) {
		status, native := colonyStatusFixture(t)
		native.rosterErr = sentinel
		if _, err := status.Read(context.Background()); !errors.Is(err, sentinel) {
			t.Fatal(err)
		}
	})
}

// A live window advances the clock between the two reads; the report keeps
// both ticks instead of refusing (the first game run of #261 returned 503
// on every sample for exactly this).
func TestColonyStatusReadTolerantOfTickBetweenReads(t *testing.T) {
	status, native := colonyStatusFixture(t)
	native.roster.GetObserved().Context.Tick = proto.Int64(native.status.Context.GetTick() + 7)
	report, err := status.Read(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if report.RosterTick != report.Tick+7 {
		t.Fatal(report.Tick, report.RosterTick)
	}
}
