package bridge

import (
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateDirectUpkeep(v *o.UpkeepFacts, size *o.MapSize, mapID int32) error {
	counts := map[string]int{"items": len(v.Items), "structures": len(v.Structures), "fires": len(v.Fires), "filth": len(v.Filth), "animals": len(v.Animals), "people": len(v.People) + len(v.Slaves) + len(v.Guests), "beds": len(v.Beds)}
	if v.HomeCoverage != nil {
		counts["home_coverage"] = 1
	}
	if v.Lighting != nil {
		counts["lighting"] = 1
	}
	if v.Flooring != nil {
		counts["flooring"] = 1
	}
	if v.Routes != nil {
		counts["routes"] = 1
	}
	for _, issue := range v.Issues {
		if counts[issue.GetField()] != 0 {
			return contract("unavailable upkeep section contains rows")
		}
	}
	number := func(p *float64) bool { return p == nil || !math.IsNaN(*p) && !math.IsInf(*p, 0) && *p >= 0 }
	seen := map[string]bool{}
	for _, row := range v.Items {
		if row == nil || !uniqueRef(row.Item, seen) || row.Count != nil && row.GetCount() < 0 || row.RotTicks != nil && row.GetRotTicks() < 0 ||
			!proto.Equal(row, &o.UpkeepItem{Item: row.Item, Count: row.Count, Roofed: row.Roofed, InStorage: row.InStorage, RotTicks: row.RotTicks, Forbidden: row.Forbidden}) {
			return contract("invalid upkeep item")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Structures {
		if row == nil || row.Building == nil {
			return contract("missing upkeep structure")
		}
		if !uniqueRef(row.Building, seen) || !proto.Equal(row, &o.UpkeepStructure{Building: row.Building, Home: row.Home}) {
			return contract("invalid upkeep structure")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Fires {
		if row == nil || !uniqueRef(row.Fire, seen) || !number(row.Size) || !proto.Equal(row, &o.FireState{Fire: row.Fire, Size: row.Size, Home: row.Home}) {
			return contract("invalid upkeep fire")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Filth {
		if row == nil || !uniqueRef(row.Filth, seen) || row.RoomRole != nil && validID(row.GetRoomRole()) != nil || !optionalRef(row.Room) || !proto.Equal(row, &o.FilthState{Filth: row.Filth, Home: row.Home, Thickness: row.Thickness, RoomRole: row.RoomRole, Room: row.Room}) {
			return contract("invalid upkeep filth")
		}
	}
	seen = map[string]bool{}
	finite := func(p *float64) bool { return p == nil || !math.IsNaN(*p) && !math.IsInf(*p, 0) }
	for _, row := range append(append(append([]*o.UpkeepPerson{}, v.People...), v.Slaves...), v.Guests...) {
		if row == nil || !uniqueRef(row.Pawn, seen) || !optionalRef(row.OwnedBed) || !finite(row.ComfortableMinC) || !finite(row.ComfortableMaxC) || !finite(row.TemperatureC) || row.ComfortableMinC != nil && row.ComfortableMaxC != nil && row.GetComfortableMinC() > row.GetComfortableMaxC() || !validRefs(row.Partners) || !validTitle(row) || !proto.Equal(row, &o.UpkeepPerson{Pawn: row.Pawn, OwnedBed: row.OwnedBed, ComfortableMinC: row.ComfortableMinC, ComfortableMaxC: row.ComfortableMaxC, TemperatureC: row.TemperatureC, Partners: row.Partners, BedSharingAllowed: row.BedSharingAllowed, RoyalTitle: row.RoyalTitle, Ascetic: row.Ascetic, Precepts: row.Precepts}) {
			return contract("invalid sleeping person")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Beds {
		if row == nil || !uniqueRef(row.Bed, seen) || !finite(row.TemperatureC) || !validRefs(row.Owners) || !validRefs(row.Users) || !validRefs(row.AccessibleTo) || !optionalRef(row.Room) || row.Quality != nil && validID(row.GetQuality()) != nil || row.Stuff != nil && validID(row.GetStuff()) != nil || !proto.Equal(row, &o.UpkeepBed{Bed: row.Bed, Medical: row.Medical, Prisoners: row.Prisoners, Roofed: row.Roofed, TemperatureC: row.TemperatureC, Owners: row.Owners, Users: row.Users, AccessibleTo: row.AccessibleTo, Room: row.Room, Quality: row.Quality, Stuff: row.Stuff, ForSlaves: row.ForSlaves}) {
			return contract("invalid upkeep bed")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Animals {
		if row == nil || !uniqueRef(row.Pawn, seen) || !optionalRef(row.SuitablePen) {
			return contract("invalid upkeep animal")
		}
		if row.RequiresPen != nil && !row.GetRequiresPen() && row.SuitablePen != nil || !proto.Equal(row, &o.AnimalFeed{Pawn: row.Pawn, RequiresPen: row.RequiresPen, SuitablePen: row.SuitablePen}) {
			return contract("conflicting upkeep animal fields")
		}
	}
	// A wild row is the factionless tame census: its diet only, never
	// feed, pen or ownership facts; the tame facts ride the table row.
	seen = map[string]bool{}
	for _, row := range v.WildAnimals {
		if row == nil || !uniqueRef(row.Pawn, seen) || row.RequiresPen != nil && row.GetRequiresPen() || !proto.Equal(row, &o.AnimalFeed{Pawn: row.Pawn, RequiresPen: row.RequiresPen}) {
			return contract("invalid wild animal")
		}
	}
	if v.HomeCoverage != nil {
		h := v.HomeCoverage.GetObserved()
		if h == nil {
			if err := validateUnavailable(v.HomeCoverage.GetUnavailable()); err != nil {
				return err
			}
		} else {
			if h.Revision == nil || h.GetRevision() < 0 {
				return contract("invalid Home coverage census")
			}
			seen := map[string]bool{}
			for _, row := range h.Targets {
				if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] || row.Snapshot != nil || row.ShapeToken != nil && validID(row.GetShapeToken()) != nil || !diagnostic(row.Blocker) || row.ExcludedCells != nil && row.MissingCells != nil && row.GetExcludedCells() > row.GetMissingCells() {
					return contract("invalid Home coverage target")
				}
				seen[row.GetId()] = true
				if g := row.ExtentGeometry; g != nil {
					if row.GetBlocker() != "" {
						return contract("invalid complete Home extent geometry")
					}
					geometryCells := map[[2]int32]bool{}
					for _, group := range [][]*c.Cell{g.EnclosedInterior, g.Corridor, g.Zone} {
						for _, cell := range group {
							key := [2]int32{cell.GetX(), cell.GetZ()}
							if !colonyCell(cell, size) || geometryCells[key] {
								return contract("invalid Home extent cell")
							}
							geometryCells[key] = true
						}
					}
				}
				cells := map[[2]int32]bool{}
				for _, cell := range row.Cells {
					if !colonyCell(cell, size) {
						return contract("invalid Home coverage cell")
					}
					key := [2]int32{cell.GetX(), cell.GetZ()}
					if cells[key] {
						return contract("duplicate Home cell")
					}
					cells[key] = true
				}
				if len(row.Cells) > 0 && row.MissingCells != nil && row.GetMissingCells() > uint32(len(row.Cells)) {
					return contract("Home deficit exceeds footprint")
				}
			}
		}
	}
	if v.Lighting != nil {
		if err := validateLighting(v.Lighting, size, mapID); err != nil {
			return err
		}
	}
	if v.Flooring != nil {
		if err := validateFlooring(v.Flooring, size); err != nil {
			return err
		}
	}
	if v.Routes != nil {
		if err := validateRoutes(v.Routes, size, mapID); err != nil {
			return err
		}
	}
	return nil
}

// validTitle checks a person's royal title inputs: the title and each precept
// are definition names; the title's requirements are its catalog row.
func validTitle(p *o.UpkeepPerson) bool {
	if p.RoyalTitle != nil && validID(p.GetRoyalTitle()) != nil {
		return false
	}
	for _, precept := range p.Precepts {
		if validID(precept) != nil {
			return false
		}
	}
	return true
}

// validateRoutes checks the routes section: unique facility refs at in-map
// cells, each travel row naming a listed pawn once with non-negative path
// numbers only when reachable (path_skipped only on a reachable row with none), breach cells unique and in the map, traffic
// cells unique and in the map.
func validateRoutes(section *o.RoutesSection, size *o.MapSize, mapID int32) error {
	f := section.GetObserved()
	if f == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	if !proto.Equal(f, &o.RoutesFacts{Facilities: f.Facilities, PawnIds: f.PawnIds, Traffic: f.Traffic, TrafficSamples: f.TrafficSamples, TrafficSinceTick: f.TrafficSinceTick}) || f.TrafficSinceTick != nil && f.GetTrafficSinceTick() < 0 {
		return contract("invalid routes facts")
	}
	pawns := map[string]bool{}
	for _, id := range f.PawnIds {
		if validID(id) != nil || pawns[id] {
			return contract("invalid routes pawn")
		}
		pawns[id] = true
	}
	seen := map[string]bool{}
	for _, row := range f.Facilities {
		if row == nil || !uniqueRef(row.Facility, seen) || RouteKindName(row.GetKind()) == "" || !colonyCell(row.Cell, size) || !optionalRef(row.Room) || !proto.Equal(row, &o.RouteFacility{Facility: row.Facility, Kind: row.Kind, Cell: row.Cell, Room: row.Room, Travel: row.Travel, Breaches: row.Breaches}) {
			return contract("invalid routes facility")
		}
		travelled := map[string]bool{}
		for _, t := range row.Travel {
			if t == nil || !pawns[t.GetPawnId()] || travelled[t.GetPawnId()] || t.PathCost != nil && (t.GetPathCost() < 0 || !t.GetReachable()) || t.PathCells != nil && (t.GetPathCells() < 0 || !t.GetReachable()) || t.PathSkipped != nil && (!t.GetReachable() || t.PathCost != nil || t.PathCells != nil) || !proto.Equal(t, &o.RouteTravel{PawnId: t.PawnId, Reachable: t.Reachable, PathCost: t.PathCost, PathCells: t.PathCells, PathSkipped: t.PathSkipped}) {
				return contract("invalid routes travel")
			}
			travelled[t.GetPawnId()] = true
		}
		cells := map[[2]int32]bool{}
		for _, b := range row.Breaches {
			if b == nil || !colonyCell(b.Cell, size) || b.Edifice == nil || validID(b.GetEdifice()) != nil || b.Pending != nil && validID(b.GetPending()) != nil || b.Distance != nil && b.GetDistance() < 0 || !proto.Equal(b, &o.RouteBreach{Cell: b.Cell, Edifice: b.Edifice, Pending: b.Pending, Distance: b.Distance}) {
				return contract("invalid routes breach")
			}
			key := [2]int32{b.Cell.GetX(), b.Cell.GetZ()}
			if cells[key] {
				return contract("routes breaches overlap")
			}
			cells[key] = true
		}
	}
	// A cell appears once per traffic layer.
	cells := map[[3]int32]bool{}
	for _, t := range f.Traffic {
		if t == nil || !colonyCell(t.Cell, size) || t.Layer < o.TrafficLayer_TRAFFIC_LAYER_COLONIST || t.Layer > o.TrafficLayer_TRAFFIC_LAYER_HOSTILE || t.Terrain != nil && validID(t.GetTerrain()) != nil || t.Pending != nil && validID(t.GetPending()) != nil || !proto.Equal(t, &o.TrafficCell{Cell: t.Cell, Samples: t.Samples, Terrain: t.Terrain, Home: t.Home, Pending: t.Pending, Layer: t.Layer}) {
			return contract("invalid traffic cell")
		}
		key := [3]int32{int32(t.Layer), t.Cell.GetX(), t.Cell.GetZ()}
		if cells[key] {
			return contract("traffic cells overlap")
		}
		cells[key] = true
	}
	return nil
}

// validateFlooring checks the flooring section: unique rooms of unique
// in-map cells, each naming a terrain (the catalog prices it).
func validateFlooring(section *o.FlooringSection, size *o.MapSize) error {
	f := section.GetObserved()
	if f == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	rooms := map[string]bool{}
	cells := map[[2]int32]bool{}
	for _, room := range f.Rooms {
		if room == nil || validID(room.GetRoom().GetId()) != nil || rooms[room.GetRoom().GetId()] || room.Role != nil && validID(room.GetRole()) != nil || !proto.Equal(room, &o.FloorRoom{Room: room.Room, Role: room.Role, Cells: room.Cells}) {
			return contract("invalid flooring room")
		}
		rooms[room.GetRoom().GetId()] = true
		for _, cell := range room.Cells {
			if cell == nil || !colonyCell(cell.Cell, size) || cell.Terrain == nil || validID(cell.GetTerrain()) != nil || cell.Pending != nil && validID(cell.GetPending()) != nil || !proto.Equal(cell, &o.FloorCell{Cell: cell.Cell, Terrain: cell.Terrain, Pending: cell.Pending}) {
				return contract("invalid flooring cell")
			}
			key := [2]int32{cell.Cell.GetX(), cell.Cell.GetZ()}
			if cells[key] {
				return contract("flooring cells overlap")
			}
			cells[key] = true
		}
	}
	return nil
}

// validateLighting checks the lighting section: work cells are unique bench
// refs at in-map cells with a unit glow, lamps are unique building states
// carrying only the service detail the power census also allows.
func validateLighting(section *o.LightingSection, size *o.MapSize, mapID int32) error {
	l := section.GetObserved()
	if l == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	benches := map[string]bool{}
	for _, row := range l.WorkCells {
		if row == nil || !uniqueRef(row.Bench, benches) || !colonyCell(row.Cell, size) || !optionalRef(row.Room) || !proto.Equal(row, &o.WorkLightCell{Bench: row.Bench, Cell: row.Cell, Glow: row.Glow, Roofed: row.Roofed, Room: row.Room, PlantDefs: row.PlantDefs}) {
			return contract("invalid lighting work cell")
		}
		if row.Glow != nil && (math.IsNaN(row.GetGlow()) || row.GetGlow() < 0 || row.GetGlow() > 1) {
			return contract("invalid lighting glow")
		}
	}
	lamps := map[string]bool{}
	for _, row := range l.Lamps {
		if row == nil || !uniqueRef(row.Building, lamps) || !optionalRef(row.Room) || !proto.Equal(row, &o.LampState{Building: row.Building, Lit: row.Lit, Room: row.Room}) {
			return contract("invalid lighting lamp")
		}
	}
	return nil
}
