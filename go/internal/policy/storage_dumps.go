package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The dump zones (#724, #1779): the worn-gear, rotten and corpse dumps are
// storage sites like the rest. Each is sited only while something waits for
// it (Needs) on the free outdoor 2x2 patch clear of living rooms nearest the
// general store, and its site room is the outdoor ground clear of living
// rooms, so a zone that a living room later crowds is moved back out.

// DumpStore is what the dump sites read: the things waiting for each dump
// and the room census that keeps them clear of living rooms. A nil DumpStore
// (census unknown) plans no dump.
type DumpStore struct {
	// Needs counts, per dump role (domain.DumpRoles), the things waiting for it.
	Needs map[string]int
	Rooms []Room
	// Anchor sites the dumps when no general store stands.
	Anchor domain.Cell
	// Incinerator is the planned incinerator room once its walls and door
	// stand (#1814); nil before.
	Incinerator *LayoutRoom
	// Planned is the layout plan's room ground (PlannedRoomGround): a dump
	// never takes it, standing room or not.
	Planned []domain.Cell
}

// DumpNeeds counts the things waiting for each dump (#724): poor stored
// apparel and the worn-out garments pawns will shed for the worn dump,
// spoiled items and rotting animal corpses for the rotten dump, humanlike
// corpses for the corpse dump, exposed fresh animal corpses for the fresh dump. An unknown census counts nothing.
func DumpNeeds(facts RoutineFacts) map[string]int {
	needs := map[string]int{}
	if gear, ok := facts.Gear.Value(); ok {
		if stored, ok := gear.Stored.Value(); ok {
			for _, row := range stored {
				if !row.Serviceable() {
					needs[domain.WornDumpRole] += row.Count
				}
			}
		}
		for _, pawn := range gear.Pawns {
			worn, _ := pawn.Apparel.Value()
			for _, a := range worn {
				if a.Condition < domain.GearHitPointFloor {
					needs[domain.WornDumpRole]++
				}
			}
		}
	}
	if waste, ok := facts.Waste.Value(); ok {
		for _, item := range waste {
			if item.State == WasteBuried {
				continue
			}
			switch {
			case item.Kind == "spoiled", item.Kind == "corpse" && item.CorpseOf == domain.CorpseAnimal && item.RotStage != domain.RotFresh:
				needs[domain.RottenDumpRole]++
			case item.Kind == "corpse" && item.CorpseOf == domain.CorpseAnimal && item.RotStage == domain.RotFresh && item.State == WasteExposed:
				needs[domain.FreshDumpRole]++
			case item.Kind == "corpse" && (item.CorpseOf == domain.CorpseColonist || item.CorpseOf == domain.CorpseStranger):
				needs[domain.CorpseDumpRole]++
			}
		}
	}
	return needs
}

// dumpAnchor is the cell the dump patches are sited nearest: the first warehouse
// cell, else the colony anchor.
func (r StorageRequest) dumpAnchor() domain.Cell {
	var store []domain.Cell
	for _, z := range r.Zones {
		if isWarehouseRole(z.Role) {
			store = append(store, z.Cells...)
		}
	}
	if store = stockpileSorted(store); len(store) > 0 {
		return store[0]
	}
	return r.Dumps.Anchor
}

// dumpSites is one keyed site per dump role with things waiting, but no fresh
// dump while the freezer's corpse shelf (shelved) stands. Candidates are the nearest free 2x2 patches
// (OutdoorDumpSites); the site room is every outdoor walkable cell clear of
// living rooms, taken or not.
func (r StorageRequest) dumpSites(shelved bool) []StockpileSite {
	d := r.Dumps
	if d == nil || len(d.Needs) == 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	var room []domain.Cell
	blocked := outdoorDumpBlocked(d.Rooms, dumpProtected(r.Protected, d.Planned))
	not := func(v bool) bool { return !v }
	for _, c := range r.Cells {
		if !blocked[c.Cell] && positive(c.Walkable) && positive(measured(c.Indoors, not)) && positive(measured(c.Roofed, not)) {
			room = append(room, c.Cell)
		}
	}
	// patchesNear is the nearest free patches to one anchor, read once each.
	byAnchor := map[domain.Cell][]Rectangle{}
	patchesNear := func(at domain.Cell) []Rectangle {
		if p, ok := byAnchor[at]; ok {
			return p
		}
		p, err := OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: at, Cells: r.Cells, Rooms: d.Rooms, Protected: dumpProtected(r.Protected, d.Planned), Width: 2, Height: 2})
		if err != nil {
			p = nil
		}
		byAnchor[at] = p
		return p
	}
	var out []StockpileSite
	for _, spec := range domain.DumpRoles() {
		if d.Needs[spec.Role] <= 0 || shelved && spec.Role == domain.FreshDumpRole {
			continue
		}
		at := r.dumpAnchor()
		site := StockpileSite{Role: spec.Role, Room: room, Filter: spec.Filter, Priority: spec.Priority, Keyed: true}
		for _, p := range patchesNear(at) {
			site.Candidates = append(site.Candidates, stockpileSorted(rectCells(p)))
		}
		out = append(out, site)
	}
	return out
}
