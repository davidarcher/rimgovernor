package policy

import (
	"errors"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	ConfirmColonyNames      ConcernID = "ConfirmColonyNames"
	AnswerDialog            ConcernID = "AnswerDialog"
	ActiveCombat            ConcernID = "ActiveCombat"
	CriticalMedicine        ConcernID = "CriticalMedical"
	RestoreWorkers          ConcernID = "RestoreWorkers"
	AllowStartingSupplies   ConcernID = "AllowStartingSupplies"
	ManageSupplySafety      ConcernID = "ManageSupplySafety"
	EnsureWorkAssignments   ConcernID = "EnsureWorkAssignments"
	EnsureFoodSupply        ConcernID = "EnsureFoodSupply"
	MaintainHousing         ConcernID = "MaintainHousing"
	EnsureTemperatureSafety ConcernID = "EnsureTemperatureSafety"
	EnsureCooking           ConcernID = "EnsureCooking"
	MaintainButcherSpot     ConcernID = "MaintainButcherSpot"
	EnsureBasicPower        ConcernID = "EnsureBasicPower"
	EnsureBasicDefense      ConcernID = "EnsureBasicDefense"
	MaintainMedicalReserves ConcernID = "MaintainMedicalReserves"
	MaintainFoodStorage     ConcernID = "MaintainFoodStorage"
	MaintainRefrigeration   ConcernID = "MaintainRefrigeration"
	EnsureComfort           ConcernID = "EnsureComfort"
	ClearPests              ConcernID = "ClearPests"
	MaintainEquipment       ConcernID = "MaintainEquipment"
	EnsureResearch          ConcernID = "EnsureResearch"
	MaintainResource        ConcernID = "MaintainResource"
	EnsureDefensiveLayout   ConcernID = "EnsureDefensiveLayout"
	TradeWithCaravan        ConcernID = "TradeWithCaravan"
)

// foodStorageUpkeepPriority is MaintainFoodStorage's entry development
// priority, the same priority medicalReservePriority (a local var, not a
// const, at its own point of use below) starts MaintainMedicalReserves at.
const foodStorageUpkeepPriority = 3

// refrigerationPriority keeps MaintainRefrigeration out of the ranked
// development queue (see DetectRounds's comment at its point of use).
const refrigerationPriority = 2

// lightingPriority ranks MaintainLighting with the other upkeep projects.
const lightingPriority = 3

// flooringPriority ranks MaintainFlooring while a clean workspace is short
// of floor; living-room flooring alone ranks one step lower and traffic
// flooring last, never below the lowest goal rank.
const flooringPriority = 3

// routesPriority ranks MaintainRoutes with the other upkeep projects: an
// unreachable facility idles whatever it serves, so it ranks with a dark
// bench, never as an emergency.
const routesPriority = 3

type RoundsPolicy struct {
	MedicalReserve                                MedicalReservePolicy
	FoodStorage                                   FoodStoragePolicy
	Cleanliness                                   CleanlinessPolicy
	Lighting                                      LightingPolicy
	Flooring                                      FlooringPolicy
	Routes                                        RoutesPolicy
	FoodMinDays, FoodTargetDays, FootholdFoodDays float64
	FoodReserveDays                               float64
	ColdEnter, ColdExit, HotExit, HotEnter        float64
	WoodMin, WoodTarget, WoodMax                  int64
	// HuntStallTicks bounds how long a dispatched Hunt-kind acquisition action
	// may sit unresolved before RoundsAcquisitionPlanner abandons it and lets
	// a fresh SelectAcquisition pass propose something else. Native's own
	// HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game
	// clock while a hunter's route stays unsafe (issue #1); that guard stays
	// fully authoritative, but without this grace the stuck action reads as
	// open work forever and blocks the planner from ever trying a different
	// prey or a non-hunt source. It counts from the last observation, and a
	// live hunt (travel, chase, the hunter's sleep and meals, a clock window
	// between observations) outlasts 6000 ticks: that bound withdrew working
	// hunts as "cancelled" and cooled their prey, so it is half a day.
	HuntStallTicks int64
	// AcquisitionStallTicks bounds how long a dispatched plant-harvest
	// acquisition may stay designated with its effect pending (no colonist
	// has taken the designation) before the acquisition and medical
	// planners cancel it so the goal re-plans from another source instead
	// of waiting on one plant (#291: wild healroot pending 120k ticks).
	AcquisitionStallTicks int64
	// ConcernStallTicks bounds how long a goal's progress record (#629) may go
	// without native evidence advancing its expected observable before
	// ExpireGoalProgress rotates the method (or keys the failed situation
	// out with a cooldown): GoalProgressContract's default contract and the
	// food ladder's rungs (FoodProgress) both use it as their deadline.
	// StageRoundsPolicy scales it down during StageFoothold
	// (StageGoalStallScale) so a stuck method rotates in a fraction of a
	// day rather than the full day this defaults to, while starvation risk
	// is highest; it returns to this value once the colony reaches
	// Reserves.
	ConcernStallTicks int64
	// ResearchLadder is the ordered roadmap EnsureResearch walks when the workshop ladder records no need
	// (DefaultResearchLadder by default; empty disables the roadmap). Each
	// rung is reached through ResearchPrerequisiteQueue like a target, a
	// current native project is respected and recovers the goal, and the
	// planner lends the clock ticks while one is current so the rung
	// finishes on its own (issue #230).
	ResearchLadder []string
	// ResourceTargets is an operator-declared map of native resource
	// definition name to the native stock floor MaintainResource should keep
	// it above; an empty map disables the goal entirely. The deficit is
	// measured against RoundsFacts.Resources each review as the worst-covered
	// target's shortfall fraction. This only supports
	// policy.SelectResourceTarget's own single-goal dynamic-target selection
	// across these targets.
	ResourceTargets map[Resource]int64
	// GearSpareTargets optionally maintains unworn replacements by definition.
	// MaintainResource owns both its stockpile zone and standing production bill.
	GearSpareTargets map[Resource]int64
	// StoneBlockTarget is an operator-declared native stock floor for stone
	// blocks of whichever Core stone the map's chunk census counts most
	// (StoneBlockTarget): it joins ResourceTargets through
	// EffectiveResourceTargets each review and planner step, so the
	// MaintainResource ladder stages a stonecutter's table and keeps a
	// do-until bill fed from map chunks without the operator naming the
	// stone. Zero disables it.
	StoneBlockTarget int64
	// Trade is TradeWithCaravan's configuration (policy/trade_routine.go).
	Trade RoundsTradePolicy
	// PrisonerReleaseAfterDays is how long MaintainPopulation feeds a
	// prisoner it has no use for (not worth recruiting, not enslavable)
	// while the food runway holds FoodTargetDays before releasing it
	// (default 15 days); below the target it releases at once. See
	// PrisonerPolicy.
	PrisonerReleaseAfterDays float64
	// DefensiveLayout is an operator-declared opt-in for EnsureDefensiveLayout
	// (issue #5): the staged chokepoint/firing-line/funnel/trap-corridor
	// construction RoundsDefenseLayoutPlanner proposes from a fresh native
	// defense-site census. It is config-only: the review does not derive layout completeness from a
	// census, the planner decides per tier from its own admitted plans.
	DefensiveLayout bool
	// Stage holds the colony stage thresholds (#630); zero fields take the
	// defaults RoundsPolicy.Stages derives from the food thresholds.
	Stage ColonyStagePolicy
	// ColonyStage is the stage the last review left (StageRoundsPolicy):
	// DetectRounds raises only the goals the stage allows
	// (StageGoalAllowed). DefaultRoundsPolicy stands at Development, so a
	// policy nobody staged raises every goal.
	ColonyStage ColonyStage `json:",omitempty"`
}

