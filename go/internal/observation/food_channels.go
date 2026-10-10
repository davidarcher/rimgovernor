package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// FoodChannels describes native source potential. It does not select work.
// A known census can contain animals with unknown component values.
type FoodChannels struct {
	FishableWater  domain.Fact[FishableWater]
	Gatherable     []GatherableAnimal
	EggLayer       []EggLayerAnimal
	PasteDispenser []PasteDispenser
	PollutedCells  domain.Fact[uint32]
	Forage         []ForagePlant
	Grazing        []policy.PenGrazing
	Slaughter      []policy.SlaughterFoodAnimal
}
type FishableWater struct {
	Regions           []FishableRegion
	FishingResearched domain.Fact[bool]
	ResearchLeadDays  domain.Fact[float64]
}
type FishableRegion struct {
	Root                                              domain.Cell
	Population, MaxPopulation                         domain.Fact[float64]
	Zoned, Reachable                                  domain.Fact[bool]
	CellCount                                         domain.Fact[uint32]
	Frozen, Delivering                                domain.Fact[bool]
	NutritionPerFish, FishPerBatch, WorkTicksPerBatch domain.Fact[float64]
	ProposedCells                                     []domain.Cell
	PawnFishWorkCapacity                              domain.Fact[float64]
	ConcurrentFishers                                 domain.Fact[uint32]
	DistanceSquared                                   domain.Fact[float64]
}
type GatherableAnimal struct {
	PawnID, Race                          string
	Fullness                              domain.Fact[float64]
	Resource                              domain.Fact[string]
	HandlerReachable                      domain.Fact[bool]
	NutritionPerDay, WorkPerDay, LeadDays domain.Fact[float64]
	Active                                domain.Fact[bool]
}
type EggLayerAnimal struct {
	PawnID, Race              string
	CanLayNow                 domain.Fact[bool]
	Progress                  domain.Fact[float64]
	NutritionPerDay, LeadDays domain.Fact[float64]
	Active                    domain.Fact[bool]
	// Fertilized: the hen's next egg is the fertilized def (CompEggLayer
	// fertilizationCount > 0).
	Fertilized domain.Fact[bool]
}
type PasteDispenser struct {
	BuildingID      string
	Powered         domain.Fact[bool]
	HopperNutrition domain.Fact[float64]
	AdjacentRoomID  domain.Fact[string]
}
type ForagePlant struct {
	DefName         string
	GrowingTwelfths []int32
	GrowingNow      domain.Fact[bool]
}

// Called only after the colony boundary validator has checked the section.
func colonyFoodChannels(section *o.FoodChannelsSection, pawns bridge.Pawns, races policy.AnimalRaceCatalog) domain.Fact[FoodChannels] {
	v := section.GetObserved()
	if v == nil {
		return domain.Unknown[FoodChannels]()
	}
	r := FoodChannels{PollutedCells: optional(v.PollutedCells)}
	for _, row := range v.Grazing {
		r.Grazing = append(r.Grazing, policy.PenGrazing{ID: row.GetPenId(), DemandPerDay: optional(row.DemandPerDay), PasturePerDay: optional(row.PasturePerDay), StoredNutrition: optional(row.StoredNutrition)})
	}
	for _, row := range v.Slaughter {
		r.Slaughter = append(r.Slaughter, slaughterFood(row, pawns, races))
	}
	if water := v.FishableWater; water != nil {
		r.FishableWater = domain.Known(colonyFishableWater(water))
	}
	for _, row := range v.Gatherable {
		r.Gatherable = append(r.Gatherable, GatherableAnimal{PawnID: row.GetPawnId(), Race: row.GetRace(), Fullness: optional(row.Fullness), Resource: optional(row.Resource), HandlerReachable: optional(row.HandlerReachable), NutritionPerDay: optional(row.NutritionPerDay), WorkPerDay: optional(row.WorkPerDay), LeadDays: optional(row.LeadDays), Active: optional(row.Active)})
	}
	for _, row := range v.EggLayer {
		r.EggLayer = append(r.EggLayer, EggLayerAnimal{PawnID: row.GetPawnId(), Race: row.GetRace(), CanLayNow: optional(row.CanLayNow), Progress: optional(row.Progress), NutritionPerDay: optional(row.NutritionPerDay), LeadDays: optional(row.LeadDays), Active: optional(row.Active), Fertilized: optional(row.Fertilized)})
	}
	for _, row := range v.PasteDispenser {
		r.PasteDispenser = append(r.PasteDispenser, PasteDispenser{BuildingID: row.GetBuildingId(), Powered: optional(row.Powered), HopperNutrition: optional(row.HopperNutrition), AdjacentRoomID: optional(row.AdjacentRoomId)})
	}
	for _, row := range v.Forage {
		r.Forage = append(r.Forage, ForagePlant{DefName: row.GetDefName(), GrowingTwelfths: append([]int32{}, row.GrowingTwelfths...), GrowingNow: optional(row.GrowingNow)})
	}
	return domain.Known(r)
}

