package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Organ harvest (#1169, epic #1160): MaintainSurgery harvests an organ from
// a prisoner the colony would not recruit (PrisonerFacts.HarvestEligible)
// only for a concrete need, and only when the gain outweighs the cost, both
// in silver:
//
//   - a colonist's failing or missing natural organ that no stocked part can
//     replace (a surgery_part_short want whose natural recipe installs a
//     harvestable organ): the gain is the capacity the install gives back,
//     servedSurgery's weight x SilverPerCapacity; the harvested organ then
//     feeds the existing restore or replacement install.
//   - a silver runway deficit (SilverShort) with no harvestable organ already
//     stocked: the gain is the organ's native market value; the organ sells
//     through the routine trade as surplus while the deficit holds.
//
// The cost is the colony mood thoughts vanilla's OrganUse precept gives,
// SilverPerMoodPoint x magnitude x affected colonists, plus the prisoner's
// faction goodwill change x SilverPerGoodwillPoint. An Abhorrent (or
// unrecognized) precept refuses outright. Only a paired organ with both
// present is taken: never a heart, liver or last lung or kidney, and never
// a harvest native reports lethal.

// Silver prices of the cost model (#1169).
//
// SilverPerMoodPoint: one mood point on one colonist for a thought's whole
// duration (vanilla's harvest thoughts last 8 days). Twenty silver makes a
// classic harvest in a five-colonist colony (-5 x 5) cost 500, about half a
// kidney's 900: harvesting is worth it for a small colony, and with a
// faction's goodwill hit on top it is refused from seven colonists (1050).
//
// SilverPerGoodwillPoint: vanilla's -70 harvest report costs 350 silver, the
// rough value of the trade and raid-relief a faction's lost goodwill gives up.
//
// SilverPerCapacity: a full body part's capacity (partWeight 1, a kidney or
// lung) given back to a colonist, priced above any organ's market value so a
// colonist's need outranks a sale.
const (
	SilverPerMoodPoint     = 20.0
	SilverPerGoodwillPoint = 5.0
	SilverPerCapacity      = 1500.0
)

// Vanilla thought magnitudes (Core Precepts_OrganUse.xml,
// Thoughts_Memory_Misc.xml; Ideology Precepts_OrganUse.xml).
const (
	classicKnowHarvestMood   = 5  // KnowGuestOrganHarvested, every colonist
	horribleKnowHarvestMood  = 4  // HarvestedOrgan_Know_Horrible_Mood
	horribleDoerHarvestMood  = 15 // HarvestedOrgan_Horrible, the surgeon
	horribleKnowSoldMood     = 2  // SoldOrgan_Know_Horrible_Mood (HorribleNoSell)
	horribleSellerSoldMood   = 8  // SoldOrgan_Disapproved, the seller
	organUseClassic          = "OrganUse_Classic"
	organUseAbhorrent        = "OrganUse_Abhorrent"
	organUseHorribleNoSell   = "OrganUse_HorribleNoSell"
	organUseHorribleSellOK   = "OrganUse_HorribleSellOK"
	organUseAcceptable       = "OrganUse_Acceptable"
	harvestInstallRecipePref = "InstallNatural"
)

// HarvestOrgans are the paired organs harvest takes, by body part defName
// (each spawns the item of the same defName).
var HarvestOrgans = []string{"Kidney", "Lung"}

func harvestOrgan(part string) bool {
	for _, organ := range HarvestOrgans {
		if organ == part {
			return true
		}
	}
	return false
}

// HarvestMood is the colony mood points a harvest costs under the OrganUse
// precept, plus the sale thoughts when the organ is sold. ok is false when
// the precept refuses: Abhorrent, or one the model does not know.
func HarvestMood(precept string, colonists int, sale bool) (mood float64, ok bool) {
	n := float64(max(0, colonists))
	switch precept {
	case "", organUseClassic:
		return classicKnowHarvestMood * n, true
	case organUseAcceptable:
		return 0, true
	case organUseHorribleSellOK:
		return horribleKnowHarvestMood*n + horribleDoerHarvestMood, true
	case organUseHorribleNoSell:
		mood = horribleKnowHarvestMood*n + horribleDoerHarvestMood
		if sale {
			mood += horribleKnowSoldMood*n + horribleSellerSoldMood
		}
		return mood, true
	}
	return 0, false
}

// HarvestCost is the silver a harvest costs the colony: mood at
// SilverPerMoodPoint plus the goodwill change (<= 0) at
// SilverPerGoodwillPoint. ok is false when the precept refuses.
func HarvestCost(c PrisonerColony, goodwill int, sale bool) (float64, bool) {
	mood, ok := HarvestMood(c.OrganUsePrecept, c.Colonists, sale)
	return SilverPerMoodPoint*mood + SilverPerGoodwillPoint*float64(max(0, -goodwill)), ok
}

// OrganNeed is one concrete need a harvest answers: a colonist's install
// (For set, Gain its capacity in silver) or a sale (For empty; the gain is
// the harvested organ's market value).
type OrganNeed struct {
	Organ string
	For   PawnID
	Gain  float64
}

// OrganNeeds lists the colonists' organ needs, then one sale need per
// organ while silver is short and none of it is stocked. A colonist's need
// is a part-short want whose natural recipe installs a harvestable organ
// none of which is stocked.
func OrganNeeds(pawns domain.Fact[[]CarePawn], wants []SurgeryWant, silverShort bool, stock map[Resource]int64) []OrganNeed {
	rows, _ := pawns.Value()
	byID := map[PawnID]CarePawn{}
	for _, row := range rows {
		byID[row.ID] = row
	}
	var out []OrganNeed
	for _, want := range wants {
		pawn, ok := byID[want.Pawn]
		if want.Reason != SurgeryPartShort || !ok {
			continue
		}
		ops, _ := pawn.Operations.Value()
		best := OrganNeed{}
		for _, op := range ops {
			part, pk := op.PartIndex.Value()
			recipe, _ := op.Recipe.Value()
			organ, _ := op.PartDefName.Value()
			if !pk || part != want.Part || !harvestOrgan(organ) || recipe != harvestInstallRecipePref+organ || stock[Resource(organ)] > 0 {
				continue
			}
			if weight, _ := servedSurgery(pawn, op); weight*SilverPerCapacity > best.Gain {
				best = OrganNeed{Organ: organ, For: pawn.ID, Gain: weight * SilverPerCapacity}
			}
		}
		if best.Gain > 0 {
			out = append(out, best)
		}
	}
	if silverShort {
		for _, organ := range HarvestOrgans {
			if stock[Resource(organ)] == 0 {
				out = append(out, OrganNeed{Organ: organ})
			}
		}
	}
	return out
}

// OrganHarvest is one prisoner removal to queue as a SurgeryIntent: an
// organ harvest, or an added part recovery (#1232, Organ the part item).
// Violation sets acknowledge_violation.
type OrganHarvest struct {
	Prisoner   domain.PawnID
	Recipe     string
	Part       int
	Organ      string
	For        PawnID
	Gain, Cost float64
	Violation  bool
	Step       PegCycleStep // a peg-leg cycling step (#1236); zero otherwise
	// Surgeon restricts a training step's bill to the doctor who needs the
	// XP (#1253); empty keeps vanilla's choice.
	Surgeon domain.PawnID
}

// SelectOrganHarvest picks at most one harvest: nothing while any prisoner
// has a queued bill or an open surgery action (inFlight), so one harvest
// lands before the next is weighed. Each eligible prisoner's harvest ops on
// a needed organ it has two of, not lethal, stocked, and within
// RestoreFailureCap for some eligible doctor, are priced; a colonist's need
// comes before a sale, then the best gain less cost, then prisoner id. A
// harvest whose cost reaches its gain, or whose precept refuses, is never
// chosen. Unknown facts refuse.
func SelectOrganHarvest(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], needs []OrganNeed, inFlight map[PawnID]bool) (OrganHarvest, bool) {
	rows, rk := prisoners.Value()
	c, ck := colony.Value()
	if !rk || !ck || len(needs) == 0 || surgeryInFlight(rows, inFlight) {
		return OrganHarvest{}, false
	}
	var best OrganHarvest
	found := false
	for _, row := range rows {
		if eligible, known := row.HarvestEligible(c).Value(); !known || !eligible {
			continue
		}
		goodwill, gk := row.HarvestGoodwill.Value()
		ops, ok := row.Operations.Value()
		if !gk || !ok {
			continue
		}
		pairs := map[string]int{}
		for _, op := range ops {
			if organ, _ := op.PartDefName.Value(); op.Kind == SurgeryHarvest {
				pairs[organ]++
			}
		}
		for _, op := range ops {
			organ, _ := op.PartDefName.Value()
			if op.Kind != SurgeryHarvest || !harvestOrgan(organ) || pairs[organ] < 2 || !harvestAcceptable(op) {
				continue
			}
			recipe, _ := op.Recipe.Value()
			part, _ := op.PartIndex.Value()
			for _, need := range needs {
				if need.Organ != organ {
					continue
				}
				gain := need.Gain
				if need.For == "" {
					value, known := op.YieldValue.Value()
					if !known || !finite(value) {
						continue
					}
					gain = value
				}
				cost, ok := HarvestCost(c, goodwill, need.For == "")
				h := OrganHarvest{Prisoner: row.Pawn, Recipe: recipe, Part: part, Organ: organ, For: need.For, Gain: gain, Cost: cost, Violation: true}
				if ok && gain > cost && (!found || betterHarvest(h, best)) {
					best, found = h, true
				}
			}
		}
	}
	return best, found
}