// DefaultRoundsPolicy admits development automatically (#655): slots
// bound planner cost only and distinct observed workers decide admission.
func DefaultRoundsPolicy() RoundsPolicy {
	return RoundsPolicy{MedicalReserve: DefaultMedicalReservePolicy(), FoodStorage: DefaultFoodStoragePolicy(), Cleanliness: DefaultCleanlinessPolicy(), Lighting: DefaultLightingPolicy(), Flooring: DefaultFlooringPolicy(), Routes: DefaultRoutesPolicy(), FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3, FoodReserveDays: DefaultFoodReserveDays, PrisonerReleaseAfterDays: 15,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500, HuntStallTicks: domain.TicksPerDay / 2, AcquisitionStallTicks: domain.TicksPerDay, ConcernStallTicks: int64(DevelopmentStallTicks), ResearchLadder: DefaultResearchLadder(), ColonyStage: StageDevelopment}
}

func (p RoundsPolicy) Validate() error {
	if p.MedicalReserve.MinimumPerColonist < 0 || p.MedicalReserve.TargetPerColonist <= p.MedicalReserve.MinimumPerColonist {
		return errors.New("invalid medicine reserve thresholds")
	}
	if !p.FoodStorage.valid() {
		return errors.New("invalid food storage thresholds")
	}
	if !p.Cleanliness.valid() {
		return errors.New("invalid cleanliness thresholds")
	}
	for _, n := range []float64{p.FoodMinDays, p.FoodTargetDays, p.FootholdFoodDays, p.FoodReserveDays, p.ColdEnter, p.ColdExit, p.HotExit, p.HotEnter, p.PrisonerReleaseAfterDays} {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("nonfinite routine threshold")
		}
	}
	if p.FoodReserveDays < 0 || p.FoodReserveDays > 60 || p.FoodMinDays <= 0 || p.FoodTargetDays <= p.FoodMinDays || p.FootholdFoodDays <= 0 ||
		p.ColdEnter >= p.ColdExit || p.ColdExit >= p.HotExit || p.HotExit >= p.HotEnter ||
		p.WoodMin < 0 || p.WoodTarget <= p.WoodMin || p.WoodMax < p.WoodTarget {
		return errors.New("unordered routine thresholds")
	}
	if p.PrisonerReleaseAfterDays < 0 || p.PrisonerReleaseAfterDays > 120 {
		return errors.New("invalid prisoner release threshold")
	}
	if err := p.Stages().validate(); err != nil {
		return err
	}
	if p.HuntStallTicks <= 0 {
		return errors.New("invalid hunt stall grace")
	}
	if p.AcquisitionStallTicks <= 0 {
		return errors.New("invalid acquisition stall grace")
	}
	if p.ConcernStallTicks <= 0 {
		return errors.New("invalid standard stall grace")
	}
	if err := p.Trade.Validate(); err != nil {
		return err
	}
	for _, rung := range p.ResearchLadder {
		if !validResource(Resource(rung)) {
			return errors.New("invalid research ladder rung")
		}
	}
	if err := ValidateResourceTargets(p.ResourceTargets); err != nil {
		return err
	}
	if err := ValidateResourceTargets(p.GearSpareTargets); err != nil {
		return err
	}
	if p.StoneBlockTarget < 0 || p.StoneBlockTarget > 10000 {
		return errors.New("invalid stone block target")
	}
	return nil
}

// Prisoners is the MaintainPopulation slice of this policy.
func (p RoundsPolicy) Prisoners() PrisonerPolicy {
	return PrisonerPolicy{ReleaseAfterDays: p.PrisonerReleaseAfterDays, FoodTargetDays: p.FoodTargetDays}
}

// ValidateResourceTargets checks every configured MaintainResource target:
// a valid native resource definition name with a positive target within the
// same StockTarget production bill bound (see domain.NewProductionBill).
func ValidateResourceTargets(targets map[Resource]int64) error {
	for resource, target := range targets {
		if !validResource(resource) || target <= 0 || target > 10000 {
			return errors.New("invalid resource target")
		}
	}
	return nil
}

