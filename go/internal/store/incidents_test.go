package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestIncidentLifecycle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "incidents.db"))
	a := IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "raid", Priority: 1, Snapshot: scope(), Tick: 10}
	first, err := s.OpenIncident(ctx, a)
	if err != nil {
		t.Fatal(err)
	}
	if first.Incident.Closed || first.Incident.Started != 10 || first.Incident.Trigger != "raid" {
		t.Fatalf("opened %+v", first.Incident)
	}
	// A re-trigger inside the occurrence keeps the row and its trigger and
	// refreshes the priority.
	a.Trigger, a.Priority, a.Tick = "manhunter", 0, 20
	again, err := s.OpenIncident(ctx, a)
	if err != nil {
		t.Fatal(err)
	}
	if again.Incident.ID != first.Incident.ID || again.Incident.Trigger != "raid" || again.Incident.Priority != 0 || again.Incident.Started != 10 {
		t.Fatalf("re-trigger %+v", again.Incident)
	}
	// Another subject is another key.
	medical := IncidentAssessment{Kind: policy.CriticalMedicine, Subject: "pawn-1", Trigger: "bleeding", Priority: 1, Snapshot: scope(), Tick: 20}
	m1, err := s.OpenIncident(ctx, medical)
	if err != nil {
		t.Fatal(err)
	}
	medical.Subject = "pawn-2"
	m2, err := s.OpenIncident(ctx, medical)
	if err != nil || m2.Incident.ID == m1.Incident.ID {
		t.Fatal("per-pawn incidents shared a row", err)
	}
	world := World{Colony: scope().Colony, Load: scope().Load, Map: scope().Map}
	if open, err := s.OpenIncidents(ctx, world); err != nil || len(open) != 3 {
		t.Fatal(len(open), err)
	}
	closed, err := s.CloseIncident(ctx, first.Incident.ID, 30)
	if err != nil || !closed.Incident.Closed || closed.Incident.Ended != 30 {
		t.Fatal(closed.Incident, err)
	}
	if _, err = s.CloseIncident(ctx, first.Incident.ID, 31); !errors.Is(err, ErrConflict) {
		t.Fatal("closed twice", err)
	}
	a.Tick = 40
	next, err := s.OpenIncident(ctx, a)
	if err != nil || next.Incident.ID == first.Incident.ID || next.Incident.Started != 40 || next.Incident.Trigger != "manhunter" {
		t.Fatal("next occurrence reused the closed row", next.Incident, err)
	}
	if reloaded, err := s.LoadIncident(ctx, first.Incident.ID); err != nil || !reloaded.Incident.Closed {
		t.Fatal(reloaded.Incident, err)
	}
}

func TestIncidentMethodCommit(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "incidents.db"))
	r := routineRequest()
	r.Enabled = false
	reviewRoutine(t, s, &r)
	state, err := s.OpenIncident(ctx, IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "raid", Priority: 0, Snapshot: scope(), Tick: 10})
	if err != nil {
		t.Fatal(err)
	}
	id := state.Incident.ID
	p := plan(t, "incident-plan", "incident-action")
	// The Rules veto an incident's method as they do a goal's (#1017).
	if _, err = s.CommitIncidentMethod(ctx, id, "fight", "", p); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause admitted an incident method", err)
	}
	r.Enabled = true
	r.Tick++
	reviewRoutine(t, s, &r)
	state, err = s.CommitIncidentMethod(ctx, id, "fight", "raid at the edge", p)
	if err != nil {
		t.Fatal(err)
	}
	if len(state.Methods) != 1 || state.Methods[0].Plan != p.ID() {
		t.Fatal(state.Methods)
	}
	if _, err = s.LoadPlan(ctx, p.ID()); err != nil {
		t.Fatal(err)
	}
	// The plan is no goal's method.
	if _, ok, err := s.PlanGoalMethod(ctx, p.ID()); ok || err != nil {
		t.Fatal("incident plan read as a goal method", err)
	}
	// Open work blocks a second method, as it does a goal's.
	if _, err = s.CommitIncidentMethod(ctx, id, "fight-2", "", plan(t, "second", "second-action")); err == nil {
		t.Fatal("second method admitted over open work")
	}
	if _, err = s.LoadPlan(ctx, "second"); !errors.Is(err, ErrNotFound) {
		t.Fatal("refused method left a plan", err)
	}
	// Closing cancels the undispatched method; a closed incident admits none.
	if _, err = s.CloseIncident(ctx, id, 20); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadPlan(ctx, p.ID())
	if err != nil || domain.GoalWorkOpen(loaded.Progress) {
		t.Fatal("close left the method open", err)
	}
	if _, err = s.CommitIncidentMethod(ctx, id, "late", "", plan(t, "late", "late-action")); !errors.Is(err, ErrConflict) {
		t.Fatal("closed incident admitted a method", err)
	}
	// goal_methods rows bind exactly one owner.
	if _, err = s.db.ExecContext(ctx, "INSERT INTO goal_methods(epoch,method_id,plan_id,priority) VALUES('0','orphan','incident-plan',1)"); err == nil {
		t.Fatal("ownerless goal_methods row accepted")
	}
}
