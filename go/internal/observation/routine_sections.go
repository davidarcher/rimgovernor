package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineSections is a routine reading's decoded census as facts.Store
// holds it: one Held per section the bracket read, with the tick that
// section's reply described and the method that produced it. A section
// the source did not offer, or whose read left the fact unknown, has an
// empty Source and File skips it. planning_cells is filed apart from
// colony: an older native lists it in the colony reply, a current one
// serves it through observations_get_cells with its own tick (#354, #356).
type RoutineSections struct {
	Colony        facts.Held[ColonyProjection]
	PlanningCells facts.Held[PlanningCells]
	Population    facts.Held[bridge.PrisonerCensus]
	Research      facts.Held[policy.ResearchFacts]
	Pawns         facts.Held[RoutinePawns]
	Emergency     facts.Held[policy.EmergencyFacts]
	Rooms         facts.Held[policy.RoomObservation]
	// Served names the sections the reading took from the store instead
	// of reading (#360): they carry the held value and its as-of tick, and
	// File leaves them as they are.
	Served map[facts.Section]bool
}

// PlanningCells is the colony facts' planning window: the observed region
// and the cells read inside it.
type PlanningCells struct {
	Region policy.Rectangle
	Cells  []policy.SiteCell
}

// RoutinePawns is the colonists' routine pawn detail projected against the
// colony and emergency census: the work, medical and mood views and the
// armed count.
type RoutinePawns struct {
	Work    []policy.WorkPawn
	Medical []policy.CarePawn
	Mood    []policy.MoodPawn
	Armed   int64
}

// File puts every section the reading read into store under scope; a
// served section is already there.
func (r RoutineSections) File(store *facts.Store, scope facts.Scope) {
	if store == nil {
		return
	}
	file(store, scope, facts.Colony, r.Colony, r.Served)
	file(store, scope, facts.PlanningCells, r.PlanningCells, r.Served)
	file(store, scope, facts.Population, r.Population, r.Served)
	file(store, scope, facts.Research, r.Research, r.Served)
	file(store, scope, facts.Pawns, r.Pawns, r.Served)
	file(store, scope, facts.Emergency, r.Emergency, r.Served)
	file(store, scope, facts.Rooms, r.Rooms, r.Served)
}

func file[T any](store *facts.Store, scope facts.Scope, section facts.Section, held facts.Held[T], served map[facts.Section]bool) {
	if held.Source == "" || served[section] {
		return
	}
	facts.Put(store, scope, section, held)
}

// sections assembles the reading's sections once every wave lane has
// landed and the projection is built.
func (s *routineBracket) sections(projection ColonyProjection) RoutineSections {
	tick := int64(projection.Identity.Tick)
	out := RoutineSections{
		Colony:        facts.Held[ColonyProjection]{Value: projection, AsOf: tick, Complete: true, Source: "rimgovernor/observations_read_colony_facts"},
		PlanningCells: projection.Window,
		Served:        s.served,
	}
	if out.PlanningCells.Source == "" && projection.Cells != nil {
		out.PlanningCells = facts.Held[PlanningCells]{Value: PlanningCells{Region: projection.Region, Cells: projection.Cells}, AsOf: tick, Complete: true, Source: "rimgovernor/observations_read_colony_facts"}
	}
	if s.emergency.Context != nil {
		complete, known := s.emergency.Facts.ColonistsComplete.Value()
		out.Emergency = facts.Held[policy.EmergencyFacts]{Value: s.emergency.Facts, AsOf: s.emergencyTick, Complete: known && complete, Source: "rimgovernor/observations_read_status"}
	}
	if work, known := s.work.Value(); known {
		medical, _ := s.medical.Value()
		mood, _ := s.mood.Value()
		armed, _ := s.armed.Value()
		out.Pawns = facts.Held[RoutinePawns]{Value: RoutinePawns{Work: work, Medical: medical, Mood: mood, Armed: armed}, AsOf: s.pawnsTick, Complete: true, Source: "rimgovernor/observations_list_pawns"}
	}
	if s.population.Context != nil || s.served[facts.Population] {
		_, prisoners := s.population.Prisoners.Value()
		_, custody := s.population.Custody.Value()
		out.Population = facts.Held[bridge.PrisonerCensus]{Value: s.population, AsOf: s.populationTick, Complete: prisoners && custody, Source: "rimgovernor/observations_read_population"}
	}
	if research, known := s.research.Value(); known {
		out.Research = facts.Held[policy.ResearchFacts]{Value: research, AsOf: s.researchTick, Complete: true, Source: "rimgovernor/observations_read_research"}
	}
	if rooms, known := s.temperature.Value(); known {
		out.Rooms = facts.Held[policy.RoomObservation]{Value: rooms, AsOf: s.roomsTick, Complete: true, Source: "rimgovernor/observations_list_rooms"}
	}
	return out
}

// AsOf is the tick each filed section describes, keyed as the store keys
// them, for the review's journal row.
func (r RoutineSections) AsOf() map[facts.Section]int64 {
	out := map[facts.Section]int64{}
	add := func(section facts.Section, source string, tick int64) {
		if source != "" {
			out[section] = tick
		}
	}
	add(facts.Colony, r.Colony.Source, r.Colony.AsOf)
	add(facts.PlanningCells, r.PlanningCells.Source, r.PlanningCells.AsOf)
	add(facts.Population, r.Population.Source, r.Population.AsOf)
	add(facts.Research, r.Research.Source, r.Research.AsOf)
	add(facts.Pawns, r.Pawns.Source, r.Pawns.AsOf)
	add(facts.Emergency, r.Emergency.Source, r.Emergency.AsOf)
	add(facts.Rooms, r.Rooms.Source, r.Rooms.AsOf)
	return out
}