// RoundsFacts holds derived native facts, not forecasts masquerading as output.
// FoodDays is the accessible diet/rot-aware stock runway. FieldCoverage is the
// separate native crop-capacity forecast; it never increases FoodDays.
type RoundsFacts struct {
	// PersonalShares are the colonists' personal wealth shares (#1846) the
	// elective surgery gate reads (#1843); nil is ungated, a live reading
	// always sets it (an empty map gates every colonist).
	PersonalShares map[PawnID]PersonalShare `json:",omitzero"`
	// Items are the catalog's item numbers (market value, nutrition,
	// medical potency, stuff factors); the zero value without a catalog.
	Items ItemFacts `json:",omitzero"`
	// Recipes are the recipe rows' derived facts (#1721): the stuff-made part
	// installs peg-leg cycling plans around.
	Recipes RecipeFacts `json:",omitzero"`
	// VetRoom is the layout's vet room; unread (the zero value) until
	// the layout exposes it, which keeps sterilize off.
	VetRoom VetRoom
	// BarnArea is the id of the bot-owned Barn allowed area (#1869), "" until
	// a standing barn has created it.
	BarnArea    domain.Fact[string]
	FoodPlan    domain.Fact[FoodPlan]
	FoodReserve domain.Fact[FoodReserveReview]
	// BabyFeeding is the babies' food review (#1681); unknown without
	// Biotech baby care or consumer facts.
	BabyFeeding          domain.Fact[BabyFeeding]
	TradeMealIngredients domain.Fact[[]FoodIngredientSlot]
	PenGrazing           domain.Fact[[]PenGrazing]
	RecoverySafety       domain.Fact[RecoverySafety]
	RecoveryWorkers      domain.Fact[[]RecoveryWorker]
	DisasterConditions   domain.Fact[[]DisasterCondition]
	// OutdoorsDark is the colony biome's permanent darkness (#1712): its map
	// conditions include a no-sunlight class.
	OutdoorsDark        domain.Fact[bool]
	RecoveryBuildings   domain.Fact[[]RecoveryBuilding]
	Disaster            *DisasterHistory
	DisasterTick        domain.Tick
	MoodPawns           domain.Fact[[]MoodPawn]
	Mood                MoodHistory
	HomeCoverage        domain.Fact[HomeCoverageObservation]
	StoneStructures     domain.Fact[[]StoneStructure]
	ConstructionClaims  domain.Fact[[]ConstructionClaim]
	CurrentConstruction domain.Fact[CurrentConstruction]
	// ConstructionDeficit is the material standing blueprints and frames
	// are still owed, per resource.
	ConstructionDeficit domain.Fact[map[Resource]int64]
	// BillReservations are live bill jobs' promised ingredients, per
	// working pawn (MaterialBudget).
	BillReservations  domain.Fact[[]IngredientReservation]
	Sleeping          domain.Fact[SleepingObservation]
	SleepingRecovered domain.Fact[bool]
	// BedroomsOwed: a planned individual bedroom step is due (#786); it
	// keeps MaintainHousing open once everyone owns a shelter bed.
	BedroomsOwed domain.Fact[bool]
	// CorpsesOwed: a tomb (#832), morgue (#1820) or incinerator (#1814) step is due; it keeps
	// MaintainWaste open while a corpse waits on one.
	CorpsesOwed domain.Fact[bool]
	// TombsWarm: the warm tombs holding a colonist (#840, WarmTombs); they
	// join MaintainRefrigeration's rooms.
	TombsWarm domain.Fact[[]string]
	// MealClosetOwed: the planned meal closet waits to be shelled while its
	// dining room stands (#936); it keeps MaintainRefrigeration open.
	MealClosetOwed domain.Fact[bool]
	// CampfireRetireOwed: a stove kitchen supersedes a cooking campfire (#1179); it keeps EnsureCooking open.
	CampfireRetireOwed domain.Fact[bool]
	// TemperatureOwed: a heat campfire's auto-refuel should switch, or a
	// sleeping room sits unheated below its sleepers' comfort minimum
	// (TemperatureOwed, #1180/#1199); it holds EnsureTemperatureSafety open.
	TemperatureOwed domain.Fact[bool]
	// SculptureRoomsOwed: a bedroom below target, weakest in beauty, has a
	// free cell for a sculpture (SculptureRoomsOwed, #1190); with a
	// qualifying artist it holds MaintainArt open.
	SculptureRoomsOwed domain.Fact[bool]
	// SafeAreaOwed: the Safe allowed area differs from the enclosed roofed
	// rooms (PlanSafeArea, #1325); it holds MaintainShelter open. Unknown
	// unless the MaintainShelter method is composed.
	SafeAreaOwed domain.Fact[bool]
	// FirebreakOwed: the firebreak ring has a cut cell with a standing
	// plant or an undesignated wooden ruin (FirebreakOwed, #1548); it holds
	// MaintainFirebreak open. Unknown unless the method is composed.
	FirebreakOwed domain.Fact[bool]
	// MechGestationOwed: a mechanitor can afford the next mech, a gestator
	// is idle and no waste is uncleared (MechGestationOwed, #1686); it holds
	// MaintainMechs open. Unknown unless the method is composed.
	MechGestationOwed domain.Fact[bool]
	// HerdRoomsOwed: a barn or vet room step is due (NextHerdStep, #1633);
	// it holds MaintainAnimalContainment open after the pen stands.
	HerdRoomsOwed domain.Fact[bool]
	// PsylinkOwed: a willing colonist has no psylink and a psylink
	// neuroformer is held (PsylinkOwed, #1609); it holds MaintainPsylink
	// open. Unknown unless the method is composed.
	PsylinkOwed domain.Fact[bool]
	// CreepJoinerOwed: a creepjoiner whose downside has not shown holds a
	// weapon (CreepJoinerDownsides.WeaponDrops, #1740); it holds
	// ManageCreepJoiners open. Unknown unless the method is composed.
	CreepJoinerOwed domain.Fact[bool]
	// RolesOwed: an active ideoligion role has a free place and a fitting
	// believer (RolesOwed, #1661); it holds MaintainIdeoRoles open.
	RolesOwed domain.Fact[bool]
	// RitualSites are the finished buildings the held rituals' patterns
	// require (observation, from the frame's building census); RitualPlans
	// the rituals to begin now (PlanRituals, #1660), whose attendees the
	// schedule planners hold off Sleep (HeldOffSleep); RitualsOwed holds
	// MaintainRituals open. Unknown unless the method is composed.
	RitualSites domain.Fact[[]RitualSite]
	RitualPlans domain.Fact[[]RitualPlan]
	RitualsOwed domain.Fact[bool]
	// ShelterArea is the Safe allowed area's native load id, "" when the
	// map has none (PlanSheltering, #1326).
	ShelterArea domain.Fact[string]
	// ShelterCombatants is the squad's draft set during a threat: the
	// colonists a raid or manhunter pack does not shelter. Unknown shelters
	// no colonist for a threat.
	ShelterCombatants domain.Fact[[]PawnID]
	// NoDangerArea is the NoDanger allowed area's native load id, ""
	// when the map has none; DangerWindow whether haulers are kept out of
	// the danger cells now (DangerWindowOf); DangerHaulers the pawns with
	// Hauling enabled (#1327); DangerSeeds the threat census's danger seeds
	// (DangerSeeds, #1802), unknown with the hostile count.
	NoDangerArea  domain.Fact[string]
	DangerSeeds   domain.Fact[[]domain.Cell]
	DangerWindow  domain.Fact[bool]
	DangerHaulers domain.Fact[[]PawnID]
	// IsolationArea is the Isolation allowed area's native load id, "" when
	// the map has none (ManageCreepJoiners, #1740).
	IsolationArea domain.Fact[string]
	// SaleArt counts the packed art no owed room reserves (SaleSculptures);
	// read only while the wealth headroom is negative, it opens a trade as
	// the shed_art need (#1247).
	SaleArt domain.Fact[int64]
	// FabricableParts are the part items a usable gear bench has a researched
	// recipe for, read only while a medical pawn wants a part (#1255); the
	// caravan assessment counts only parts no bench can make.
	FabricableParts   map[Resource]bool
	AnimalUpkeep      AnimalUpkeepObservation
	FoodStorageUpkeep FoodStorageObservation
	MedicalReserve    MedicalReserveObservation
	// Prisoners carries Population-*'s recruit/maintain census: unlike
	// AnimalUpkeep, this has no generic per-tick colony read to piggyback on
	// (recruitable/current-interaction facts live only on the dedicated
	// rimgovernor/observations_read_population census), so it is populated by
	// a dedicated per-cycle RoundsSource read instead of ObserveColony's
	// always-present projection.
	Prisoners domain.Fact[[]PrisonerFacts]
	// PrisonerColony is the same read's colony side of each prisoner's
	// use: the free colonists' best skills and the Ideology facts.
	PrisonerColony domain.Fact[PrisonerColony]
	// Custody carries Population-*'s capture/rescue candidate census: every
	// observed humanlike from the same dedicated population read Prisoners
	// uses, broadened past prisoners alone so RoundsPopulationCustodyPlanner
	// can detect and select a downed hostile or unadmitted guest to dispatch.
	Custody domain.Fact[[]CustodyFacts]
	// Containment is the containment cell's inputs (#1741) and the entity
	// rows the capture rule decides (#1742), set by the routine reading.
	Containment ContainmentPlanning
	// Outlook is the same population read's storyteller outlook (#1031).
	Outlook PopulationOutlook
	// OwnedNames is the same read's owned-pawn short-name census (#1310).
	OwnedNames domain.Fact[[]OwnedName]
	// QuestOffers carries MaintainPopulation's joiner census: every visible
	// quest row (rimgovernor/observations_read_world_progression), read per
	// cycle by a RoundsSource offering RoundsQuestSource, for JoinerDeficit
	// to detect and SelectJoinerMethod to answer a joiner offer from.
	QuestOffers domain.Fact[[]JoinerOffer]
	// Royalty is the royalty facts (#1599) from the pawn rows, the def mirror
	// and the colony section; unknown when Royalty is not applicable or a read
	// failed.
	Royalty domain.Fact[RoyaltyFacts]
	// Ideology is the primary ideoligion (#1654) from the frame's ideology
	// section with the catalog's defs; unknown when the frame carries no
	// section (no Ideology, or no primary ideoligion).
	Ideology domain.Fact[Ideoligion]
	// IdeologyInstalled is whether the Ideology expansion is active (#1922);
	// unknown when the frame does not say.
	IdeologyInstalled domain.Fact[bool]
	// TitleClaimQuests are the bestowing-ceremony quests the title claim
	// gate allows to accept now (ClaimQuests, #1605); the review fills it
	// once the plan, rooms and royalty read are known.
	TitleClaimQuests []domain.QuestID
	JoinerLetters    domain.Fact[[]JoinerLetterOffer]
	RaidPoints       domain.Fact[float64]
	// ShellsShort is the armory shell review (#1207): a built mortar's shell
	// stock below half its target puts MaintainEquipment in deficit.
	ShellsShort domain.Fact[bool]
	// DefenseCapacity is the colonists' and powered turrets' observed
	// combat strength in raid-point units (#1188, DefenseCapacity).
	DefenseCapacity domain.Fact[float64]
	// Waste carries MaintainWaste's exposed/eligible native item census (the
	// same WasteReply the generic per-tick colony read already carries), for
	// pendingWaste/WasteDeficit to detect and, eventually, SelectWasteMethod
	// to dispatch containment/burial candidates from.
	Waste domain.Fact[[]WasteItem]
	// Traders is the map trader census (bridge.ListTraders) TradeWithCaravan
	// needs; a source without the read leaves it unknown and the goal off.
	Traders domain.Fact[[]TraderFacts]
	// Blight carries RemoveBlight's blighted-plant census (the colony read's
	// blighted_plants section), for BlightDeficit to detect and
	// SelectBlightCuts to designate from.
	Blight domain.Fact[[]BlightedPlant]
	// Pollution carries ManagePollution's wastepack verdicts and the polluted
	// cells outside the clear area (the Biotech colony section, #1679); unknown
	// without Biotech or when the read failed, and then the need is unknown.
	Pollution domain.Fact[PollutionFacts]
	// MechChargerOwed is whether the colony owes one more mech charger
	// (MechChargerNeed, #1688); unknown without Biotech, mechs or chargers
	// read, and then the EnsureMechCharger need is unknown.
	MechChargerOwed domain.Fact[bool]
	// GeneBankOwed is whether more genepacks lie loose than the standing
	// gene banks have room for (GeneBankNeed, #1933); unknown without
	// Biotech or a complete gene-building read, and then MaintainGeneBank
	// has no assessment.
	GeneBankOwed domain.Fact[bool]
	// LayoutTidy is the layout tidying review (#611) the reviewer measures
	// from the room census against each room's derived interior plan;
	// unknown without a tier.
	LayoutTidy domain.Fact[TidyReview]
	// Stockpiles is the MaintainStockpiles review (#725): this cycle's
	// stockpile edits within the haul budget, or why none stands.
	Stockpiles domain.Fact[StockpileReview]
	// AvailableMethods is supplied by the configured runtime, never native facts.
	AvailableMethods domain.Fact[[]ConcernID]
	Upkeep           UpkeepObservation
	// ShrineHolds is each Upkeep.Shrines row's breach judgement (#458) as
	// the reviewer read it, journalled beside the review; empty while the
	// census is unknown.
	ShrineHolds  []ShrineHold
	UpkeepIssued map[ConcernID]bool
	Gear         domain.Fact[GearObservation]
	Comfort      domain.Fact[ComfortObservation]
	// BasicComfort is the same census before the hosting-room filter: every
	// indoor seat at an eating surface and every recreation source, whatever
	// room (or none) hosts it. EnsureComfort's basic phase measures it; Comfort keeps
	// only facilities in rooms whose native role the facility catalog hosts.
	BasicComfort         domain.Fact[ComfortObservation]
	ComfortRecovered     domain.Fact[bool]
	ComfortDeficit       domain.Fact[float64]
	StartingSupplies     domain.Fact[[]StartingSupply]
	EventLoot            domain.Fact[[]LootItem]
	EventLootPending     domain.Fact[bool]
	LootReadiness        LootReadiness // the loot census's reach readiness (#522)
	MapBounds            domain.Fact[Bounds]
	MedicalPawns         domain.Fact[[]CarePawn]
	MedicalCareRecovered domain.Fact[bool]
	Workers              domain.Fact[int]
	// Labor is the per-work-type census of the same pawns Workers counts
	// (RoundsLabor); unknown labor leaves only the coarse worker bound.
	Labor domain.Fact[map[WorkType]int]
	// LaborUse is what those pawns are doing (RoundsLaborUse): the evidence
	// RankDevelopment releases an idle commitment's slot on.
	LaborUse domain.Fact[LaborUse]
	// WorkRoster is the planner's per-work-type coverage (PlanWork): the
	// owners each type wanted and found and the pawns capable of it, so a
	// goal can name a missing capability instead of stalling.
	WorkRoster domain.Fact[[]WorkCoverage]
	// WorkDecaying is the same plan's skills above 10 that no assignment
	// exercises (WorkDecision.Decaying) and WorkProfiles every work pawn's
	// typed profile (Profiles); both are presentation facts the review
	// records for presentation (#448), never planner inputs.
	WorkDecaying                                                               domain.Fact[[]DecayingSkill]
	WorkProfiles                                                               domain.Fact[[]PawnProfile]
	Colonists, HousingTarget, BedCapacity, IndoorCapacity, GrowingCells, Armed domain.Fact[int64]
	// Unarmed counts living, conscious colonists able to fight who hold no
	// weapon; EnsureBasicDefense stays owed while it is positive.
	Unarmed                 domain.Fact[int64]
	FoodDays, FieldCoverage domain.Fact[float64]
	// WorkHelp is the same plan's construction helper record (#653);
	// nil when the plan ran without the helper input.
	WorkHelp *ConstructionHelpRecord
	// Calendar is the tile's native growing calendar (policy.Calendar).
	// DetectRounds widens the policy's food and wood targets by its
	// harvest gap (RoundsPolicy.Seasonal) before measuring any latch;
	// an unknown calendar keeps the configured flat targets.
	Calendar                                                    domain.Fact[Calendar]
	SleepingMin, SleepingMax, OutdoorTemperature, PowerHeadroom domain.Fact[float64]
	// Forward are the shadow projector inputs the facts above lack (#1913).
	Forward ForwardObserved `json:",omitzero"`
	Wood    domain.Fact[int64]
	// Dependencies are the live typed shortfall edges (#651) carried from the
	// last review: an open shortfall raises a MaintainResource floor for
	// the bounded difference (#711, #728).
	Dependencies []DevelopmentDependency
	// Resources is the generic reachable, unforbidden player item census
	// (the same colony facts rows Wood is taken from), so MaintainResource's
	// deficit is measured at review time instead of assumed from config.
	Resources          domain.Fact[[]Amount]
	ResourceSurfaceOre map[Resource]domain.Fact[int64]
	ResourceRunways    []ResourceRunway
	// Wealth is the colony wealth split (#395) TradeWithCaravan's
	// wealth-driven surplus keys on; unknown leaves that surplus out.
	Wealth domain.Fact[WealthFacts]
	// Research is the native research state read inside the same paused
	// identity bracket as the other routine facts. Unknown when the source
	// cannot read research; missing facts never recover EnsureResearch.
	Research domain.Fact[ResearchFacts]
	// ResearchNeeds are the sorted ResearchProjectDefs other goals' recorded
	// evidence is waiting on (the workshop ladder's research rung); the
	// first unfinished one is EnsureResearch's target when none is
	// configured.
	ResearchNeeds []string
	// ResourceNeeds are derived stock floors other goals' recorded evidence
	// asks for (the defensive layout's turret fuel the census found no
	// stock of, #205); ResourceGoalTargets merges them into the operator's
	// MaintainResource targets, never lowering a configured floor.
	ResourceNeeds map[Resource]int64
	// DefensiveLayoutStanding is journal evidence for EnsureDefensiveLayout:
	// known true while the stored layout was verified complete and every
	// tier still stood at the last census, so a standing layout no longer
	// outranks the upkeep goals (repairs, power) that keep it working.
	DefensiveLayoutStanding    domain.Fact[bool]
	Hostiles, CriticalPatients domain.Fact[int64]
	// UrgentPatients counts the critical patients who are bleeding or downed
	// with a tend outstanding (policy.UrgentPatients). CriticalMedicine is an
	// emergency, suspending every other goal, only while one exists or the
	// count is unknown; a colonist who merely needs tending, or is downed
	// with nothing to tend, keeps the goal active at priority 2 so the
	// colony's other work and the clock go on around the tend or rescue.
	UrgentPatients                                                    domain.Fact[int64]
	AllPatientsResting, ColonyNaming, CleanupPawns, ForbiddenSupplies domain.Fact[bool]
	// HostilityOwed is a colonist whose hostility response differs from
	// the one it should hold (#1299); EnsureWorkAssignments writes it.
	HostilityOwed domain.Fact[bool]
	// MedicalCareOwed is a pawn whose medical care differs from its cap
	// (#1301); Guests are the colony's guests' cap inputs.
	MedicalCareOwed domain.Fact[bool]
	Guests          domain.Fact[[]CarePatient]
	// MedicineCarryOwed is a colonist whose medicine carry count differs
	// from the planned one (#1307); EnsureWorkAssignments writes it.
	MedicineCarryOwed domain.Fact[bool]
	// SelfTendOwed is a colonist whose self-tend setting differs from the
	// one it should hold (#1305); EnsureWorkAssignments writes it.
	SelfTendOwed domain.Fact[bool]
	// NamesOwed is an owned pawn whose short name an older owned pawn
	// holds (#1310); EnsureWorkAssignments renames it.
	NamesOwed domain.Fact[bool]
	// ChoiceDialog is true while the game is force-paused by a choice dialog
	// it opened by itself (#156); AnswerDialog is the goal that answers it.
	ChoiceDialog domain.Fact[bool]
	// ButcherBenches are the standing butcher benches and the room each stands
	// in; MaintainButcherSpot is recovered once one stands outside the kitchen.
	ButcherBenches                                                       domain.Fact[[]ButcherBench]
	FoodStorage, Cooking, WorkCoverage, PowerRequired, DisabledConsumers domain.Fact[bool]
	PowerWeatherSafe                                                     domain.Fact[bool]
	ShortCircuitTick                                                     domain.Fact[domain.Tick]
}

