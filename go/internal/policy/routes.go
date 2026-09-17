package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainRoutes keeps every colony facility reachable by the colonists
// who use it (issue #6 slice 5). It reasons only from observed travel: the
// native census answers, for each bed, work bench, storage, dining surface
// and defence emplacement, whether each mobile colonist can reach it from
// where that colonist stands right now (the game's own pathing with the
// doors that colonist may open) and what the game's chosen path costs. A
// facility no colonist reaches is a deficit, latched by facility until a
// measured census reads it reachable. The method opens the enclosing wall
// with a door on one of the breach cells native lists for it: a player wall
// on the facility room's border whose outer neighbour some colonist can
// stand on. Nothing here flood-fills or measures straight lines.
const MaintainRoutes GoalID = "MaintainRoutes"

type RoutesPolicy struct {
	// Door is the definition admitted on a breach cell, Stuff its material.
	Door, Stuff string
	// MaxBreaches bounds the breach cells the planner previews per facility.
	MaxBreaches int
}

func DefaultRoutesPolicy() RoutesPolicy {
	return RoutesPolicy{Door: "Door", Stuff: "WoodLog", MaxBreaches: 4}
}

func (p RoutesPolicy) valid() bool {
	return foodID(p.Door) && foodID(p.Stuff) && p.MaxBreaches >= 1 && p.MaxBreaches <= 16
}

// RoutesObservation is the native routes census.
type RoutesObservation struct {
	// Pawns lists the mobile colonists measured; an empty census with no
	// pawns cannot declare anything unreachable.
	Pawns      []string
	Facilities []RouteFacility
	// Traffic lists the most-travelled cells observed since TrafficSince
	// with TrafficSamples samples in all.
	Traffic        []TrafficCell
	TrafficSamples uint32
	TrafficSince   domain.Fact[domain.Tick]
}
type RouteFacility struct {
	ID, Definition, Kind string
	Cell                 domain.Cell
	Room                 domain.Fact[string]
	Travel               []RouteTravel
	Breaches             []RouteBreach
}
type RouteTravel struct {
	Pawn      string
	Reachable bool
	// Cost and Cells are the game's own path for the reachable pair;
	// unknown when the census hit its measurement bound.
	Cost, Cells domain.Fact[int32]
}
type RouteBreach struct {
	Cell    domain.Cell
	Edifice string
	// Pending names the door already ordered on the wall cell, empty when
	// none.
	Pending  string
	Distance int32
}
type TrafficCell struct {
	Cell    domain.Cell
	Samples uint32
	Terrain string
	Home    bool
	// Pending names the floor already ordered on the cell, empty when none.
	Pending string
}

func (v RoutesObservation) Validate() error {
	if len(v.Pawns) > 32 || len(v.Facilities) > 256 || len(v.Traffic) > 256 {
		return errors.New("routes census exceeds bound")
	}
	pawns := map[string]bool{}
	for _, id := range v.Pawns {
		if !foodID(id) || pawns[id] {
			return errors.New("invalid routes pawn")
		}
		pawns[id] = true
	}
	ids := map[string]bool{}
	for _, f := range v.Facilities {
		if !foodID(f.ID) || ids[f.ID] || !foodID(f.Definition) || !foodID(f.Kind) || len(f.Breaches) > 16 {
			return errors.New("invalid routes facility")
		}
		ids[f.ID] = true
		if room, known := f.Room.Value(); known && !foodID(room) {
			return errors.New("invalid routes facility room")
		}
		seen := map[string]bool{}
		for _, t := range f.Travel {
			if !pawns[t.Pawn] || seen[t.Pawn] {
				return errors.New("invalid routes travel")
			}
			seen[t.Pawn] = true
			if cost, known := t.Cost.Value(); known && (cost < 0 || !t.Reachable) {
				return errors.New("invalid routes travel cost")
			}
			if cells, known := t.Cells.Value(); known && (cells < 0 || !t.Reachable) {
				return errors.New("invalid routes travel cells")
			}
		}
		cells := map[domain.Cell]bool{}
		for _, b := range f.Breaches {
			if !foodID(b.Edifice) || b.Pending != "" && !foodID(b.Pending) || b.Distance < 0 || cells[b.Cell] {
				return errors.New("invalid routes breach")
			}
			cells[b.Cell] = true
		}
	}
	cells := map[domain.Cell]bool{}
	for _, t := range v.Traffic {
		if !foodID(t.Terrain) || cells[t.Cell] || t.Pending != "" && !foodID(t.Pending) {
			return errors.New("invalid traffic cell")
		}
		cells[t.Cell] = true
	}
	return nil
}

// RouteDeficit is one facility no mobile colonist can reach.
type RouteDeficit struct {
	Facility RouteFacility
	// Breaches are the facility's unordered breach cells nearest first;
	// Pending counts the breach cells with a door already ordered.
	Breaches []RouteBreach
	Pending  int
}

// RoutesReview is the per-facility latch: Deficits lists every unreachable
// facility by kind precedence (storage and stockpiles first, then benches,
// dining, beds, defence) and then ID; Latched is the ID set persisted
// between reviews. Known is false when the census was unknown, in which
// case Latched repeats the previous latch and Deficits is empty.
type RoutesReview struct {
	Active   bool
	Known    bool
	Deficits []RouteDeficit
	Latched  []string
}

var routeKindOrder = map[string]int{"stockpile": 0, "storage": 0, "bench": 1, "dining": 2, "bed": 3, "defense": 4}

