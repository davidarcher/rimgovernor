package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateDirectUpkeep(v *o.UpkeepFacts, size *o.MapSize, mapID int32) error {
	counts := map[string]int{"items": len(v.Items), "structures": len(v.Structures), "fires": len(v.Fires), "filth": len(v.Filth), "animals": len(v.Animals)}
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
			!proto.Equal(b, &o.BuildingState{Building: b.Building, HitPoints: b.HitPoints, MaxHitPoints: b.MaxHitPoints}) || !proto.Equal(row, &o.UpkeepStructure{Building: b, Home: row.Home, RepairPriority: row.RepairPriority}) {
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
	return nil
}
