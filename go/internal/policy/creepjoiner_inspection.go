package policy

import (
	"encoding/json"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Surgical inspection of a creepjoiner (#1740, epic #1694). A creepjoiner's
// downside is hidden at arrival; a surgical inspection reveals crumbling
// mind, organ decay and psychic agony early (the hediffs become visible) and
// nothing else (https://rimworldwiki.com/wiki/Doctoring#Surgical_inspection).
// The game records no "inspected" flag on the pawn: a clean inspection leaves
// a letter, a temporary Anesthetic hediff and a small surgical cut. So the
// colony keeps its own record, in ManageCreepJoiners's goal record (the save's
// GovernorState blob, persistence-contracts.md): the pawns it ordered an
// inspection of, and those whose inspection bill has ended.

// InspectionStage is where the colony's inspection of one creepjoiner is.
type InspectionStage string

const (
	// InspectionOrdered: the inspection bill was queued; it has not ended.
	InspectionOrdered InspectionStage = "ordered"
	// InspectionDone: the bill has ended. The colony inspected the pawn.
	InspectionDone InspectionStage = "done"
)

// CreepJoinerRecord is ManageCreepJoiners's goal record: the colony's
// inspection of each creepjoiner it has ordered one for.
type CreepJoinerRecord struct {
	Inspections map[PawnID]InspectionStage `json:"inspections,omitempty"`
}

// ParseCreepJoinerRecord reads a goal record; an empty record is empty.
func ParseCreepJoinerRecord(raw string) (CreepJoinerRecord, error) {
	var r CreepJoinerRecord
	if raw == "" {
		return r, nil
	}
	if err := json.Unmarshal([]byte(raw), &r); err != nil {
		return CreepJoinerRecord{}, fmt.Errorf("creepjoiner record: %w", err)
	}
	for pawn, stage := range r.Inspections {
		if pawn == "" || stage != InspectionOrdered && stage != InspectionDone {
			return CreepJoinerRecord{}, fmt.Errorf("creepjoiner record: pawn %q has inspection stage %q", pawn, stage)
		}
	}
	return r, nil
}

// Encode is the record as stored on the goal; an empty record encodes "".
func (r CreepJoinerRecord) Encode() string {
	if len(r.Inspections) == 0 {
		return ""
	}
	data, _ := json.Marshal(r) // a map of strings always encodes
	return string(data)
}

// InspectionRecipes are the recipe def names whose worker is the game's
// surgical inspection (RecipeDef.workerClass, read from the def mirror).
type InspectionRecipes map[string]bool

// InspectionOrder is one creepjoiner to queue a surgical inspection on.
type InspectionOrder struct {
	Pawn   PawnID
	Recipe string
	Part   int
}

// Inspections is one review's inspection work: the orders to queue, the
// record the review leaves (orders become ordered, ended bills become done,
// pawns no longer in the colony drop out), and why a creepjoiner awaiting an
// inspection cannot be ordered one (plain English, one per pawn).
type Inspections struct {
	Orders  []InspectionOrder
	Record  CreepJoinerRecord
	Waiting []string
}

// Owed reports whether the review has inspection work: an order to queue or
// a record that changed from was.
func (i Inspections) Owed(was CreepJoinerRecord) bool {
	return len(i.Orders) > 0 || i.Record.Encode() != was.Encode()
}

// Inspections finds what the colony owes its creepjoiners. A creepjoiner
// needs an inspection while its downside is unrevealed (Revealed known
// false) and the record holds none for it. An available colonist with the
// inspection operation on offer, a doctor able to perform it and its
// ingredients on the map is ordered one; the operation is the offered one
// whose recipe is in recipes, on its lowest part index. A colonist whose
// downside or inspection operation cannot be read is not ordered (and says
// why in Waiting). An ordered inspection ends when no inspection bill is
// queued on the pawn any more.
func (d CreepJoinerDownsides) Inspections(hands []CreepJoinerHand, recipes InspectionRecipes, record CreepJoinerRecord) Inspections {
	out := Inspections{Record: CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{}}}
	present := map[PawnID]bool{}
	for _, h := range hands {
		present[h.Pawn] = true
	}
	for pawn, stage := range record.Inspections {
		if present[pawn] {
			out.Record.Inspections[pawn] = stage
		}
	}
	for _, h := range hands {
		if joiner, known := h.Facts.CreepJoiner.Value(); !known || joiner == nil {
			continue
		}
		stage := record.Inspections[h.Pawn]
		if stage == InspectionOrdered {
			if ended, known := h.inspectionEnded(recipes); known && ended {
				out.Record.Inspections[h.Pawn] = InspectionDone
			}
			continue
		}
		if stage != "" {
			continue
		}
		revealed, known := d.Revealed(h.Facts).Value()
		if !known {
			out.Waiting = append(out.Waiting, fmt.Sprintf("creepjoiner %s cannot be inspected: its downside could not be read", h.Pawn))
			continue
		}
		if revealed {
			continue
		}
		order, why := h.inspectionOrder(recipes)
		if why != "" {
			out.Waiting = append(out.Waiting, fmt.Sprintf("creepjoiner %s cannot be inspected: %s", h.Pawn, why))
			continue
		}
		out.Orders = append(out.Orders, order)
		out.Record.Inspections[h.Pawn] = InspectionOrdered
	}
	slices.SortFunc(out.Orders, func(a, b InspectionOrder) int {
		switch {
		case a.Pawn < b.Pawn:
			return -1
		case a.Pawn > b.Pawn:
			return 1
		}
		return 0
	})
	return out
}

// inspectionEnded is whether the pawn has no inspection bill queued; unknown
// while its bill stack cannot be read.
func (h CreepJoinerHand) inspectionEnded(recipes InspectionRecipes) (ended, known bool) {
	queued, qk := h.QueuedSurgeries.Value()
	switch {
	case !qk:
		return false, false
	case queued == 0:
		return true, true
	case h.QueuedRecipes == nil:
		return false, false
	}
	return !slices.ContainsFunc(h.QueuedRecipes, func(r string) bool { return recipes[r] }), true
}

// inspectionOrder picks the inspection operation to queue, or says in plain
// English why none can be.
func (h CreepJoinerHand) inspectionOrder(recipes InspectionRecipes) (InspectionOrder, string) {
	if len(recipes) == 0 {
		return InspectionOrder{}, "the game defines no surgical inspection recipe"
	}
	if available, known := h.Available.Value(); !known {
		return InspectionOrder{}, "its availability could not be read"
	} else if !available {
		return InspectionOrder{}, "it cannot take an order now"
	}
	if queued, known := h.inspectionQueued(recipes); !known {
		return InspectionOrder{}, "its queued surgery bills could not be read"
	} else if queued {
		return InspectionOrder{}, "an inspection bill is already queued"
	}
	ops, known := h.Operations.Value()
	if !known {
		return InspectionOrder{}, "its available operations could not be read"
	}
	var best *SurgeryOperation
	bestPart := 0
	for i, op := range ops {
		recipe, rk := op.Recipe.Value()
		if !rk || !recipes[recipe] {
			continue
		}
		part, pk := op.PartIndex.Value()
		if !pk {
			part = domain.NoSurgeryPart
		}
		if best == nil || part < bestPart {
			best, bestPart = &ops[i], part
		}
	}
	if best == nil {
		return InspectionOrder{}, "the game offers no surgical inspection on it"
	}
	if doctors, dk := best.EligibleDoctors.Value(); !dk || doctors == 0 {
		return InspectionOrder{}, "no eligible doctor"
	}
	if stocked, sk := best.IngredientsOnMap.Value(); !sk || !stocked {
		return InspectionOrder{}, "the inspection's ingredients are not on the map"
	}
	recipe, _ := best.Recipe.Value()
	return InspectionOrder{Pawn: h.Pawn, Recipe: recipe, Part: bestPart}, ""
}

func (h CreepJoinerHand) inspectionQueued(recipes InspectionRecipes) (queued, known bool) {
	ended, known := h.inspectionEnded(recipes)
	return !ended, known
}