// ReviewRoutes measures every facility against the colonists' observed
// reachability. A facility is deficient when the census lists at least one
// mobile colonist and none of them reaches it; a colony with no mobile
// colonist cannot declare anything unreachable. There is no hysteresis:
// reachability is the game's own answer and does not flap. A latched
// facility whose border still carries an ordered door stays deficient until
// the door lands: a door frame is walkable, so the census reads the room
// reachable before the door stands, and releasing then would strand the
// open plan on a half-built wall (as flooring holds on an ordered cell).
func ReviewRoutes(fact domain.Fact[RoutesObservation], previous []string, p RoutesPolicy) (RoutesReview, error) {
	if !p.valid() {
		return RoutesReview{}, errors.New("invalid routes policy")
	}
	if len(previous) > 256 {
		return RoutesReview{}, errors.New("invalid routes latch")
	}
	v, known := fact.Value()
	if !known {
		latched := append([]string(nil), previous...)
		sort.Strings(latched)
		return RoutesReview{Active: len(latched) > 0, Latched: latched}, nil
	}
	if err := v.Validate(); err != nil {
		return RoutesReview{}, err
	}
	r := RoutesReview{Known: true}
	if len(v.Pawns) == 0 {
		return r, nil
	}
	held := map[string]bool{}
	for _, id := range previous {
		held[id] = true
	}
	for _, f := range v.Facilities {
		reached, ordered := false, false
		for _, t := range f.Travel {
			if t.Reachable {
				reached = true
				break
			}
		}
		for _, b := range f.Breaches {
			ordered = ordered || b.Pending != ""
		}
		if reached && !(held[f.ID] && ordered) {
			continue
		}
		// A facility already reachable waits for its ordered door only;
		// no second breach is proposed.
		d := RouteDeficit{Facility: f}
		for _, b := range f.Breaches {
			if b.Pending != "" {
				d.Pending++
				continue
			}
			if !reached {
				d.Breaches = append(d.Breaches, b)
			}
		}
		sort.SliceStable(d.Breaches, func(i, j int) bool { return d.Breaches[i].Distance < d.Breaches[j].Distance })
		r.Deficits = append(r.Deficits, d)
		r.Latched = append(r.Latched, f.ID)
	}
	sort.Slice(r.Deficits, func(i, j int) bool {
		a, b := r.Deficits[i].Facility, r.Deficits[j].Facility
		if routeKindOrder[a.Kind] != routeKindOrder[b.Kind] {
			return routeKindOrder[a.Kind] < routeKindOrder[b.Kind]
		}
		return a.ID < b.ID
	})
	sort.Strings(r.Latched)
	r.Active = len(r.Deficits) > 0
	return r, nil
}

type RoutesMethod string

const (
	RoutesUnknown         RoutesMethod = "unknown"
	RoutesNoMethod        RoutesMethod = "no_deficit"
	RoutesDoorPending     RoutesMethod = "route_door_pending"
	RoutesNoBreach        RoutesMethod = "route_no_breach"
	RoutesDoorUnavailable RoutesMethod = "route_door_unavailable"
	RoutesBuild           RoutesMethod = "open_route"
)

type RoutesProposal struct {
	Method RoutesMethod
	Key    domain.MethodID
	// Facility names the unreachable facility served; Definition and Stuff
	// the door; Breaches the candidate wall cells nearest first, each to be
	// validated by a native placement preview before one is admitted.
	Facility   string
	Definition string
	Stuff      string
	Breaches   []domain.Cell
}

// RoutesFacts is what SelectRoutesMethod needs beyond the review: the
// planning census row for the door definition.
type RoutesFacts struct {
	DoorAvailable domain.Fact[bool]
}

// SelectRoutesMethod resolves the first deficit that has a breach to open.
// A facility whose breaches are all ordered already waits for them; one
// with no breach at all (no player wall borders its room, or no colonist
// stands anywhere outside it) is reported so the deficit stays visible.
func SelectRoutesMethod(review RoutesReview, facts RoutesFacts, p RoutesPolicy) (RoutesProposal, error) {
	if !p.valid() {
		return RoutesProposal{}, errors.New("invalid routes policy")
	}
	if !review.Active {
		return RoutesProposal{Method: RoutesNoMethod}, nil
	}
	if !review.Known {
		return RoutesProposal{Method: RoutesUnknown}, nil
	}
	available, known := facts.DoorAvailable.Value()
	if !known {
		return RoutesProposal{Method: RoutesUnknown}, nil
	}
	if !available {
		return RoutesProposal{Method: RoutesDoorUnavailable}, nil
	}
	var deferred RoutesMethod
	for _, d := range review.Deficits {
		if len(d.Breaches) == 0 {
			next := RoutesNoBreach
			if d.Pending > 0 {
				next = RoutesDoorPending
			}
			if deferred == "" || deferred == RoutesNoBreach {
				deferred = next
			}
			continue
		}
		cells := make([]domain.Cell, 0, min(len(d.Breaches), p.MaxBreaches))
		for _, b := range d.Breaches[:min(len(d.Breaches), p.MaxBreaches)] {
			cells = append(cells, b.Cell)
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%s/%d,%d", d.Facility.ID, p.Door, p.Stuff, cells[0].X, cells[0].Z)))
		return RoutesProposal{Method: RoutesBuild, Key: domain.MethodID(fmt.Sprintf("routes-%x", digest[:12])), Facility: d.Facility.ID, Definition: p.Door, Stuff: p.Stuff, Breaches: cells}, nil
	}
	if deferred == "" {
		return RoutesProposal{Method: RoutesNoMethod}, nil
	}
	return RoutesProposal{Method: deferred}, nil
}