// surgeryInFlight: some living prisoner has a queued bill (or an unknown
// bill stack) or an open surgery action, so no new cut is weighed.
func surgeryInFlight(rows []PrisonerFacts, inFlight map[PawnID]bool) bool {
	for _, row := range rows {
		if queued, known := row.QueuedSurgeries.Value(); !known || queued > 0 || inFlight[PawnID(row.Pawn)] {
			if dead, _ := row.Dead.Value(); !dead {
				return true
			}
		}
	}
	return false
}

// betterHarvest: a colonist's need before a sale, then the earlier peg
// step, then gain less cost, then prisoner id.
func betterHarvest(h, best OrganHarvest) bool {
	if (h.For != "") != (best.For != "") {
		return h.For != ""
	}
	if h.Step != best.Step {
		return h.Step < best.Step
	}
	if h.Gain-h.Cost != best.Gain-best.Cost {
		return h.Gain-h.Cost > best.Gain-best.Cost
	}
	return h.Prisoner < best.Prisoner
}

// harvestAcceptable: a part harvest native reports non-lethal, with its
// ingredients on the map and some eligible doctor within RestoreFailureCap.
// The violation is acknowledged, not refused.
func harvestAcceptable(op SurgeryOperation) bool {
	doctors, dk := op.EligibleDoctors.Value()
	chance, ck := op.SuccessChance.Value()
	lethal, lk := op.Lethal.Value()
	stocked, sk := op.IngredientsOnMap.Value()
	_, pk := op.PartIndex.Value()
	_, rk := op.Recipe.Value()
	return dk && ck && lk && sk && pk && rk && doctors > 0 && stocked && !lethal && 1-chance <= RestoreFailureCap+1e-9
}

