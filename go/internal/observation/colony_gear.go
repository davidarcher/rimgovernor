package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyGear(v *o.ColonyFactsSnapshot) domain.Fact[policy.GearObservation] {
	gear := v.GetPlanning().GetObserved().GetGear()
	if gear == nil || v.ColonistCount == nil || uint32(len(gear.Pawns)) != v.GetColonistCount() {
		return domain.Unknown[policy.GearObservation]()
	}
	result := policy.GearObservation{Pawns: []policy.GearPawn{}}
	for _, p := range gear.Pawns {
		row := policy.GearPawn{Pawn: policy.PawnID(p.Pawn.GetId()), Loadout: p.Snapshot.GetToken(), Blocked: p.Blocker != nil, Deficit: optional(p.Deficit)}
		needs := []policy.GearReplacement{}
		for _, n := range p.ReplacementNeeds {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(n.GetDefName()), Stuff: policy.Resource(n.GetStuff()), Reason: n.GetReason()})
		}
		row.Candidates = domain.Known(GearCandidateFacts(p))
		row.Replacements = domain.Known(needs)
		row.Apparel = GearApparelFacts(p.Equipment)
		row.Climate = GearClimateFacts(gear)
		result.Pawns = append(result.Pawns, row)
	}
	return domain.Known(result)
}

// GearClimateFacts maps a validated optional seasonal observation. Older
// producers omit the entire group, preserving ambient-only policy.
func GearClimateFacts(gear *o.GearSnapshot) *policy.GearClimate {
	if gear == nil || len(gear.GetOutdoorTemperatureByTwelfthC()) == 0 {
		return nil
	}
	climate := &policy.GearClimate{CurrentTwelfth: int(gear.GetCurrentTwelfth()), TicksToNextTwelfth: int64(gear.GetTicksToNextTwelfth())}
	for _, n := range gear.GetOutdoorTemperatureByTwelfthC() {
		climate.Temperatures = append(climate.Temperatures, float64(n))
	}
	if w := gear.GetActiveWeather(); w != nil {
		climate.Weather = &policy.GearWeather{Definition: w.GetDefName(), RemainingTicks: w.GetRemainingTicks(), TemperatureOffset: float64(w.GetTemperatureOffsetC())}
	}
	return climate
}

// GearCandidateFacts decodes one loadout's loose replacement candidates:
// the apparel a wear order (gear_replace, native ImproveGear) can target.
// A weapon the census still lists (its eligibility is the equip family's)
// is skipped: the wear operation looks its target up among loose apparel
// only, so a plan proposing one was refused on every attempt and held its
// development slot for the run (#339).
func GearCandidateFacts(p *o.GearLoadout) []policy.GearCandidate {
	candidates := []policy.GearCandidate{}
	for _, c := range p.GetCandidates() {
		item := c.GetItem()
		if !item.GetApparel() {
			continue
		}
		candidates = append(candidates, policy.GearCandidate{Target: item.GetThing().GetId(), Gain: c.GetGain(), Definition: policy.Resource(item.GetThing().GetDefName())})
	}
	return candidates
}

// GearApparelFacts decodes one pawn's worn apparel from the loadout's
// equipment read: unknown when the native apparel tracker could not be read
// (the equipment carries an "apparel" issue), otherwise each garment's
// definition, condition fraction (1 without hit points) and body-part
// groups.
func GearApparelFacts(equipment *o.PawnEquipment) domain.Fact[[]policy.GearApparel] {
	if equipment == nil {
		return domain.Unknown[[]policy.GearApparel]()
	}
	for _, issue := range equipment.GetIssues() {
		if issue.GetField() == "apparel" {
			return domain.Unknown[[]policy.GearApparel]()
		}
	}
	apparel := []policy.GearApparel{}
	for _, item := range equipment.GetApparel() {
		row := policy.GearApparel{Definition: policy.Resource(item.GetThing().GetDefName()), Condition: 1, Groups: append([]string{}, item.GetBodyPartGroups()...)}
		if item.ConditionFraction != nil {
			row.Condition = item.GetConditionFraction()
		}
		apparel = append(apparel, row)
	}
	return domain.Known(apparel)
}

func routineGear(v domain.Fact[policy.GearObservation], emergency policy.EmergencyFacts) domain.Fact[policy.GearObservation] {
	gear, known := v.Value()
	complete, ck := emergency.ColonistsComplete.Value()
	if !known || !ck || !complete || len(gear.Pawns) != len(emergency.Colonists) {
		return domain.Unknown[policy.GearObservation]()
	}
	ids := map[policy.PawnID]bool{}
	for _, p := range emergency.Colonists {
		if ids[p.ID] {
			return domain.Unknown[policy.GearObservation]()
		}
		ids[p.ID] = true
	}
	for _, p := range gear.Pawns {
		if !ids[p.Pawn] {
			return domain.Unknown[policy.GearObservation]()
		}
		delete(ids, p.Pawn)
	}
	return v
}
