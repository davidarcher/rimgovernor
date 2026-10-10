package observation

import (
	"fmt"
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyUpkeep decodes the upkeep censuses. A census whose frame lacks a field
// is unknown; one that needs a def the catalog cannot answer for is an error
// naming the census and the def, never an unknown.
func colonyUpkeep(v *o.ColonyFactsSnapshot, tables bridge.Tables) (policy.UpkeepObservation, error) {
	buildings := tables.Buildings
	r := policy.UpkeepObservation{}
	u := v.GetUpkeep().GetObserved()
	if u == nil {
		return r, nil
	}
	if !hasIssue(u.Issues, "structures") {
		rows := []policy.UpkeepStructure{}
		known := true
		medical := map[string]bool{}
		for _, bed := range u.Beds {
			medical[bed.GetBed().GetId()] = bed.GetMedical()
		}
		for _, item := range u.Structures {
			b, ok := buildings.Row(item.Building)
			if !ok || item.Home == nil || b.HitPoints == nil || b.MaxHitPoints == nil {
				known = false
				break
			}
			if tables.Catalog == nil {
				return r, fmt.Errorf("upkeep structures: no definition catalog was loaded")
			}
			priority, err := tables.Catalog.RepairPriority(b.GetBuilding().GetDefName(), medical[item.Building.GetId()])
			if err != nil {
				return r, fmt.Errorf("upkeep structure %s: %w", item.Building.GetId(), err)
			}
			rows = append(rows, policy.UpkeepStructure{ID: item.Building.GetId(), Definition: b.GetBuilding().GetDefName(), Cell: domain.Cell{X: b.GetBuilding().GetPosition().GetX(), Z: b.GetBuilding().GetPosition().GetZ()}, Home: item.GetHome(), HitPoints: int64(b.GetHitPoints()), MaxHitPoints: int64(b.GetMaxHitPoints()), Priority: priority})
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
	if !hasIssue(u.Issues, "filth") && headed(tables, u.Filth, (*o.FilthState).GetFilth) {
		rows := []policy.UpkeepFilth{}
		known := true
		for _, item := range u.Filth {
			if item.Home == nil || item.Thickness == nil {
				known = false
				break
			}
			head := tables.Entity(item.Filth)
			rows = append(rows, policy.UpkeepFilth{ID: item.Filth.GetId(), Definition: head.GetDefName(), Cell: domain.Cell{X: head.GetPosition().GetX(), Z: head.GetPosition().GetZ()}, Home: item.GetHome(), Room: item.GetRoomRole(), RoomID: optionalRef(item.Room), Thickness: item.GetThickness()})
		}
		if known {
			r.Filth = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "lighting") {
		lighting, err := colonyLighting(u.Lighting, buildings, tables.Catalog)
		if err != nil {
			return policy.UpkeepObservation{}, err
		}
		r.Lighting = lighting
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
		flooring, err := colonyFlooring(u.Flooring, routes, tables.Catalog)
		if err != nil {
			return r, fmt.Errorf("flooring census: %w", err)
		}
		r.Flooring = flooring
	}
	return r, nil
}

// colonyFlooring decodes the flooring section; a cell missing its terrain
// leaves the whole census unknown so MaintainFlooring keeps its previous
// latch, and a terrain the catalog has no stat or def row for is an error.
func colonyFlooring(section *o.FlooringSection, routes *o.RoutesSection, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.FlooringObservation], error) {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[policy.FlooringObservation]{}, nil
	}
	terrains, err := catalog.FloorTerrains()
	if err != nil {
		return domain.Fact[policy.FlooringObservation]{}, err
	}
	r := policy.FlooringObservation{Rooms: []policy.FloorRoom{}, Terrains: maps.Clone(terrains)}
	// The traffic tier reads the routes census's observed travel; an
	// unknown routes census leaves the whole flooring census unknown rather
	// than silently dropping the tier. Traffic cells whose terrain the
	// flooring table does not name are unmeasured and left out.
	if routes != nil {
		t, known := colonyRoutes(routes).Value()
		if !known {
			return domain.Fact[policy.FlooringObservation]{}, nil
		}
		r.TrafficSamples = t.TrafficSamples
		r.Traffic = t.Traffic
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
		out := policy.FloorRoom{ID: room.GetRoom().GetId(), Cells: []policy.FloorCell{}}
		if room.Role != nil {
			out.Role = domain.Known(policy.RoomRole(room.GetRole()))
		}
		for _, cell := range room.Cells {
			if cell.Terrain == nil {
				return domain.Fact[policy.FlooringObservation]{}, nil
			}
			if _, priced := r.Terrains[cell.GetTerrain()]; !priced {
				return domain.Fact[policy.FlooringObservation]{}, fmt.Errorf("%w: flooring cell stands on terrain %s, which the catalog has no row for", ErrContract, cell.GetTerrain())
			}
			out.Cells = append(out.Cells, policy.FloorCell{Cell: domain.Cell{X: cell.Cell.GetX(), Z: cell.Cell.GetZ()}, Terrain: cell.GetTerrain(), Pending: cell.GetPending()})
		}
		r.Rooms = append(r.Rooms, out)
	}
	return domain.Known(r), nil
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
		out := policy.RouteFacility{ID: row.Facility.GetId(), Kind: bridge.RouteKindName(row.GetKind()), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Travel: []policy.RouteTravel{}, Breaches: []policy.RouteBreach{}}
		if row.Room != nil {
			out.Room = domain.Known(row.GetRoom().GetId())
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
			travel.Skipped = t.GetPathSkipped()
			out.Travel = append(out.Travel, travel)
		}
		for _, b := range row.Breaches {
			out.Breaches = append(out.Breaches, policy.RouteBreach{Cell: domain.Cell{X: b.Cell.GetX(), Z: b.Cell.GetZ()}, Edifice: b.GetEdifice(), Pending: b.GetPending(), Distance: b.GetDistance()})
		}
		r.Facilities = append(r.Facilities, out)
	}
	for _, t := range f.Traffic {
		layer, ok := trafficLayers[t.Layer]
		if t.Samples == nil || t.Terrain == nil || t.Home == nil || !ok {
			return domain.Fact[policy.RoutesObservation]{}
		}
		r.Traffic = append(r.Traffic, policy.TrafficCell{Cell: domain.Cell{X: t.Cell.GetX(), Z: t.Cell.GetZ()}, Layer: layer, Samples: t.GetSamples(), Terrain: t.GetTerrain(), Home: t.GetHome(), Pending: t.GetPending()})
	}
	return domain.Known(r)
}

var trafficLayers = map[o.TrafficLayer]policy.TrafficLayer{
	o.TrafficLayer_TRAFFIC_LAYER_COLONIST: policy.TrafficColonist,
	o.TrafficLayer_TRAFFIC_LAYER_CROSSING: policy.TrafficCrossing,
	o.TrafficLayer_TRAFFIC_LAYER_ANIMAL:   policy.TrafficAnimal,
	o.TrafficLayer_TRAFFIC_LAYER_VISITOR:  policy.TrafficVisitor,
	o.TrafficLayer_TRAFFIC_LAYER_HOSTILE:  policy.TrafficHostile,
}

// colonyLighting decodes the lighting section; any row missing a measured
// glow, roof or lit flag leaves the whole census unknown so MaintainLighting
// keeps its previous latch instead of reasoning from half a map.
func colonyLighting(section *o.LightingSection, buildings bridge.Buildings, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.LightingObservation], error) {
	l := section.GetObserved()
	if l == nil {
		return domain.Fact[policy.LightingObservation]{}, nil
	}
	r := policy.LightingObservation{WorkCells: []policy.WorkLightCell{}, Lamps: []policy.Lamp{}}
	for _, row := range l.WorkCells {
		if row.Glow == nil || row.Roofed == nil || buildings.Entity(row.Bench) == nil {
			return domain.Fact[policy.LightingObservation]{}, nil
		}
		r.WorkCells = append(r.WorkCells, policy.WorkLightCell{Bench: row.Bench.GetId(), Definition: buildings.Entity(row.Bench).GetDefName(), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Glow: row.GetGlow(), Roofed: row.GetRoofed(), Room: optionalRef(row.Room), LightSensitive: slices.ContainsFunc(row.PlantDefs, catalog.PlantDiesToLight)})
	}
	for _, row := range l.Lamps {
		b, ok := buildings.Row(row.GetBuilding())
		ref := b.GetBuilding()
		if !ok || row.Lit == nil {
			return domain.Fact[policy.LightingObservation]{}, nil
		}
		radius, glows, err := catalog.GlowRadius(ref.GetDefName())
		if err != nil {
			return domain.Fact[policy.LightingObservation]{}, err
		}
		if !glows {
			return domain.Fact[policy.LightingObservation]{}, fmt.Errorf("lamp %s: def %s has no glower comp", ref.GetId(), ref.GetDefName())
		}
		s := b.GetService()
		fuels, err := catalog.RefuelFuels(ref.GetDefName())
		if err != nil {
			return domain.Fact[policy.LightingObservation]{}, err
		}
		r.Lamps = append(r.Lamps, policy.Lamp{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Radius: radius, Lit: row.GetLit(), Room: optionalRef(row.Room),
			Powered: optional(s.PowerOn), Connected: optional(s.Connected), SwitchedOn: optional(s.SwitchedOn), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: fuels})
	}
	return domain.Known(r), nil
}