// SaleHarvestWanted reports whether a sale harvest would be queued now: the
// silver runway is short and some eligible prisoner's organ clears its cost.
// DetectRoutine holds MaintainSurgery open on it (#1169).
func SaleHarvestWanted(f RoutineFacts, silverShort domain.Fact[bool]) bool {
	if !positive(silverShort) {
		return false
	}
	stock := map[Resource]int64{}
	rows, _ := f.Resources.Value()
	for _, row := range rows {
		stock[row.Resource] += row.Count
	}
	_, ok := SelectOrganHarvest(f.Prisoners, f.PrisonerColony, OrganNeeds(domain.Unknown[[]CarePawn](), nil, true, stock), nil)
	return ok
}

// OrganSaleSurplus adds each harvested organ in stock to the trade need's
// surplus while the silver runway is short (#1169): the sale path's organ
// sells through SelectTrade like any surplus, keeping none.
func OrganSaleSurplus(need domain.Fact[TradeNeed], resources domain.Fact[[]Amount], colonists domain.Fact[int64]) domain.Fact[TradeNeed] {
	n, nk := need.Value()
	rows, rk := resources.Value()
	if !nk || !rk || !positive(SilverShort(need, SilverStock(resources), colonists)) {
		return need
	}
	stock := map[Resource]int64{}
	for _, row := range rows {
		stock[row.Resource] += row.Count
	}
	organs := append([]string{}, HarvestOrgans...)
	sort.Strings(organs)
	for _, organ := range organs {
		if count := stock[Resource(organ)]; count > 0 {
			n.Surplus = append(n.Surplus, Amount{Resource: Resource(organ), Count: count})
			n.retain(Resource(organ), 0)
		}
	}
	return domain.Known(n)
}

