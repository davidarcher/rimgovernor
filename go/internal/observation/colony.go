package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type PlanningDefinition struct {
	Name                                                                                  string
	Stuff                                                                                 domain.Fact[string]
	Available                                                                             domain.Fact[bool]
	ConstructionSkill                                                                     domain.Fact[int32]
	Costs                                                                                 domain.Fact[[]policy.Amount]
	Size                                                                                  domain.Fact[policy.Bounds]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, NutritionDemandPerDay domain.Fact[float64]
}
type ColonyProjection struct {
	WorkPawns          domain.Fact[[]policy.WorkPawn]
	FieldCrops         domain.Fact[[]policy.FieldCrop]
	CookingBenches     domain.Fact[[]CookingBench]
	PowerPlanning      domain.Fact[policy.PowerTopology]
	Identity           Identity
	Facts              policy.RoutineFacts
	Workers            domain.Fact[int]
	Bounds             policy.Bounds
	Center             domain.Cell
	Cells              []policy.SiteCell
	Definitions        []PlanningDefinition
	FoodSupply         domain.Fact[policy.FoodSupply]
	CombinedFoodSupply domain.Fact[policy.FoodSupply]
}

type CookingBench struct {
	Definition string
	Usable     domain.Fact[bool]
}

func optional[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}
func countFact(p *uint32) domain.Fact[int64] {
	if p == nil {
		return domain.Unknown[int64]()
	}
	return domain.Known(int64(*p))
}
func hasIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}
func nativePresence(value *string, issues []*o.ReadIssue, field string) domain.Fact[bool] {
	if value != nil {
		return domain.Known(true)
	}
	for _, issue := range issues {
		if issue.GetField() == field && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
			return domain.Known(false)
		}
	}
	return domain.Unknown[bool]()
}

