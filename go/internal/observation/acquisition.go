package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	operationspb "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// ColonyAcquisition decodes the native-approved harvest/hunt source census
// of one colony facts read; unknown when the acquisition section failed.
// Hunt rows policy.HuntGate holds are left out (ColonyProjection.HuntHolds).
func ColonyAcquisition(v *o.ColonyFactsSnapshot, tables bridge.Tables) domain.Fact[[]policy.AcquisitionSource] {
	rows, _ := decodeAcquisition(v, tables)
	return rows
}

// decodeAcquisition maps the census rows. A native read carries the raw hunt
// census, and every hunt row passes policy.HuntGate: a held row is not a source
// but a hold.
func decodeAcquisition(v *o.ColonyFactsSnapshot, tables bridge.Tables) (domain.Fact[[]policy.AcquisitionSource], []policy.HuntHold) {
	if hasIssue(v.Issues, "acquisition") || !headed(tables, v.Acquisition, (*o.AcquisitionFacts).GetSource) {
		return domain.Unknown[[]policy.AcquisitionSource](), nil
	}
	races, err := tables.Catalog.AnimalRaces()
	if err != nil {
		return domain.Unknown[[]policy.AcquisitionSource](), nil
	}
	census, err := huntCensus(v.HuntCensus, tables.Catalog)
	if err != nil {
		return domain.Unknown[[]policy.AcquisitionSource](), nil
	}
	rows := []policy.AcquisitionSource{}
	var holds []policy.HuntHold
	for _, row := range v.Acquisition {
		source := tables.Entity(row.Source)
		race, _ := races.Race(policy.Resource(source.GetDefName()))
		offered := policy.AcquisitionSource{ID: row.Source.GetId(), Resource: row.GetResource(), Token: row.SourceSnapshot.GetToken(), Definition: source.GetDefName(), Cell: domain.Cell{X: source.GetPosition().GetX(), Z: source.GetPosition().GetZ()}, Hunt: row.GetHunt(), Food: row.GetFood(), Designated: row.GetDesignated(), Yield: row.GetYield(), HerdSize: int(row.GetHerdSize()), Downed: row.GetDowned(), Sleeping: row.GetSleeping(), Pest: race.Pest, DesignatedTick: domain.Tick(row.GetDesignatedTick()), Taken: row.GetTaken(), Growth: row.GetGrowth(), Plantation: row.GetPlantation()}
		if offered.Hunt {
			// The race row's own flags and numbers: an inedible hunt is only
			// ever a pest; the meat's nutrition is the live MeatAmount stat
			// times the meat def's Nutrition.
			offered.Food, offered.Predator = !race.Pest, race.Predator
			chance, ok := race.ManhunterOnDamage.Value()
			if !ok {
				return domain.Unknown[[]policy.AcquisitionSource](), nil
			}
			offered.RevengeChance = chance
			if !race.Pest {
				perUnit, ok := race.MeatNutritionPerUnit.Value()
				if !ok || row.MeatAmount == nil {
					return domain.Unknown[[]policy.AcquisitionSource](), nil
				}
				offered.NutritionYield = max(0, row.GetMeatAmount()) * perUnit
			}
			offered.MeleeOnly = meleeable(row, tables.Pawns, race, offered)
		} else {
			offered.Tree = tables.Catalog.PlantIsTree(source.GetDefName())
			if offered.Food {
				nutrition, shown, err := tables.Catalog.ShownStatValue(row.GetResource(), "", bridge.StatNutrition)
				if err != nil {
					return domain.Unknown[[]policy.AcquisitionSource](), nil
				}
				if shown {
					offered.NutritionYield = row.GetYield() * float64(nutrition)
				}
			}
		}
		if offered.Hunt {
			offered.Products = race.Butchery
			verdict := census.Gate(policy.HuntPrey{Source: offered, Fogged: row.GetFogged(), Mental: row.GetInMentalState()})
			if verdict.Hold != nil {
				hold := *verdict.Hold
				hold.Source = offered
				holds = append(holds, hold)
				continue
			}
			offered.WeaponRange = verdict.WeaponRange
		}
		rows = append(rows, offered)
	}
	return domain.Known(rows), holds
}

