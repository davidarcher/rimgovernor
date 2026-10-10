package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// testDining is the dining furniture shapes the dining template lays out: a
// chair and pin of one cell, a one-by-two table, and the pin's six-cell lane
// (watch stand distance 5 plus the pin).
var testDining = DiningFurniture{
	Chair: InteriorPieceDef{Def: "DiningChair", Size: domain.Cell{X: 1, Z: 1}},
	Table: InteriorPieceDef{Def: "Table1x2c", Size: domain.Cell{X: 1, Z: 2}},
	Pin:   InteriorPieceDef{Def: "HorseshoesPin", Size: domain.Cell{X: 1, Z: 1}},
	Lane:  6,
}

// The Core furniture the room furniture rules choose (testFurniture), named
// once for the tests that expect them.
const (
	testSarcophagus  = "Sarcophagus"
	testStove        = "FueledStove"
	testWorkshop     = "TableStonecutter"
	testResearch     = "SimpleResearchBench"
	testAnimalSpot   = "AnimalSleepingSpot"
	testAnimalBed    = "AnimalBed"
	testCoupleBed    = "DoubleBed"
	testPrimaryBed   = "Bed"
	testBedroll      = SleepingBedrollDefinition
	testSleepingSpot = SleepingSpotDefinition
)

// testFurniture is the furniture the catalog rules choose on Core, as the
// recorded catalog gives it (observation.TestRecordedCatalogFurniture).
var testFurniture = RoomFurniture{
	Beds: []FurnitureBed{
		{testPrimaryBed, 1, false}, {testBedroll, 1, false}, {testSleepingSpot, 1, true},
		{testCoupleBed, 2, false}, {SleepingCoupleBedrollDefinition, 2, false}, {"RoyalBed", 2, false}, {"DoubleSleepingSpot", 2, true},
	},
	Sarcophagus: testSarcophagus, Heater: "Heater", AnimalSpot: testAnimalSpot, AnimalBed: testAnimalBed,
	Bench:       map[RoomRole]string{RoomRoleKitchen: testStove, RoomRoleWorkshop: testWorkshop, RoomRoleLaboratory: testResearch},
	EndTable:    FacilityLink{Def: "EndTable", MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true, CardinalToHead: true},
	Dresser:     FacilityLink{Def: "Dresser", MaxDistance: 6, MaxSimultaneous: 1},
	Cabinet:     FacilityLink{Def: "ToolCabinet", MaxDistance: 8, MaxSimultaneous: 2},
	Monitor:     FacilityLink{Def: "VitalsMonitor", MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true},
	AdvancedLab: "HiTechResearchBench",
	Analyzer:    FacilityLink{Def: "MultiAnalyzer", MaxDistance: 8, MaxSimultaneous: 1},
}

// testShapes are the Core furniture shapes the interior templates lay out, as
// the catalog rows give them, with testFurniture.
var testShapes = func() PieceShapes {
	front := &domain.Cell{X: 0, Z: -1}
	defs := map[string]InteriorPieceDef{}
	out := PieceShapes{Defs: defs, Furniture: testFurniture}
	add := func(def string, w, h int32, family RoomRole, interaction *domain.Cell) {
		defs[def] = InteriorPieceDef{Def: def, Size: domain.Cell{X: w, Z: h}, Family: family, Interaction: interaction}
	}
	for _, def := range []string{"Bed", "SleepingSpot", "Bedroll", "SlabBed"} {
		add(def, 1, 2, RoomRoleBedroom, nil)
	}
	for _, def := range []string{"DoubleBed", "RoyalBed", "DoubleSleepingSpot"} {
		add(def, 2, 2, RoomRoleBedroom, nil)
	}
	add("HospitalBed", 1, 2, RoomRoleHospital, nil)
	add("EndTable", 1, 1, "", nil)
	add("Dresser", 2, 1, "", nil)
	add("StandingLamp", 1, 1, "", nil)
	add("Heater", 1, 1, "", nil)
	add("ToolCabinet", 2, 1, "", nil)
	add("ShelfSmall", 1, 1, "", nil)
	add("VitalsMonitor", 1, 1, "", nil)
	add(testSarcophagus, 1, 2, "", nil)
	for _, def := range []string{testStove, "ElectricStove"} {
		add(def, 3, 1, RoomRoleKitchen, front)
	}
	for _, def := range []string{testWorkshop, "TableStonecutter", "ElectricSmithy", "HandTailoringBench"} {
		add(def, 3, 1, RoomRoleWorkshop, front)
	}
	add("FabricationBench", 5, 2, RoomRoleWorkshop, front)
	add("TableButcher", 3, 1, "", front)
	add(testResearch, 3, 2, RoomRoleLaboratory, front)
	add("Campfire", 1, 1, "", nil)
	add("CraftingSpot", 1, 1, "", nil)
	add("PassiveCooler", 1, 1, "", nil)
	add("AnimalSleepingSpot", 1, 1, "", nil)
	add("AnimalBed", 1, 1, "", nil)
	add("HiTechResearchBench", 5, 2, RoomRoleLaboratory, front)
	add("MultiAnalyzer", 2, 2, "", nil)
	return out
}()

