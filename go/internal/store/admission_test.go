package store

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func evidence(tick domain.Tick, count int64) Admission {
	return Admission{Snapshot: scope(), Tick: tick, Costs: []MaterialCost{{Definition: "Steel", Count: count}}, Footprint: []domain.Cell{{X: 3, Z: 7}, {X: 4, Z: 7}}}
}
func TestAdmissionReopenAndDefensiveRecords(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	for _, id := range []domain.ActionID{"a", "b", "c"} {
		if _, err := s.ReserveAndPrepare(ctx, "p", id, evidence(10, 40)); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := s.Dispatch(ctx, "p", "b", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "p", "c", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "p", domain.Observation{Action: "c", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted}, scope()); err != nil {
		t.Fatal(err)
	}
	before, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if len(before.Admissions) != 3 {
		t.Fatal("missing admission records")
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	after, err := s.LoadPlan(ctx, "p")
	if err != nil || !reflect.DeepEqual(before, after) {
		t.Fatal("reopen lost accounting", err)
	}
	after.Admissions[0].Admission.Costs[0].Count = 999
	after.Admissions[0].Admission.Footprint[0].X = 999
	again, err := s.LoadPlan(ctx, "p")
	if err != nil || !reflect.DeepEqual(before, again) {
		t.Fatal("returned records mutated persistence", err)
	}
	for _, id := range []domain.ActionID{"b", "c"} {
		if _, err = s.ReserveAndPrepare(ctx, "p", id, evidence(12, 1)); err == nil {
			t.Fatal("dispatched or completed accounting replaced")
		}
	}
}
func TestAdmissionAtomicRollbackAndPreparedReplacement(t *testing.T) {
	ctx := context.Background()
	s, _ := fixture(t)
	if _, err := s.db.Exec(`CREATE TRIGGER fail_prepare BEFORE INSERT ON transitions BEGIN SELECT RAISE(ABORT,'prepare failed'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(10, 40)); err == nil {
		t.Fatal("injected failure accepted")
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil || len(state.Admissions) != 0 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal("failed preparation left reservation", err)
	}
	if _, err = s.db.Exec("DROP TRIGGER fail_prepare"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReserveAndPrepare(ctx, "p", "a", evidence(10, 40)); err != nil {
		t.Fatal(err)
	}
	updated, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(20, 60))
	if err != nil {
		t.Fatal(err)
	}
	if updated.View().Tick != 10 {
		t.Fatal("revalidation changed preparation history")
	}
	state, err = s.LoadPlan(ctx, "p")
	if err != nil || state.Admissions[0].Admission.Tick != 20 || state.Admissions[0].Admission.Costs[0].Count != 60 {
		t.Fatal("latest validation not persisted", err)
	}
	if _, err = s.Dispatch(ctx, "p", "a", scope(), 19); err == nil {
		t.Fatal("dispatch used stale admission tick")
	}
	if _, err = s.ReserveAndPrepare(ctx, "p", "a", evidence(19, 30)); err == nil {
		t.Fatal("admission tick moved backwards")
	}
	changed := evidence(20, 60)
	changed.Snapshot.Direction++
	if _, err = s.ReserveAndPrepare(ctx, "p", "a", changed); err == nil {
		t.Fatal("revalidation changed authority")
	}
	if _, err = s.db.Exec(`CREATE TRIGGER fail_replace BEFORE UPDATE ON admissions BEGIN SELECT RAISE(ABORT,'replacement failed'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReserveAndPrepare(ctx, "p", "a", evidence(21, 80)); err == nil {
		t.Fatal("replacement failure accepted")
	}
	again, err := s.LoadPlan(ctx, "p")
	if err != nil || !reflect.DeepEqual(state, again) {
		t.Fatal("failed replacement changed old evidence", err)
	}
}
func TestAdmissionCannotEraseUnknownAndCanReplaceAfterAbsence(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(10, 40)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "p", "a", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(11, 0)); err == nil {
		t.Fatal("unknown write released costs")
	}
	if _, err := s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectAbsent}, scope()); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Prepare(ctx, "p", "a", scope(), 12); err == nil {
		t.Fatal("plain Prepare bypassed durable accounting")
	}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(12, 50)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 12); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "p", "a"); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(13, 0)); err == nil {
		t.Fatal("cancelled uncertain costs replaced")
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "p")
	if err != nil || state.Progress[0].View().Attempt != 2 || !state.Progress[0].View().Unresolved || state.Admissions[0].Admission.Costs[0].Count != 50 {
		t.Fatal("restart lost retry/cancel accounting", err)
	}
}
func TestAdmissionValidationAndCorruption(t *testing.T) {
	ctx := context.Background()
	for _, change := range []func(*Admission){func(a *Admission) { a.Costs = nil }, func(a *Admission) { a.Costs[0].Count = -1 }, func(a *Admission) { a.Costs = append(a.Costs, a.Costs[0]) }, func(a *Admission) { a.Footprint = nil }, func(a *Admission) { a.Footprint = append(a.Footprint, a.Footprint[0]) }, func(a *Admission) { a.Footprint = []domain.Cell{{X: 0, Z: 0}} }, func(a *Admission) { a.Snapshot.Plan = "different" }} {
		s, _ := fixture(t)
		a := evidence(10, 40)
		change(&a)
		if _, err := s.ReserveAndPrepare(ctx, "p", "a", a); err == nil {
			t.Fatal("invalid admission accepted")
		}
	}
	s, _ := fixture(t)
	free := evidence(10, 0)
	free.Costs = []MaterialCost{}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", free); err != nil {
		t.Fatal("known free placement rejected", err)
	}
	bad := evidence(10, 40)
	bad.Footprint = nil
	raw, err := json.Marshal(bad)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("UPDATE admissions SET payload=? WHERE action_id='a'", raw); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadPlan(ctx, "p"); err == nil {
		t.Fatal("corrupt admission accepted")
	}
}
