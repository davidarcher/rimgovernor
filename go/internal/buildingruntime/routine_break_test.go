package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type breakNative struct {
	*equipTestNative
	downed bool
}

func (b *breakNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	out, r, err := b.equipTestNative.ReadEmergency(ctx, id)
	for i := range out.Facts.Colonists {
		p := &out.Facts.Colonists[i]
		if p.ID == "broken" {
			p.MentalState = domain.Known(policy.MentalState{DefName: "Berserk", IsAggro: true, TicksInState: 30})
			p.Downed = domain.Known(b.downed)
		}
	}
	return out, r, err
}
func newBreakNative(base *routineNative) *breakNative {
	b := &breakNative{equipTestNative: &equipTestNative{routineNative: base, ids: []string{"a", "b", "broken"}}}
	b.editPawn = func(p *n.PawnState) {
		p.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), NeedsTend: proto.Bool(false), Bleeding: proto.Bool(false)}
		p.Job = &n.JobEvidence{DefName: proto.String("Wait"), PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}
		p.InBed = proto.Bool(false)
		x := int32(12)
		if p.Pawn.GetId() == "b" {
			x = 13
		}
		if p.Pawn.GetId() == "broken" {
			x = 10
			p.MentalState = proto.String("Berserk")
			p.MentalStateIsAggro = proto.Bool(true)
			p.MentalStateTicks = proto.Int32(30)
			p.Downed = proto.Bool(b.downed)
		}
		p.Pawn.Position = &c.Cell{X: proto.Int32(x), Z: proto.Int32(10)}
		p.Equipment = &n.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("weapon-" + p.Pawn.GetId()), Equipped: []*n.GearItem{{Thing: &n.EntityRef{Id: proto.String("weapon-" + p.Pawn.GetId())}, Weapon: proto.Bool(true), Melee: proto.Bool(true), Ranged: proto.Bool(false)}}}
	}
	return b
}
func TestBreakResponsePlannerDraftsSubduesThenOffersRescue(t *testing.T) {
	ctx := context.Background()
	reviewer, db, session, _, base := routineFixture(t)
	native := newBreakNative(base)
	planner, _ := NewRoutineDefensePlanner(reviewer, native)
	current := session.State().Snapshot
	review := func(hostiles, patients int64) {
		t.Helper()
		old, err := db.LoadRoutineReview(ctx)
		if err != nil {
			t.Fatal(err)
		}
		_, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: old.Revision, Current: current, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{Workers: domain.Known(2), Hostiles: domain.Known(hostiles), CriticalPatients: domain.Known(patients), CleanupPawns: domain.Known(false)}})
		if err != nil {
			t.Fatal(err)
		}
	}
	review(1, 0)
	out, err := planner.Step(ctx)
	if err != nil || out.Reason != BuildingMethodAdmitted {
		t.Fatal(out, err)
	}
	plan, err := db.LoadPlan(ctx, out.Plan)
	if err != nil {
		t.Fatal(err)
	}
	drafts, subdues := 0, 0
	for _, a := range plan.Spec.Actions() {
		if _, ok := a.OwnedDraft(); ok {
			drafts++
		}
		if m, ok := a.MeleeAttack(); ok {
			if !m.Subdue() || m.Target() != "broken" {
				t.Fatal(a)
			}
			subdues++
		}
	}
	if drafts != 2 || subdues != 2 {
		t.Fatal(drafts, subdues)
	}
	native.downed = true
	census, _, _ := native.ReadEmergency(ctx, nil)
	if err = releaseBreakWork(ctx, db, current, census.Facts, []store.PlanState{plan}); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, out.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range plan.Progress {
		if p.Action().Subdues() && p.View().Stage != domain.Cancelled {
			t.Fatal(p.View())
		}
	}
	review(0, 1)
	rescue, _ := NewRoutineRescuePlanner(reviewer, native)
	rescued, err := rescue.Step(ctx)
	if err != nil || rescued.Reason != BuildingMethodAdmitted {
		t.Fatal(rescued, err)
	}
	rp, err := db.LoadPlan(ctx, rescued.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range rp.Spec.Actions() {
		r, ok := a.Rescue()
		if !ok || r.Patient() != "broken" {
			t.Fatal("must rescue, never capture", a)
		}
	}
}
func TestBreakResponseReleasesNonviolentWorkerClaims(t *testing.T) {
	for _, name := range []string{"Wander_Sad", "Binging_Food", "Tantrum", "HideInRoom"} {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			r, db, session, _, _ := routineFixture(t)
			_ = r
			intent, _ := domain.NewClean("broken", "filth", domain.Cell{X: 10, Z: 10})
			a, _ := domain.NewCleanAction("clean-broken", intent)
			plan, _ := domain.NewPlan("break-work", 1, []domain.Action{a})
			if err := db.CreatePlan(ctx, plan); err != nil {
				t.Fatal(err)
			}
			stored, _ := db.LoadPlan(ctx, plan.ID())
			f := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "broken", MentalState: domain.Known(policy.MentalState{DefName: name})}}}
			if err := releaseBreakWork(ctx, db, session.State().Snapshot, f, []store.PlanState{stored}); err != nil {
				t.Fatal(err)
			}
			stored, _ = db.LoadPlan(ctx, plan.ID())
			if stored.Progress[0].View().Stage != domain.Cancelled {
				t.Fatal(stored.Progress[0].View())
			}
		})
	}
}
func TestBreakResponseDispatchRadiusAndSquadExemption(t *testing.T) {
	ctx := context.Background()
	r, db, session, _, base := routineFixture(t)
	native := newBreakNative(base)
	var actions []domain.Action
	for _, tc := range []struct {
		id   string
		cell domain.Cell
	}{{"near", domain.Cell{X: 17, Z: 10}}, {"far", domain.Cell{X: 40, Z: 40}}} {
		b, _ := domain.NewBuilding("Wall", tc.cell, domain.North, "WoodLog")
		a, _ := domain.NewBuildingAction(domain.ActionID(tc.id), b)
		actions = append(actions, a)
	}
	d, _ := domain.NewOwnedDraft("a")
	da, _ := domain.NewOwnedDraftAction("draft", d)
	m, _ := domain.NewSubdue("a", "broken", "draft")
	ma, _ := domain.NewMeleeAttackAction("subdue", m)
	actions = append(actions, da, ma)
	plan, err := domain.NewPlan("radius", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	stored, _ := db.LoadPlan(ctx, plan.ID())
	w := &Worker{player: r.player, config: WorkerConfig{BreakSource: native}}
	held, err := w.breakDispatchHolds(ctx, session.State().Snapshot, []store.PlanState{stored})
	if err != nil || !held["near"] || held["far"] || held["draft"] || held["subdue"] {
		t.Fatal(held, err)
	}
}
