package stateval

import d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"

// The gear, difficulty and market facts of the gear-group parts and workers
// (epic #2621, #2639). Difficulty is per game, not per thing, so it is
// Env.Difficulty; everything else is read off the thing a request is about and
// rides in StatContext.Gear. Every fact is Known[T]: one the caller did not
// state is an error naming it, never a default.

// Difficulty is the storyteller state the difficulty parts and the
// cost-for-difficulty lists read (Find.Storyteller).
type Difficulty struct {
	// NoStoryteller is Find.Storyteller == null: no game is running. The yield
	// parts then use factor 1 and no cost list applies.
	NoStoryteller bool
	// ButcherYieldFactor and FishingYieldFactor are Find.Storyteller.difficulty's
	// settings.
	ButcherYieldFactor float32
	FishingYieldFactor float32
	// Flags are the public bool fields of Difficulty by name: what
	// CostListForDifficulty.difficultyVar names.
	Flags map[string]bool
}

// GearFacts are the gear-group facts of one thing request.
type GearFacts struct {
	// Stats are Thing.GetStatValue of this very thing by StatDef name (the
	// base worker's thing-side terms are not ported, so a value computed from
	// another stat of the thing is stated). A stat a class reads and the map
	// lacks is an error naming it.
	Stats map[string]float32
	// Apparel is Pawn.apparel.WornApparel in order of a pawn; Some(nil) is a
	// pawn with no apparel tracker or none worn.
	Apparel Known[[]GearItem]
	// Primary is Pawn.equipment.Primary of a pawn; Some(nil) is none.
	Primary Known[*GearItem]
	// Mass is the gear and inventory of a Pawn, or of the InnerPawn of a
	// Corpse, that MassUtility.GearAndInventoryMass sums.
	Mass Known[MassFacts]
	// ForPawn is StatRequest.Pawn != null: the request carries a pawn.
	ForPawn Known[bool]
	// Bladelink are the WeaponTraitDef names of the thing's CompBladelinkWeapon
	// (TraitsListForReading); Some(nil) is no such comp or no traits.
	Bladelink Known[[]string]
	// Reloadable is the thing's CompApparelReloadable, else its
	// CompEquippableAbilityReloadable; Some(nil) is neither.
	Reloadable Known[*ReloadableState]
	// WornByCorpse is Apparel.WornByCorpse of an apparel thing.
	WornByCorpse Known[bool]
	// Comps are the stat terms of the thing's comps when it is a
	// ThingWithComps; Some(nil) is any other thing.
	Comps Known[*CompStatTerms]
	// RelicStyle is Thing.StyleSourcePrecept is Precept_Relic.
	RelicStyle Known[bool]
	// PawnPrice is the pawn state PriceUtility reads, of a Pawn.
	PawnPrice Known[PawnPriceFacts]
	// StatOffsetComp is the thing's CompStatOffsetBase; Some(nil) is none.
	StatOffsetComp Known[*StatOffsetCompState]
	// MeleeVerbs is Pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false) of a
	// pawn.
	MeleeVerbs Known[[]MeleeVerbEntry]
	// Wielder is StatWorker_MeleeAverageDPS.GetCurrentWeaponUser of a weapon;
	// Some(nil) is a weapon nobody holds or wears.
	Wielder Known[*WielderState]
}

// GearItem is one worn apparel or a primary weapon: a thing in its own right,
// whose stats the wearer's parts read.
type GearItem struct {
	// Def, Stuff and Quality are the item's StatRequest.For(thing): its def,
	// stuff def name ("" for none) and quality category (nil is normal).
	Def     string
	Stuff   string
	Quality *int32
	// Stats are the item's GetStatValue by StatDef name.
	Stats map[string]float32
	// Bladelink are the WeaponTraitDef names of its CompBladelinkWeapon;
	// Some(nil) is none.
	Bladelink Known[[]string]
	// Context is the item's own thing context, for the stat parts
	// StatWorker.StatOffsetFromGear runs over it; nil is a thing with no
	// stated facts.
	Context *StatContext
}

// MassFacts are the item masses MassUtility.GearAndInventoryMass sums: the
// apparel and equipment Mass stat values in list order, and the inventory
// stacks.
type MassFacts struct {
	Apparel   []float32
	Equipment []float32
	Inventory []StackMass
}

// StackMass is an inventory stack: its count and the Mass stat of one item.
type StackMass struct {
	Count int32
	Mass  float32
}

