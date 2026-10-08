package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	rpb "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type fakeGovernorState struct {
	blobs map[string]string
	reads int
	// batches is every PutGovernorStateBatch argument, in call order.
	batches []map[string]string
}

func (f *fakeGovernorState) GovernorState(context.Context) (map[string]string, error) {
	f.reads++
	return f.blobs, nil
}

func (f *fakeGovernorState) PutGovernorStateBatch(_ context.Context, blobs map[string]string) error {
	f.batches = append(f.batches, blobs)
	f.blobs = blobs
	return nil
}

// A second world in the same process re-reads its save (#994); the same
// world does not.
func TestWorldRebuildRereadsSaveOnWorldChange(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, out: &out}
	native := &fakeGovernorState{blobs: map[string]string{}}
	first := governorWorld{Colony: "a", Map: 1, Load: "load-a", Generation: 1}
	if err = rebuild.ensure(ctx, first, native); err != nil || native.reads != 1 {
		t.Fatal("first world save not read", native.reads, err)
	}
	if err = rebuild.ensure(ctx, first, native); err != nil || native.reads != 1 {
		t.Fatal("same world re-read", native.reads, err)
	}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "b", Map: 1, Load: "load-b", Generation: 1}, native); err != nil || native.reads != 2 {
		t.Fatal("second world save not read", native.reads, err)
	}
}

func governorStandardBlob(t *testing.T, id domain.ConcernID, colony domain.ColonyID, revision uint64) string {
	t.Helper()
	g, err := domain.NewStandard(id, 1, domain.GenerationSnapshot{Colony: colony, Load: "l", Plan: "p"})
	if err != nil {
		t.Fatal(err)
	}
	data, err := json.Marshal(store.GovernorStandardBlob{SchemaVersion: store.GovernorStateSchemaVersion, Standard: g, Revision: revision})
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

// A world change rebuilds the family tables from the save (#1005): the
// store's layout plan goes, and a saved production ladder is not written
// back.
func TestWorldRebuildRebuildsFamiliesOnWorldChange(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	ladder := store.ProductionLadderRecord{World: store.World{Colony: "b", Load: "l", Map: 1}, Tick: 7, Resource: "MeleeWeapon_Gladius"}
	if err = database.SaveProductionLadder(ctx, ladder); err != nil {
		t.Fatal(err)
	}
	blobs, err := database.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	saved := map[string]string{store.GovernorProductionLadderKey: blobs[store.GovernorProductionLadderKey]}
	if err = database.SaveProductionLadder(ctx, store.ProductionLadderRecord{World: store.World{Colony: "a", Load: "l", Map: 1}, Tick: 9, Resource: "Steel"}); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, out: &out}
	native := &fakeGovernorState{blobs: saved}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "b", Map: 1, Load: "l", Generation: 1}, native); err != nil {
		t.Fatal(err)
	}
	if got, ok, err := database.LoadProductionLadder(ctx, ladder.World); err != nil || !ok || got.Resource != ladder.Resource {
		t.Fatal(got, ok, err, out.String())
	}
}

// Loading world 2 replaces world 1's goals with world 2's saved goals
// (#998): the save wins over the store.
func TestWorldRebuildRebuildsGoalsOnWorldChange(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, out: &out}
	first := &fakeGovernorState{blobs: map[string]string{"standard/a": governorStandardBlob(t, "a", "one", 3)}}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "one", Map: 1, Load: "l", Generation: 1}, first); err != nil {
		t.Fatal(err)
	}
	if state, err := database.LoadStandard(ctx, "a"); err != nil || state.Revision != 3 {
		t.Fatal("world 1 goals not rebuilt", state, err)
	}
	second := &fakeGovernorState{blobs: map[string]string{"standard/b": governorStandardBlob(t, "b", "two", 7)}}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "two", Map: 1, Load: "l", Generation: 1}, second); err != nil {
		t.Fatal(err)
	}
	if _, err := database.LoadStandard(ctx, "a"); !errors.Is(err, store.ErrNotFound) {
		t.Fatal("world 1 goal survived the load", err)
	}
	if state, err := database.LoadStandard(ctx, "b"); err != nil || state.Revision != 7 || state.Standard.Snapshot.Colony != "two" || len(state.Methods) != 0 {
		t.Fatal("world 2 goals not rebuilt", state, err)
	}
}

