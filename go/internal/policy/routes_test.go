package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func routesFacility(id, kind string, reachable ...bool) RouteFacility {
	f := RouteFacility{ID: id, Definition: "Thing", Kind: kind, Cell: domain.Cell{X: 10, Z: 10}, Room: domain.Known("7")}
	for i, r := range reachable {
		t := RouteTravel{Pawn: []string{"a", "b"}[i], Reachable: r}
		if r {
			t.Cost = domain.Known[int32](120)
			t.Cells = domain.Known[int32](9)
		}
		f.Travel = append(f.Travel, t)
	}
	return f
}

func routesCensus() RoutesObservation {
	sealed := routesFacility("zone-3", "stockpile", false, false)
	sealed.Breaches = []RouteBreach{
		{Cell: domain.Cell{X: 12, Z: 9}, Edifice: "Wall", Distance: 40},
		{Cell: domain.Cell{X: 9, Z: 10}, Edifice: "Wall", Distance: 4},
		{Cell: domain.Cell{X: 10, Z: 9}, Edifice: "Wall", Distance: 9, Pending: "Door"},
	}
	bed := routesFacility("bed-1", "bed", false, false)
	bed.Breaches = []RouteBreach{{Cell: domain.Cell{X: 20, Z: 20}, Edifice: "Wall", Distance: 1}}
	return RoutesObservation{
		Pawns:      []string{"a", "b"},
		Facilities: []RouteFacility{bed, routesFacility("bench-1", "bench", true, false), sealed},
		Traffic:    []TrafficCell{{Cell: domain.Cell{X: 5, Z: 5}, Samples: 30, Terrain: "Soil", Home: true}},
	}
}

func TestReviewRoutesLatchesUnreachableFacilitiesByKind(t *testing.T) {
	p := DefaultRoutesPolicy()
	r, err := ReviewRoutes(domain.Known(routesCensus()), nil, p)
	if err != nil || !r.Known || !r.Active || len(r.Deficits) != 2 {
		t.Fatal(r, err)
	}
	// Storage outranks beds; the sealed stockpile's breaches come nearest
	// first with the ordered door counted, not listed.
	if r.Deficits[0].Facility.ID != "zone-3" || r.Deficits[1].Facility.ID != "bed-1" {
		t.Fatal(r.Deficits)
	}
	d := r.Deficits[0]
	if d.Pending != 1 || len(d.Breaches) != 2 || d.Breaches[0].Cell != (domain.Cell{X: 9, Z: 10}) || d.Breaches[1].Cell != (domain.Cell{X: 12, Z: 9}) {
		t.Fatal(d)
	}
	if len(r.Latched) != 2 || r.Latched[0] != "bed-1" || r.Latched[1] != "zone-3" {
		t.Fatal(r.Latched)
	}
	// An unknown census keeps the latch and reports nothing new.
	held, err := ReviewRoutes(domain.Unknown[RoutesObservation](), r.Latched, p)
	if err != nil || held.Known || !held.Active || len(held.Deficits) != 0 || len(held.Latched) != 2 {
		t.Fatal(held, err)
	}
	// A measured census reading everything reachable releases a facility,
	// except a latched one whose ordered door is still on the wall: the
	// frame is walkable before the door stands.
	v := routesCensus()
	v.Facilities[0].Travel[1].Reachable = true
	v.Facilities[2].Travel[0].Reachable = true
	holding, err := ReviewRoutes(domain.Known(v), r.Latched, p)
	if err != nil || !holding.Known || !holding.Active || len(holding.Latched) != 1 || holding.Latched[0] != "zone-3" || holding.Deficits[0].Pending != 1 {
		t.Fatal(holding, err)
	}
	if got, _ := SelectRoutesMethod(holding, RoutesFacts{DoorAvailable: domain.Known(true)}, p); got.Method != RoutesDoorPending {
		t.Fatal(got)
	}
	// An unlatched facility with an ordered door is nobody's deficit.
	if fresh, err := ReviewRoutes(domain.Known(v), nil, p); err != nil || fresh.Active {
		t.Fatal(fresh, err)
	}
	v.Facilities[2].Breaches[2].Pending = ""
	clear, err := ReviewRoutes(domain.Known(v), r.Latched, p)
	if err != nil || !clear.Known || clear.Active || len(clear.Latched) != 0 {
		t.Fatal(clear, err)
	}
}

