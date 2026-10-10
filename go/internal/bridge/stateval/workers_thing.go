package stateval

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The thing-group StatWorkers (epic #2621, #2639). MechEnergyLossPerHP and
// PsyfocusCost override only display strings, so they are plain workers.

func thingWorkerList() []Worker {
	return []Worker{
		workerColdContainmentBonus{}, workerContainmentStrength{}, workerMinimumContainmentStrength{},
		workerMeleeDamageAmountTrap{}, workerRoomReadingBonus{}, workerMaxPowerOutput{},
		workerMechEnergyLossPerHP{}, workerPsyfocusCost{}, workerSurgerySuccessChanceFactor{},
	}
}

const (
	classCompStudiable             = "RimWorld.CompStudiable"
	classCompHoldingPlatformTarget = "RimWorld.CompHoldingPlatformTarget"
	classCompEntityHolder          = "RimWorld.CompEntityHolder"
	classBuildingBookcase          = "RimWorld.Building_Bookcase"
	classBuildingBed               = "RimWorld.Building_Bed"
	classBuildingTrap              = "RimWorld.Building_Trap"

	statContainmentStrength = "ContainmentStrength"
)

// --- containment utility ---

// showContainmentStats is ContainmentUtility.ShowContainmentStats(req.Thing);
// a definition request has no thing, so it is false.
func showContainmentStats(req *Request) (bool, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return false, nil
	}
	studiable, err := thingCompProps(req, classCompStudiable)
	if err != nil {
		return false, err
	}
	if studiable != nil && ctx.Pawn != nil {
		// CompStudiable.RequiresHoldingPlatform: a mutant, else the props flag.
		mutant, err := need(ctx.Thing.PawnIsMutant, "whether the pawn is a mutant")
		if err != nil || mutant {
			return mutant, err
		}
		v, err := compField(studiable, "requiresHoldingPlatform")
		if err != nil {
			return false, err
		}
		return v.Bool(), nil
	}
	target, err := thingCompProps(req, classCompHoldingPlatformTarget)
	if err != nil || target == nil {
		return false, err
	}
	v, err := compField(target, "heldPawnKind")
	if err != nil {
		return false, err
	}
	return v.String() != "", nil
}

// coldContainmentBonus is ContainmentUtility.TryGetColdContainmentBonus.
func coldContainmentBonus(req *Request) (modifier float32, ok bool, err error) {
	ctx := thingContext(req)
	if ctx == nil {
		return 0, false, nil
	}
	target, err := thingCompProps(req, classCompHoldingPlatformTarget)
	if err != nil || target == nil {
		return 0, false, err
	}
	v, err := compField(target, "getsColdContainmentBonus")
	if err != nil || !v.Bool() {
		return 0, false, err
	}
	temp, err := need(ctx.Thing.AmbientTemperature, "the thing's ambient temperature")
	if err != nil {
		return 0, false, err
	}
	constants, err := req.Evaluator.catalog.GameConstants()
	if err != nil {
		return 0, false, err
	}
	modifier, err = EvaluateCurve(constants.GetContainmentUtility().GetColdEscapeFactorCurve(), temp)
	return modifier, err == nil, err
}

// workerColdContainmentBonus is StatWorker_ColdContainmentBonus.
type workerColdContainmentBonus struct{}

func (workerColdContainmentBonus) Class() string { return "StatWorker_ColdContainmentBonus" }

func (workerColdContainmentBonus) Show(req *Request, _ func() (bool, error)) (bool, error) {
	show, err := showContainmentStats(req)
	if err != nil || !show {
		return false, err
	}
	_, ok, err := coldContainmentBonus(req)
	return ok, err
}

func (workerColdContainmentBonus) Unfinalized(req *Request) (float32, error) {
	modifier, _, err := coldContainmentBonus(req)
	if err != nil {
		return 0, err
	}
	return float32(modifier - 1), nil
}

// workerMinimumContainmentStrength is StatWorker_MinimumContainmentStrength.
type workerMinimumContainmentStrength struct{}

func (workerMinimumContainmentStrength) Class() string {
	return "StatWorker_MinimumContainmentStrength"
}

func (workerMinimumContainmentStrength) Show(req *Request, _ func() (bool, error)) (bool, error) {
	return showContainmentStats(req)
}

// --- StatWorker_ContainmentStrength ---

// workerContainmentStrength is StatWorker_ContainmentStrength.
type workerContainmentStrength struct{}

func (workerContainmentStrength) Class() string { return "StatWorker_ContainmentStrength" }

func (workerContainmentStrength) Show(req *Request, _ func() (bool, error)) (bool, error) {
	if req.Thing != nil {
		holder, err := req.Evaluator.hasCompFrom(req.Thing, classCompEntityHolder)
		if err != nil || holder {
			return holder, err
		}
	}
	v, err := modifierFromList(req.statBases(), statContainmentStrength, 0)
	return v != 0, err
}

