package bridge

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// haulModel is the goods a colonist works out of the map and what each costs in
// work (policy.HaulModel): per natural-rock mineable def with a yield, the work
// of its hit points at the pick's rate and the yield it drops; per tree def with
// a harvest, its harvest work and yield. The defs of one yielded item average. A
// mineable or tree whose numbers the catalog does not hold adds nothing (unknown,
// never zero); a catalog without the four stats leaves the model unknown.
func (catalog *DefinitionCatalog) haulModel() policy.HaulModel {
	game := policy.HaulGame()
	stat := map[string]float64{}
	for _, name := range []string{game.MiningSpeed, game.MiningYield, game.PlantWorkSpeed, game.PlantHarvestYield} {
		row := DefRow[*d.StatDef](catalog, name)
		if row == nil || !(row.GetDefaultBaseValue() > 0) {
			return policy.HaulModel{}
		}
		stat[name] = float64(row.GetDefaultBaseValue())
	}
	mined, chopped := map[policy.Resource][]policy.HaulSource{}, map[policy.Resource][]policy.HaulSource{}
	for _, row := range catalog.ThingDefs {
		if b := row.GetBuilding(); b.GetMineableThing() != "" && b.GetIsNaturalRock() && b.GetMineableYield() > 0 {
			hp, ok := statBaseEntry(row, "MaxHitPoints")
			if !ok || hp <= 0 {
				continue
			}
			units := math.Max(1, float64(b.GetMineableYield())) * float64(b.GetMineableDropChance())
			if b.GetMineableYieldWasteable() {
				units *= stat[game.MiningYield]
			}
			work := math.Ceil(hp/game.NaturalHit) * game.PickTicks / stat[game.MiningSpeed]
			good := policy.Resource(b.GetMineableThing())
			mined[good] = append(mined[good], policy.HaulSource{Good: good, Units: units, Work: work})
		}
		if p := row.GetPlant(); p.GetHarvestedThingDef() != "" && p.GetTreeCategory() != d.TreeCategory_TREE_CATEGORY_NONE && !p.GetIsStump() && p.GetHarvestWork() > 0 && p.GetHarvestYield() > 0 {
			good := policy.Resource(p.GetHarvestedThingDef())
			chopped[good] = append(chopped[good], policy.HaulSource{Good: good, Units: float64(p.GetHarvestYield()) * stat[game.PlantHarvestYield], Work: float64(p.GetHarvestWork()) / stat[game.PlantWorkSpeed]})
		}
	}
	return policy.HaulModel{Mined: averageHaulSources(mined), Chopped: averageHaulSources(chopped)}
}

// averageHaulSources is one source a good: the mean of its defs' yields and work.
func averageHaulSources(byGood map[policy.Resource][]policy.HaulSource) []policy.HaulSource {
	out := make([]policy.HaulSource, 0, len(byGood))
	for good, defs := range byGood {
		var units, work float64
		for _, s := range defs {
			units += s.Units
			work += s.Work
		}
		n := float64(len(defs))
		out = append(out, policy.HaulSource{Good: good, Units: units / n, Work: work / n})
	}
	slices.SortFunc(out, func(a, b policy.HaulSource) int { return compareStrings(string(a.Good), string(b.Good)) })
	return out
}
