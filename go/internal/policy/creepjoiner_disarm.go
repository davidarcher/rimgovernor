package policy

import (
	"fmt"
	"slices"
)

// Disarm arrested creepjoiners by installing and removing replacements for
// removable melee-tool body parts. The decompiled game rules require:
//
//   - A natural Hand or Jaw is offered no removal: Recipe_RemoveBodyPart
//     offers a part only when it carries an added part, is clean and its def
//     spawns a thing on removal (neither body part def does), carries a bad
//     visible condition or is forceAlwaysRemovable.
//   - Recipe_InstallArtificialBodyPart lifts that: installing over a natural
//     part is no violation on a pawn of the bill doer's faction or of none
//     (IsViolationOnPawn), and the added part then makes the part removable
//     (HasDirectlyAddedPartFor). Removing it leaves the part missing.
//   - A pawn's melee tools are the race's ThingDef.tools, each linked to a
//     body part group; the tool whose group is ensureLinkedBodyPartsGroupAlwaysUsable
//     (the head) works whatever is missing. Every other tool needs a part
//     carrying its group, so a pawn missing all of those can only use that one.
//
// So the sites are, per race, the part that holds each other tool's group (the
// lowest part that contains every part carrying it), and the work per site is
// the cheapest install that is no violation, then its removal. Defs name
// nothing here: the sites come from the body and tools rows, the prices from
// the recipe rows.

// DisarmSite is one body part of a race whose absence silences a melee tool.
type DisarmSite struct {
	// Index is the part's index in BodyDef.AllParts (the game's pre-order),
	// Part its BodyPartDef, Ancestors the indexes of its ancestors.
	Index     int
	Part      string
	Ancestors []int
}

// CreepJoinerDisarm is the def-sourced input of the disarming: the sites of
// each pawn kind's race body and the price of each install recipe's item.
type CreepJoinerDisarm struct {
	Sites map[string][]DisarmSite
	// InstallValue is the market value of the items an install recipe consumes
	// by name (its single-def ingredient slots); the medicine is priced by the
	// operation. A recipe with no entry has no known price.
	InstallValue map[string]float64
}

// DisarmOrder is one surgery to queue on an arrested creepjoiner: an install
// of the cheapest prosthetic on a site, or the removal of the prosthetic on it.
type DisarmOrder struct {
	Pawn   PawnID
	Recipe string
	Part   int
	Remove bool
}

// DisarmWork is one review's disarming work: the surgeries to queue, one per
// prisoner, and why a creepjoiner prisoner could not be worked on (plain
// English).
type DisarmWork struct {
	Orders  []DisarmOrder
	Waiting []string
}

// Orders finds the disarming the colony owes its arrested creepjoiners. A
// prisoner known to be a creepjoiner with no surgery bill queued is worked
// site by site: a site whose part (or an ancestor) is missing is done; one
// carrying an added part is ordered the removal the game offers; a natural one
// is ordered the cheapest install the game offers that is no violation, has an
// eligible doctor and stocked ingredients. Removals go first, then sites in
// index order; one bill per prisoner at a time. Unread facts leave a prisoner
// unworked with the reason in Waiting.
func (d CreepJoinerDisarm) Orders(prisoners []PrisonerFacts) DisarmWork {
	var work DisarmWork
	for _, p := range sortedPrisoners(prisoners) {
		joiner, known := p.CreepJoiner.Value()
		if !known {
			work.Waiting = append(work.Waiting, fmt.Sprintf("prisoner %s cannot be disarmed: its creepjoiner tracker could not be read", p.Pawn))
			continue
		}
		if !joiner {
			continue
		}
		if dead, dk := p.Dead.Value(); dk && dead {
			continue
		}
		order, why := d.order(p)
		if why != "" {
			work.Waiting = append(work.Waiting, fmt.Sprintf("prisoner %s cannot be disarmed: %s", p.Pawn, why))
			continue
		}
		if order != nil {
			work.Orders = append(work.Orders, *order)
		}
	}
	return work
}

