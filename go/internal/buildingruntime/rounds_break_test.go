package buildingruntime

import (
	"context"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type subdueInjuryNative struct {
	framed
	events    []*mp.CombatEventRow
	uncertain bool
}

func (f *subdueInjuryNative) ReadCombat(ctx context.Context, id *c.Identity) (bridge.Combat, error) {
	combat, err := f.framed.ReadCombat(ctx, id)
	combat.Events = f.events
	combat.Context.Tick = proto.Int64(203152)
	if len(f.events) > 0 {
		combat.Context.Tick = proto.Int64(203387)
	}
	for i := range combat.Emergency.Facts.Colonists {
		if combat.Emergency.Facts.Colonists[i].ID == "broken" {
			combat.Emergency.Facts.Colonists[i].MentalState = domain.Known(policy.MentalState{DefName: "Tantrum", IsAggro: true})
		}
	}
	return combat, err
}

func (f *subdueInjuryNative) CombatOrders(ctx context.Context, id *c.Identity, key string, orders *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	results, err := f.framed.CombatOrders(ctx, id, key, orders)
	if f.uncertain {
		return nil, err
	}
	return results, err
}

// The recorded playtest crossed .5 while subduing a Tantrum colonist
// (.5626237 -> .48647246 at tick 203357). Ownership must arm benign
// injury stops, cancel both attacks and retain uncertain work for cleanup.
func TestSubdueRecordedInjuryOwnsStopAndReconciles(t *testing.T) {
	for _, hurt := range []string{"broken", "a"} {
		t.Run(hurt, func(t *testing.T) {
			ctx := context.Background()
			r, db, session, _, base := roundsFixture(t)
			b := newBreakNative(base)
			f := &subdueInjuryNative{framed: framed{b}}
			edit := b.editPawn
			b.editPawn = func(pawn *n.PawnState) {
				edit(pawn)
				if pawn.GetPawn().GetId() == "broken" {
					pawn.Health.SummaryFraction = proto.Float64(.5626237)
					if len(f.events) > 0 {
						pawn.Health.SummaryFraction = proto.Float64(.48647246)
					}
				}
			}
			planner, _ := newCombatTestPlanner(t, r, f)
			current := session.State().Snapshot
			old, _ := db.LoadRounds(ctx)
			if _, err := db.ReviewRounds(ctx, store.RoundsRequest{Revision: old.Revision, Current: current, Tick: 7, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: policy.RoundsFacts{Workers: domain.Known(2), Hostiles: domain.Known(int64(1)), CleanupPawns: domain.Known(false)}}); err != nil {
				t.Fatal(err)
			}
			out, err := planner.Step(ctx)
			if err != nil || out.Verdict != BuildingReasonAdmitted {
				t.Fatal(out, err)
			}
			combat, owned, _, err := clockSchedulerCombatPlan(ctx, db, current)
			if err != nil || !combat || !owned || !slices.Contains(armedCombatStops(owned), k.CombatEvent_COMBAT_EVENT_SERIOUS_INJURY) {
				t.Fatal("subdue did not own its injury policy", combat, owned, err)
			}
			// Native emits the armed combat event instead of the generic health
			// hold; this is the inbox boundary that preserves Auto authority.
			injuryStop := &k.Event{Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_COMBAT_EVENT.Enum()}}}
			healthHold := &k.Event{Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_COLONIST_HEALTH.Enum()}}}
			if clock.EventInterrupts(injuryStop) || !clock.EventInterrupts(healthHold) {
				t.Fatal("owned injury crossed the Auto revocation boundary")
			}
			plan, _ := db.LoadPlan(ctx, out.Plan)
			// One dispatch has no receipt yet; cancellation must not invent one.
			var uncertain domain.ActionID
			for _, action := range plan.Spec.Actions() {
				if subdue, ok := action.Subdue(); ok && subdue.Pawn() == "a" {
					uncertain = action.ID()
				}
			}
			attemptSnapshot := current
			attemptSnapshot.Plan = out.Plan
			if _, err = db.Prepare(ctx, out.Plan, uncertain, attemptSnapshot, 7); err != nil {
				t.Fatal(err)
			}
			if _, err = db.Dispatch(ctx, out.Plan, uncertain, attemptSnapshot, 7); err != nil {
				t.Fatal(err)
			}
			f.events = []*mp.CombatEventRow{{At: &mp.Watermark{Tick: proto.Int64(203357)}, Stop: k.CombatEvent_COMBAT_EVENT_SERIOUS_INJURY.Enum(), ThingId: proto.String(hurt)}, {At: &mp.Watermark{Tick: proto.Int64(203358)}, Stop: k.CombatEvent_COMBAT_EVENT_MELEE_CONTACT.Enum()}}
			f.uncertain = true
			if _, err = planner.Step(ctx); err != nil {
				t.Fatal(err)
			}
			fight, _, _ := db.LoadCombatFight(ctx, out.Plan)
			plan, _ = db.LoadPlan(ctx, out.Plan)
			if !fight.Open || !session.State().Enabled {
				t.Fatal("uncertain stop lost ownership or Auto")
			}
			for _, progress := range plan.Progress {
				if progress.View().Stage != domain.Cancelled {
					t.Fatal(progress.View())
				}
				if progress.Action().ID() == uncertain && !progress.View().Unresolved {
					t.Fatal("uncertain dispatch was settled without evidence")
				}
			}
			f.uncertain = false
			if _, err = planner.Step(ctx); err != nil {
				t.Fatal(err)
			}
			fight, _, _ = db.LoadCombatFight(ctx, out.Plan)
			if !fight.Open {
				t.Fatal("closed before uncertain action reconciled")
			}
			if _, err = db.RecordReceipt(ctx, out.Plan, uncertain, 1, domain.ReceiptAccepted); err != nil {
				t.Fatal(err)
			}
			if _, err = planner.Step(ctx); err != nil {
				t.Fatal(err)
			}
			fight, _, _ = db.LoadCombatFight(ctx, out.Plan)
			needed, err := plannedDrafts(ctx, db)
			if err != nil || fight.Open || len(needed) != 0 || !session.State().Enabled {
				t.Fatal("fight cleanup did not release claims", fight, needed, err)
			}
			for _, batch := range b.orders.batches {
				if len(batch.Orders) != 2 {
					t.Fatal(batch)
				}
				for _, order := range batch.Orders {
					if order.GetStop() == nil {
						t.Fatal("injury must stop, never attack", order)
					}
				}
			}
			if out2, err := planner.Step(ctx); err != nil || out2.Verdict == BuildingReasonAdmitted {
				t.Fatal("cancelled response attacked again", out2, err)
			}
		})
	}
}

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
func newBreakNative(base *roundsNative) *breakNative {
	b := &breakNative{equipTestNative: &equipTestNative{roundsNative: base, ids: []string{"a", "b", "broken"}}}
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
			var issues []*n.ReadIssue
			for _, issue := range p.Issues {
				if issue.GetField() != "mental_state" {
					issues = append(issues, issue)
				}
			}
			p.Issues = issues
			p.Downed = proto.Bool(b.downed)
		}
		p.Pawn.Position = &c.Cell{X: proto.Int32(x), Z: proto.Int32(10)}
		p.Equipment = &n.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("weapon-" + p.Pawn.GetId()), Equipped: []*n.GearItem{{Thing: b.thing(&n.Thing{Thing: &n.EntityRef{Id: proto.String("weapon-" + p.Pawn.GetId()), DefName: proto.String("MeleeWeapon_Club")}})}}}
	}
	return b
}
func TestBreakResponsePlannerDraftsSubduesThenOffersRescue(t *testing.T) {
	ctx := context.Background()
	reviewer, db, session, _, base := roundsFixture(t)
	native := newBreakNative(base)
	planner, _ := newCombatTestPlanner(t, reviewer, framed{native})
	current := session.State().Snapshot
	review := func(hostiles, patients int64) {
		t.Helper()
		old, err := db.LoadRounds(ctx)
		if err != nil {
			t.Fatal(err)
		}
		_, err = db.ReviewRounds(ctx, store.RoundsRequest{Revision: old.Revision, Current: current, Tick: 7, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: policy.RoundsFacts{Workers: domain.Known(2), Hostiles: domain.Known(hostiles), CriticalPatients: domain.Known(patients), CleanupPawns: domain.Known(false)}})
		if err != nil {
			t.Fatal(err)
		}
	}
	review(1, 0)
	out, err := planner.Step(ctx)
	if err != nil || out.Verdict != BuildingReasonAdmitted {
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
		if m, ok := a.Subdue(); ok {
			if m.Target() != "broken" {
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
		if p.Action().Kind() == domain.SubdueAction && p.View().Stage != domain.Cancelled {
			t.Fatal(p.View())
		}
	}
	review(0, 1)
	rescue, _ := NewRoundsRescuePlanner(reviewer, native)
	rescued, err := rescue.Step(ctx)
	if err != nil || rescued.Verdict != BuildingReasonAdmitted {
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
			r, db, session, _, _ := roundsFixture(t)
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
	r, db, session, _, base := roundsFixture(t)
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
	ma, _ := domain.NewSubdueAction("subdue", m)
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

func (n *breakNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