// testComfortFurniture names the comfort methods the comfort tests expect.
var testComfortFurniture = DiningFurniture{
	Chair: InteriorPieceDef{Def: "Chair", Size: domain.Cell{X: 1, Z: 1}},
	Table: InteriorPieceDef{Def: "Table", Size: domain.Cell{X: 1, Z: 2}},
	Pin:   InteriorPieceDef{Def: "Pin", Size: domain.Cell{X: 1, Z: 1}},
	Lane:  6,
}

// TierStyleStockOf folds a stock census into the map the rules read,
// summing repeated rows and ignoring negative counts.
func TierStyleStockOf(rows []Amount) TierStyleStock {
	stock := TierStyleStock{}
	for _, row := range rows {
		if row.Count > 0 {
			stock[row.Resource] += row.Count
		}
	}
	return stock
}

// Tier returns the named tier; ok is false for an unknown name.
func (l DefenseLayout) Tier(name DefenseTierName) (DefenseTier, bool) {
	for _, t := range l.Tiers {
		if t.Name == name {
			return t, true
		}
	}
	return DefenseTier{}, false
}

// HorseshoesLane is the canonical rectangle a pin at the back wall keeps
// clear: three cells wide, from the pin five cells toward the entrance.
func HorseshoesLane(f InteriorFrame) Rectangle {
	return Rectangle{X: CentreStart(f.Width, 1) - 1, Z: f.Depth - f.Dining.Lane, Width: 3, Height: f.Dining.Lane - 1}
}