func TestReviewRoutesNeedsAMobileColonist(t *testing.T) {
	v := routesCensus()
	v.Pawns = nil
	for i := range v.Facilities {
		v.Facilities[i].Travel = nil
	}
	r, err := ReviewRoutes(domain.Known(v), []string{"zone-3"}, DefaultRoutesPolicy())
	if err != nil || !r.Known || r.Active || len(r.Deficits) != 0 {
		t.Fatal(r, err)
	}
}

func TestReviewRoutesRefusesInvalidCensus(t *testing.T) {
	for _, mutate := range []func(*RoutesObservation){
		func(v *RoutesObservation) { v.Pawns = append(v.Pawns, "a") },
		func(v *RoutesObservation) { v.Facilities[0].Travel[0].Pawn = "zed" },
		func(v *RoutesObservation) { v.Facilities = append(v.Facilities, v.Facilities[0]) },
		func(v *RoutesObservation) { v.Facilities[1].Travel[1].Cost = domain.Known[int32](3) },
		func(v *RoutesObservation) { v.Facilities[1].Travel[0].Cost = domain.Known[int32](-1) },
		func(v *RoutesObservation) { v.Facilities[2].Breaches[0].Distance = -1 },
		func(v *RoutesObservation) { v.Facilities[2].Breaches[1].Cell = v.Facilities[2].Breaches[0].Cell },
		func(v *RoutesObservation) { v.Traffic[0].Terrain = "" },
		func(v *RoutesObservation) { v.Traffic = append(v.Traffic, v.Traffic[0]) },
	} {
		v := routesCensus()
		mutate(&v)
		if _, err := ReviewRoutes(domain.Known(v), nil, DefaultRoutesPolicy()); err == nil {
			t.Fatal("accepted invalid routes census")
		}
	}
}

func TestSelectRoutesMethodOpensNearestBreach(t *testing.T) {
	p := DefaultRoutesPolicy()
	r, err := ReviewRoutes(domain.Known(routesCensus()), nil, p)
	if err != nil {
		t.Fatal(err)
	}
	proposal, err := SelectRoutesMethod(r, RoutesFacts{DoorAvailable: domain.Known(true)}, p)
	if err != nil || proposal.Method != RoutesBuild || proposal.Facility != "zone-3" || proposal.Definition != "Door" || proposal.Stuff != "WoodLog" {
		t.Fatal(proposal, err)
	}
	if len(proposal.Breaches) != 2 || proposal.Breaches[0] != (domain.Cell{X: 9, Z: 10}) || proposal.Key == "" {
		t.Fatal(proposal)
	}
	again, _ := SelectRoutesMethod(r, RoutesFacts{DoorAvailable: domain.Known(true)}, p)
	if again.Key != proposal.Key {
		t.Fatal("method key drifted", again.Key, proposal.Key)
	}
	if got, _ := SelectRoutesMethod(r, RoutesFacts{}, p); got.Method != RoutesUnknown {
		t.Fatal(got)
	}
	if got, _ := SelectRoutesMethod(r, RoutesFacts{DoorAvailable: domain.Known(false)}, p); got.Method != RoutesDoorUnavailable {
		t.Fatal(got)
	}
	// Only the ordered door left: wait for it. No breach at all: say so.
	v := routesCensus()
	v.Facilities[2].Breaches = v.Facilities[2].Breaches[2:]
	v.Facilities[0].Breaches = nil
	r, _ = ReviewRoutes(domain.Known(v), nil, p)
	if got, _ := SelectRoutesMethod(r, RoutesFacts{DoorAvailable: domain.Known(true)}, p); got.Method != RoutesDoorPending {
		t.Fatal(got)
	}
	v.Facilities[2].Breaches = nil
	r, _ = ReviewRoutes(domain.Known(v), nil, p)
	if got, _ := SelectRoutesMethod(r, RoutesFacts{DoorAvailable: domain.Known(true)}, p); got.Method != RoutesNoBreach {
		t.Fatal(got)
	}
	if got, _ := SelectRoutesMethod(RoutesReview{}, RoutesFacts{}, p); got.Method != RoutesNoMethod {
		t.Fatal(got)
	}
	if got, _ := SelectRoutesMethod(RoutesReview{Active: true}, RoutesFacts{}, p); got.Method != RoutesUnknown {
		t.Fatal(got)
	}
}

