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
	census := huntCensus(v.HuntCensus)
	rows := []policy.AcquisitionSource{}
	var holds []policy.HuntHold
	for _, row := range v.Acquisition {
		source := tables.Entity(row.Source)
		// An inedible hunt is only ever a pest (#247), a race row's flag.
		race, _ := races.Race(policy.Resource(source.GetDefName()))
		if row.GetHunt() && !row.GetFood() && !race.Pest {
			continue
		}
		offered := policy.AcquisitionSource{ID: row.Source.GetId(), Resource: row.GetResource(), Token: row.SourceSnapshot.GetToken(), Definition: source.GetDefName(), Cell: domain.Cell{X: source.GetPosition().GetX(), Z: source.GetPosition().GetZ()}, Hunt: row.GetHunt(), Tree: row.GetTree(), Food: row.GetFood(), Designated: row.GetDesignated(), Yield: row.GetYield(), NutritionYield: row.GetNutritionYield(), RevengeChance: row.GetRevengeChance(), HerdSize: int(row.GetHerdSize()), MeleeOnly: row.GetMeleeOnly(), Downed: row.GetDowned(), BodySize: row.GetBodySize(), Sleeping: row.GetSleeping(), Predator: row.GetPredator(), Pest: race.Pest, DesignatedTick: domain.Tick(row.GetDesignatedTick()), Taken: row.GetTaken()}
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

// huntCensus decodes native's raw hunt facts.
func huntCensus(v *o.HuntCensus) (census policy.HuntCensus) {
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
			RouteSafePrey: set(h.RouteSafePrey), ReachableBenches: set(h.ReachableBenches)}
		if w := h.Weapon; w != nil {
			weapon := &policy.HuntWeapon{DefName: w.GetDefName(), Ranged: w.GetRanged(), Melee: w.GetMelee()}
			for _, verb := range w.Verbs {
				weapon.Verbs = append(weapon.Verbs, policy.HuntVerb{Melee: verb.GetMelee(), AIWeapon: verb.GetAiWeapon(), Range: verb.GetRange(), ExplosionRadius: verb.GetExplosionRadius(), Warmup: verb.GetWarmup(),
					Projectile: huntProjectile(verb.GetProjectileKind()), DamageDef: verb.GetDamageDef(), DamageWorker: verb.GetDamageWorker()})
			}
			hunter.Weapon = weapon
		}
		census.Hunters = append(census.Hunters, hunter)
	}
	return census
}

func huntProjectile(kind o.HuntProjectileKind) policy.HuntProjectile {
	switch kind {
	case o.HuntProjectileKind_HUNT_PROJECTILE_KIND_BULLET:
		return policy.HuntProjectileBullet
	case o.HuntProjectileKind_HUNT_PROJECTILE_KIND_ARROW:
		return policy.HuntProjectileArrow
	case o.HuntProjectileKind_HUNT_PROJECTILE_KIND_OTHER:
		return policy.HuntProjectileOther
	}
	return policy.HuntProjectileNone
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
