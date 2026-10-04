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
	r := roundsRequest()
	r.Enabled = false
	reviewRounds(t, s, &r)
	state, err := s.OpenIncident(ctx, IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "raid", Priority: 0, Snapshot: scope(), Tick: 10})
	if err != nil {
		t.Fatal(err)
	}
	id := state.Incident.ID
	p := plan(t, "incident-plan", "incident-action")
	// The Safeguards veto an incident's method as they do a goal's (#1017).
	if _, err = s.CommitIncidentMethod(ctx, id, "fight", "", p); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause admitted an incident method", err)
	}
	// The enabled review asserts the occurrence: it re-triggers the open
	// row rather than opening another (#1020).
	r.Enabled = true
	r.Tick++
	r.Facts.Hostiles = domain.Known(int64(3))
	out := reviewRounds(t, s, &r)
	if b, ok := out.Review.Incident(policy.ActiveCombat); !ok || b.Incident != id || b.Situation != domain.SituationActive {
		t.Fatal("review did not bind the open occurrence", out.Review.Incidents)
	}
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
	// The plan's method names the Response kind and the incident.
	if m, ok, err := s.PlanMethod(ctx, p.ID()); !ok || err != nil || m.Concern != policy.ActiveCombat || m.Incident != id || m.Reason != "raid at the edge" {
		t.Fatal("incident plan method", m, err)
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
	if err != nil || domain.StandardWorkOpen(loaded.Progress) {
		t.Fatal("close left the method open", err)
	}
	if _, err = s.CommitIncidentMethod(ctx, id, "late", "", plan(t, "late", "late-action")); !errors.Is(err, ErrConflict) {
		t.Fatal("closed incident admitted a method", err)
	}
	// A method row needs a plan_owner row of its kind, and the plan's
	// lookup names the incident.
	if _, err = s.db.ExecContext(ctx, "INSERT INTO incident_methods(incident_id,method_id,plan_id,priority) VALUES(?,'orphan','nowhere',1)", id); err == nil {
		t.Fatal("incident method without a plan_owner row accepted")
	}
	var kind, owner string
	if err = s.db.QueryRowContext(ctx, "SELECT kind,owner_id FROM plan_methods WHERE plan_id=?", p.ID()).Scan(&kind, &owner); err != nil || kind != "incident" || owner != string(id) {
		t.Fatal("incident plan lookup", kind, owner, err)
	}
}

// The review owns the incident kinds' occurrences (#1020): a deficit opens
// one and files no goal; a recovered one closes once its work settles; the
// next deficit opens a new row; a world change abandons an open one.
func TestRoundsIncidentLifecycle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "incidents.db"))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.CriticalPatients = domain.Known(int64(1))
	out := reviewRounds(t, s, &r)
	for _, b := range out.Review.Standards {
		if policy.IsIncidentKind(b.Concern) {
			t.Fatal("incident kind filed a goal", b)
		}
	}
	b, ok := out.Review.Incident(policy.CriticalMedicine)
	if !ok || b.Situation != domain.SituationActive || len(out.Incidents) != 1 {
		t.Fatal("deficit opened no incident", out.Review.Incidents)
	}
	first := b.Incident
	p := plan(t, "tend-plan", "tend-action")
	if _, err := s.CommitIncidentMethod(ctx, first, "tend-alice-0", "", p); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = p.ID(), p.Revision()
	if err := s.AuthorizeRoundsPlan(ctx, r.Current, target); err != nil {
		t.Fatal("incident plan not authorized", err)
	}
	if _, err := s.Prepare(ctx, p.ID(), "tend-action", target, r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, p.ID(), "tend-action", target, r.Tick); err != nil {
		t.Fatal(err)
	}
	// Recovered with dispatched work: the occurrence stays bound, recovered,
	// and authorizes nothing.
	r.Facts.CriticalPatients = domain.Known(int64(0))
	out = reviewRounds(t, s, &r)
	if b, ok = out.Review.Incident(policy.CriticalMedicine); !ok || b.Incident != first || b.Situation != domain.SituationClear {
		t.Fatal("recovered occurrence with open work", out.Review.Incidents)
	}
	if err := s.AuthorizeRoundsPlan(ctx, r.Current, target); err == nil {
		t.Fatal("recovered incident authorized its plan")
	}
	// A world change abandons it and cancels its work.
	r.Current.Load = "second-load"
	if out = reviewRounds(t, s, &r); len(out.Review.Incidents) != 0 {
		t.Fatal("world change kept the occurrence", out.Review.Incidents)
	}
	if closed, err := s.LoadIncident(ctx, first); err != nil || !closed.Incident.Closed {
		t.Fatal("occurrence not closed", closed.Incident, err)
	}
	// Recovered with only undispatched work: it is cancelled and the
	// occurrence closes at once; the next deficit opens a new row.
	r.Facts.CriticalPatients = domain.Known(int64(2))
	out = reviewRounds(t, s, &r)
	b, _ = out.Review.Incident(policy.CriticalMedicine)
	pending := plan(t, "tend-pending", "tend-pending-action")
	if _, err := s.CommitIncidentMethod(ctx, b.Incident, "tend-bob-0", "", pending); err != nil {
		t.Fatal(err)
	}
	r.Facts.CriticalPatients = domain.Known(int64(0))
	if out = reviewRounds(t, s, &r); len(out.Review.Incidents) != 0 {
		t.Fatal("settled occurrence stayed open", out.Review.Incidents)
	}
	if loaded, err := s.LoadPlan(ctx, pending.ID()); err != nil || domain.StandardWorkOpen(loaded.Progress) {
		t.Fatal("recovery left undispatched work open", err)
	}
	r.Facts.CriticalPatients = domain.Known(int64(1))
	out = reviewRounds(t, s, &r)
	if next, ok := out.Review.Incident(policy.CriticalMedicine); !ok || next.Incident == b.Incident {
		t.Fatal("next deficit reused the closed occurrence", out.Review.Incidents)
	}
	b, _ = out.Review.Incident(policy.CriticalMedicine)
	second := b.Incident
	world := World{Colony: r.Current.Colony, Load: r.Current.Load, Map: r.Current.Map}
	if latest, ok, err := s.LatestIncident(ctx, world, policy.CriticalMedicine); err != nil || !ok || latest.Incident.ID != second {
		t.Fatal("latest incident", latest.Incident, err)
	}
	// Pausing keeps the occurrence; replacing the world ends it.
	r.Enabled = false
	if out = reviewRounds(t, s, &r); len(out.Review.Incidents) != 1 {
		t.Fatal("disabled review dropped the occurrence", out.Review.Incidents)
	}
	r.Current.Load = "other-load"
	if out = reviewRounds(t, s, &r); len(out.Review.Incidents) != 0 {
		t.Fatal("world change kept the occurrence", out.Review.Incidents)
	}
	if abandoned, err := s.LoadIncident(ctx, second); err != nil || !abandoned.Incident.Closed {
		t.Fatal("world change left the occurrence open", abandoned.Incident, err)
	}
}