// A save without governor state starts with no goals (D5).
func TestWorldRebuildEmptySaveStartsWithoutGoals(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	g, err := domain.NewStandard("stale", 1, domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"})
	if err != nil {
		t.Fatal(err)
	}
	if err = database.SeedStandard(ctx, g); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, out: &out}
	native := &fakeGovernorState{blobs: map[string]string{}}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "c", Map: 1, Load: "l", Generation: 1}, native); err != nil {
		t.Fatal(err)
	}
	if _, err := database.LoadStandard(ctx, "stale"); !errors.Is(err, store.ErrNotFound) || len(native.blobs) != 0 {
		t.Fatal("empty save kept a goal", err, native.blobs)
	}
}

type fakeOrphanNative struct {
	session bridge.TradeSessionRead
	applied []*o.Action
}

func (f *fakeOrphanNative) ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error) {
	return f.session, bridge.Result{}, nil
}

func (f *fakeOrphanNative) Apply(_ context.Context, _ *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	f.applied = append(f.applied, actions...)
	reply := &o.ApplyReply{}
	for _, a := range actions {
		reply.Results = append(reply.Results, &o.ActionResult{Key: a.Key, Outcome: &o.ActionResult_Applied{Applied: &rpb.Receipt{}}})
	}
	return reply, bridge.Result{}, nil
}

// Loading a world with a committed method cancels the open trade
// session no rebuilt goal owns (#1000, D3); with no session nothing is sent.
func TestWorldRebuildCancelsOrphanTrade(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	snapshot := domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}
	g, err := domain.NewStandard("trade", 1, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if err = database.SeedStandard(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := database.ReviewStandard(ctx, g.ID, 0, snapshot, 10, domain.FindingUnmet)
	if err != nil {
		t.Fatal(err)
	}
	end, err := domain.NewTradeEnd("trader", "negotiator", domain.TradeEndCancel, false)
	if err != nil {
		t.Fatal(err)
	}
	id := domain.MintPlanID()
	action, err := domain.NewTradeAction(domain.ActionID(id+"-0"), end)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = database.CommitMethod(ctx, g.ID, state.Revision, "trade", spec); err != nil {
		t.Fatal(err)
	}
	world := governorWorld{Colony: "c", Map: 1, Load: "l", Generation: 1}
	native := &fakeOrphanNative{session: bridge.TradeSessionRead{Trader: "trader", Negotiator: "negotiator", Open: true}}
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, orphans: native, out: &out}
	if err = rebuild.ensure(ctx, world, &fakeGovernorState{blobs: map[string]string{}}); err != nil {
		t.Fatal(err)
	}
	if len(native.applied) != 1 || native.applied[0].GetTrade().GetEnd().GetKind() != o.EndTradeKind_END_TRADE_KIND_CANCEL {
		t.Fatal("orphan trade not cancelled", native.applied, out.String())
	}
	idle := &fakeOrphanNative{}
	if err = orphanSweep(idle, world, &out)(ctx, nil); err != nil || len(idle.applied) != 0 {
		t.Fatal("no session still cancelled", err, idle.applied)
	}
}

// The clock worker's first step in a world rebuilds before it reviews (#1123),
// and a later step in the same world leaves the review alone; a rebuild after
// the review makes the worker's next step re-review.
func TestWorldRebuildRunsBeforeTheWorkerReview(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var out bytes.Buffer
	rebuild := &worldRebuild{database: database, out: &out}
	native := &fakeGovernorState{blobs: map[string]string{}}
	gate := rebuild.workerGate(native)
	observed := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("l"), MapId: proto.Int32(1)}, NativeGeneration: proto.Uint64(4)}
	if reset, err := gate(ctx, observed); err != nil || !reset {
		t.Fatal("first step did not rebuild", reset, err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "c", Map: 1, Load: "l", Plan: "p"}
	if _, err = database.ReviewRounds(ctx, store.RoundsRequest{Current: snapshot, Tick: 27, Enabled: true, Policy: policy.DefaultRoundsPolicy()}); err != nil {
		t.Fatal(err)
	}
	if reset, err := gate(ctx, observed); err != nil || reset {
		t.Fatal("same world rebuilt again", reset, err)
	}
	if err = rebuild.ensure(ctx, governorWorld{Colony: "other", Map: 1, Load: "l", Generation: 5}, native); err != nil {
		t.Fatal(err)
	}
	if reset, err := gate(ctx, observed); err != nil || !reset {
		t.Fatal("a reset after the review did not force a re-review", reset, err)
	}
}
