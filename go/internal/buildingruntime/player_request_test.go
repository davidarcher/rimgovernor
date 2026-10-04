package buildingruntime

import (
	"context"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// surveyNative serves one map survey; nothing else of the routine source
// is read.
type surveyNative struct {
	observation.RoutineSource
	survey policy.MapSurvey
}

func (n surveyNative) ReadMapSurvey(context.Context, *c.Identity, policy.Bounds) (policy.MapSurvey, bridge.Result, error) {
	return n.survey, bridge.Result{}, nil
}

func openSurvey(n int32) policy.MapSurvey {
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: 1})
		}
	}
	return s
}

func actionIDs(actions []policy.PanelAction) []string {
	var out []string
	for _, a := range actions {
		out = append(out, a.ID)
	}
	return out
}

// #957: Replan layout proposes a fresh plan beside the saved one, Discard
// drops it, Apply records it; a reload, rewind or a newer saved plan drops
// a standing proposal.
func TestPlayerRequestsReplanApplyDiscard(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	survey := openSurvey(200)
	r := &Rounder{player: s.player, native: surveyNative{survey: survey}, layoutOverlay: true}
	snapshot := s.player.session.State().Snapshot
	plan, ok := policy.DeriveLayoutPlan(survey, 3, policy.BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	// The saved plan holds an unstarted room a fresh replan drops.
	stale := policy.LayoutRoom{Role: policy.ModuleReserve, Interior: policy.Rectangle{X: 20, Z: 20, Width: 3, Height: 3}, Door: domain.Cell{X: 21, Z: 19}, DoorRot: domain.South}
	plan.Rooms = append(plan.Rooms, stale)
	if err := s.player.journal.RecordLayoutPlan(ctx, snapshot, 100, plan); err != nil {
		t.Fatal(err)
	}
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: 200}, Bounds: survey.Bounds}
	projection.Facts.Colonists = domain.Known(int64(3))
	projection.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	serve := func(requests ...string) {
		t.Helper()
		for i, action := range requests {
			r.requests.push([]playerRequest{{Action: action, ID: string(rune('a' + i))}})
		}
		layout, have, err := r.layoutPlan(ctx, snapshot, projection.Identity.Tick)
		if err != nil {
			t.Fatal(err)
		}
		if layout, have, err = r.servePlayerRequests(ctx, snapshot, &projection, layout, have); err != nil {
			t.Fatal(err)
		}
		projection.LayoutPlan = domain.Unknown[policy.LayoutPlan]()
		if have {
			projection.LayoutPlan = domain.Known(layout.Plan)
		}
	}

	serve()
	if _, actions := r.layoutPanel(&projection); !slices.Equal(actionIDs(actions), []string{panelReplanLayout}) {
		t.Fatal("no Replan layout button", actions)
	}
	serve(panelReplanLayout)
	if r.proposal == nil || slices.ContainsFunc(r.proposal.Plan.Rooms, stale.Same) {
		t.Fatal("no proposal dropping the stale room")
	}
	rows, actions := r.layoutPanel(&projection)
	if !slices.Equal(actionIDs(actions), []string{panelApplyLayout, panelDiscardLayout}) || len(rows) != 1 || rows[0].Key != "layout" {
		t.Fatal(rows, actions)
	}
	serve(panelDiscardLayout)
	if r.proposal != nil {
		t.Fatal("discard kept the proposal")
	}
	if saved, _, _ := r.layoutPlan(ctx, snapshot, 200); !reflect.DeepEqual(saved.Plan, plan) {
		t.Fatal("discard changed the saved plan")
	}
	serve(panelReplanLayout)
	proposed := r.proposal.Plan
	projection.Identity.Tick = 300
	serve(panelApplyLayout)
	saved, _, _ := r.layoutPlan(ctx, snapshot, 300)
	if r.proposal != nil || saved.Tick != 300 || !reflect.DeepEqual(saved.Plan, proposed) {
		t.Fatal("apply did not adopt the proposal", saved.Tick)
	}
	if got, _ := projection.LayoutPlan.Value(); !reflect.DeepEqual(got, proposed) {
		t.Fatal("the review did not serve the applied plan")
	}
	// A replan of the applied plan finds nothing to change.
	serve(panelReplanLayout)
	if r.proposal != nil || r.note.text != "no change" {
		t.Fatal("a fresh plan re-proposed itself", r.note)
	}
}

func TestLayoutProposalDropsOnReloadRewindAndNewerPlan(t *testing.T) {
	world := store.World{Colony: "colony", Load: "load-1", Map: 1}
	p := &layoutProposal{World: world, Tick: 500, Base: 100}
	layout := store.LayoutPlanRecord{Tick: 100}
	if !p.current(world, 600, layout, true) {
		t.Fatal("a standing proposal dropped")
	}
	reloaded := world
	reloaded.Load = "load-2"
	if p.current(reloaded, 600, layout, true) {
		t.Fatal("a reload kept the proposal")
	}
	if p.current(world, 400, layout, true) {
		t.Fatal("a rewind past the proposal kept it")
	}
	if p.current(world, 600, store.LayoutPlanRecord{Tick: 550}, true) || p.current(world, 600, layout, false) {
		t.Fatal("a newer saved plan kept the proposal")
	}
}

// The poll hands the reviewer the page's presses from this world, past the
// history watermark.
func TestClockPlayerRequestsFiltersHistoryAndOtherWorlds(t *testing.T) {
	here := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load-2"), MapId: proto.Int32(1)}}
	there := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load-1"), MapId: proto.Int32(1)}}
	press := func(cursor int64, at *c.ObservationContext, id string) *k.Event {
		return &k.Event{Cursor: proto.Int64(cursor), Context: at, Event: &k.Event_PlayerRequest{PlayerRequest: &k.PlayerRequest{Action: proto.String(panelReplanLayout), RequestId: proto.String(id)}}}
	}
	page := &k.EventsPage{Context: here, Events: []*k.Event{
		press(4, here, "old"),
		press(5, there, "other-load"),
		{Cursor: proto.Int64(6), Context: here, Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(2)}}},
		press(7, here, "fresh"),
	}}
	got := clockPlayerRequests(page, 4)
	if !reflect.DeepEqual(got, []playerRequest{{Action: panelReplanLayout, ID: "fresh"}}) {
		t.Fatal(got)
	}
}