// The foothold facts below are read live from one review's facts: the
// foothold goals open on them (DetectRounds), and the colony stage
// (StageColonyFacts), the disaster services (DisasterServiceFacts) and the
// food ladder (FoodProgress) read the ones they need.

// footholdCount is the colonist count the foothold sizes for: the housing
// target when it is larger.
func footholdCount(f RoundsFacts) domain.Fact[int64] {
	count := f.Colonists
	if target, k := f.HousingTarget.Value(); k {
		if n, nk := count.Value(); nk {
			count = domain.Known(max(n, target))
		}
	}
	return count
}
func footholdSleeping(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.BedCapacity, footholdCount(f), 1)
}
func footholdShelter(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.IndoorCapacity, footholdCount(f), 1)
}
func footholdProduction(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.GrowingCells, footholdCount(f), 10)
}
func footholdFood(f RoundsFacts, p RoundsPolicy) domain.Fact[bool] {
	return measured(f.FoodDays, func(v float64) bool { return v >= p.FootholdFoodDays })
}
func footholdTemperature(f RoundsFacts, p RoundsPolicy) domain.Fact[bool] {
	return allFacts(measured(f.SleepingMin, func(v float64) bool { return v >= p.ColdEnter }), measured(f.SleepingMax, func(v float64) bool { return v <= p.HotEnter }))
}
func footholdPower(f RoundsFacts) domain.Fact[bool] {
	if safe, known := f.PowerWeatherSafe.Value(); known && !safe {
		return domain.Known(false)
	}
	power := measured(f.PowerHeadroom, func(v float64) bool { return v >= 0 })
	if required, k := f.PowerRequired.Value(); k && !required {
		power = domain.Known(true)
	} else if !k {
		power = domain.Unknown[bool]()
	}
	return allFacts(power, measured(f.DisabledConsumers, func(v bool) bool { return !v }))
}