// DecodeColony projects only same-tick validated facts. Native raw food runway
// cannot stand in for the diet/rot/competing-feed forecast needed by FoodDays.
func DecodeColony(reply *o.ColonyFactsReply, expected Identity) (ColonyProjection, error) {
	if err := expected.Validate(); err != nil {
		return ColonyProjection{}, err
	}
	if reply == nil {
		return ColonyProjection{}, ErrContract
	}
	if reply.GetUnavailable() != nil {
		return ColonyProjection{}, bridge.ErrUnavailable
	}
	if reply.GetFailure() != nil {
		return ColonyProjection{}, bridge.ErrRefused
	}
	v := reply.GetObserved()
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	if err := bridge.ValidateColonyFacts(v, id); err != nil {
		return ColonyProjection{}, err
	}
	identity, err := contextIdentity(v.Context)
	if err != nil {
		return ColonyProjection{}, err
	}
	if identity.Tick != expected.Tick {
		return ColonyProjection{}, ErrChanged
	}
	if generation, known := expected.NativeGeneration.Value(); known {
		if actual, observed := identity.NativeGeneration.Value(); observed && actual != generation {
			return ColonyProjection{}, ErrChanged
		}
	}
	r := ColonyProjection{Identity: identity, Bounds: policy.Bounds{Width: int32(v.MapSize.GetWidth()), Height: int32(v.MapSize.GetHeight())}, Center: domain.Cell{X: v.Center.GetX(), Z: v.Center.GetZ()}}
	r.Facts = policy.RoutineFacts{Colonists: countFact(v.ColonistCount), BedCapacity: countFact(v.BedCapacity), IndoorCapacity: countFact(v.IndoorSleepingCapacity), SleepingMin: optional(v.SleepingTemperatureMinC), SleepingMax: optional(v.SleepingTemperatureMaxC), OutdoorTemperature: optional(v.OutdoorTemperatureC), FoodStorage: optional(v.FoodStorage)}
	if development := v.GetDevelopment().GetObserved(); development != nil {
		power := make([]policy.PowerBuilding, 0, len(development.Power))
		topology := policy.PowerTopology{}
		geometryKnown := true
		if !hasIssue(v.Issues, "environment") {
			blackout := false
			for _, condition := range v.Environment {
				blackout = blackout || condition.GetDefName() == "SolarFlare"
			}
			topology.Blackout = domain.Known(blackout)
		}
		for _, row := range development.Power {
			s := row.Building.Service
			power = append(power, policy.PowerBuilding{BaseW: optional(row.BaseW), OutputW: optional(s.PowerOutputW), Powered: optional(s.PowerOn), Connected: optional(s.Connected), Network: optional(s.PowerNetId), Forbidden: optional(row.Building.Settings.Forbidden), SwitchedOn: optional(s.SwitchedOn)})
			ref := row.Building.Building
			geometryKnown = geometryKnown && ref.DefName != nil && ref.Position != nil && len(row.Building.OccupiedCells) > 0
			site := policy.PowerSite{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, PowerBuilding: power[len(power)-1]}
			for _, c := range row.Building.OccupiedCells {
				site.Occupied = append(site.Occupied, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			topology.Buildings = append(topology.Buildings, site)
		}
		for _, row := range development.Furniture {
			topology.Conduits = append(topology.Conduits, domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()})
		}
		if geometryKnown {
			r.PowerPlanning = domain.Known(topology)
		}
		r.Facts.PowerRequired, r.Facts.PowerHeadroom, r.Facts.DisabledConsumers = policy.PowerCoverage(domain.Known(power))
	}
	colonyProduction(v, &r.Facts)
	r.Facts.Comfort = colonyComfort(v)
	r.Facts.HomeCoverage = colonyHomeCoverage(v)
	r.Facts.StoneStructures = colonyStoneStructures(v)
	r.Facts.Sleeping = colonySleeping(v)
	r.Facts.AnimalUpkeep.Animals = colonyAnimals(v)
	r.Facts.Upkeep = colonyUpkeep(v)
	r.Facts.MedicalReserve = colonyMedicalReserve(v)
	if v.Naming != nil {
		r.Facts.ColonyNaming = domain.Known(true)
	} else {
		for _, issue := range v.Issues {
			if issue.GetField() == "naming" && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
				r.Facts.ColonyNaming = domain.Known(false)
			}
		}
	}
	if !hasIssue(v.Issues, "cooking") {
		benches := []CookingBench{}
		for _, bench := range v.Cooking {
			benches = append(benches, CookingBench{Definition: bench.Bench.GetDefName(), Usable: optional(bench.Usable)})
		}
		r.CookingBenches = domain.Known(benches)
	}
	if food := v.GetFoodSupply().GetObserved(); food != nil {
		supply, err := DecodeFoodSupply(food)
		if err != nil {
			return ColonyProjection{}, err
		}
		r.FoodSupply = domain.Known(supply)
	}
	if forecast := v.GetForecast().GetObserved(); forecast != nil {
		combined, err := DecodeFoodSupply(forecast.CombinedFoodSupply)
		if err != nil {
			return ColonyProjection{}, err
		}
		r.CombinedFoodSupply = domain.Known(combined)
		if human, known := r.FoodSupply.Value(); known && len(human.Consumers) > 0 {
			selected := make([]policy.PawnID, 0, len(human.Consumers))
			for _, consumer := range human.Consumers {
				selected = append(selected, consumer.ID)
			}
			if food, err := policy.ForecastFood(combined, selected); err == nil {
				r.Facts.FoodDays = food.RunwayDays
			}
		}
	}
	r.Facts.AnimalUpkeep.Food = r.CombinedFoodSupply
	if v.WorkerCount != nil {
		r.Workers = domain.Known(int(v.GetWorkerCount()))
	}
	if !hasIssue(v.Issues, "resources") {
		r.Facts.Wood = domain.Known(int64(0))
		for _, q := range v.Resources {
			if q.GetDefName() == "WoodLog" {
				r.Facts.Wood = optional(q.Units)
			}
		}
	}
	if !hasIssue(v.Issues, "forbidden_supplies") {
		r.Facts.ForbiddenSupplies = domain.Known(len(v.ForbiddenSupplies) > 0)
		cells := make([]domain.Cell, 0, len(v.ForbiddenSupplies))
		for _, cell := range v.ForbiddenSupplies {
			cells = append(cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		r.Facts.StartingSupplyCells = domain.Known(cells)
	}
	if planning := v.GetPlanning().GetObserved(); planning != nil {
		for _, row := range planning.Definitions {
			d := PlanningDefinition{Name: row.Definition.GetDefName(), Stuff: optional(row.Stuff), Available: optional(row.Available), ConstructionSkill: optional(row.ConstructionSkill), GrowDays: optional(row.GrowDays), FertilityMin: optional(row.FertilityMin), FertilitySensitivity: optional(row.FertilitySensitivity), HarvestNutrition: optional(row.HarvestNutrition), NutritionDemandPerDay: optional(row.NutritionDemandPerDay)}
			if row.Size != nil {
				d.Size = domain.Known(policy.Bounds{Width: int32(row.Size.GetWidth()), Height: int32(row.Size.GetHeight())})
			}
			known := !hasIssue(row.Issues, "costs")
			var costs []policy.Amount
			for _, q := range row.Costs {
				if q.Units == nil {
					known = false
				}
				costs = append(costs, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
			}
			if known {
				d.Costs = domain.Known(costs)
			}
			r.Definitions = append(r.Definitions, d)
		}
		for _, row := range planning.Cells.Cells {
			// Missing visibility is not evidence that a cell is safe to plan on.
			if row.Fogged == nil || row.GetFogged() {
				continue
			}
			r.Cells = append(r.Cells, policy.SiteCell{Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Walkable: optional(row.Walkable), Occupied: optional(row.Occupied), Zone: nativePresence(row.ZoneId, row.Issues, "zone_id"), Roofed: nativePresence(row.Roof, row.Issues, "roof"), Indoors: optional(row.Indoors), SupportsLight: optional(row.SupportsLight), Fertility: optional(row.Fertility)})
		}
	}
	r.FieldCrops = colonyFieldCrops(v, r.Definitions)
	r.Facts.Gear = colonyGear(v)
	return r, nil
}
