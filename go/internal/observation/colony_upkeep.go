package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyUpkeep(v *o.ColonyFactsSnapshot) policy.UpkeepObservation {
	r := policy.UpkeepObservation{}
	u := v.GetUpkeep().GetObserved()
	if u == nil {
		return r
	}
	if !hasIssue(u.Issues, "items") {
		rows := []policy.UpkeepItem{}
		known := true
		for _, item := range u.Items {
			if item.Roofed == nil || item.InStorage == nil || item.Forbidden == nil || item.BaseDeteriorationRate == nil || item.Medicine == nil || item.Count == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepItem{ID: item.Item.GetId(), Definition: item.Item.GetDefName(), Cell: domain.Cell{X: item.Item.GetPosition().GetX(), Z: item.Item.GetPosition().GetZ()}, Roofed: item.GetRoofed(), InStorage: item.GetInStorage(), Forbidden: item.GetForbidden(), Deterioration: item.GetBaseDeteriorationRate(), Medicine: item.GetMedicine(), Count: item.GetCount(), RotTicks: optional(item.RotTicks)})
		}
		if known {
			r.Items = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "structures") {
		rows := []policy.UpkeepStructure{}
		known := true
		for _, item := range u.Structures {
			b := item.Building
			if item.Home == nil || item.RepairPriority == nil || b.HitPoints == nil || b.MaxHitPoints == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepStructure{ID: b.Building.GetId(), Cell: domain.Cell{X: b.Building.GetPosition().GetX(), Z: b.Building.GetPosition().GetZ()}, Home: item.GetHome(), HitPoints: int64(b.GetHitPoints()), MaxHitPoints: int64(b.GetMaxHitPoints()), Priority: int(item.GetRepairPriority())})
		}
		if known {
			r.Structures = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "fires") {
		rows := []policy.UpkeepFire{}
		known := true
		for _, item := range u.Fires {
			if item.Home == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepFire{ID: item.Fire.GetId(), Home: item.GetHome(), Size: optional(item.Size)})
		}
		if known {
			r.Fires = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "filth") {
		rows := []policy.UpkeepFilth{}
		known := true
		for _, item := range u.Filth {
			if item.Home == nil || item.Thickness == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepFilth{ID: item.Filth.GetId(), Definition: item.Filth.GetDefName(), Cell: domain.Cell{X: item.Filth.GetPosition().GetX(), Z: item.Filth.GetPosition().GetZ()}, Home: item.GetHome(), Room: item.GetRoomRole(), RoomID: optional(item.RoomId), Thickness: item.GetThickness()})
		}
		if known {
			r.Filth = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "lighting") {
		r.Lighting = colonyLighting(u.Lighting)
	}
	if !hasIssue(u.Issues, "routes") {
		r.Routes = colonyRoutes(u.Routes)
	}
	if !hasIssue(u.Issues, "flooring") {
		// The flooring census joins the routes census's traffic evidence, so a
		// routes issue leaves flooring unknown too rather than judging the
		// traffic tier from nothing.
		routes := u.Routes
		if hasIssue(u.Issues, "routes") {
			routes = &o.RoutesSection{}
		}
		r.Flooring = colonyFlooring(u.Flooring, routes)
	}
	return r
}

// colonyFlooring decodes the flooring section; a terrain row missing any
// stat or a cell missing its terrain leaves the whole census unknown so
// MaintainFlooring keeps its previous latch.
func colonyFlooring(section *o.FlooringSection, routes *o.RoutesSection) domain.Fact[policy.FlooringObservation] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[policy.FlooringObservation]{}
	}
	r := policy.FlooringObservation{Rooms: []policy.FloorRoom{}, Terrains: map[string]policy.FloorTerrain{}}
	// The traffic tier reads the routes census's observed travel; an
	// unknown routes census leaves the whole flooring census unknown rather
	// than silently dropping the tier. Traffic cells whose terrain the
	// flooring table does not name are unmeasured and left out.
	if routes != nil {
		t, known := colonyRoutes(routes).Value()
		if !known {
			return domain.Fact[policy.FlooringObservation]{}
		}
		r.TrafficSamples = t.TrafficSamples
		r.Traffic = t.Traffic
	}
	for _, row := range f.Terrains {
		if row.Cleanliness == nil || row.Beauty == nil || row.Flammability == nil || row.PathCost == nil || row.Natural == nil {
			return domain.Fact[policy.FlooringObservation]{}
		}
		r.Terrains[row.GetDefName()] = policy.FloorTerrain{Cleanliness: row.GetCleanliness(), Beauty: row.GetBeauty(), Flammability: row.GetFlammability(), PathCost: row.GetPathCost(), Natural: row.GetNatural()}
	}
	if len(r.Traffic) > 0 {
		kept := r.Traffic[:0]
		for _, t := range r.Traffic {
			if _, ok := r.Terrains[t.Terrain]; ok {
				kept = append(kept, t)
			}
		}
		r.Traffic = kept
	}
	for _, room := range f.Rooms {
		out := policy.FloorRoom{ID: room.GetRoomId(), Cells: []policy.FloorCell{}}
		if room.Role != nil {
			out.Role = domain.Known(policy.RoomRole(room.GetRole()))
		}
		for _, cell := range room.Cells {
			if cell.Terrain == nil {
				return domain.Fact[policy.FlooringObservation]{}
			}
			out.Cells = append(out.Cells, policy.FloorCell{Cell: domain.Cell{X: cell.Cell.GetX(), Z: cell.Cell.GetZ()}, Terrain: cell.GetTerrain(), Pending: cell.GetPending()})
		}
		r.Rooms = append(r.Rooms, out)
	}
	return domain.Known(r)
}

