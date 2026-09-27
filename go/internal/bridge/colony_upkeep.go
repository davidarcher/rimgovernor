package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateDirectUpkeep(v *o.UpkeepFacts, size *o.MapSize, mapID int32) error {
	counts := map[string]int{"items": len(v.Items), "structures": len(v.Structures), "fires": len(v.Fires), "filth": len(v.Filth), "animals": len(v.Animals), "people": len(v.People), "beds": len(v.Beds)}
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
	if v.Rooms != nil {
		counts["rooms"] = 1
	}
	for _, issue := range v.Issues {
		if counts[issue.GetField()] != 0 {
			return contract("unavailable upkeep section contains rows")
		}
	}
	entity := func(e *o.EntityRef, seen map[string]bool) bool {
		if e == nil || validID(e.GetId()) != nil || validID(e.GetDefName()) != nil || e.MapId == nil || e.GetMapId() != mapID || !colonyCell(e.Position, size) || e.Label != nil || e.Snapshot != nil || seen[e.GetId()] {
			return false
		}
		seen[e.GetId()] = true
		return true
	}
	number := func(p *float64) bool { return p == nil || !math.IsNaN(*p) && !math.IsInf(*p, 0) && *p >= 0 }
	seen := map[string]bool{}
	for _, row := range v.Items {
		if row == nil || !entity(row.Item, seen) || !number(row.DeteriorationRate) || !number(row.BaseDeteriorationRate) || row.Count != nil && row.GetCount() < 0 || row.RotTicks != nil && row.GetRotTicks() < 0 ||
			!proto.Equal(row, &o.UpkeepItem{Item: row.Item, Count: row.Count, Roofed: row.Roofed, InStorage: row.InStorage, DeteriorationRate: row.DeteriorationRate, BaseDeteriorationRate: row.BaseDeteriorationRate, RotTicks: row.RotTicks, Perishable: row.Perishable, Forbidden: row.Forbidden, Medicine: row.Medicine}) {
			return contract("invalid upkeep item")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Structures {
		if row == nil || row.Building == nil {
			return contract("missing upkeep structure")
		}
		b := row.Building
		if !entity(b.Building, seen) || b.HitPoints != nil && b.GetHitPoints() < 0 || b.MaxHitPoints != nil && b.GetMaxHitPoints() < 0 || b.HitPoints != nil && b.MaxHitPoints != nil && b.GetHitPoints() > b.GetMaxHitPoints() || row.RepairPriority != nil && (row.GetRepairPriority() < 0 || row.GetRepairPriority() > 2) ||
			!proto.Equal(b, &o.BuildingState{Building: b.Building, HitPoints: b.HitPoints, MaxHitPoints: b.MaxHitPoints}) || !number(row.Flammability) || !proto.Equal(row, &o.UpkeepStructure{Building: b, Home: row.Home, RepairPriority: row.RepairPriority, Flammability: row.Flammability}) {
			return contract("invalid upkeep structure")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Fires {
		if row == nil || !entity(row.Fire, seen) || !number(row.Size) || !proto.Equal(row, &o.FireState{Fire: row.Fire, Size: row.Size, Home: row.Home}) {
			return contract("invalid upkeep fire")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Filth {
		if row == nil || !entity(row.Filth, seen) || row.RoomRole != nil && validID(row.GetRoomRole()) != nil || row.RoomId != nil && validID(row.GetRoomId()) != nil || !proto.Equal(row, &o.FilthState{Filth: row.Filth, Home: row.Home, Thickness: row.Thickness, RoomRole: row.RoomRole, RoomId: row.RoomId}) {
			return contract("invalid upkeep filth")
		}
	}
	seen = map[string]bool{}
	finite := func(p *float64) bool { return p == nil || !math.IsNaN(*p) && !math.IsInf(*p, 0) }
	ids := func(values []string) bool {
		found := map[string]bool{}
		for _, id := range values {
			if validID(id) != nil || found[id] {
				return false
			}
			found[id] = true
		}
		return true
	}
	for _, row := range v.People {
		if row == nil || row.Pawn == nil || !entity(row.Pawn.Pawn, seen) || !proto.Equal(row.Pawn, &o.PawnState{Pawn: row.Pawn.Pawn}) || row.OwnedBedId != nil && row.GetOwnedBedId() != "" && validID(row.GetOwnedBedId()) != nil || !finite(row.ComfortableMinC) || !finite(row.ComfortableMaxC) || !finite(row.TemperatureC) || row.ComfortableMinC != nil && row.ComfortableMaxC != nil && row.GetComfortableMinC() > row.GetComfortableMaxC() || !ids(row.PartnerIds) || !validTitle(row.Title) || !proto.Equal(row, &o.UpkeepPerson{Pawn: row.Pawn, OwnedBedId: row.OwnedBedId, ComfortableMinC: row.ComfortableMinC, ComfortableMaxC: row.ComfortableMaxC, TemperatureC: row.TemperatureC, PartnerIds: row.PartnerIds, BedSharingAllowed: row.BedSharingAllowed, Title: row.Title}) {
			return contract("invalid sleeping person")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Beds {
		if row == nil || !entity(row.Bed, seen) || row.Slots != nil && row.GetSlots() > 256 || !finite(row.RestEffectiveness) || !finite(row.TemperatureC) || !ids(row.Owners) || !ids(row.Users) || !ids(row.AccessibleTo) || row.RoomId != nil && validID(row.GetRoomId()) != nil || row.Quality != nil && validID(row.GetQuality()) != nil || row.Stuff != nil && validID(row.GetStuff()) != nil || !proto.Equal(row, &o.UpkeepBed{Bed: row.Bed, Slots: row.Slots, Humanlike: row.Humanlike, RestEffectiveness: row.RestEffectiveness, Medical: row.Medical, Prisoners: row.Prisoners, Roofed: row.Roofed, TemperatureC: row.TemperatureC, Owners: row.Owners, Users: row.Users, AccessibleTo: row.AccessibleTo, RoomId: row.RoomId, Quality: row.Quality, Stuff: row.Stuff}) {
			return contract("invalid upkeep bed")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Animals {
		if row == nil || row.Pawn == nil || !entity(row.Pawn.Pawn, seen) || row.Pawn.AnimalState == nil || row.Diet != nil && validID(row.GetDiet()) != nil || row.SuitablePenId != nil && validID(row.GetSuitablePenId()) != nil || !ids(row.ReachableBenchIds) {
			return contract("invalid upkeep animal")
		}
		for _, zone := range row.ReachableStorage {
			if zone == nil || zone.ZoneId == nil || validID(zone.GetZoneId()) != nil || !ids(zone.Accepts) || !proto.Equal(zone, &o.AnimalFeedStorage{ZoneId: zone.ZoneId, Accepts: zone.Accepts}) {
				return contract("invalid upkeep animal storage")
			}
		}
		for _, cell := range row.StorageCandidates {
			if cell == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
				return contract("invalid upkeep animal storage candidate")
			}
		}
		p := row.Pawn
		a := p.AnimalState
		// Training rows carry MaintainHerd's training deficit: one per
		// trainable def, each with its own facts.
		trainables := map[string]bool{}
		for _, entry := range a.Training {
			if entry == nil || validID(entry.GetDefName()) != nil || trainables[entry.GetDefName()] || !proto.Equal(entry, &o.TrainingEntry{DefName: entry.DefName, Learned: entry.Learned, Wanted: entry.Wanted, Available: entry.Available}) {
				return contract("invalid upkeep animal training")
			}
			trainables[entry.GetDefName()] = true
		}
		if !proto.Equal(p, &o.PawnState{Pawn: p.Pawn, Predator: p.Predator, AnimalState: a}) || !proto.Equal(a, &o.AnimalState{Contained: a.Contained, PenId: a.PenId, Release: a.Release, Slaughter: a.Slaughter, SafeToRelease: a.SafeToRelease, AllowedAreaId: a.AllowedAreaId, SupportsAllowedAreas: a.SupportsAllowedAreas, Training: a.Training, SafeToSlaughter: a.SafeToSlaughter, Gender: a.Gender, AgeYears: a.AgeYears, LifeExpectancyYears: a.LifeExpectancyYears, Adult: a.Adult, Sick: a.Sick, ManhunterOnTameFail: a.ManhunterOnTameFail, SlaughterBarred: a.SlaughterBarred, Venerated: a.Venerated}) || a.PenId != nil && (validID(a.GetPenId()) != nil || a.Contained != nil && !a.GetContained()) || row.RequiresPen != nil && !row.GetRequiresPen() && (a.Contained != nil || a.PenId != nil || row.SuitablePenId != nil) || !proto.Equal(row, &o.AnimalFeed{Pawn: p, Diet: row.Diet, RequiresPen: row.RequiresPen, SuitablePenId: row.SuitablePenId, ReachableStoredFeed: row.ReachableStoredFeed, ReachableBenchIds: row.ReachableBenchIds, ReachableStorage: row.ReachableStorage, StorageCandidates: row.StorageCandidates}) {
			return contract("conflicting upkeep animal fields")
		}
		stocks := map[string]bool{}
		for _, stock := range row.ReachableStoredFeed {
			if stock == nil || !entity(stock.Item, stocks) || !number(stock.Nutrition) || stock.Count != nil && stock.GetCount() < 0 || stock.RotTicks != nil && stock.GetRotTicks() < 0 || stock.HolderId != nil && stock.GetHolderId() != "" || len(stock.EaterIds) != 1 || stock.EaterIds[0] != p.Pawn.GetId() || !proto.Equal(stock, &o.FoodStock{Item: stock.Item, Count: stock.Count, HolderId: stock.HolderId, Nutrition: stock.Nutrition, EaterIds: stock.EaterIds, Perishable: stock.Perishable, RotTicks: stock.RotTicks, Roofed: stock.Roofed}) {
				return contract("invalid reachable animal feed")
			}
		}
	}
	// A wild row is the factionless tame census: native tame eligibility
	// and the tame designation only, never feed, pen or ownership facts.
	seen = map[string]bool{}
	for _, row := range v.WildAnimals {
		if row == nil || row.Pawn == nil || !entity(row.Pawn.Pawn, seen) || row.Pawn.AnimalState == nil || !row.Pawn.GetWild() || row.Diet != nil && validID(row.GetDiet()) != nil || row.RequiresPen != nil && row.GetRequiresPen() {
			return contract("invalid wild animal")
		}
		p := row.Pawn
		a := p.AnimalState
		if !proto.Equal(p, &o.PawnState{Pawn: p.Pawn, Wild: p.Wild, Predator: p.Predator, AnimalState: a}) || !proto.Equal(a, &o.AnimalState{Tameable: a.Tameable, Tame: a.Tame, MinimumHandlingSkill: a.MinimumHandlingSkill, Gender: a.Gender, AgeYears: a.AgeYears, LifeExpectancyYears: a.LifeExpectancyYears, Adult: a.Adult, Sick: a.Sick, ManhunterOnTameFail: a.ManhunterOnTameFail, SlaughterBarred: a.SlaughterBarred, Venerated: a.Venerated}) || a.MinimumHandlingSkill != nil && a.GetMinimumHandlingSkill() < 0 || !proto.Equal(row, &o.AnimalFeed{Pawn: p, Diet: row.Diet, RequiresPen: row.RequiresPen}) {
			return contract("conflicting wild animal fields")
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
		if err := validateLighting(v.Lighting, size, mapID, entity); err != nil {
			return err
		}
	}
	if v.Flooring != nil {
		if err := validateFlooring(v.Flooring, size); err != nil {
			return err
		}
	}
	if v.Rooms != nil {
		if err := validateUpkeepRooms(v.Rooms); err != nil {
			return err
		}
	}
	if v.Routes != nil {
		if err := validateRoutes(v.Routes, size, mapID, entity); err != nil {
			return err
		}
	}
	return nil
}

// validTitle checks a royal title row: a definition, a non-negative
// seniority and bedroom requirements that are non-negative, with each thing
// requirement naming 1-16 unique definitions and a positive count.
func validTitle(t *o.RoyalTitleFacts) bool {
	if t == nil {
		return true
	}
	if t.DefName == nil || validID(t.GetDefName()) != nil || t.Seniority != nil && t.GetSeniority() < 0 || t.BedroomMinArea != nil && t.GetBedroomMinArea() < 0 || t.BedroomMinImpressiveness != nil && t.GetBedroomMinImpressiveness() < 0 ||
		!proto.Equal(t, &o.RoyalTitleFacts{DefName: t.DefName, Seniority: t.Seniority, BedroomMinArea: t.BedroomMinArea, BedroomMinImpressiveness: t.BedroomMinImpressiveness, BedroomFloored: t.BedroomFloored, BedroomThings: t.BedroomThings}) {
		return false
	}
	for _, req := range t.BedroomThings {
		if req == nil || len(req.AnyOf) == 0 || req.Count == nil || req.GetCount() < 1 || !proto.Equal(req, &o.BedroomThingRequirement{AnyOf: req.AnyOf, Count: req.Count}) {
			return false
		}
		seen := map[string]bool{}
		for _, def := range req.AnyOf {
			if validID(def) != nil || seen[def] {
				return false
			}
			seen[def] = true
		}
	}
	return true
}

// validateUpkeepRooms checks the upkeep room census: unique rooms with finite quality stats, a non-negative space and wealth, and bed
// ids unique across the whole census.
func validateUpkeepRooms(section *o.UpkeepRoomsSection) error {
	f := section.GetObserved()
	if f == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	if !proto.Equal(f, &o.UpkeepRoomsFacts{Rooms: f.Rooms}) {
		return contract("invalid room quality census")
	}
	rooms := map[string]bool{}
	beds := map[string]bool{}
	for _, r := range f.Rooms {
		if r == nil || validID(r.GetRoomId()) != nil || rooms[r.GetRoomId()] || r.Role != nil && validID(r.GetRole()) != nil ||
			!proto.Equal(r, &o.UpkeepRoom{RoomId: r.RoomId, Role: r.Role, CellCount: r.CellCount, BedIds: r.BedIds, Impressiveness: r.Impressiveness, Wealth: r.Wealth, Beauty: r.Beauty, Space: r.Space, Cleanliness: r.Cleanliness}) {
			return contract("invalid room quality row")
		}
		rooms[r.GetRoomId()] = true
		for _, value := range []*float64{r.Impressiveness, r.Wealth, r.Beauty, r.Space, r.Cleanliness} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e9) {
				return contract("invalid room quality stat")
			}
		}
		if r.GetWealth() < 0 || r.GetSpace() < 0 {
			return contract("invalid room quality stat")
		}
		for _, id := range r.BedIds {
			if validID(id) != nil || beds[id] {
				return contract("invalid room quality bed")
			}
			beds[id] = true
		}
	}
	return nil
}

// validateRoutes checks the routes section: unique facility refs at in-map
// cells, each travel row naming a listed pawn once with non-negative path
// numbers only when reachable, breach cells unique and in the map, traffic
// cells unique and in the map.
func validateRoutes(section *o.RoutesSection, size *o.MapSize, mapID int32, entity func(*o.EntityRef, map[string]bool) bool) error {
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
		if row == nil || !entity(row.Facility, seen) || row.Kind == nil || validID(row.GetKind()) != nil || !colonyCell(row.Cell, size) || row.RoomId != nil && validID(row.GetRoomId()) != nil || !proto.Equal(row, &o.RouteFacility{Facility: row.Facility, Kind: row.Kind, Cell: row.Cell, RoomId: row.RoomId, Travel: row.Travel, Breaches: row.Breaches}) {
			return contract("invalid routes facility")
		}
		travelled := map[string]bool{}
		for _, t := range row.Travel {
			if t == nil || !pawns[t.GetPawnId()] || travelled[t.GetPawnId()] || t.PathCost != nil && (t.GetPathCost() < 0 || !t.GetReachable()) || t.PathCells != nil && (t.GetPathCells() < 0 || !t.GetReachable()) || !proto.Equal(t, &o.RouteTravel{PawnId: t.PawnId, Reachable: t.Reachable, PathCost: t.PathCost, PathCells: t.PathCells}) {
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
	// A cell appears once per traffic layer (#817).
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
// in-map cells, each naming a terrain listed once in the terrain table,
// whose stats are finite numbers.
func validateFlooring(section *o.FlooringSection, size *o.MapSize) error {
	f := section.GetObserved()
	if f == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	terrains := map[string]bool{}
	for _, row := range f.Terrains {
		if row == nil || validID(row.GetDefName()) != nil || terrains[row.GetDefName()] || !proto.Equal(row, &o.FloorTerrain{DefName: row.DefName, Cleanliness: row.Cleanliness, PathCost: row.PathCost, Beauty: row.Beauty, Flammability: row.Flammability, Natural: row.Natural}) {
			return contract("invalid flooring terrain")
		}
		terrains[row.GetDefName()] = true
		for _, value := range []*float64{row.Cleanliness, row.Beauty, row.Flammability} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e6) {
				return contract("invalid flooring terrain stat")
			}
		}
		if row.PathCost != nil && (row.GetPathCost() < 0 || row.GetPathCost() > 10000) {
			return contract("invalid flooring path cost")
		}
	}
	rooms := map[string]bool{}
	cells := map[[2]int32]bool{}
	for _, room := range f.Rooms {
		if room == nil || validID(room.GetRoomId()) != nil || rooms[room.GetRoomId()] || room.Role != nil && validID(room.GetRole()) != nil || !proto.Equal(room, &o.FloorRoom{RoomId: room.RoomId, Role: room.Role, Cells: room.Cells}) {
			return contract("invalid flooring room")
		}
		rooms[room.GetRoomId()] = true
		for _, cell := range room.Cells {
			if cell == nil || !colonyCell(cell.Cell, size) || cell.Terrain == nil || !terrains[cell.GetTerrain()] || cell.Pending != nil && validID(cell.GetPending()) != nil || !proto.Equal(cell, &o.FloorCell{Cell: cell.Cell, Terrain: cell.Terrain, Pending: cell.Pending}) {
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
func validateLighting(section *o.LightingSection, size *o.MapSize, mapID int32, entity func(*o.EntityRef, map[string]bool) bool) error {
	l := section.GetObserved()
	if l == nil {
		return validateUnavailable(section.GetUnavailable())
	}
	benches := map[string]bool{}
	for _, row := range l.WorkCells {
		if row == nil || !entity(row.Bench, benches) || !colonyCell(row.Cell, size) || row.RoomId != nil && validID(row.GetRoomId()) != nil || !proto.Equal(row, &o.WorkLightCell{Bench: row.Bench, Cell: row.Cell, Glow: row.Glow, Roofed: row.Roofed, RoomId: row.RoomId, LightSensitive: row.LightSensitive}) {
			return contract("invalid lighting work cell")
		}
		if row.Glow != nil && (math.IsNaN(row.GetGlow()) || row.GetGlow() < 0 || row.GetGlow() > 1) {
			return contract("invalid lighting glow")
		}
	}
	lamps := map[string]bool{}
	for _, row := range l.Lamps {
		b := row.GetBuilding()
		if row == nil || b == nil || !entity(b.Building, lamps) || row.RoomId != nil && validID(row.GetRoomId()) != nil || !proto.Equal(row, &o.LampState{Building: b, GlowRadius: row.GlowRadius, Lit: row.Lit, RoomId: row.RoomId}) || !proto.Equal(b, &o.BuildingState{Building: b.Building, Service: b.Service}) {
			return contract("invalid lighting lamp")
		}
		if row.GlowRadius != nil && (math.IsNaN(row.GetGlowRadius()) || row.GetGlowRadius() < 0 || row.GetGlowRadius() > 1e3) {
			return contract("invalid lamp glow radius")
		}
		s := b.Service
		if s == nil || !proto.Equal(s, &o.BuildingServiceState{Connected: s.Connected, PowerOn: s.PowerOn, PowerOutputW: s.PowerOutputW, SwitchedOn: s.SwitchedOn, Fuel: s.Fuel, TargetFuel: s.TargetFuel, OutOfFuel: s.OutOfFuel, BrokenDown: s.BrokenDown, AllowedFuelDefs: s.AllowedFuelDefs}) {
			return contract("unsupported lamp service detail")
		}
		for _, def := range s.AllowedFuelDefs {
			if validID(def) != nil {
				return contract("invalid lamp fuel definition")
			}
		}
		for _, value := range []*float64{s.Fuel, s.TargetFuel} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0 || *value > 1e12) {
				return contract("invalid lamp service quantity")
			}
		}
		if s.PowerOutputW != nil && (math.IsNaN(s.GetPowerOutputW()) || math.IsInf(s.GetPowerOutputW(), 0) || math.Abs(s.GetPowerOutputW()) > 1e12) {
			return contract("invalid lamp wattage")
		}
	}
	return nil
}