// WoodProposals: each designatable tree is one cut.
func WoodProposals(goal ConcernID, method domain.MethodID, definition string, trees []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range trees {
		out = append(out, ReadyProposal{Concern: goal, Method: method, Stage: "cut_plant:" + definition, Work: WorkPlantCutting, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// SupplyHaulProposals: each loose stack is one haul; two goals naming the
// same stack project one candidate.
func SupplyHaulProposals(goal ConcernID, method domain.MethodID, definition string, things []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range things {
		out = append(out, ReadyProposal{Concern: goal, Method: method, Stage: "haul:" + definition, Work: WorkHauling, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// MirrorPiece is a piece's mirror image across the frame's centre line,
// under a new slot name.
func (f InteriorFrame) MirrorPiece(p InteriorPiece, slot string) InteriorPiece {
	p.Slot = slot
	p.Rect.X = MirrorStart(f.Width, p.Rect.X, p.Rect.Width)
	if p.Rot == domain.East || p.Rot == domain.West {
		p.Rot = rotateCW(p.Rot, 2)
	}
	return p
}

// MirrorStart is where the mirror image of a span starting at start lies.
func MirrorStart(length, start, span int32) int32 { return length - start - span }

// Batteries is the number of batteries that close the storage shortfall.
func (b PowerBudget) Batteries() int {
	if b.StorageShortfallWD <= 0 {
		return 0
	}
	return int(math.Ceil(b.StorageShortfallWD / testBattery.CapacityWD))
}

// testBattery is the stock Battery def's CompProperties_Battery.
var testBattery = PowerBattery{CapacityWD: 600, Efficiency: 0.5}

// testPowerSources are the catalog's delivery profiles of the stock generators.
func testPowerSources() map[string]PowerSourceProfile {
	return map[string]PowerSourceProfile{
		"SolarGenerator": SolarPowerProfile, WindTurbineDefinition: WindPowerProfile, "WoodFiredGenerator": ConstantPowerProfile,
		"ChemfuelPoweredGenerator": ConstantPowerProfile, GeothermalDefinition: ConstantPowerProfile,
	}
}

// testPowerPlanning is DefaultPowerPlanning over the stock catalog rows.
func testPowerPlanning() PowerPlanning {
	p := DefaultPowerPlanning()
	p.Battery, p.Sources, p.Light = testBattery, testPowerSources(), LightTerrains{"Soil": true}
	return p
}

// testGeneratorOptions is DefaultGeneratorOptions over the stock sources.
func testGeneratorOptions(available func(string) domain.Fact[bool], stock func(Resource) domain.Fact[int64]) []GeneratorOption {
	options, err := DefaultGeneratorOptions(available, stock, testPowerSources())
	if err != nil {
		panic(err)
	}
	return options
}

// The Impressiveness room stat's stage scores of Core's RoomStats.xml, as the
// catalog's ImpressivenessLevels reads them.
const (
	ImpressivenessDull               = 20.0
	ImpressivenessMediocre           = 30.0
	ImpressivenessDecent             = 40.0
	ImpressivenessSlightlyImpressive = 50.0
)

var testImpressiveness = ImpressivenessLevels{Dull: ImpressivenessDull, Mediocre: ImpressivenessMediocre, Decent: ImpressivenessDecent, SlightlyImpressive: ImpressivenessSlightlyImpressive}

type traitKey struct {
	Name   string
	Degree int
}

// testTraitRows are the catalog-derived effects of the traits the planner
// tests use (DefinitionCatalog.TraitEffects, compared with the full game
// recording in the bridge tests); the code-applied sociability comes from
// TraitSociable.
var testTraitRows = map[traitKey]TraitEffects{
	{"Industriousness", 2}:   {WorkSpeed: 0.35},
	{"Industriousness", -2}:  {WorkSpeed: -0.35},
	{"FastLearner", 0}:       {LearnRate: 0.75},
	{"TooSmart", 0}:          {LearnRate: 0.75},
	{"SpeedOffset", 2}:       {MoveSpeed: 0.4},
	{"QuickSleeper", 0}:      {QuickSleeper: true},
	{"Pyromaniac", 0}:        {DisabledWork: []WorkType{WorkFirefighter}, Pyromaniac: true},
	{"NightOwl", 0}:          {NightShift: true},
	{"Tough", 0}:             {FrontLine: true},
	{"Nimble", 0}:            {FrontLine: true},
	{"Ascetic", 0}:           {Ascetic: true},
	{"Greedy", 0}:            {Greedy: true},
	{"Jealous", 0}:           {Jealous: true},
	{"DrugDesire", 2}:        {ChemicalInterest: 2},
	{"DrugDesire", 1}:        {ChemicalInterest: 1},
	{"DrugDesire", -1}:       {ChemicalInterest: -1},
	{"Cannibal", 0}:          {Cannibal: true, HumanButcher: true},
	{"Psychopath", 0}:        {Execution: true, HumanButcher: true, SurgeonSafe: true},
	{"Bloodlust", 0}:         {Execution: true, HumanButcher: true, SurgeonSafe: true, TaintFree: true},
	{"Nudist", 0}:            {Nudist: true},
	{"Brawler", 0}:           {MeleeOnly: true, FrontLine: true},
	{"ShootingAccuracy", 1}:  {RearRanged: true},
	{"ShootingAccuracy", -1}: {RearRanged: true},
	{"Undergrounder", 0}:     {Undergrounder: true},
	{"Gourmand", 0}:          {Gourmand: true},
}

// testTrait is a pawn trait with its effects resolved the way the pawn read
// resolves them from the catalog.
func testTrait(name string, degree int) PawnTrait {
	key := traitKey{name, degree}
	effects := testTraitRows[key]
	effects.Sociable = TraitSociable(name)
	return PawnTrait{Name: name, Degree: degree, Effects: effects}
}