// colonyRoutes decodes the routes section; a travel row missing its
// reachability leaves the whole census unknown so MaintainRoutes keeps its
// previous latch instead of declaring a facility reachable by omission.
func colonyRoutes(section *o.RoutesSection) domain.Fact[policy.RoutesObservation] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[policy.RoutesObservation]{}
	}
	r := policy.RoutesObservation{Pawns: append([]string{}, f.PawnIds...), Facilities: []policy.RouteFacility{}, Traffic: []policy.TrafficCell{}, TrafficSamples: f.GetTrafficSamples()}
	if f.TrafficSinceTick != nil {
		r.TrafficSince = domain.Known(domain.Tick(f.GetTrafficSinceTick()))
	}
	for _, row := range f.Facilities {
		out := policy.RouteFacility{ID: row.Facility.GetId(), Definition: row.Facility.GetDefName(), Kind: row.GetKind(), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Travel: []policy.RouteTravel{}, Breaches: []policy.RouteBreach{}}
		if row.RoomId != nil {
			out.Room = domain.Known(row.GetRoomId())
		}
		for _, t := range row.Travel {
			if t.Reachable == nil {
				return domain.Fact[policy.RoutesObservation]{}
			}
			travel := policy.RouteTravel{Pawn: t.GetPawnId(), Reachable: t.GetReachable()}
			if t.PathCost != nil {
				travel.Cost = domain.Known(t.GetPathCost())
			}
			if t.PathCells != nil {
				travel.Cells = domain.Known(t.GetPathCells())
			}
			out.Travel = append(out.Travel, travel)
		}
		for _, b := range row.Breaches {
			out.Breaches = append(out.Breaches, policy.RouteBreach{Cell: domain.Cell{X: b.Cell.GetX(), Z: b.Cell.GetZ()}, Edifice: b.GetEdifice(), Pending: b.GetPending(), Distance: b.GetDistance()})
		}
		r.Facilities = append(r.Facilities, out)
	}
	for _, t := range f.Traffic {
		if t.Samples == nil || t.Terrain == nil || t.Home == nil {
			return domain.Fact[policy.RoutesObservation]{}
		}
		r.Traffic = append(r.Traffic, policy.TrafficCell{Cell: domain.Cell{X: t.Cell.GetX(), Z: t.Cell.GetZ()}, Samples: t.GetSamples(), Terrain: t.GetTerrain(), Home: t.GetHome(), Pending: t.GetPending()})
	}
	return domain.Known(r)
}

// colonyLighting decodes the lighting section; any row missing a measured
// glow, roof or lit flag leaves the whole census unknown so MaintainLighting
// keeps its previous latch instead of reasoning from half a map.
func colonyLighting(section *o.LightingSection) domain.Fact[policy.LightingObservation] {
	l := section.GetObserved()
	if l == nil {
		return domain.Fact[policy.LightingObservation]{}
	}
	r := policy.LightingObservation{WorkCells: []policy.WorkLightCell{}, Lamps: []policy.Lamp{}}
	for _, row := range l.WorkCells {
		if row.Glow == nil || row.Roofed == nil {
			return domain.Fact[policy.LightingObservation]{}
		}
		r.WorkCells = append(r.WorkCells, policy.WorkLightCell{Bench: row.Bench.GetId(), Definition: row.Bench.GetDefName(), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Glow: row.GetGlow(), Roofed: row.GetRoofed(), Room: optional(row.RoomId)})
	}
	for _, row := range l.Lamps {
		b := row.GetBuilding()
		s := b.GetService()
		if row.GlowRadius == nil || row.Lit == nil {
			return domain.Fact[policy.LightingObservation]{}
		}
		r.Lamps = append(r.Lamps, policy.Lamp{ID: b.GetBuilding().GetId(), Definition: b.GetBuilding().GetDefName(), Cell: domain.Cell{X: b.GetBuilding().GetPosition().GetX(), Z: b.GetBuilding().GetPosition().GetZ()}, Radius: row.GetGlowRadius(), Lit: row.GetLit(), Room: optional(row.RoomId),
			Powered: optional(s.PowerOn), Connected: optional(s.Connected), SwitchedOn: optional(s.SwitchedOn), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: append([]string(nil), s.GetAllowedFuelDefs()...)})
	}
	return domain.Known(r)
}