// Artificial part recovery (#1232, epic #1160): MaintainSurgery removes an
// added part (bionic, prosthetic, archotech) from a HarvestEligible
// prisoner so the colony can install or sell it. The gain is a colonist's
// capacity the part restores (a surgery_part_short want with an install
// recipe for it, servedSurgery's weight x SilverPerCapacity) or else the
// part's market value in stock, where the #1168 part demand and the trade
// surplus use it. The prisoner's own market value is not a cost: vanilla's
// price already counts the part, and nothing sells prisoners. The cost is
// the medicine used, plus HarvestCost's mood and goodwill only when native
// flags the removal a violation (then acknowledged). Shares the harvest's
// one-surgery-at-a-time rule; SelectOrganHarvest's harvest goes first.

// unrecoveredParts are added part hediffs never removed: worth nothing
// (a peg leg or wooden hand/foot gives 1 wood, dentures nothing) or not
// removable at all (joywire, death acidifier, mindscrew).
var unrecoveredParts = map[string]bool{
	"PegLeg": true, "WoodenHand": true, "WoodenFoot": true, "Denture": true,
	"Joywire": true, "DeathAcidifier": true, "Mindscrew": true,
}

// keptBodyParts are body parts whose added part stays: a heart kills and a
// spine leaves the prisoner helpless.
var keptBodyParts = map[string]bool{"Heart": true, "Spine": true}

// PartRecoveryNeeds lists the colonists' part-short wants an added part
// could serve: one need per install option, Organ the part item.
func PartRecoveryNeeds(pawns domain.Fact[[]CarePawn], wants []SurgeryWant) []OrganNeed {
	rows, _ := pawns.Value()
	byID := map[PawnID]CarePawn{}
	for _, row := range rows {
		byID[row.ID] = row
	}
	var out []OrganNeed
	for _, want := range wants {
		pawn, ok := byID[want.Pawn]
		if want.Reason != SurgeryPartShort || !ok {
			continue
		}
		ops, _ := pawn.Operations.Value()
		for _, op := range ops {
			part, pk := op.PartIndex.Value()
			recipe, _ := op.Recipe.Value()
			item, ik := SurgeryPartItem(recipe)
			if !pk || part != want.Part || !ik || harvestOrgan(string(item)) {
				continue
			}
			if weight, _ := servedSurgery(pawn, op); weight > 0 {
				out = append(out, OrganNeed{Organ: string(item), For: pawn.ID, Gain: weight * SilverPerCapacity})
			}
		}
	}
	return out
}