func (workerContainmentStrength) Unfinalized(req *Request) (float32, error) {
	base, err := req.Evaluator.baseUnfinalized(req)
	if err != nil {
		return 0, err
	}
	sum, err := containmentStrengthSum(req)
	if err != nil {
		return 0, err
	}
	return float32(base + sum), nil
}

// containmentStrengthSum is CalculateValues(req).Sum: zero for a request
// with no thing or no scored room.
func containmentStrengthSum(req *Request) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return 0, nil
	}
	room, err := need(ctx.Thing.Containment, "the thing's containment room")
	if err != nil || room == nil {
		return 0, err
	}
	e := req.Evaluator
	gc, err := e.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	c := gc.GetStatWorker_ContainmentStrength()

	outdoors := room.PsychologicallyOutdoors
	var lighting float32
	if !outdoors {
		for _, g := range room.CellGlows {
			lighting = float32(lighting + g)
		}
	}
	lighting = mul(float32(lighting/float32(room.CellCount)), c.GetLightingMultiplier())

	var wallHP, doorHP, floor, offset float32
	holderFactor := float32(1)
	if !outdoors {
		// CalculateDoorStats.
		var doorSum float32
		breached := false
		for _, door := range room.Doors {
			if door.ContainmentBreach {
				breached = true
				doorSum = 0
			} else if !breached {
				doorSum = float32(doorSum + float32(door.HitPoints))
			}
		}
		if comp, err := thingCompProps(req, classCompEntityHolder); err != nil {
			return 0, err
		} else if comp != nil {
			v, err := compField(comp, "containmentFactor")
			if err != nil {
				return 0, err
			}
			holderFactor = float32(v.Float())
		}
		var floorSum float32
		for _, name := range room.OpenCellTerrains {
			terrain := e.catalog.TerrainDef(name)
			if terrain == nil {
				return 0, fmt.Errorf("catalog has no terrain def %s", name)
			}
			s, err := modifierFromList(terrain.GetStatBases(), statContainmentStrength, 0)
			if err != nil {
				return 0, err
			}
			floorSum = float32(floorSum + s)
		}
		var wallSum float32
		for _, hp := range room.BorderEdificeHitPoints {
			wallSum = float32(wallSum + float32(hp))
		}
		if n := len(room.OpenCellTerrains); n != 0 {
			floor = float32(floorSum / float32(n))
		}
		if n := len(room.BorderEdificeHitPoints); n != 0 {
			wallHP, err = EvaluateCurve(c.GetWallContainmentStrengthFromHPCurve(), float32(wallSum/float32(n)))
			if err != nil {
				return 0, err
			}
		}
		if n := len(room.Doors); n != 0 {
			doorHP = float32(float32(doorSum/float32(n)) / c.GetDoorHpDivisor())
		}
	}
	total := float32(float32(float32(lighting+wallHP)+doorHP) + floor)
	before := total
	for i := int32(0); i < room.OtherHolders; i++ {
		total = mul(total, c.GetOtherHoldingPlatformsFactor())
	}
	offset = float32(total - before)
	var fullyRoofed float32
	if !outdoors && room.OpenRoofCount > 0 {
		fullyRoofed = c.GetNotFullyRoofed()
	}
	// globalOffset is always 0 in the game.
	sum := float32(lighting + wallHP)
	sum = float32(sum + doorHP)
	sum = float32(sum + offset)
	sum = float32(sum + 0)
	sum = float32(sum + fullyRoofed)
	sum = float32(sum + floor)
	return mul(sum, holderFactor), nil
}

// --- StatWorker_MeleeDamageAmountTrap (a StatWorker_MeleeDamageAmount) ---

// workerMeleeDamageAmountTrap is StatWorker_MeleeDamageAmountTrap.
type workerMeleeDamageAmountTrap struct{}

func (workerMeleeDamageAmountTrap) Class() string { return "StatWorker_MeleeDamageAmountTrap" }

func (workerMeleeDamageAmountTrap) Show(req *Request, _ func() (bool, error)) (bool, error) {
	if ctx := thingContext(req); ctx != nil {
		trap, err := thingClassIs(req, classBuildingTrap)
		if err != nil {
			return false, err
		}
		if trap {
			show, err := need(ctx.Thing.ShowTrapDamageStat, "Building_Trap.ShouldShowTrapDamageStat")
			if err != nil || !show {
				return false, err
			}
		}
	}
	if req.Thing == nil || req.Thing.GetCategory() != d.ThingCategory_THING_CATEGORY_BUILDING {
		return false, nil
	}
	building := req.Thing.GetBuilding()
	if building == nil {
		return false, fmt.Errorf("building def %s has no building properties", req.Subject.Def)
	}
	return building.GetIsTrap(), nil
}

func (workerMeleeDamageAmountTrap) Unfinalized(req *Request) (float32, error) {
	val, err := meleeDamageAmount(req)
	if err != nil {
		return 0, err
	}
	return float32(val / 5), nil
}

