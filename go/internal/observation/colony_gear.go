package observation

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyGear(v *o.ColonyFactsSnapshot) domain.Fact[policy.GearObservation] {
	gear := v.GetPlanning().GetObserved().GetGear()
	if gear == nil || v.ColonistCount == nil || uint32(len(gear.Pawns)) != v.GetColonistCount() {
		return domain.Unknown[policy.GearObservation]()
	}
	result := policy.GearObservation{Pawns: []policy.GearPawn{}, Stored: GearStorageFacts(gear)}
	for _, p := range gear.Pawns {
		row := policy.GearPawn{Pawn: policy.PawnID(p.Pawn.GetId()), Loadout: p.Snapshot.GetToken(), Blocked: p.Blocker != nil, Deficit: optional(p.Deficit)}
		needs := []policy.GearReplacement{}
		for _, n := range p.ReplacementNeeds {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(n.GetDefName()), Stuff: policy.Resource(n.GetStuff()), Reason: n.GetReason()})
		}
		row.Candidates = domain.Known(GearCandidateFacts(p))
		row.Replacements = domain.Known(needs)
		row.Apparel = GearApparelFacts(p.Equipment)
		row.Policy = ApparelPolicyFacts(p)
		row.Climate = GearClimateFacts(gear)
		row.LoadoutModel = GearLoadoutModelFacts(gear, p, row.Policy)
		result.Pawns = append(result.Pawns, row)
	}
	return domain.Known(result)
}

// GearLoadoutModelFacts maps one pawn's loadout-model inputs. The role is the
// apparel-policy role (with the model's traits), and unworn options are
// narrowed to the definitions that role's apparel policy permits, so the
// model never plans a garment DesiredApparelPolicy would forbid. Unknown when
// the producer sent no model, role or temperatures, or when the model falls
// outside the policy's bounds (PlanGearLoadout's Validate): the census then
// keeps the native deficit path.
func GearLoadoutModelFacts(gear *o.GearSnapshot, p *o.GearLoadout, state domain.Fact[policy.ApparelPolicyState]) domain.Fact[policy.GearLoadoutInput] {
	m := p.GetLoadoutModel()
	role, known := state.Value()
	if m == nil || !known || gear.OutdoorTemperatureC == nil || p.ComfortableMinC == nil || p.ComfortableMaxC == nil {
		return domain.Unknown[policy.GearLoadoutInput]()
	}
	in := policy.GearLoadoutInput{Role: role.Role, Female: m.GetFemale(), Research: append([]string{}, gear.GetFinishedResearch()...), Ambient: gear.GetOutdoorTemperatureC(), ComfortableMin: p.GetComfortableMinC(), ComfortableMax: p.GetComfortableMaxC()}
	traits := []policy.PawnTrait{}
	for _, t := range m.GetTraits() {
		traits = append(traits, policy.PawnTrait{Name: t.GetDefName(), Degree: int(t.GetDegree())})
	}
	in.Role.Work.Traits = domain.Known(traits)
	var allowed map[string]bool
	if len(role.Definitions) > 0 {
		allowed = map[string]bool{}
		if desired, ok := policy.RoleApparelPolicy(policy.PawnID(p.GetPawn().GetId()), policy.DeriveGearRole(role.Role), role); ok {
			for _, d := range desired.Spec().Definitions {
				allowed[d] = true
			}
		}
	}
	for _, x := range m.GetWorn() {
		if option, ok := gearLoadoutOption(x); ok {
			in.Worn = append(in.Worn, option)
		}
	}
	for _, x := range m.GetOptions() {
		if option, ok := gearLoadoutOption(x); ok && (allowed == nil || allowed[x.GetDefName()]) {
			in.Options = append(in.Options, option)
		}
	}
	if in.Validate() != nil {
		return domain.Unknown[policy.GearLoadoutInput]()
	}
	return domain.Known(in)
}

// gearLoadoutOption maps one native option; false for a garment the model's
// slots do not name.
func gearLoadoutOption(x *o.GearLoadoutOption) (policy.GearOption, bool) {
	slot, ok := gearSlot(x.GetApparelLayers(), x.GetBodyPartGroups())
	if !ok {
		return policy.GearOption{}, false
	}
	option := policy.GearOption{ID: x.GetId(), Definition: policy.Resource(x.GetDefName()), Stuff: policy.Resource(x.GetStuff()), Quality: int(x.GetQuality()), Slot: slot, Layers: append([]string{}, x.GetApparelLayers()...), Groups: append([]string{}, x.GetBodyPartGroups()...), Source: policy.GearSource(x.GetSource()), Condition: x.GetCondition(), Sharp: x.GetArmorSharp(), Blunt: x.GetArmorBlunt(), Cold: x.GetInsulationCold(), Heat: x.GetInsulationHeat(), MoveSpeed: x.GetMoveSpeed(), Cost: x.GetMarketValue(), Tainted: x.GetTainted(), Locked: x.GetLocked(), Shield: x.GetShield(), Psychic: x.GetPsychic(), Smokepop: x.GetSmokepop(), Research: append([]string{}, x.GetResearch()...)}
	for _, q := range x.GetIngredients() {
		option.Ingredients = append(option.Ingredients, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return option, true
}

// gearSlot is the model slot a garment's native apparel layers and body-part
// groups fill: belt, headgear (overhead or eye cover), outer (any shell layer,
// so plate armour's middle+shell is outer), skin torso or legs, then middle
// torso (a flak vest).
func gearSlot(layers, groups []string) (policy.GearSlot, bool) {
	switch {
	case slices.Contains(layers, "Belt"):
		return policy.GearBelt, true
	case slices.Contains(layers, "Overhead"), slices.Contains(layers, "EyeCover"):
		return policy.GearHeadgear, true
	case slices.Contains(layers, "Shell"):
		return policy.GearOuter, true
	case slices.Contains(layers, "OnSkin") && slices.Contains(groups, "Torso"):
		return policy.GearSkinTorso, true
	case slices.Contains(layers, "OnSkin") && slices.Contains(groups, "Legs"):
		return policy.GearSkinLegs, true
	case slices.Contains(layers, "Middle"):
		return policy.GearMiddleTorso, true
	}
	return "", false
}

// GearClimateFacts maps a validated optional seasonal observation. Older
// producers omit the entire group, preserving ambient-only policy.
func GearStorageFacts(gear *o.GearSnapshot) domain.Fact[[]policy.GearStock] {
	if gear.GetStoredApparel() == nil {
		return domain.Unknown[[]policy.GearStock]()
	}
	rows := []policy.GearStock{}
	for _, row := range gear.GetStoredApparel().GetRows() {
		rows = append(rows, policy.GearStock{Definition: policy.Resource(row.GetDefName()), Stuff: policy.Resource(row.GetStuff()), Quality: int(row.GetQuality()), HPBand: int(row.GetHpBand()), Count: int(row.GetCount())})
	}
	return domain.Known(rows)
}

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
// the apparel a wear order (gear_replace, native PawnOrderIntent WEAR) can target.
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
