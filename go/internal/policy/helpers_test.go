package policy

import (
	"math"
	"sort"

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
	Sarcophagus: testSarcophagus, AnimalSpot: testAnimalSpot, AnimalBed: testAnimalBed,
	Bench:    map[RoomRole]string{RoomRoleKitchen: testStove, RoomRoleWorkshop: testWorkshop, RoomRoleLaboratory: testResearch},
	EndTable: FacilityLink{Def: "EndTable", MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true, CardinalToHead: true},
	Dresser:  FacilityLink{Def: "Dresser", MaxDistance: 6, MaxSimultaneous: 1},
	Cabinet:  FacilityLink{Def: "ToolCabinet", MaxDistance: 8, MaxSimultaneous: 2},
	Monitor:  FacilityLink{Def: "VitalsMonitor", MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true},
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
	add("AnimalSleepingSpot", 1, 1, "", nil)
	add("AnimalBed", 1, 1, "", nil)
	add("HiTechResearchBench", 5, 2, RoomRoleLaboratory, front)
	return out
}()

// testComfortFurniture names the comfort methods the comfort tests expect.
var testComfortFurniture = DiningFurniture{
	Chair: InteriorPieceDef{Def: "Chair", Size: domain.Cell{X: 1, Z: 1}},
	Table: InteriorPieceDef{Def: "Table", Size: domain.Cell{X: 1, Z: 2}},
	Pin:   InteriorPieceDef{Def: "Pin", Size: domain.Cell{X: 1, Z: 1}},
	Lane:  6,
}

// GearMaterialBudget is the loadout model's Budget: what each measured
// material can fund after holds, the same
// floor food bills honour (#470). Unmeasured resources are absent, which the
// model treats as unfunded.
func GearMaterialBudget(stock []Stock, holds []Amount) []Amount {
	available, known := gearAvailable(stock, holds)
	out := []Amount{}
	for resource := range known {
		out = append(out, Amount{resource, available[resource]})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return out
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

// WasteDeficit is the binary MaintainWaste
// deficit signal: unknown census stays unknown (absence is never evidence of
// recovery), otherwise deficit is simply "any pending item remains".
func WasteDeficit(items domain.Fact[[]WasteItem]) domain.Fact[bool] {
	rows, known := items.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(pendingWaste(rows)) > 0)
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

// TurbineWindCells mirrors WindTurbineUtility.CalculateWindCells for a
// 7x2 turbine: 7 wide, 10 rows in front and 6 behind.
func TurbineWindCells(center domain.Cell, rot domain.Rotation) []domain.Cell {
	off, front, back := int32(0), int32(9), int32(5)
	if rot != domain.North && rot != domain.East {
		off, front, back = -1, 5, 9
	}
	var a, b Rectangle // X, Z, Width, Height as min/extent
	if rot == domain.East || rot == domain.West {
		a = Rectangle{X: center.X + 2 + off, Z: center.Z - 3, Width: front + 1, Height: 7}
		b = Rectangle{X: center.X - 1 - back + off, Z: center.Z - 3, Width: back + 1, Height: 7}
	} else {
		a = Rectangle{X: center.X - 3, Z: center.Z + 2 + off, Width: 7, Height: front + 1}
		b = Rectangle{X: center.X - 3, Z: center.Z - 1 - back + off, Width: 7, Height: back + 1}
	}
	var out []domain.Cell
	for _, r := range []Rectangle{a, b} {
		for z := r.Z; z < r.Z+r.Height; z++ {
			for x := r.X; x < r.X+r.Width; x++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

// MoodUnownedThought reports whether the thought is removable environment
// pressure no goal owns.
func MoodUnownedThought(def string) bool { return moodUnownedThoughts[def] }

// MoodProvisionOwners names the goals whose facility removes the thought,
// if the catalog knows any.
func MoodProvisionOwners(def string) []GoalID {
	return append([]GoalID(nil), moodProvisionOwners[def]...)
}

// WoodProposals: each designatable tree is one cut.
func WoodProposals(goal GoalID, method domain.MethodID, definition string, trees []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range trees {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "cut_plant:" + definition, Work: WorkPlantCutting, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// SupplyHaulProposals: each loose stack is one haul; two goals naming the
// same stack project one candidate.
func SupplyHaulProposals(goal GoalID, method domain.MethodID, definition string, things []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range things {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "haul:" + definition, Work: WorkHauling, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// AnimalFeedProposals: MaintainAnimalFeed's methods as alternatives of one
// group. A stock-sourced method is a haul; kibble is a bill on any of the
// shared benches (each bench an alternative) that can run only when its
// ingredients are observed; a hay field is growing on its cells.
func AnimalFeedProposals(goal GoalID, m AnimalFeedMethod, ingredients domain.Fact[bool], hay []domain.Cell) []ReadyProposal {
	group := string(goal) + "/feed"
	var out []ReadyProposal
	if m.Produced {
		for _, b := range m.Benches {
			out = append(out, ReadyProposal{Goal: goal, Method: "kibble", Stage: "bill:Make_Kibble", Work: WorkCooking, Claims: []ReadyClaim{{"bench", b}}, Alternative: group, Eligible: ingredients, Parallelism: 1})
		}
	} else if m.Resource != "" {
		out = append(out, ReadyProposal{Goal: goal, Method: domain.MethodID("stock-" + string(m.Resource)), Stage: "haul:" + string(m.Resource), Work: WorkHauling, Alternative: group, Eligible: domain.Known(m.Delivered), Reason: reasonIf(!m.Delivered, string(domain.HeldStorageMissing)), Parallelism: 1})
	}
	if len(hay) > 0 {
		var claims []ReadyClaim
		for _, c := range hay {
			claims = append(claims, CellClaim(c))
		}
		out = append(out, ReadyProposal{Goal: goal, Method: "hay", Stage: "grow:Hay", Work: WorkGrowing, Claims: claims, Alternative: group, Eligible: domain.Known(true), Parallelism: 1})
	}
	return out
}

func reasonIf(cond bool, reason string) string {
	if cond {
		return reason
	}
	return ""
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
	p.Battery, p.Sources = testBattery, testPowerSources()
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

// testTraitRows are the catalog-derived effects of the traits the planner
// tests use (DefinitionCatalog.TraitEffects, compared with the full game
// recording in the bridge tests); the code-applied flags come from traitFlags.
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
}

// testTrait is a pawn trait with its effects resolved the way the pawn read
// resolves them from the catalog.
func testTrait(name string, degree int) PawnTrait {
	key := traitKey{name, degree}
	return PawnTrait{Name: name, Degree: degree, Effects: testTraitRows[key].Add(traitFlags[key])}
}
