package observation

import (
	"fmt"
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyGear decodes the gear census against the frame's definitions: the
// catalog's apparel rows and stat table give every garment's layers, groups
// and stats, and the finished research its bill options need (#1732). A frame
// without a catalog leaves the census unknown.
func colonyGear(v *o.ColonyFactsSnapshot, tables bridge.Tables, defs GearDefinitions) domain.Fact[policy.GearObservation] {
	gear := v.GetPlanning().GetObserved().GetGear()
	if gear == nil || defs.Catalog == nil || v.ColonistCount == nil || uint32(len(gear.Pawns)) != v.GetColonistCount() {
		return domain.Unknown[policy.GearObservation]()
	}
	result := policy.GearObservation{Pawns: []policy.GearPawn{}, Stored: GearStorageFacts(gear)}
	for _, p := range gear.Pawns {
		row := policy.GearPawn{Pawn: policy.PawnID(p.Pawn.GetId()), Loadout: p.Snapshot.GetToken(), Blocked: p.Blocker != nil, Deficit: optional(p.Deficit)}
		needs := []policy.GearReplacement{}
		for _, n := range p.ReplacementNeeds {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(n.GetDefName()), Stuff: policy.Resource(n.GetStuff()), Reason: n.GetReason()})
		}
		row.Candidates = GearCandidateFacts(p, tables, defs.Catalog)
		row.Replacements = domain.Known(needs)
		row.Apparel = GearApparelFacts(p.Equipment, tables, defs.Catalog)
		row.Policy = ApparelPolicyFacts(p, defs.Catalog)
		row.Climate = GearClimateFacts(gear)
		row.LoadoutModel = GearLoadoutModelFacts(defs, v.OutdoorTemperatureC, p, row.Policy)
		result.Pawns = append(result.Pawns, row)
	}
	result.Outfits = OutfitIDs(ColonyPolicies(v.Policies))
	return domain.Known(result)
}

// GearLoadoutModelFacts maps one pawn's loadout-model inputs. The role is the
// apparel-policy role (with the model's traits), and unworn options are
// narrowed to the definitions that role's apparel policy permits, so the
// model never plans a garment DesiredApparelPolicy would forbid. Unknown when
// the producer sent no model, role or temperatures, when the catalog lacks a
// garment's row or stat values or the finished research is unknown, or when
// the model falls outside the policy's bounds (PlanGearLoadout's Validate):
// the census then keeps the native deficit path.
func GearLoadoutModelFacts(defs GearDefinitions, outdoor *float64, p *o.GearLoadout, state domain.Fact[policy.ApparelPolicyState]) domain.Fact[policy.GearLoadoutInput] {
	m := p.GetLoadoutModel()
	role, known := state.Value()
	finished, researched := defs.Finished.Value()
	if m == nil || !known || !researched || p.Gender == nil || outdoor == nil || p.ComfortableMinC == nil || p.ComfortableMaxC == nil {
		return domain.Unknown[policy.GearLoadoutInput]()
	}
	in := policy.GearLoadoutInput{Role: role.Role, Female: p.GetGender() == d.Gender_GENDER_FEMALE, Research: slices.Sorted(maps.Keys(finished)), Ambient: *outdoor, ComfortableMin: p.GetComfortableMinC(), ComfortableMax: p.GetComfortableMaxC()}
	traits := []policy.PawnTrait{}
	for _, t := range m.GetTraits() {
		traits = append(traits, policy.PawnTrait{Name: t.GetDefName(), Degree: int(t.GetDegree())})
	}
	in.Role.Work.Traits = domain.Known(traits)
	var allowed map[string]bool
	if len(role.Definitions) > 0 {
		allowed = map[string]bool{}
		for _, name := range policy.RoleApparelDefinitions(policy.DeriveGearRole(role.Role), role) {
			allowed[name] = true
		}
	}
	for _, x := range m.GetWorn() {
		option, ok, err := gearLoadoutOption(x, defs.Catalog)
		if err != nil {
			return domain.Unknown[policy.GearLoadoutInput]()
		}
		if ok {
			in.Worn = append(in.Worn, option)
		}
	}
	for _, x := range m.GetOptions() {
		option, ok, err := gearLoadoutOption(x, defs.Catalog)
		if err != nil {
			return domain.Unknown[policy.GearLoadoutInput]()
		}
		if ok && (allowed == nil || allowed[x.GetDefName()]) {
			in.Options = append(in.Options, option)
		}
	}
	if in.Validate() != nil {
		return domain.Unknown[policy.GearLoadoutInput]()
	}
	return domain.Known(in)
}