// meleeDamageAmount is StatWorker_MeleeDamageAmount.GetValueUnfinalized: the
// base value times the stuff's multiplier stat of the damage category, which
// the subclass names (the trap's trapDamageCategory).
func meleeDamageAmount(req *Request) (float32, error) {
	e := req.Evaluator
	val, err := e.baseUnfinalized(req)
	if err != nil {
		return 0, err
	}
	if req.Thing == nil {
		return 0, fmt.Errorf("%s: melee damage amount casts the requested terrain def to ThingDef", req.Subject.Def)
	}
	if req.Stuff == nil {
		return val, nil
	}
	building := req.Thing.GetBuilding()
	if building == nil {
		return 0, fmt.Errorf("def %s has no building properties for its trap damage category", req.Subject.Def)
	}
	category := building.GetTrapDamageCategory()
	if category == "" {
		return val, nil
	}
	row := bridge.DefRow[*d.DamageArmorCategoryDef](e.catalog, category)
	if row == nil {
		return 0, fmt.Errorf("catalog has no damage armor category %s", category)
	}
	multStat := row.GetMultStat()
	if multStat == "" {
		return val, nil
	}
	mult, err := e.Value(multStat, ThingSubject(req.Stuff.GetDefName(), ""))
	if err != nil {
		return 0, err
	}
	return mul(val, mult), nil
}

// --- bookcase and bed shows ---

// showForClass is the ShouldShowFor of a worker that is the base's and a
// ThingDef whose thingClass derives from class.
func showForClass(req *Request, base func() (bool, error), class string) (bool, error) {
	ok, err := base()
	if err != nil || !ok {
		return false, err
	}
	if req.Thing == nil {
		return false, nil
	}
	return req.Evaluator.classIsA(req.Thing.GetThingClass(), class)
}

// workerRoomReadingBonus is StatWorker_RoomReadingBonus.
type workerRoomReadingBonus struct{}

func (workerRoomReadingBonus) Class() string { return "StatWorker_RoomReadingBonus" }

func (workerRoomReadingBonus) Show(req *Request, base func() (bool, error)) (bool, error) {
	return showForClass(req, base, classBuildingBookcase)
}

// workerSurgerySuccessChanceFactor is StatWorker_SurgerySuccessChanceFactor.
type workerSurgerySuccessChanceFactor struct{}

func (workerSurgerySuccessChanceFactor) Class() string {
	return "StatWorker_SurgerySuccessChanceFactor"
}

func (workerSurgerySuccessChanceFactor) Show(req *Request, base func() (bool, error)) (bool, error) {
	return showForClass(req, base, classBuildingBed)
}

// --- StatWorker_MaxPowerOutput ---

// workerMaxPowerOutput is StatWorker_MaxPowerOutput: the negated power
// consumption of the def's power plant comp.
type workerMaxPowerOutput struct{}

func (workerMaxPowerOutput) Class() string { return "StatWorker_MaxPowerOutput" }

func (workerMaxPowerOutput) Unfinalized(req *Request) (float32, error) {
	if req.Thing == nil {
		return 0, nil // the game logs "non-thing" and answers 0
	}
	comp, err := thingCompProps(req, "RimWorld.CompPowerPlant")
	if err != nil {
		return 0, err
	}
	if comp == nil {
		return 0, nil // the game logs "no CompPowerPlant" and answers 0
	}
	props, ok := comp.Interface().(*d.CompProperties_Power)
	if !ok {
		return 0, fmt.Errorf("power plant comp of %s is a %s, not CompProperties_Power", req.Subject.Def, comp.Descriptor().FullName())
	}
	consumption := props.GetBasePowerConsumption()
	for _, up := range props.GetPowerUpgrades() {
		upgrade := up.GetValue()
		if upgrade == nil {
			return 0, fmt.Errorf("power upgrade of %s is empty", req.Subject.Def)
		}
		project := upgrade.GetResearchProject()
		if project == "" {
			continue
		}
		var finished map[string]bool
		if ctx := thingContext(req); ctx != nil {
			finished, err = need(ctx.Thing.ResearchFinished, "the finished research projects")
		} else {
			err = fmt.Errorf("stat evaluation needs the finished research projects for %s's power upgrade: a definition request states none", req.Subject.Def)
		}
		if err != nil {
			return 0, err
		}
		if finished[project] {
			consumption = mul(consumption, upgrade.GetFactor())
		}
	}
	return float32(0 - consumption), nil
}

// --- display-only workers ---

// workerMechEnergyLossPerHP is StatWorker_MechEnergyLossPerHP, which only
// scales the value for display.
type workerMechEnergyLossPerHP struct{}

func (workerMechEnergyLossPerHP) Class() string { return "StatWorker_MechEnergyLossPerHP" }

// workerPsyfocusCost is StatWorker_PsyfocusCost, which only overrides the
// draw-entry label and the explanation.
type workerPsyfocusCost struct{}

func (workerPsyfocusCost) Class() string { return "StatWorker_PsyfocusCost" }