func sortedPrisoners(rows []PrisonerFacts) []PrisonerFacts {
	out := slices.Clone(rows)
	slices.SortFunc(out, func(a, b PrisonerFacts) int {
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

// order is the next surgery on one creepjoiner prisoner: nil with no reason
// when nothing is owed or a bill is already queued, a reason when something
// is owed and cannot be ordered.
func (d CreepJoinerDisarm) order(p PrisonerFacts) (*DisarmOrder, string) {
	sites := d.Sites[p.Kind]
	if len(sites) == 0 {
		return nil, fmt.Sprintf("the game gives pawn kind %q no melee tool part to remove", p.Kind)
	}
	queued, qk := p.QueuedSurgeries.Value()
	ops, ok := p.Operations.Value()
	missing, mk := p.MissingParts.Value()
	if !qk || !ok || !mk {
		return nil, "its surgery facts could not be read"
	}
	if queued > 0 {
		return nil, ""
	}
	var installs []DisarmOrder
	var why string
	for _, site := range sites {
		if siteGone(site, missing) {
			continue
		}
		if op, found := removalOn(site, ops); found {
			recipe, _ := op.Recipe.Value()
			if reason := surgeryBlocked(op); reason != "" {
				why = fmt.Sprintf("removing the %s on its %s: %s", addedPartName(op), site.Part, reason)
				continue
			}
			return &DisarmOrder{Pawn: PawnID(p.Pawn), Recipe: recipe, Part: site.Index, Remove: true}, ""
		}
		install, reason := d.cheapestInstall(PawnID(p.Pawn), site, ops)
		if reason != "" {
			why = reason
			continue
		}
		installs = append(installs, install)
	}
	if len(installs) > 0 {
		return &installs[0], ""
	}
	return nil, why
}

// siteGone: the site's part or one of its ancestors is missing.
func siteGone(site DisarmSite, missing []MissingPart) bool {
	for _, m := range missing {
		if index, known := m.PartIndex.Value(); known && (index == site.Index || slices.Contains(site.Ancestors, index)) {
			return true
		}
	}
	return false
}

// removalOn is the removal the game offers of an added part on the site.
func removalOn(site DisarmSite, ops []SurgeryOperation) (SurgeryOperation, bool) {
	for _, op := range ops {
		if index, known := op.PartIndex.Value(); !known || index != site.Index || op.Kind != SurgeryAmputate {
			continue
		}
		if added, known := op.AddedPart.Value(); known && added != "" {
			if _, rk := op.Recipe.Value(); rk {
				return op, true
			}
		}
	}
	return SurgeryOperation{}, false
}

func addedPartName(op SurgeryOperation) string {
	name, _ := op.AddedPart.Value()
	return name
}

// surgeryBlocked says why an offered operation cannot be queued now, "" when
// it can: a violation or an unread one, no eligible doctor, ingredients not
// on the map.
func surgeryBlocked(op SurgeryOperation) string {
	switch violation, known := op.Violation.Value(); {
	case !known:
		return "whether it is a violation could not be read"
	case violation:
		return "the game counts it a violation"
	}
	if doctors, known := op.EligibleDoctors.Value(); !known || doctors == 0 {
		return "no eligible doctor"
	}
	if stocked, known := op.IngredientsOnMap.Value(); !known || !stocked {
		return "its ingredients are not on the map"
	}
	return ""
}

// cheapestInstall is the install on the site with the least item and medicine
// value among the offered ones that can be queued, else the reason.
func (d CreepJoinerDisarm) cheapestInstall(pawn PawnID, site DisarmSite, ops []SurgeryOperation) (DisarmOrder, string) {
	var best DisarmOrder
	bestCost, found := 0.0, false
	why := fmt.Sprintf("the game offers no install on its %s", site.Part)
	for _, op := range ops {
		if index, known := op.PartIndex.Value(); !known || index != site.Index || op.Kind != SurgeryInstall {
			continue
		}
		recipe, rk := op.Recipe.Value()
		if !rk {
			continue
		}
		if reason := surgeryBlocked(op); reason != "" {
			why = fmt.Sprintf("installing %s on its %s: %s", recipe, site.Part, reason)
			continue
		}
		items, priced := d.InstallValue[recipe]
		if !priced {
			why = fmt.Sprintf("installing %s on its %s: the recipe has no known price", recipe, site.Part)
			continue
		}
		medicine, _ := op.MedicineValue.Value()
		cost := items + medicine
		if !found || cost < bestCost || cost == bestCost && recipe < best.Recipe {
			best, bestCost, found = DisarmOrder{Pawn: pawn, Recipe: recipe, Part: site.Index}, cost, true
		}
	}
	if !found {
		return DisarmOrder{}, why
	}
	return best, ""
}
