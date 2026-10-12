package policy

import (
	"reflect"
	"slices"
	"testing"
)

// rerouteConsumers are the consumers of conduitPlan's two rooms, plus a third
// room at (20, 20) when more is set.
func rerouteConsumers(ids ...int32) []conduitNode {
	at := map[int32]Rectangle{1: {X: 20, Z: 5, Width: 3, Height: 3}, 2: {X: 20, Z: 15, Width: 3, Height: 3}, 3: {X: 20, Z: 20, Width: 3, Height: 3}}
	var out []conduitNode
	for _, id := range ids {
		out = append(out, conduitNode{ref: RoomRef(id), cells: RectRegion(at[id]).Cells()})
	}
	return out
}

func linkFrom(links []ConduitLink, ref ConduitRef) (ConduitLink, bool) {
	for _, l := range links {
		if l.From == ref {
			return l, true
		}
	}
	return ConduitLink{}, false
}

func reasonOf(ds []ConduitDecision, ref ConduitRef) string {
	for _, d := range ds {
		if d.From == ref {
			return d.Reason
		}
	}
	return ""
}

// A change elsewhere in the plan moves no conduit that still holds: the links
// the colony holds are kept and only the new room routes.
func TestRouteConduitsKeepsTheHeldLinks(t *testing.T) {
	plan, survey := conduitPlan(), conduitSurvey(30, 24, nil)
	first, _ := RouteConduits(plan, rerouteConsumers(1, 2), survey, nil)
	links, decisions := RouteConduits(plan, rerouteConsumers(1, 2, 3), survey, first)
	for _, ref := range []ConduitRef{RoomRef(1), RoomRef(2)} {
		held, _ := linkFrom(first, ref)
		got, ok := linkFrom(links, ref)
		if !ok || !reflect.DeepEqual(held, got) || reasonOf(decisions, ref) != ReasonConduitKept {
			t.Errorf("%s: held %v, now %v (%s)", ref, held, got, reasonOf(decisions, ref))
		}
	}
	if reasonOf(decisions, RoomRef(3)) != ReasonConduitRouted {
		t.Errorf("the new room routes: %+v", decisions)
	}
}

// Ground a held link crosses that stops taking a conduit (the wall or room
// that changed) moves that link, and the new path avoids it.
func TestRouteConduitsRoutesAroundAChangedCell(t *testing.T) {
	plan := conduitPlan()
	first, _ := RouteConduits(plan, rerouteConsumers(1), conduitSurvey(30, 24, nil), nil)
	held, _ := linkFrom(first, RoomRef(1))
	bad := held.Cells[len(held.Cells)/2]
	survey := conduitSurvey(30, 24, func(c *SurveyCell) {
		if c.Cell == bad {
			c.Walkable, c.Footing = false, FootingNone
		}
	})
	links, decisions := RouteConduits(plan, rerouteConsumers(1), survey, first)
	got, ok := linkFrom(links, RoomRef(1))
	if !ok || slices.Contains(got.Cells, bad) || reasonOf(decisions, RoomRef(1)) != ReasonConduitRouted {
		t.Fatalf("the blocked cell %v: %v (%s)", bad, got, reasonOf(decisions, RoomRef(1)))
	}
	plan.Conduits = links
	if !plan.Valid() {
		t.Fatal("the re-routed net must be valid")
	}
}

// A site that retires takes its path with it: the links that ended on it route
// to the net that stands, and no link names the retired site.
func TestRouteConduitsDropsTheRetiredSitePath(t *testing.T) {
	plan, survey := conduitPlan(), conduitSurvey(30, 24, nil)
	first, _ := RouteConduits(plan, rerouteConsumers(1), survey, nil)
	next := conduitPlan()
	next.Sites = []PlannedSite{solarSite("solar-2", Rectangle{X: 2, Z: 10, Width: 2, Height: 2})}
	links, _ := RouteConduits(next, rerouteConsumers(1), survey, first)
	got, ok := linkFrom(links, RoomRef(1))
	if !ok || got.To != SiteRef("solar-2") {
		t.Fatalf("the room joins the site that stands: %+v", got)
	}
	next.Conduits = links
	if !next.Valid() {
		t.Fatal("the net must be valid without the retired site")
	}
	for _, l := range links {
		if l.To == SiteRef("solar-1") || l.From == SiteRef("solar-1") {
			t.Errorf("a link names the retired site: %+v", l)
		}
	}
}

// A room that replaces another where it stood keeps the path its predecessor's
// power runs on.
func TestRouteConduitsKeepsTheConduitOfAReplacedRoom(t *testing.T) {
	plan, survey := conduitPlan(), conduitSurvey(30, 24, nil)
	first, _ := RouteConduits(plan, rerouteConsumers(1, 2), survey, nil)
	held, _ := linkFrom(first, RoomRef(1))
	next := conduitPlan()
	next.Rooms[0].ID, next.Rooms[0].Replaces, next.NextRoomID = 3, 1, 4
	consumers := rerouteConsumers(3, 2)
	consumers[0].cells = rerouteConsumers(1)[0].cells
	links, decisions := RouteConduits(next, consumers, survey, first)
	got, ok := linkFrom(links, RoomRef(3))
	if !ok || !reflect.DeepEqual(got.Cells, held.Cells) || reasonOf(decisions, RoomRef(3)) != ReasonConduitKept {
		t.Fatalf("the successor keeps %v: %v (%s)", held.Cells, got.Cells, reasonOf(decisions, RoomRef(3)))
	}
}

// The rooms a replan keeps that the tiler did not plan join the new net by the
// link they held; a room that drew nothing is left alone.
func TestRejoinHeldJoinsTheKeptRoomsToTheNewNet(t *testing.T) {
	survey := conduitSurvey(30, 24, nil)
	prev := conduitPlan()
	prev.Conduits, _ = RouteConduits(prev, rerouteConsumers(1, 2), survey, nil)
	tiled := conduitPlan()
	tiled.Rooms = tiled.Rooms[1:] // room 1 has no node in the new embedding
	tiled.Conduits, _ = RouteConduits(tiled, rerouteConsumers(2), survey, prev.Conduits)
	plan := conduitPlan()
	plan.Conduits = tiled.Conduits
	links, decisions := rejoinHeld(prev, tiled, plan, survey)
	if len(decisions) != 1 || decisions[0].From != RoomRef(1) {
		t.Fatalf("the held room joins: %+v", decisions)
	}
	if _, ok := linkFrom(links, RoomRef(1)); !ok || len(links) != len(tiled.Conduits)+1 {
		t.Fatalf("links %v", links)
	}
	plan.Conduits = links
	if !plan.Valid() {
		t.Fatal("the joined net must be valid")
	}
	// A kept room with no held link draws nothing: no join, no decision.
	none := prev
	none.Conduits = nil
	if links, decisions := rejoinHeld(none, tiled, plan, survey); len(decisions) != 0 || !reflect.DeepEqual(links, plan.Conduits) {
		t.Fatalf("a room that drew nothing joined: %+v", decisions)
	}
}