// footholdArmed is two armed fighters (every colonist when fewer).
func footholdArmed(f RoundsFacts) domain.Fact[bool] {
	if n, k := footholdCount(f).Value(); k {
		return measured(f.Armed, func(v int64) bool { return v >= min(2, n) })
	}
	return domain.Unknown[bool]()
}

// DisasterServiceFacts are the survival services ReviewDisaster tracks,
// read live from the review's facts (infrastructure is its own).
func DisasterServiceFacts(f RoundsFacts, p RoundsPolicy) map[DisasterService]domain.Fact[bool] {
	return map[DisasterService]domain.Fact[bool]{
		DisasterFood: footholdFood(f, p), DisasterProduction: footholdProduction(f), DisasterSleeping: footholdSleeping(f),
		DisasterShelter: footholdShelter(f), DisasterTemperature: footholdTemperature(f, p), DisasterCooking: f.Cooking,
		DisasterPower: footholdPower(f), DisasterStorage: f.FoodStorage,
	}
}

type RoundsLatches struct {
	HomeCoverage, StoneShell bool
	Sleeping                 bool
	Animals                  AnimalUpkeepHistory
	MedicalReserve           bool
	FoodStorage              bool
	Refrigeration            bool
	// RefrigerationSince is the review tick the Refrigeration latch last
	// engaged, kept while it holds and zero when it is released: the
	// refrigeration planner lends native cooling time from it when the
	// goal's epoch has no cooler method of its own (#202).
	RefrigerationSince domain.Tick `json:",omitempty"`
	// Lighting holds the bench IDs MaintainLighting last measured dark.
	Lighting []string
	// Flooring holds the room keys MaintainFlooring last measured short.
	Flooring []string
	// Routes holds the facility IDs MaintainRoutes last measured unreachable.
	Routes                []string
	Food, Cold, Hot, Wood bool
	Upkeep                UpkeepHistory
	// Soldiers latches once the gear census derives a soldier role: the
	// research roadmap then walks the armor ladder (ArmorResearchLadder,
	// #470) and keeps walking it when the squad is later undrafted.
	Soldiers bool `json:",omitempty"`
	// Housing is the MaintainHousing phase this review left owed
	// (reviewHousing); empty once housing recovered. The housing planners
	// run only for their own phase.
	Housing Phase `json:",omitempty"`
	// Comfort is the EnsureComfort phase this review left owed: basic
	// (reachable table, seat and recreation) before ranked (hosting rooms
	// and proof of use, from StageDevelopment). Empty once recovered.
	Comfort Phase `json:",omitempty"`
	// Medical is the MaintainMedicalReserves phase this review left owed:
	// care (sick colonists resting, the hospital) before reserves (the
	// medicine stock). Empty once recovered.
	Medical Phase `json:",omitempty"`
}
type RoundsFindings struct {
	Disaster *DisasterHistory
	Latches  RoundsLatches
	Concerns []DevelopmentConcern
	// Assessments are the Standard and Project needs the review files rows for;
	// Incidents are the incident kinds' assessments (#1020, #1121).
	Assessments []RoundsAssessment
	Incidents   []RoundsAssessment
	// NoOps is every detector that raised nothing, with the typed reason
	// (#1909), in registry order.
	NoOps []NoOpRecord `json:",omitempty"`
	// WoodFloor is the WoodLog stock floor the wood latch asks of
	// MaintainResource, 0 while the latch is off (#728).
	WoodFloor int64
	// ResourceTargets are the effective MaintainResource stock targets the
	// review held the census to (configured, derived and the wood floor);
	// the stock overlay (#825) tints stockpiles by them.
	ResourceTargets map[Resource]int64 `json:",omitempty"`
}