// meleeable is the day-one interim food rule over the race row: safe prey (not
// in a mental state, its meat nourishing) that is no predator, and downed or
// docile (no manhunter chance on damage) and no bigger than a colonist, which
// flees rather than fights back, so a melee weapon or bare hands can run it
// down. Pawn.BodySize is the current life stage's bodySizeFactor (the prey's
// life stage index, the pawn row's) times the race's baseBodySize; a prey whose
// row or stage is not held is not known to be small, so not meleeable. The
// comparison is in float32, as the game's.
func meleeable(row *o.AcquisitionFacts, pawns bridge.Pawns, race policy.AnimalRace, prey policy.AcquisitionSource) bool {
	nutrition, known := race.MeatNutritionPerUnit.Value()
	if row.GetInMentalState() || race.Predator || !known || !(nutrition > 0) {
		return false
	}
	if prey.Downed {
		return true
	}
	if chance, ok := race.ManhunterOnDamage.Value(); !ok || chance != 0 {
		return false
	}
	state, ok := pawns.Row(row.Source)
	size, sized := race.BodySize.Value()
	if !ok || state.GetAnimalState() == nil || !sized {
		return false
	}
	index := state.GetAnimalState().LifeStageIndex
	if index == nil || *index < 0 || int(*index) >= len(race.LifeStages) {
		return false
	}
	return float32(race.LifeStages[*index].BodySizeFactor)*float32(size) <= 1.0
}

// huntCensus decodes native's raw hunt facts.
func huntCensus(v *o.HuntCensus, catalog *bridge.DefinitionCatalog) (census policy.HuntCensus, err error) {
	for _, b := range v.GetBenches() {
		bench := policy.HuntBench{ID: b.GetBenchId(), Usable: b.GetUsable()}
		for _, bill := range b.Bills {
			row := policy.HuntBill{Suspended: bill.GetSuspended(), Paused: bill.GetPaused(), Count: int(bill.GetRepeatCount()), Target: int(bill.GetTargetCount()), AllowedCorpses: set(bill.AllowedCorpses)}
			switch bill.GetRepeatMode() {
			case operationspb.RepeatMode_REPEAT_MODE_FOREVER:
				row.Repeat = policy.HuntRepeatForever
			case operationspb.RepeatMode_REPEAT_MODE_COUNT:
				row.Repeat = policy.HuntRepeatCount
			case operationspb.RepeatMode_REPEAT_MODE_TARGET:
				row.Repeat = policy.HuntRepeatTarget
			}
			if bill.ProductCount != nil {
				n := int(bill.GetProductCount())
				row.Product = &n
			}
			bench.Bills = append(bench.Bills, row)
		}
		census.Benches = append(census.Benches, bench)
	}
	for _, h := range v.GetHunters() {
		hunter := policy.HuntHunter{ID: h.GetPawnId(), Cell: domain.Cell{X: h.GetPosition().GetX(), Z: h.GetPosition().GetZ()},
			Downed: h.GetDowned(), MentalState: h.GetInMentalState(), Drafted: h.GetDrafted(),
			HuntingPriority: int(h.GetHuntingPriority()), HuntingActive: h.GetHuntingActive(), HuntingDisabled: h.GetHuntingDisabled(), CookingActive: h.GetCookingActive(),
			HasHuntingWeapon: h.GetHasHuntingWeapon(), RangedBlockingShield: h.GetRangedBlockingShield(),
			ReachableBenches: set(h.ReachableBenches)}
		hunter.RouteSafePrey, hunter.RouteUnsafePrey, hunter.RouteSkippedPrey = map[string]bool{}, map[string]bool{}, map[string]bool{}
		for _, r := range h.Routes {
			switch {
			case r.GetSkipped():
				hunter.RouteSkippedPrey[r.GetPreyId()] = true
			case r.GetSafe():
				hunter.RouteSafePrey[r.GetPreyId()] = true
			default:
				hunter.RouteUnsafePrey[r.GetPreyId()] = true
			}
		}
		// The weapon's verbs and projectile are the def rows' (Catalog.HuntWeapon).
		if hunter.Weapon, err = catalog.HuntWeapon(h.GetWeaponDef()); err != nil {
			return census, err
		}
		census.Hunters = append(census.Hunters, hunter)
	}
	return census, nil
}

func set(values []string) map[string]bool {
	out := make(map[string]bool, len(values))
	for _, v := range values {
		out[v] = true
	}
	return out
}

func colonyAcquisition(v *o.ColonyFactsSnapshot, tables bridge.Tables, r *ColonyProjection) {
	r.Acquisition, r.HuntHolds = decodeAcquisition(v, tables)
	if !hasIssue(v.Issues, "pending_food_nutrition") {
		r.PendingFoodNutrition = optional(v.PendingFoodNutrition)
	}
	if !hasIssue(v.Issues, "pending_hunts") && v.PendingHunts != nil {
		r.PendingHunts = domain.Known(int(v.GetPendingHunts()))
	}
	if !hasIssue(v.Issues, "pending_wood_units") {
		r.PendingWoodUnits = optional(v.PendingWoodUnits)
	}
}
