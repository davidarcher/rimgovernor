package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsSections is a routine reading's decoded census as facts.Store
// holds it: one Held per section the bracket read, with the tick that
// section's reply described and the method that produced it. A section
// the source did not offer, or whose read left the fact unknown, has an
// empty Source and File skips it. planning_cells is filed apart from
// colony: an older native lists it in the colony reply, a current one
// serves it through observations_get_cells with its own tick.
type RoundsSections struct {
	Colony        facts.Held[ColonyProjection]
	PlanningCells facts.Held[PlanningCells]
	Population    facts.Held[bridge.PrisonerCensus]
	Research      facts.Held[policy.ResearchFacts]
	Pawns         facts.Held[RoundsPawns]
	Emergency     facts.Held[policy.EmergencyFacts]
	Rooms         facts.Held[policy.RoomObservation]
	Zones         facts.Held[bridge.ZonesRead]
	Ideology      facts.Held[policy.Ideoligion]
}

// PlanningCells is the colony facts' planning window: the observed region
// and the cells read inside it.
type PlanningCells struct {
	Region policy.Rectangle
	Cells  []policy.SiteCell
}

// RoundsPawns is the colonists' routine pawn detail projected against the
// colony and emergency census: the work, medical and mood views and the
// armed count.
type RoundsPawns struct {
	Work    []policy.WorkPawn
	Medical []policy.CarePawn
	Mood    []policy.MoodPawn
	Armed   int64
}

// File puts every section the reading read into store under scope.
func (r RoundsSections) File(store *facts.Store, scope facts.Scope) {
	if store == nil {
		return
	}
	file(store, scope, facts.Colony, r.Colony)
	file(store, scope, facts.PlanningCells, r.PlanningCells)
	file(store, scope, facts.Population, r.Population)
	file(store, scope, facts.Research, r.Research)
	file(store, scope, facts.Pawns, r.Pawns)
	file(store, scope, facts.Emergency, r.Emergency)
	file(store, scope, facts.Rooms, r.Rooms)
	file(store, scope, facts.Zones, r.Zones)
	file(store, scope, facts.Ideology, r.Ideology)
}

func file[T any](store *facts.Store, scope facts.Scope, section facts.Section, held facts.Held[T]) {
	if held.Source == "" {
		return
	}
	facts.Put(store, scope, section, held)
}

// roundsSections files the reading's sections: every one has the frame's
// tick, except the room census read beside it.
func roundsSections(frame bridge.RoundsFrame, projection ColonyProjection, rooms *o.RoomsSnapshot) RoundsSections {
	tick := frame.Context.GetTick()
	out := RoundsSections{
		Colony:        facts.Held[ColonyProjection]{Value: projection, AsOf: tick, Complete: true, Source: "rimgovernor/observations_read_colony_facts"},
		PlanningCells: projection.Window,
		Zones:         projection.Zones,
	}
	emergency := frame.Emergency.Facts
	complete, known := emergency.ColonistsComplete.Value()
	out.Emergency = facts.Held[policy.EmergencyFacts]{Value: emergency, AsOf: tick, Complete: known && complete, Source: "rimgovernor/observations_read_status"}
	if work, known := projection.WorkPawns.Value(); known {
		medical, _ := projection.Facts.MedicalPawns.Value()
		mood, _ := projection.Facts.MoodPawns.Value()
		armed, _ := projection.Facts.Armed.Value()
		out.Pawns = facts.Held[RoundsPawns]{Value: RoundsPawns{Work: work, Medical: medical, Mood: mood, Armed: armed}, AsOf: tick, Complete: true, Source: "rimgovernor/observations_list_pawns"}
	}
	if population := frame.Population; population != nil {
		_, prisoners := population.Prisoners.Value()
		_, custody := population.Custody.Value()
		out.Population = facts.Held[bridge.PrisonerCensus]{Value: *population, AsOf: tick, Complete: prisoners && custody, Source: "rimgovernor/observations_read_population"}
	}
	if research, known := projection.Facts.Research.Value(); known {
		out.Research = facts.Held[policy.ResearchFacts]{Value: research, AsOf: tick, Complete: true, Source: "rimgovernor/observations_read_research"}
	}
	if ideology, known := projection.Facts.Ideology.Value(); known {
		out.Ideology = facts.Held[policy.Ideoligion]{Value: ideology, AsOf: tick, Complete: true, Source: "rimgovernor/snapshot_frame_ideology"}
	}
	if census, known := projection.Rooms.Value(); known && rooms != nil {
		out.Rooms = facts.Held[policy.RoomObservation]{Value: census, AsOf: rooms.GetContext().GetTick(), Complete: true, Source: "rimgovernor/observations_list_rooms"}
	}
	return out
}