// Assessments cover recovered and unknown needs as well as actionable deficits.
// Absence from the scheduling list is never evidence of recovery.
type RoundsAssessment struct {
	ID ConcernID
	// Subject is the pawn a per-pawn Response (EnsureMood) is assessed
	// for; empty otherwise. (ID, Subject) keys its incident (#1019).
	Subject  domain.PawnID `json:",omitempty"`
	Priority int
	Finding  domain.Finding
	// MethodUnavailable marks an emergency-tier upkeep need (a home fire)
	// whose serve family this runtime did not declare. The review still
	// records the need, but it must not suspend every other goal: with no
	// method to clear it and the clock held for it, nothing could ever
	// resume, which parked a power enclosure build behind an unfought
	// short-circuit fire (#435).
	MethodUnavailable bool
	// Hunt is the squad prey of an ActiveCombat deficit raised by the food
	// plan with no hostile standing (#1617): the incident's hunt origin.
	Hunt []domain.PawnID `json:",omitempty"`
}

// combatCleared is whether ActiveCombat has nothing to answer: no hostile
// stands and the food plan opens no squad hunt.
func combatCleared(f RoundsFacts) domain.Fact[bool] {
	cleared := measured(f.Hostiles, func(n int64) bool { return n == 0 })
	if positive(cleared) && len(HuntRequest(f.FoodPlan)) > 0 {
		return domain.Known(false)
	}
	return cleared
}

