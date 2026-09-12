package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateDirectUpkeep(v *o.UpkeepFacts, size *o.MapSize, mapID int32) error {
	counts := map[string]int{"items": len(v.Items), "structures": len(v.Structures), "fires": len(v.Fires), "filth": len(v.Filth), "animals": len(v.Animals), "people": len(v.People), "beds": len(v.Beds)}
	if v.HomeCoverage != nil {
		counts["home_coverage"] = 1
	}
	for _, n := range counts {
		if n > 256 {
			return contract("upkeep census exceeds bound")
		}
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
		if row == nil || !entity(row.Filth, seen) || row.RoomRole != nil && validID(row.GetRoomRole()) != nil || !proto.Equal(row, &o.FilthState{Filth: row.Filth, Home: row.Home, Thickness: row.Thickness, RoomRole: row.RoomRole}) {
			return contract("invalid upkeep filth")
		}
	}
	seen = map[string]bool{}
	finite := func(p *float64) bool { return p == nil || !math.IsNaN(*p) && !math.IsInf(*p, 0) }
	ids := func(values []string) bool {
		if len(values) > 256 {
			return false
		}
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
		if row == nil || row.Pawn == nil || !entity(row.Pawn.Pawn, seen) || !proto.Equal(row.Pawn, &o.PawnState{Pawn: row.Pawn.Pawn}) || row.OwnedBedId != nil && row.GetOwnedBedId() != "" && validID(row.GetOwnedBedId()) != nil || !finite(row.ComfortableMinC) || !finite(row.ComfortableMaxC) || !finite(row.TemperatureC) || row.ComfortableMinC != nil && row.ComfortableMaxC != nil && row.GetComfortableMinC() > row.GetComfortableMaxC() || !proto.Equal(row, &o.UpkeepPerson{Pawn: row.Pawn, OwnedBedId: row.OwnedBedId, ComfortableMinC: row.ComfortableMinC, ComfortableMaxC: row.ComfortableMaxC, TemperatureC: row.TemperatureC}) {
			return contract("invalid sleeping person")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Beds {
		if row == nil || !entity(row.Bed, seen) || row.Slots != nil && row.GetSlots() > 256 || !finite(row.RestEffectiveness) || !finite(row.TemperatureC) || !ids(row.Owners) || !ids(row.Users) || !ids(row.AccessibleTo) || !proto.Equal(row, &o.UpkeepBed{Bed: row.Bed, Slots: row.Slots, Humanlike: row.Humanlike, RestEffectiveness: row.RestEffectiveness, Medical: row.Medical, Prisoners: row.Prisoners, Roofed: row.Roofed, TemperatureC: row.TemperatureC, Owners: row.Owners, Users: row.Users, AccessibleTo: row.AccessibleTo}) {
			return contract("invalid upkeep bed")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Animals {
		if row == nil || row.Pawn == nil || !entity(row.Pawn.Pawn, seen) || row.Pawn.AnimalState == nil || row.Diet != nil && validID(row.GetDiet()) != nil || row.SuitablePenId != nil && validID(row.GetSuitablePenId()) != nil || len(row.ReachableStoredFeed) > 256 {
			return contract("invalid upkeep animal")
		}
		p := row.Pawn
		a := p.AnimalState
		if !proto.Equal(p, &o.PawnState{Pawn: p.Pawn, AnimalState: a}) || !proto.Equal(a, &o.AnimalState{Contained: a.Contained, PenId: a.PenId, Release: a.Release, Slaughter: a.Slaughter}) || a.PenId != nil && (validID(a.GetPenId()) != nil || a.Contained != nil && !a.GetContained()) || row.RequiresPen != nil && !row.GetRequiresPen() && (a.Contained != nil || a.PenId != nil || row.SuitablePenId != nil) || !proto.Equal(row, &o.AnimalFeed{Pawn: p, Diet: row.Diet, RequiresPen: row.RequiresPen, SuitablePenId: row.SuitablePenId, ReachableStoredFeed: row.ReachableStoredFeed}) {
			return contract("conflicting upkeep animal fields")
		}
		stocks := map[string]bool{}
		for _, stock := range row.ReachableStoredFeed {
			if stock == nil || !entity(stock.Item, stocks) || !number(stock.Nutrition) || stock.Count != nil && stock.GetCount() < 0 || stock.RotTicks != nil && stock.GetRotTicks() < 0 || stock.HolderId != nil && stock.GetHolderId() != "" || len(stock.EaterIds) != 1 || stock.EaterIds[0] != p.Pawn.GetId() || !proto.Equal(stock, &o.FoodStock{Item: stock.Item, Count: stock.Count, HolderId: stock.HolderId, Nutrition: stock.Nutrition, EaterIds: stock.EaterIds, Perishable: stock.Perishable, RotTicks: stock.RotTicks, Roofed: stock.Roofed}) {
				return contract("invalid reachable animal feed")
			}
		}
	}
	if v.HomeCoverage != nil {
		h := v.HomeCoverage.GetObserved()
		if h == nil {
			if err := validateUnavailable(v.HomeCoverage.GetUnavailable()); err != nil {
				return err
			}
		} else {
			if h.Revision == nil || h.GetRevision() < 0 || colonyCounts(h.Completeness, len(h.Targets), 256) != nil {
				return contract("invalid Home coverage census")
			}
			seen := map[string]bool{}
			for _, row := range h.Targets {
				if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] || row.Snapshot != nil || row.ShapeToken != nil && validID(row.GetShapeToken()) != nil || !diagnostic(row.Blocker) || row.MissingCells != nil && row.GetMissingCells() > 256 || row.ExcludedCells != nil && (row.GetExcludedCells() > 256 || row.MissingCells != nil && row.GetExcludedCells() > row.GetMissingCells()) || len(row.Cells) > 256 {
					return contract("invalid Home coverage target")
				}
				seen[row.GetId()] = true
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
	return nil
}