// slaughterFood is the meat, feed and reproduction numbers of one animal the
// colony would eat. Native sends only the animal's live MeatAmount, and only
// when the colony eats the meat; the race row gives the meat's nutrition, the
// life stage's hunger scales the adult feed (for a plant eater), and the egg
// interval or the gestation is the reproduction period.
func slaughterFood(row *o.FoodSlaughterAnimal, pawns bridge.Pawns, races policy.AnimalRaceCatalog) policy.SlaughterFoodAnimal {
	out := policy.SlaughterFoodAnimal{ID: policy.PawnID(row.GetPawnId()), Race: policy.Resource(row.GetRace())}
	if row.MeatAmount == nil {
		return out
	}
	race, known := races.Race(out.Race)
	if !known {
		return out
	}
	if per, ok := race.MeatNutritionPerUnit.Value(); ok {
		out.MeatNutrition = domain.Known(row.GetMeatAmount() * per)
	}
	if race.EatsPlant {
		if feed, ok := stageFeedPerDay(race, pawns, row.GetPawnId()); ok {
			out.FeedPerDay = domain.Known(feed)
		}
	}
	days := race.GestationDays
	for _, product := range race.Products {
		if product.Kind == "eggs" {
			if interval, ok := product.IntervalDays.Value(); ok {
				days = interval
			}
		}
	}
	if days > 0 {
		out.ReproductionDays = domain.Known(days)
	}
	return out
}

// stageFeedPerDay is the nutrition per day the animal eats in its current
// life stage: the adult feed scaled by the stage's hunger factor against the
// adult stage's.
func stageFeedPerDay(race policy.AnimalRace, pawns bridge.Pawns, id string) (float64, bool) {
	adult, ok := race.AdultFeedPerDay.Value()
	row, found := pawns.Get(id)
	if !ok || !found || row.GetAnimalState() == nil || row.GetAnimalState().LifeStageIndex == nil {
		return 0, false
	}
	stages, idx := race.LifeStages, int(row.GetAnimalState().GetLifeStageIndex())
	if idx < 0 || idx >= len(stages) || !(stages[len(stages)-1].HungerRateFactor > 0) {
		return 0, false
	}
	return adult * stages[idx].HungerRateFactor / stages[len(stages)-1].HungerRateFactor, true
}

// colonyFishableWater applies the policy decisions to native's raw fishing
// facts: research lead, footprint, reachability verdict, delivery and capacity.
func colonyFishableWater(water *o.FishableWater) FishableWater {
	w := FishableWater{FishingResearched: optional(water.FishingResearched)}
	w.ResearchLeadDays = policy.FishingResearchLeadDays(policy.FishingResearchInputs{Researched: w.FishingResearched,
		BaseCost: optional(water.ResearchBaseCost), Progress: optional(water.ResearchProgress), CostFactor: optional(water.ResearchCostFactor),
		ResearcherSpeeds: water.ResearcherSpeeds, PointsPerWorkTick: optional(water.ResearchPointsPerWorkTick), Difficulty: optional(water.ResearchDifficultySpeedFactor)})
	fishers := make([]policy.Fisher, 0, len(water.Fishers))
	for _, f := range water.Fishers {
		fishers = append(fishers, policy.Fisher{Yield: f.GetFishingYield(), Speed: f.GetFishingSpeed()})
	}
	for _, row := range water.Regions {
		region := FishableRegion{Root: domain.Cell{X: row.Root.GetX(), Z: row.Root.GetZ()}, Population: optional(row.Population), MaxPopulation: optional(row.MaxPopulation),
			Zoned: optional(row.Zoned), CellCount: optional(row.CellCount), Frozen: optional(row.Frozen),
			NutritionPerFish: optional(row.NutritionPerFish), FishPerBatch: optional(row.FishPerBatch), WorkTicksPerBatch: optional(row.WorkTicksPerBatch),
			DistanceSquared: optional(row.NearestDistanceSquared), ConcurrentFishers: domain.Known(uint32(len(fishers)))}
		cells := make([]policy.FishingCell, 0, len(row.Cells))
		for _, c := range row.Cells {
			cells = append(cells, policy.FishingCell{Cell: domain.Cell{X: c.Cell.GetX(), Z: c.Cell.GetZ()}, Reachable: c.GetReachable(), DistanceSquared: c.GetDistanceSquared()})
		}
		region.ProposedCells = policy.FishingFootprint(cells, len(fishers))
		region.Reachable = policy.FishingReachable(optional(row.Reachable), region.Zoned, len(region.ProposedCells), len(fishers))
		zones := make([]policy.FishingZoneState, 0, len(row.Zones))
		for _, z := range row.Zones {
			zones = append(zones, policy.FishingZoneState{Allowed: z.GetAllowed(), DoForever: z.GetDoForever(), HasFishableCells: z.GetHasFishableCells()})
		}
		region.Delivering = domain.Known(policy.FishingDelivering(zones))
		region.PawnFishWorkCapacity = policy.FishingWorkCapacity(fishers, optional(row.YieldCurveValue), region.NutritionPerFish, optional(water.BaseFishingDurationTicks))
		w.Regions = append(w.Regions, region)
	}
	return w
}

// AnimalProducts keeps native unknown rates unknown. Non-food comps and inactive
// animals have no nutrition to contribute; eggs require no handler gathering.
func (f FoodChannels) AnimalProducts() []policy.AnimalProduct {
	var rows []policy.AnimalProduct
	for _, a := range f.Gatherable {
		rows = append(rows, policy.AnimalProduct{Pawn: a.PawnID, Race: a.Race, Active: a.Active, Reachable: a.HandlerReachable, NutritionPerDay: a.NutritionPerDay, WorkPerDay: a.WorkPerDay, LeadDays: a.LeadDays})
	}
	for _, a := range f.EggLayer {
		rows = append(rows, policy.AnimalProduct{Pawn: a.PawnID, Race: a.Race, Active: a.Active, Reachable: domain.Known(true), NutritionPerDay: a.NutritionPerDay, WorkPerDay: domain.Known(0.0), LeadDays: a.LeadDays})
	}
	return rows
}