// ReloadableState is an IReloadableComp.
type ReloadableState struct {
	RemainingCharges int32
	MaxCharges       int32
	// AmmoDef is the ThingDef name of AmmoDef, "" for none.
	AmmoDef string
	// MaxAmmoNeeded is MaxAmmoNeeded(allowForcedReload: true).
	MaxAmmoNeeded int32
	// ChargedDestroyOnEmpty is the comp being a CompApparelVerbOwner_Charged
	// whose Props.destroyOnEmpty is set.
	ChargedDestroyOnEmpty bool
}

// CompStatTerms are the terms the comps of a ThingWithComps add through
// ThingComp.GetStatOffset and GetStatFactor, per comp in AllComps order, by
// StatDef name. A stat not listed has no comp overriding it (offset 0, factor
// 1).
type CompStatTerms struct {
	Offsets map[string][]float32
	Factors map[string][]float32
}

// PawnPriceFacts are the pawn facts PriceUtility.PawnQualityPriceFactor and
// PawnQualityPriceOffset read.
type PawnPriceFacts struct {
	// SummaryHealthPercent is health.summaryHealth.SummaryHealthPercent.
	SummaryHealthPercent float32
	// Capacities are the pawn's capacities by PawnCapacityDef name.
	Capacities map[string]CapacityState
	// HasSkills is pawn.skills != null; SkillLevels are the skills' levels.
	HasSkills   bool
	SkillLevels []int32
	// LifeStage is the LifeStageDef name of ageTracker.CurLifeStage.
	LifeStage string
	// TraitValueOffsets are CurrentData.marketValueFactorOffset of each
	// trait that is not suppressed, in trait order.
	TraitValueOffsets []float32
	// PawnBeauty is the pawn's PawnBeauty stat.
	PawnBeauty float32
	// Hediffs are the HediffDef names of health.hediffSet.hediffs in order.
	Hediffs []string
}

// CapacityState is a pawn's standing on one PawnCapacityDef.
type CapacityState struct {
	// Capable is health.capacities.CapableOf.
	Capable bool
	// TradeLevel is PawnCapacityUtility.CalculateCapacityLevel with
	// forTradePrice.
	TradeLevel float32
}

// StatOffsetCompState is a CompStatOffsetBase.
type StatOffsetCompState struct {
	// StatDef is Props.statDef.
	StatDef string
	// Offset is GetStatOffset(req.Pawn).
	Offset float32
}

// MeleeVerbEntry is a VerbEntry of a pawn's available melee verbs, with the
// figures the melee workers average.
type MeleeVerbEntry struct {
	// IsMeleeAttack is VerbEntry.IsMeleeAttack.
	IsMeleeAttack bool
	// SelectionWeight is GetSelectionWeight(null).
	SelectionWeight float32
	// Damage is verbProps.AdjustedMeleeDamageAmount(verb, pawn).
	Damage float32
	// ArmorPenetration is verbProps.AdjustedArmorPenetration(verb, pawn).
	ArmorPenetration float32
	// CooldownTicks is verbProps.AdjustedCooldownTicks(verb, pawn).
	CooldownTicks int32
}

// WielderState is the pawn holding or wearing a weapon.
type WielderState struct {
	// Intelligence is attacker.RaceProps.intelligence.
	Intelligence d.Intelligence
	// LifeStageMeleeDamageFactor is ageTracker.CurLifeStage.meleeDamageFactor.
	LifeStageMeleeDamageFactor float32
	// Stats are the pawn's GetStatValue by StatDef name (MeleeDamageFactor,
	// MeleeCooldownFactor).
	Stats map[string]float32
	// PartEfficiency is PawnCapacityUtility.CalculateNaturalPartsAverageEfficiency
	// by BodyPartGroupDef name.
	PartEfficiency map[string]float32
}

// stat is this thing's GetStatValue of stat, or an error naming it.
func (g *GearFacts) stat(stat string) (float32, error) {
	return namedStat(g.Stats, stat, "the thing")
}

// itemStat is the item's GetStatValue of stat.
func (g GearItem) stat(stat string) (float32, error) {
	return namedStat(g.Stats, stat, "gear "+g.Def)
}

func (w *WielderState) stat(stat string) (float32, error) {
	return namedStat(w.Stats, stat, "the wielder")
}

// context is the item's thing context.
func (g GearItem) context() *StatContext {
	if g.Context != nil {
		return g.Context
	}
	return &StatContext{}
}