// gearLoadoutOption maps one native option against the catalog: its layers
// and groups from the def's apparel row, its stats from the stat table for
// its (def, stuff), its move speed, psychic and shield facts from the row.
// False for a garment the model's slots do not name; an error for a def the
// catalog lacks or does not know as apparel, or a pair without stat values.
func gearLoadoutOption(x *o.GearLoadoutOption, catalog *bridge.DefinitionCatalog) (policy.GearOption, bool, error) {
	row, apparel := apparelRow(catalog, x.GetDefName())
	if apparel == nil {
		return policy.GearOption{}, false, fmt.Errorf("gear option %s: no apparel row for def %s", x.GetId(), x.GetDefName())
	}
	layers, groups := distinct(apparel.GetLayers()), distinct(apparel.GetBodyPartGroups())
	slot, ok := gearSlot(layers, groups)
	if !ok {
		return policy.GearOption{}, false, nil
	}
	option := policy.GearOption{ID: x.GetId(), Definition: policy.Resource(x.GetDefName()), Stuff: policy.Resource(x.GetStuff()), Quality: int(x.GetQuality()), Slot: slot, Layers: layers, Groups: groups, Source: policy.GearSource(x.GetSource()), Condition: x.GetCondition(),
		MoveSpeed: float64(statOffset(row, statMoveSpeed)), Tainted: x.GetTainted(), Locked: x.GetLocked(), Shield: hasShield(row), Psychic: statOffset(row, statPsychic) < 0, Smokepop: x.GetSmokepop(), Research: append([]string{}, x.GetResearch()...)}
	for stat, into := range map[string]*float64{statArmorSharp: &option.Sharp, statArmorBlunt: &option.Blunt, statInsulationCold: &option.Cold, statInsulationHeat: &option.Heat, statMarketValue: &option.Cost} {
		value, err := optionStat(catalog, x.GetDefName(), x.GetStuff(), stat)
		if err != nil {
			return policy.GearOption{}, false, err
		}
		*into = value
	}
	for _, q := range x.GetIngredients() {
		option.Ingredients = append(option.Ingredients, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return option, true, nil
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
// the apparel a wear order (gear_replace, native GiveJobIntent Wear) can target.
// A def the catalog does not know as apparel is skipped: the wear operation
// looks its target up among loose apparel only, so a plan proposing one was
// refused on every attempt and held its development slot for the run (#339).
// A candidate whose things table row the frame lacks, or whose def the
// catalog lacks, leaves them unknown (#1342).
func GearCandidateFacts(p *o.GearLoadout, tables bridge.Tables, catalog *bridge.DefinitionCatalog) domain.Fact[[]policy.GearCandidate] {
	candidates := []policy.GearCandidate{}
	for _, c := range p.GetCandidates() {
		item := c.GetItem()
		head := tables.Entity(item.GetThing())
		if head == nil || catalog.ThingDef(head.GetDefName()) == nil {
			return domain.Unknown[[]policy.GearCandidate]()
		}
		if _, apparel := apparelRow(catalog, head.GetDefName()); apparel == nil {
			continue
		}
		candidates = append(candidates, policy.GearCandidate{Target: item.GetThing().GetId(), Gain: c.GetGain(), Definition: policy.Resource(head.GetDefName())})
	}
	return domain.Known(candidates)
}

// GearApparelFacts decodes one pawn's worn apparel from the loadout's
// equipment read: unknown when the native apparel tracker could not be read
// (the equipment carries an "apparel" issue), otherwise each garment's
// definition, condition fraction (1 without hit points) and body-part
// groups.
func GearApparelFacts(equipment *o.PawnEquipment, tables bridge.Tables, catalog *bridge.DefinitionCatalog) domain.Fact[[]policy.GearApparel] {
	if equipment == nil || !headed(tables, equipment.GetApparel(), (*o.GearItem).GetThing) {
		return domain.Unknown[[]policy.GearApparel]()
	}
	for _, issue := range equipment.GetIssues() {
		if issue.GetField() == "apparel" {
			return domain.Unknown[[]policy.GearApparel]()
		}
	}
	apparel := []policy.GearApparel{}
	for _, item := range equipment.GetApparel() {
		def := tables.Entity(item.GetThing()).GetDefName()
		_, worn := apparelRow(catalog, def)
		if worn == nil {
			return domain.Unknown[[]policy.GearApparel]()
		}
		row := policy.GearApparel{Definition: policy.Resource(def), Condition: 1, Groups: distinct(worn.GetBodyPartGroups())}
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

// OutfitIDs is every outfit policy's load id; unknown with the policy
// section.
func OutfitIDs(f domain.Fact[Policies]) domain.Fact[[]string] {
	p, known := f.Value()
	if !known {
		return domain.Unknown[[]string]()
	}
	ids := []string{}
	for _, row := range p.Outfit {
		ids = append(ids, row.ID)
	}
	return domain.Known(ids)
}
