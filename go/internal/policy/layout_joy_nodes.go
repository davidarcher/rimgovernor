package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Joy outings in the station graph (#2997, child of #2949). A colonist's joy
// need falls while they are awake and rises by the sessions they take; the
// ones taken outdoors cross the base's exterior doors, so they are trips to the
// outside node like the day cycle's. The model, from the game (ilspycmd,
// RimWorld.Need_Joy, JoyUtility.JoyTickCheckEnd, JoyGiver and the JoyGiverDef,
// JobDef and NeedDef rows of the catalog):
//
//   - Need_Joy falls FallPerInterval a need interval (150 ticks) while the pawn
//     is not gaining joy, by the band of its level: 0.0015 from 0.3 to 0.85,
//     the band a colony that keeps its people Satisfied holds them in. The
//     NeedDef freezes the need asleep (freezeWhileSleeping), so the day's fall is
//     the awake hours of the schedule's template (scheduleTemplate) of the day.
//   - A session gains joyGainRate * 0.36 / 2500 a tick for joyDuration ticks
//     (JoyUtility.JoyTickCheckEnd): the JobDef's two numbers, scaled.
//   - A colonist picks a giver by its chance (JoyGiver.GetChance: baseChance),
//     and only the pctPawnsEverDo share of colonists ever do a giver.
//   - A giver is outdoors when its def says so (unroofedOnly,
//     requiresEnjoyOutdoors) or, for the walk, when its class does (the table).
//
// The sessions a day a colonist needs are the fall over the mean gain of a
// session; the outdoor ones are the chance-weighted share of the outdoor
// givers; each outdoor session is one trip out (the way back is counted where
// trips become door passages, as every edge's). One session is one outing: no
// judgment constant stands between them.
//
// Not modelled, so the outings are a lower bound: joy tolerance (a repeated
// kind gives less, so more sessions), the fall-rate stat, weather and night
// (EnjoyableOutsideNow), and the givers that need a building or an item (the
// recreation room's tables, the telescope, the horseshoe pin, chocolate, drugs;
// they wait on a rec room with stations of its own in the graph, which
// RecreationNode's indoor sessions are the demand of). An unread NeedDef, giver
// or job adds no flow: it is unknown, never zero.

// ReasonFlowJoyOuting is why a bed has joy trips to the outside node. The edge
// merges with the bed's day-cycle edge, which keeps its own reason.
const ReasonFlowJoyOuting = "flow_joy_outing"

// ReasonRecreation is why the graph holds the recreation node: the joy the
// colonists need a day.
const ReasonRecreation = "recreation_joy_need"

// JoyTable is the joy model's game numbers the defs do not carry, registered in
// DefTables so a renamed def fails the dangling-reference gate. IntervalTicks is
// Pawn_NeedsTracker's need interval, FallPerInterval Need_Joy's Satisfied-band
// fall, GainPerTick JoyUtility.JoyTickCheckEnd's 0.36 / 2500. OutdoorGivers are
// the JoyGiverDefs whose class is outdoors although no flag of the def says so:
// JoyGiver_GoForWalk picks only unroofed cells of an outdoor region.
type JoyTable struct {
	Need            string
	IntervalTicks   float64
	FallPerInterval float64
	GainPerTick     float64
	OutdoorGivers   []string
}

var joyGame = JoyTable{
	Need: "Joy", IntervalTicks: 150, FallPerInterval: 0.0015, GainPerTick: 0.36 / 2500,
	OutdoorGivers: []string{"GoForWalk"},
}

// JoyGame is the table the bridge reads the joy givers with.
func JoyGame() JoyTable { return joyGame }

// JoySource is one joy giver a colonist may use without a building: its chance
// (baseChance times the share of pawns that ever do it), the joy a session of
// its job gives before the game's per-tick scale (joyGainRate * joyDuration),
// and whether it is outdoors.
type JoySource struct {
	Giver   string
	Weight  float64
	Session float64
	Outdoor bool
}

// JoyModel is the catalog's joy givers. The zero value is unknown and leaves
// joy unsized.
type JoyModel struct {
	Sources []JoySource
}

// RecreationNode is the colony's joy demand: what a colonist needs of
// recreation a day and how it splits between the outdoors and the indoors. It is
// a layout fact, not a station; clustering and the embedder ignore it, and the
// indoor sessions are the demand a rec room's stations will serve.
type RecreationNode struct {
	// NeedPerDay is the joy a colonist loses awake in a day.
	NeedPerDay float64
	// Session is the mean joy a session gives; Sessions the sessions a colonist
	// takes a day.
	Session, Sessions float64
	// OutdoorShare is the chance-weighted share of sessions taken outdoors;
	// Outdoor and Indoor are the sessions a colonist takes a day of each.
	OutdoorShare, Outdoor, Indoor float64
	Reason                        string
}

// awakeShare is the share of the day a colonist is not on the Sleep block of
// the schedule template.
func awakeShare() float64 {
	slots := scheduleTemplate(TraitEffects{})
	awake := 0
	for _, s := range slots {
		if s != ScheduleSleep {
			awake++
		}
	}
	return float64(awake) / float64(len(slots))
}

// RecreationDemand is the joy demand the model implies; unknown without a
// source that gives joy.
func (m JoyModel) RecreationDemand() domain.Fact[RecreationNode] {
	var weight, gain, outdoor float64
	for _, s := range m.Sources {
		if !(s.Weight > 0) || !(s.Session > 0) || math.IsInf(s.Weight, 0) || math.IsInf(s.Session, 0) {
			continue
		}
		weight += s.Weight
		gain += s.Weight * s.Session * joyGame.GainPerTick
		if s.Outdoor {
			outdoor += s.Weight
		}
	}
	if weight <= 0 || gain <= 0 {
		return domain.Unknown[RecreationNode]()
	}
	need := joyGame.FallPerInterval * domain.TicksPerDay / joyGame.IntervalTicks * awakeShare()
	session := gain / weight
	sessions := need / session
	share := outdoor / weight
	return domain.Known(RecreationNode{
		NeedPerDay: need, Session: session, Sessions: sessions,
		OutdoorShare: share, Outdoor: sessions * share, Indoor: sessions * (1 - share), Reason: ReasonRecreation,
	})
}

// recreation adds the joy outings: each healthy sleeper's outdoor sessions a
// day, trips from their bed to the outside node, and returns the recreation node
// (unknown with the model).
func (b *graphBuilder) recreation() domain.Fact[RecreationNode] {
	demand := b.in.Catalog.Joy.RecreationDemand()
	node, known := demand.Value()
	if !known || b.in.Population.Colonists <= 0 {
		return demand
	}
	for _, n := range b.nodes {
		if n.Purpose == PurposeSleep {
			b.addEdge(n.ID, OutsideNode, node.Outdoor*float64(b.healthySleepers(n)), false, ReasonFlowJoyOuting)
		}
	}
	return demand
}