func positive(v domain.Fact[bool]) bool { b, k := v.Value(); return k && b }
func negative(v domain.Fact[bool]) bool { b, k := v.Value(); return k && !b }
func measured[T any](f domain.Fact[T], predicate func(T) bool) domain.Fact[bool] {
	v, k := f.Value()
	if !k {
		return domain.Unknown[bool]()
	}
	return domain.Known(predicate(v))
}
func allFacts(facts ...domain.Fact[bool]) domain.Fact[bool] {
	unknown := false
	for _, f := range facts {
		v, k := f.Value()
		if k && !v {
			return domain.Known(false)
		}
		unknown = unknown || !k
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(true)
}
func latchValue(active bool, fact domain.Fact[float64], enter, exit float64, high bool) bool {
	v, k := fact.Value()
	if !k {
		return active
	}
	if high {
		if active {
			return v >= exit
		}
		return v > enter
	}
	if active {
		return v <= exit
	}
	return v < enter
}

// fallback is f when measured, else other: an unmeasured sleeping-room
// temperature reads as the outdoor temperature (sleeping outdoors).
func fallback[T any](f, other domain.Fact[T]) domain.Fact[T] {
	if _, k := f.Value(); k {
		return f
	}
	return other
}
func countCapacity(capacity, count domain.Fact[int64], multiplier int64) domain.Fact[bool] {
	c, k := count.Value()
	v, vk := capacity.Value()
	if !k || !vk {
		return domain.Unknown[bool]()
	}
	return domain.Known(c > 0 && v >= c*multiplier)
}

// DetectRounds ports colony_policy.criteria/priority_nodes for the common
// survival goals: it reviews the facts once, then runs every registered
// inspection in registry order (inspections). Family-specific needs
// join these same goals during review.
func InspectRounds(f RoundsFacts, previous RoundsLatches, p RoundsPolicy) (RoundsFindings, error) {
	if c, known := f.Calendar.Value(); known && !c.Valid() {
		return RoundsFindings{}, errors.New("invalid calendar fact")
	}
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	owned, err := OwnedConstructions(f.ConstructionClaims, f.CurrentConstruction)
	if err != nil {
		return RoundsFindings{}, err
	}
	c := &roundsRun{f: f, previous: previous, p: p}
	if c.home, err = PlanHomeArea(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage); err != nil {
		return RoundsFindings{}, err
	}
	if c.stone, err = ReviewStoneShell(owned, f.StoneStructures); err != nil {
		return RoundsFindings{}, err
	}
	if c.animals, err = ReviewAnimalUpkeep(f.AnimalUpkeep, previous.Animals, p.FoodReserveDays); err != nil {
		return RoundsFindings{}, err
	}
	medicineFacts := f.MedicalReserve
	medicineFacts.Colonists = f.Colonists
	if c.medicine, err = ReviewMedicalReserve(medicineFacts, previous.MedicalReserve, p.MedicalReserve); err != nil {
		return RoundsFindings{}, err
	}
	if c.foodStorage, err = ReviewFoodStorage(f.FoodStorageUpkeep, previous.FoodStorage, p.FoodStorage); err != nil {
		return RoundsFindings{}, err
	}
	if c.refrigeration, err = ReviewRefrigeration(f.FoodStorageUpkeep, previous.Refrigeration, p.FoodStorage); err != nil {
		return RoundsFindings{}, err
	}
	c.refrigeration = c.refrigeration.WithTombs(f.TombsWarm)
	if c.upkeep, err = ReviewUpkeepWith(f.Upkeep, previous.Upkeep, f.UpkeepIssued, p.Cleanliness); err != nil {
		return RoundsFindings{}, err
	}
	if c.lighting, err = ReviewLighting(f.Upkeep.Lighting, previous.Lighting, p.Lighting, SkyDarkHold(f.DisasterConditions, f.OutdoorsDark)); err != nil {
		return RoundsFindings{}, err
	}
	if c.flooring, err = ReviewFlooring(f.Upkeep.Flooring, f.Upkeep.Rooms, previous.Flooring, p.Flooring); err != nil {
		return RoundsFindings{}, err
	}
	if c.routes, err = ReviewRoutes(f.Upkeep.Routes, previous.Routes, p.Routes); err != nil {
		return RoundsFindings{}, err
	}
	if v, known := f.ComfortDeficit.Value(); known && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v > 1) {
		return RoundsFindings{}, errors.New("invalid comfort deficit")
	}
	if err := p.Validate(); err != nil {
		return RoundsFindings{}, err
	}
	for _, fact := range []domain.Fact[int64]{f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Unarmed, f.Wood, f.Hostiles, f.CriticalPatients, f.UrgentPatients} {
		if v, k := fact.Value(); k && (v < 0 || v > math.MaxInt64/10) {
			return RoundsFindings{}, errors.New("invalid routine count")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage, f.SleepingMin, f.SleepingMax, f.OutdoorTemperature, f.PowerHeadroom} {
		if v, k := fact.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0)) {
			return RoundsFindings{}, errors.New("nonfinite routine fact")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage} {
		if v, k := fact.Value(); k && v < 0 {
			return RoundsFindings{}, errors.New("negative food fact")
		}
	}
	wood := domain.Unknown[float64]()
	if n, k := f.Wood.Value(); k {
		wood = domain.Known(float64(n))
	}
	c.sleepingActive = previous.Sleeping
	if recovered, known := f.SleepingRecovered.Value(); known {
		c.sleepingActive = !recovered
	}
	c.homeActive, c.stoneActive = previous.HomeCoverage, previous.StoneShell
	if diff, known := c.home.Value(); known {
		c.homeActive = !diff.Empty()
	}
	if rows, known := c.stone.Value(); known {
		c.stoneActive = len(rows) > 0
	}
	c.l = RoundsLatches{
		HomeCoverage: c.homeActive, StoneShell: c.stoneActive,
		Sleeping:       c.sleepingActive,
		Animals:        c.animals.History,
		MedicalReserve: c.medicine.Active,
		FoodStorage:    c.foodStorage.Active,
		Refrigeration:  c.refrigeration.Active,
		Lighting:       c.lighting.Dark,
		Flooring:       c.flooring.Latched,
		Routes:         c.routes.Latched,
		Upkeep:         c.upkeep.History,
		Food:           latchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
		Cold:           latchValue(previous.Cold, fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
		Hot:            latchValue(previous.Hot, fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
		Wood:           latchValue(previous.Wood, wood, float64(p.WoodMin), float64(p.WoodTarget), false),
		Soldiers:       previous.Soldiers || GearSoldierPresent(f.Gear),
	}
	c.r = RoundsFindings{Latches: c.l}
	for _, d := range inspections {
		goals, assessments := len(c.r.Concerns), len(c.r.Assessments)
		if err := d.Inspect(c); err != nil {
			return RoundsFindings{}, err
		}
		if n, noOp := noOpOf(d.Concern, c.r.Concerns[goals:], c.r.Assessments[assessments:]); noOp {
			c.r.NoOps = append(c.r.NoOps, n)
		}
	}
	r := c.r
	// Dominant environment thought pressure raises the owning upkeep goal's
	// deficit to at least the fraction of pawns under it (#255): the goal's
	// own census still decides whether it is active and what it builds, so a
	// recovered owner is not re-raised, and the pawn's EnsureMood incident
	// defers to it (MoodProvision) instead of dispatching need relief.
	for i := range r.Concerns {
		pressure, ok := MoodProvisionDeficits(f.Mood)[r.Concerns[i].ID]
		if !ok {
			continue
		}
		if current, known := r.Concerns[i].Deficit.Value(); !known || current < pressure {
			r.Concerns[i].Deficit = domain.Known(pressure)
		}
	}
	r.Concerns = raisedAtStage(r.Concerns, f, p, c.l)
	if methods, known := f.AvailableMethods.Value(); known {
		available := map[ConcernID]bool{}
		recognized := map[ConcernID]bool{}
		for _, assessment := range r.Assessments {
			recognized[assessment.ID] = true
		}
		// Disaster recovery is only assessed once a disaster history exists,
		// but its method capability is declared at composition time, before
		// any facts are read; it must validate against empty facts too.
		recognized[RecoverDisasterServices] = true
		for _, id := range methods {
			if !recognized[id] || available[id] {
				return RoundsFindings{}, errors.New("invalid routine method capability " + string(id))
			}
			available[id] = true
		}
		for i := range r.Concerns {
			if r.Concerns[i].Priority >= 3 && !available[r.Concerns[i].ID] {
				r.Concerns[i].MethodUnavailable = true
			}
		}
		// Each upkeep need's method is its own serve family; an undeclared
		// one at emergency priority is recorded without the suspension.
		declarable := map[ConcernID]bool{}
		for _, n := range c.upkeep.Needs {
			declarable[n.Concern] = true
		}
		for i := range r.Assessments {
			if a := r.Assessments[i]; a.Priority < 2 && declarable[a.ID] && !available[a.ID] {
				r.Assessments[i].MethodUnavailable = true
			}
		}
	}
	all := r.Assessments
	r.Assessments = nil
	for _, a := range all {
		if IsIncidentKind(a.ID) {
			r.Incidents = append(r.Incidents, a)
		} else {
			r.Assessments = append(r.Assessments, a)
		}
	}
	return r, nil
}

// All is every assessment, goal needs then incident kinds.
func (r RoundsFindings) All() []RoundsAssessment {
	return slices.Concat(r.Assessments, r.Incidents)
}

// criticalMedicinePriority is 1 (an emergency that suspends every other goal)
// while any critical patient is urgent (policy.UrgentPatients) or the urgent
// count is unknown, and 2 while every patient is stable: resting under care,
// only needing a tend, or downed with nothing to tend. A stable patient is
// served by the same tend and rescue methods; what the lower priority drops
// is the suspension that otherwise parked the colony and its clock behind a
// condition nobody could clear (#66, #304).
func criticalMedicinePriority(f RoundsFacts) int {
	patients, pk := f.CriticalPatients.Value()
	if !pk || patients == 0 {
		return 1
	}
	if positive(f.AllPatientsResting) {
		return 2
	}
	if urgent, known := f.UrgentPatients.Value(); known && urgent == 0 {
		return 2
	}
	return 1
}

// raisedAtStage marks the goals the colony stage does not raise yet
// (StageGoalAllowed) as Staged: they stay in the ranking with a waiting
// reason instead of vanishing, and take no slot. The exceptions: the stage's exceptions: MaintainResource opens
// early for the wood floor or a construction dependency, MaintainRefrigeration
// for a full spoiling emergency, the stone shell waits for stone blocks
// (a known unfinished Stonecutting) and the animal goals for a tame animal (a census that knows of none
// raises none).
func raisedAtStage(goals []DevelopmentConcern, f RoundsFacts, p RoundsPolicy, l RoundsLatches) []DevelopmentConcern {
	stage := p.ColonyStage
	for i := range goals {
		g := goals[i]
		allowed := StageConcernAllowed(g.ID, stage)
		switch g.ID {
		case MaintainResource:
			allowed = allowed || l.Wood || len(DependencyResourceNeeds(f.Dependencies)) > 0
		case MaintainRefrigeration:
			d, known := g.Deficit.Value()
			allowed = allowed || known && d >= 1
		case MaintainStoneShell:
			allowed = allowed && !stonecuttingUnfinished(f.Research)
		case MaintainAnimalContainment, MaintainAnimalFeed, MaintainHerd:
			animals, known := f.AnimalUpkeep.Animals.Value()
			allowed = allowed && !(known && len(animals) == 0)
		}
		goals[i].Staged = !allowed
	}
	return goals
}

// stonecuttingUnfinished: the research census is known and has not
// finished Stonecutting, so no stone block can be cut for a shell.
func stonecuttingUnfinished(research domain.Fact[ResearchFacts]) bool {
	r, known := research.Value()
	return known && !slices.Contains(r.Finished, "Stonecutting")
}

// cookingMet is EnsureCooking's recovery: cooking ready and no cooking
// campfire owed its retirement (#1179).
func cookingMet(f RoundsFacts) domain.Fact[bool] {
	if positive(f.CampfireRetireOwed) {
		return domain.Known(false)
	}
	return f.Cooking
}

// ButcherBench is a standing butcher bench and the room it stands in
// (unknown outdoors).
type ButcherBench struct {
	ID         string
	Definition string
	Room       domain.Fact[string]
}

// ButcherTableWood is the WoodLog stock at which a stand-in butcher spot is
// owed its table: the table is stuff-built, so the goal stays open below it
// only to wait, never to starve the colony of wood.
const ButcherTableWood int64 = 120

// butcherSpotMet is MaintainButcherSpot's recovery: a butcher bench stands
// that does not share a room with a cooking bench. A butcher bench inside
// the kitchen keeps the colony fed but not clean, so the goal stays open for
// a separate spot; the free, instant spot is always wanted, whatever the
// food runway, because hunts and hides are processed on it.
func butcherSpotMet(f RoundsFacts) domain.Fact[bool] {
	benches, known := f.ButcherBenches.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if len(benches) == 0 {
		return domain.Known(false)
	}
	shared, known := KitchenSeparation(f.Upkeep.Rooms).Value()
	if !known {
		return domain.Known(true)
	}
	colocated := map[string]bool{}
	for _, room := range shared {
		colocated[room.ID] = true
	}
	apart, table, spot := false, false, false
	for _, bench := range benches {
		if room, known := bench.Room.Value(); !known || !colocated[room] {
			apart = true
			table = table || bench.Definition != "ButcherSpot"
			spot = spot || bench.Definition == "ButcherSpot"
		}
	}
	if !apart {
		return domain.Known(false)
	}
	// A table standing beside its stand-in spot owes the spot's removal.
	if table && spot {
		return domain.Known(false)
	}
	// Only a stand-in spot stands apart: with the wood to build one, the
	// goal stays open for the butcher table.
	if wood, ok := f.Wood.Value(); !table && ok && wood >= ButcherTableWood {
		return domain.Known(false)
	}
	return domain.Known(true)
}
