package observation

import (
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
func colonyFoodChannels(section *o.FoodChannelsSection) domain.Fact[FoodChannels] {
	v := section.GetObserved()
	if v == nil {
		return domain.Unknown[FoodChannels]()
	}
	r := FoodChannels{PollutedCells: optional(v.PollutedCells)}
	for _, row := range v.Grazing {
		r.Grazing = append(r.Grazing, policy.PenGrazing{ID: row.GetPenId(), DemandPerDay: optional(row.DemandPerDay), PasturePerDay: optional(row.PasturePerDay), StoredNutrition: optional(row.StoredNutrition)})
	}
	for _, row := range v.Slaughter {
		r.Slaughter = append(r.Slaughter, policy.SlaughterFoodAnimal{ID: policy.PawnID(row.GetPawnId()), Race: policy.Resource(row.GetRace()), MeatNutrition: optional(row.MeatNutrition), FeedPerDay: optional(row.FeedPerDay), ReproductionDays: optional(row.ReproductionDays)})
	}
	if water := v.FishableWater; water != nil {
		w := FishableWater{FishingResearched: optional(water.FishingResearched), ResearchLeadDays: optional(water.ResearchLeadDays)}
		for _, row := range water.Regions {
			w.Regions = append(w.Regions, FishableRegion{Root: domain.Cell{X: row.Root.GetX(), Z: row.Root.GetZ()}, Population: optional(row.Population), MaxPopulation: optional(row.MaxPopulation), Zoned: optional(row.Zoned), Reachable: optional(row.Reachable), CellCount: optional(row.CellCount)})
			last := &w.Regions[len(w.Regions)-1]
			last.Frozen, last.Delivering = optional(row.Frozen), optional(row.Delivering)
			last.PawnFishWorkCapacity, last.ConcurrentFishers = optional(row.PawnFishWorkCapacity), optional(row.ConcurrentFishers)
			last.NutritionPerFish, last.FishPerBatch, last.WorkTicksPerBatch = optional(row.NutritionPerFish), optional(row.FishPerBatch), optional(row.WorkTicksPerBatch)
			for _, cell := range row.ProposedCells {
				last.ProposedCells = append(last.ProposedCells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
			}
		}
		r.FishableWater = domain.Known(w)
	}
	for _, row := range v.Gatherable {
		r.Gatherable = append(r.Gatherable, GatherableAnimal{PawnID: row.GetPawnId(), Race: row.GetRace(), Fullness: optional(row.Fullness), Resource: optional(row.Resource), HandlerReachable: optional(row.HandlerReachable), NutritionPerDay: optional(row.NutritionPerDay), WorkPerDay: optional(row.WorkPerDay), LeadDays: optional(row.LeadDays), Active: optional(row.Active)})
	}
	for _, row := range v.EggLayer {
		r.EggLayer = append(r.EggLayer, EggLayerAnimal{PawnID: row.GetPawnId(), Race: row.GetRace(), CanLayNow: optional(row.CanLayNow), Progress: optional(row.Progress), NutritionPerDay: optional(row.NutritionPerDay), LeadDays: optional(row.LeadDays), Active: optional(row.Active)})
	}
	for _, row := range v.PasteDispenser {
		r.PasteDispenser = append(r.PasteDispenser, PasteDispenser{BuildingID: row.GetBuildingId(), Powered: optional(row.Powered), HopperNutrition: optional(row.HopperNutrition), AdjacentRoomID: optional(row.AdjacentRoomId)})
	}
	for _, row := range v.Forage {
		r.Forage = append(r.Forage, ForagePlant{DefName: row.GetDefName(), GrowingTwelfths: append([]int32{}, row.GrowingTwelfths...), GrowingNow: optional(row.GrowingNow)})
	}
	return domain.Known(r)
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
