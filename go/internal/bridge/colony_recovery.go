package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyRecovery(colony *o.ColonyFactsSnapshot) error {
	reply := colony.Recovery
	if reply == nil {
		return nil
	} // Older native producers explicitly omit this section.
	for _, issue := range colony.Issues {
		if issue.GetField() == "recovery" {
			return contract("unavailable recovery contains a reply")
		}
	}
	if reply.GetUnavailable() != nil {
		return validateUnavailable(reply.GetUnavailable())
	}
	v := reply.GetObserved()
	if v == nil || !proto.Equal(v.Context, colony.Context) || !proto.Equal(v, &o.RecoverySnapshot{Context: v.Context, RoofHazard: v.RoofHazard, Areas: v.Areas, Restrictions: v.Restrictions, Buildings: v.Buildings, Completeness: v.Completeness}) {
		return contract("invalid recovery context or outcome")
	}
	if err := colonyCounts(v.Completeness, len(v.Buildings)+len(v.Areas)+len(v.Restrictions), 256); err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, b := range v.Buildings {
		if b == nil || !powerEntity(b.Building, colony.Context.Identity, colony.MapSize) || seen[b.Building.GetId()] {
			return contract("invalid recovery building identity")
		}
		seen[b.Building.GetId()] = true
		if !proto.Equal(b, &o.BuildingState{Building: b.Building, UsesHitPoints: b.UsesHitPoints, HitPoints: b.HitPoints, MaxHitPoints: b.MaxHitPoints, Burning: b.Burning, Settings: b.Settings, Service: b.Service}) {
			return contract("unsupported recovery building detail")
		}
		if b.Settings == nil || !proto.Equal(b.Settings, &o.BuildingSettings{Forbidden: b.Settings.Forbidden}) {
			return contract("invalid recovery settings")
		}
		if b.HitPoints != nil && b.GetHitPoints() < 0 || b.MaxHitPoints != nil && b.GetMaxHitPoints() <= 0 || b.HitPoints != nil && b.MaxHitPoints != nil && b.GetHitPoints() > b.GetMaxHitPoints() || b.UsesHitPoints != nil && !b.GetUsesHitPoints() && (b.HitPoints != nil || b.MaxHitPoints != nil) {
			return contract("invalid recovery hit points")
		}
		s := b.Service
		if s == nil || !proto.Equal(s, &o.BuildingServiceState{BrokenDown: s.BrokenDown, Fuel: s.Fuel, TargetFuel: s.TargetFuel, AllowedFuelDefs: s.AllowedFuelDefs, Issues: s.Issues}) || !combatNumber(s.Fuel, true) || !combatNumber(s.TargetFuel, true) || len(s.AllowedFuelDefs) > 256 {
			return contract("invalid recovery service")
		}
		defs := map[string]bool{}
		for _, d := range s.AllowedFuelDefs {
			if validID(d) != nil || defs[d] {
				return contract("invalid recovery fuel definition")
			}
			defs[d] = true
		}
		if err := pawnsIssues(s.Issues, s.ProtoReflect()); err != nil {
			return err
		}
		if len(s.Issues) > 1 {
			return contract("unexpected recovery issue")
		}
		for _, issue := range s.Issues {
			if issue.GetField() != "fuel" || issue.GetUnavailable().GetReason() != c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE || s.Fuel != nil || s.TargetFuel != nil || len(s.AllowedFuelDefs) > 0 {
				return contract("conflicting recovery fuel availability")
			}
		}
	}
	seen = map[string]bool{}
	for _, a := range v.Areas {
		if a == nil || validID(a.GetId()) != nil || seen[a.GetId()] || !a.GetRoofed() || !proto.Equal(a, &o.RecoveryArea{Id: a.Id, Roofed: a.Roofed, Cells: a.Cells, Completeness: a.Completeness}) || len(a.Cells) == 0 {
			return contract("invalid recovery area")
		}
		seen[a.GetId()] = true
		if err := colonyCounts(a.Completeness, len(a.Cells), 4096); err != nil {
			return err
		}
		cells := map[[2]int32]bool{}
		for _, cell := range a.Cells {
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if !colonyCell(cell, colony.MapSize) || cells[key] {
				return contract("invalid recovery area geometry")
			}
			cells[key] = true
		}
	}
	seen = map[string]bool{}
	for _, r := range v.Restrictions {
		if r == nil || !powerEntity(r.Pawn, colony.Context.Identity, colony.MapSize) || seen[r.Pawn.GetId()] || r.AreaId != nil && validID(r.GetAreaId()) != nil || !proto.Equal(r, &o.RecoveryRestriction{Pawn: r.Pawn, AreaId: r.AreaId}) {
			return contract("invalid recovery restriction")
		}
		seen[r.Pawn.GetId()] = true
	}
	return nil
}
