package policy

import (
	"cmp"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Foreign things in a reconciliation (#2268, epic #2241): the things on the
// room's ground that are not the colony's own (SiteCell.Things) are diffed
// against the plan by one obstruction policy: claim a ruin as wall where it
// fits the ring, else minify what packs, else deconstruct a building or cut an
// impassable plant, and move a haulable item. Natural rock and mineables stay
// with the dig path. A thing the policy refuses is a hold with a named reason;
// its cells wait like a cell being cleared. The kinds are operations only: the
// executor (#2269) maps each to an existing action, and the native placement
// preview stays the legality authority.

// ReconcileHold is a foreign thing the policy leaves standing and why:
// "ancient_danger", "casket" or "not_deconstructible".
type ReconcileHold struct {
	Cell   domain.Cell
	Def    string
	Reason string
}

// foreignWork is what the ground's foreign things owe: the items, the cells
// they block (cleared or held), the ring cells a claim keeps as wall and the
// holds.
type foreignWork struct {
	items   []reconcileItem
	blocked map[domain.Cell]bool
	claimed map[domain.Cell]bool
	holds   []ReconcileHold
}

// SlotPlantCuts is the cut wave of a furniture slot the native preview refused
// naming a plant (#2303): every plant, passable or not, on the slot's footprint
// or interaction cell that the refusal names by def (or any plant when it names
// only a category). A slot has no outdoor stand-in, so the plant is a foreign
// obstruction like an impassable one on a room's ground. The preview stays the
// legality authority: nothing is cut unless it refused on a plant.
func SlotPlantCuts(cells []SiteCell, piece InteriorPiece, blockers []PlacementBlocker) []ClearanceTarget {
	named := map[string]bool{}
	anyPlant := false
	for _, b := range blockers {
		if b.DefName != "" {
			named[b.DefName] = true
		} else if strings.EqualFold(b.Category, "plant") {
			anyPlant = true
		}
	}
	slot := map[domain.Cell]bool{}
	for _, c := range rectCells(piece.Rect) {
		slot[c] = true
	}
	if c, ok := piece.Interaction(); ok {
		slot[c] = true
	}
	var out []ClearanceTarget
	for _, sc := range cells {
		if !slot[sc.Cell] {
			continue
		}
		for _, t := range sc.Things {
			if t.Category != ThingPlant || t.ID == 0 || !named[t.Def] && !anyPlant {
				continue
			}
			out = append(out, ClearanceTarget{EntityID: t.LoadID(), DefName: t.Def, Minimum: sc.Cell, Maximum: sc.Cell, Class: "foreign", InHome: true, Designated: t.Has(FlagDesignated)})
		}
	}
	slices.SortStableFunc(out, func(a, b ClearanceTarget) int {
		return cmp.Or(cmp.Compare(a.Minimum.Z, b.Minimum.Z), cmp.Compare(a.Minimum.X, b.Minimum.X), cmp.Compare(a.EntityID, b.EntityID))
	})
	return out
}

func foreignThings(in ReconcileInput, ring Rectangle, wallDef string, doorWanted map[domain.Cell]bool) foreignWork {
	w := foreignWork{blocked: map[domain.Cell]bool{}, claimed: map[domain.Cell]bool{}}
	ground := roomGround(in.Room.Interior)
	type key struct {
		id   uint64
		def  string
		cell domain.Cell
	}
	index := map[key]int{}
	// place records t on c as an item of kind, widening the target of a thing
	// that spans cells.
	place := func(kind OpKind, t Thing, c domain.Cell, packable bool) {
		k := key{id: t.ID, def: t.Def}
		if t.ID == 0 {
			k.cell = c
		}
		w.blocked[c] = true
		if i, ok := index[k]; ok {
			tg := &w.items[i].target
			tg.Minimum = domain.Cell{X: min(tg.Minimum.X, c.X), Z: min(tg.Minimum.Z, c.Z)}
			tg.Maximum = domain.Cell{X: max(tg.Maximum.X, c.X), Z: max(tg.Maximum.Z, c.Z)}
			return
		}
		index[k] = len(w.items)
		w.items = append(w.items, reconcileItem{kind: kind, cell: c, ready: true, foreign: true, target: ClearanceTarget{
			EntityID: t.LoadID(), DefName: t.Def, Minimum: c, Maximum: c, Class: "foreign", Deconstructible: t.Has(FlagDeconstructible), Count: int64(t.Count),
			InHome: true, Designated: t.Has(FlagDesignated), Packable: packable, AncientDanger: t.Has(FlagAncientDanger),
		}})
	}
	hold := func(t Thing, c domain.Cell, reason string) {
		w.blocked[c] = true
		w.holds = append(w.holds, ReconcileHold{Cell: c, Def: t.Def, Reason: reason})
	}
	for _, sc := range in.Cells {
		c := sc.Cell
		if !inside(c, ground) {
			continue
		}
		claimCell := wallDef != "" && onRing(c, ring) && !doorWanted[c] && !in.Room.Outdoor && sc.ClaimableRuin() == wallDef && sc.RuinHold() == ""
		for _, t := range sc.Things {
			switch {
			case t.Category == ThingPlant:
				if t.Has(FlagImpassable) {
					place(OpCut, t, c, false)
				}
			case t.Category == ThingItem:
				if t.Has(FlagHaulable) && !t.Has(FlagForbidden) {
					place(OpHaulOut, t, c, false)
				}
			case t.Category == ThingBuilding && t.Faction != FactionPlayer && !t.Has(FlagBlueprint) && !t.Has(FlagFrame):
				switch {
				case t.Has(FlagAncientDanger):
					hold(t, c, "ancient_danger")
				case t.Building != nil && len(t.Building.Casket) > 0:
					hold(t, c, "casket")
				case claimCell && t.Has(FlagEdifice):
					// A claimed ruin stands as wall: nothing to raise or clear.
					was := w.blocked[c]
					w.claimed[c] = true
					place(OpClaim, t, c, false)
					w.blocked[c] = was
				case t.Has(FlagMinifiable):
					place(OpPack, t, c, true)
				case t.Has(FlagDeconstructible):
					place(OpFurnitureOut, t, c, false)
				case t.Has(FlagEdifice):
					// Natural rock or a mineable: the dig path's.
				default:
					hold(t, c, "not_deconstructible")
				}
			}
		}
	}
	slices.SortStableFunc(w.items, func(a, b reconcileItem) int {
		return cmp.Or(cmp.Compare(a.target.Minimum.Z, b.target.Minimum.Z), cmp.Compare(a.target.Minimum.X, b.target.Minimum.X), cmp.Compare(a.target.EntityID, b.target.EntityID))
	})
	return w
}