// SelectPartRecovery picks at most one added part removal under the
// harvest's in-flight rule: a colonist's need first, then the best gain
// less cost, then prisoner id. A removal is skipped when its part is in
// unrecoveredParts, sits on a keptBodyParts part, is a leg on a prisoner
// already missing one, or fails harvestAcceptable (lethal included).
func SelectPartRecovery(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], needs []OrganNeed, inFlight map[PawnID]bool) (OrganHarvest, bool) {
	rows, rk := prisoners.Value()
	c, ck := colony.Value()
	if !rk || !ck || surgeryInFlight(rows, inFlight) {
		return OrganHarvest{}, false
	}
	var best OrganHarvest
	found := false
	for _, row := range rows {
		if eligible, known := row.HarvestEligible(c).Value(); !known || !eligible {
			continue
		}
		ops, ok := row.Operations.Value()
		missing, mk := row.MissingParts.Value()
		if !ok || !mk {
			continue
		}
		legless := false
		for _, m := range missing {
			if name, _ := m.PartDefName.Value(); name == "Leg" {
				legless = true
			}
		}
		for _, op := range ops {
			hediff, hk := op.AddedPart.Value()
			item, ik := op.YieldThing.Value()
			value, vk := op.YieldValue.Value()
			body, _ := op.PartDefName.Value()
			if op.Kind != SurgeryAmputate || !hk || !ik || !vk || !finite(value) || unrecoveredParts[hediff] ||
				keptBodyParts[body] || (body == "Leg" && legless) || !harvestAcceptable(op) {
				continue
			}
			cost, ok := partRecoveryCost(op, row, c)
			if !ok {
				continue
			}
			recipe, _ := op.Recipe.Value()
			part, _ := op.PartIndex.Value()
			violation, _ := op.Violation.Value()
			h := OrganHarvest{Prisoner: row.Pawn, Recipe: recipe, Part: part, Organ: item, Gain: value, Cost: cost, Violation: violation}
			for _, need := range needs {
				if need.Organ == item && need.For != "" && (h.For == "" || need.Gain > h.Gain) {
					h.For, h.Gain = need.For, need.Gain
				}
			}
			if h.Gain > h.Cost && (!found || betterHarvest(h, best)) {
				best, found = h, true
			}
		}
	}
	return best, found
}

// partRecoveryCost is the medicine's value plus, when native flags the
// removal a violation, HarvestCost's mood and goodwill. ok is false when a
// needed fact is unknown or the precept refuses.
func partRecoveryCost(op SurgeryOperation, row PrisonerFacts, c PrisonerColony) (float64, bool) {
	cost := 0.0
	if medicine, known := op.MedicineValue.Value(); known {
		if !finite(medicine) {
			return 0, false
		}
		cost = medicine
	}
	violation, vk := op.Violation.Value()
	if !vk {
		return 0, false
	}
	if violation {
		goodwill, gk := row.HarvestGoodwill.Value()
		extra, ok := HarvestCost(c, goodwill, false)
		if !gk || !ok {
			return 0, false
		}
		cost += extra
	}
	return cost, true
}

// PartRecoveryWanted reports whether a stock recovery would be queued now;
// DetectRoutine holds MaintainSurgery open on it (#1232).
func PartRecoveryWanted(f RoutineFacts) bool {
	_, ok := SelectPartRecovery(f.Prisoners, f.PrisonerColony, nil, nil)
	return ok
}

// ReserveSurgeryStock keeps one stocked unit per open colonist restore or
// install (#1254): each want and each choice this review would queue holds
// back its best recipe item still in the surplus, so a harvested organ or
// recovered part is not sold before its install is queued.
func ReserveSurgeryStock(need domain.Fact[TradeNeed], pawns domain.Fact[[]CarePawn]) domain.Fact[TradeNeed] {
	n, known := need.Value()
	if !known || len(n.Surplus) == 0 {
		return need
	}
	s := SelectSurgery(pawns, nil, SurgeryContext{})
	options := make([][]string, 0, len(s.Wants)+len(s.Queue))
	for _, want := range s.Wants {
		options = append(options, want.Options)
	}
	for _, choice := range s.Queue {
		options = append(options, []string{choice.Recipe})
	}
	surplus := append([]Amount(nil), n.Surplus...)
	retained := map[Resource]int64{}
	for resource, count := range n.Retained {
		retained[resource] = count
	}
	reserved := false
	for _, recipes := range options {
	recipe:
		for _, recipe := range recipes {
			item, ok := SurgeryPartItem(recipe)
			if organ, natural := strings.CutPrefix(recipe, harvestInstallRecipePref); natural {
				item = Resource(organ) // InstallNaturalKidney consumes Kidney
			}
			if !ok {
				continue
			}
			for i := range surplus {
				if surplus[i].Resource == item && surplus[i].Count > 0 {
					surplus[i].Count--
					retained[item]++
					reserved = true
					break recipe
				}
			}
		}
	}
	if !reserved {
		return need
	}
	n.Surplus = surplus[:0]
	for _, row := range surplus {
		if row.Count > 0 {
			n.Surplus = append(n.Surplus, row)
		}
	}
	n.Retained = retained
	return domain.Known(n)
}