func TestFlooringTrafficTierFloorsBusyNaturalHomeCells(t *testing.T) {
	p := flooringPolicy()
	v := FlooringObservation{Rooms: []FloorRoom{flooringRoom("done", RoomRoleKitchen, "WoodPlankFloor", 40, 40)}, Terrains: flooringTerrains(), TrafficSamples: 100}
	v.Traffic = []TrafficCell{
		{Cell: domain.Cell{X: 1, Z: 1}, Samples: 40, Terrain: "Soil", Home: true},
		{Cell: domain.Cell{X: 2, Z: 1}, Samples: 20, Terrain: "Soil", Home: true, Pending: "WoodPlankFloor"},
		{Cell: domain.Cell{X: 3, Z: 1}, Samples: 30, Terrain: "Soil", Home: false},
		{Cell: domain.Cell{X: 4, Z: 1}, Samples: 5, Terrain: "Soil", Home: true},
		{Cell: domain.Cell{X: 5, Z: 1}, Samples: 50, Terrain: "WoodPlankFloor", Home: true},
		{Cell: domain.Cell{X: 40, Z: 40}, Samples: 60, Terrain: "Soil", Home: true},
	}
	r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || !r.Active || len(r.Deficits) != 1 {
		t.Fatal(r, err)
	}
	d := r.Deficits[0]
	// Only the busy natural home cell outside a tiered room is short; the
	// ordered one is counted, the quiet, outdoor, floored and roomed cells are not.
	if d.Tier != FloorTierTraffic || d.Key != trafficKey || d.Pending != 1 || len(d.Cells) != 1 || d.Cells[0] != (domain.Cell{X: 1, Z: 1}) {
		t.Fatal(d)
	}
	proposal, err := SelectFlooringMethod(r, flooringDefinitions(), p)
	if err != nil || proposal.Method != FlooringBuild || proposal.Tier != FloorTierTraffic || len(proposal.Cells) != 1 {
		t.Fatal(proposal, err)
	}
	// A short sampling window judges nothing.
	v.TrafficSamples = 4*p.TrafficMinSamples - 1
	r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || r.Active {
		t.Fatal(r, err)
	}
	// Traffic ranks after room tiers.
	v.TrafficSamples = 100
	v.Rooms = append(v.Rooms, flooringRoom("bed", RoomRoleBedroom, "Soil", 20, 20))
	r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 2 || r.Deficits[0].Tier != FloorTierLiving || r.Deficits[1].Tier != FloorTierTraffic {
		t.Fatal(r, err)
	}
	v.Traffic[0].Terrain = "Lava"
	if _, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p); err == nil {
		t.Fatal("accepted a traffic cell on an unmeasured terrain")
	}
}

func TestDetectRoutineRanksTrafficFlooringLast(t *testing.T) {
	f := stableRoutine()
	v := FlooringObservation{Rooms: []FloorRoom{flooringRoom("done", RoomRoleKitchen, "WoodPlankFloor", 40, 40)}, Terrains: flooringTerrains(), TrafficSamples: 100}
	v.Traffic = []TrafficCell{{Cell: domain.Cell{X: 1, Z: 1}, Samples: 40, Terrain: "Soil", Home: true}}
	f.Upkeep.Flooring = domain.Known(v)
	r := needs(t, f, RoutineLatches{})
	found := false
	for _, g := range r.Goals {
		if g.ID == MaintainFlooring {
			found = true
			// The lowest goal rank is 4; a traffic deficit must not overflow it.
			if g.Priority != 4 {
				t.Fatal(g)
			}
		}
	}
	if !found {
		t.Fatal(r.Goals)
	}
}
