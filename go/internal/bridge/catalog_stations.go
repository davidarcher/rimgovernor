package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The CLR classes and place workers the station catalog matches.
const (
	classWorkTable      = "RimWorld.Building_WorkTable"
	classStorage        = "RimWorld.Building_Storage"
	classSarcophagus    = "RimWorld.Building_Sarcophagus"
	classThrone         = "RimWorld.Building_Throne"
	placeAttachedToWall = "RimWorld.Placeworker_AttachedToWall"
	placeMustBeIndoors  = "RimWorld.PlaceWorker_MustBeIndoors"
	placeNotUnderRoof   = "RimWorld.PlaceWorker_NotUnderRoof"

	// designationProduction is the DesignationCategoryDefOf.Production the
	// kitchen role worker tests buildings against.
	designationProduction = "Production"
	// statWorkTableSpeed is the StatDef every work table's rate runs through.
	statWorkTableSpeed = "WorkTableWorkSpeedFactor"
	// statBabyPlayGain and lifeStageHumanlikeChild are the stat and life stage
	// the playroom and nursery workers read.
	statBabyPlayGain        = "BabyPlayGainFactor"
	lifeStageHumanlikeChild = "HumanlikeChild"
)

// StationCatalog is the catalog of buildable stations: for every building
// that adds to a native room role, has a capacity or has environment needs,
// its shape (the same as PieceShapes), access, role contributions, capacity,
// needs and content. Shapes, membership, capacity and needs come from the
// defs and the stat parts and constants the catalog mirrors; only the role
// weights, which are literals in RoomRoleWorker code, come from policy
// (roomRoleScores, a DefTables entry).
func (catalog *DefinitionCatalog) StationCatalog() (policy.StationCatalog, error) {
	if catalog == nil {
		return policy.StationCatalog{}, contract("no definition catalog")
	}
	catalog.stationsOnce.Do(func() { catalog.stations, catalog.stationsErr = catalog.buildStations() })
	return catalog.stations, catalog.stationsErr
}

func (catalog *DefinitionCatalog) buildStations() (policy.StationCatalog, error) {
	shapes, err := catalog.PieceShapes()
	if err != nil {
		return policy.StationCatalog{}, err
	}
	recipes := catalog.recipesByStation()
	rec, err := catalog.recRoomDefs()
	if err != nil {
		return policy.StationCatalog{}, err
	}
	childSize, hasChild := lifeStageBodySize(catalog)
	out := policy.StationCatalog{Stations: map[string]policy.Station{}}
	for name, row := range catalog.ThingDefs {
		shape, ok := shapes.Defs[name]
		if !ok || row.GetCategory() != d.ThingCategory_THING_CATEGORY_BUILDING {
			continue
		}
		b := stationBuilder{catalog: catalog, name: name, row: row, recipes: recipes[name], rec: rec, childSize: childSize, hasChild: hasChild}
		station := policy.Station{Def: name, Shape: shape, Access: policy.AccessFloor, Content: row.GetModPackageId(), Minifiable: row.GetMinifiedDef() != ""}
		if row.GetBuilding().GetCanPlaceOverWall() || row.GetBuilding().GetIsPlaceOverableWall() || slices.Contains(row.GetPlaceWorkers(), placeAttachedToWall) {
			station.Access = policy.AccessWall
		}
		if station.Contributions, err = b.contributions(); err != nil {
			return policy.StationCatalog{}, err
		}
		station.EmptyCountsForBarracks = row.GetBuilding().GetBedEmptyCountsForBarracks()
		if station.Capacity, err = b.capacity(); err != nil {
			return policy.StationCatalog{}, err
		}
		if station.Needs, err = b.needs(); err != nil {
			return policy.StationCatalog{}, err
		}
		station.Cooling = b.cooling()
		if station.Access == policy.AccessFloor && len(station.Contributions) == 0 && station.Capacity.Kind == policy.CapacityNone && len(station.Needs) == 0 && !station.Cooling.Cools() {
			continue
		}
		out.Stations[name] = station
	}
	return out, nil
}

// recipesByStation are the recipes each building hosts: those it lists and
// those that list it as a user (ThingDef.AllRecipes).
func (catalog *DefinitionCatalog) recipesByStation() map[string][]*d.RecipeDef {
	out := map[string][]*d.RecipeDef{}
	rows := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	for name, row := range catalog.ThingDefs {
		for _, recipe := range row.GetRecipes() {
			if r, ok := rows[recipe].(*d.RecipeDef); ok && !slices.Contains(out[name], r) {
				out[name] = append(out[name], r)
			}
		}
	}
	for _, m := range rows {
		recipe := m.(*d.RecipeDef)
		for _, user := range recipe.GetRecipeUsers() {
			if !slices.Contains(out[user], recipe) {
				out[user] = append(out[user], recipe)
			}
		}
	}
	for _, list := range out {
		slices.SortFunc(list, func(a, b *d.RecipeDef) int { return compareStrings(a.GetDefName(), b.GetDefName()) })
	}
	return out
}

func compareStrings(a, b string) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	}
	return 0
}

// recRoomDefs are the defs a joy giver that counts for the rec room lists.
func (catalog *DefinitionCatalog) recRoomDefs() (map[string]bool, error) {
	out := map[string]bool{}
	for _, m := range catalog.Defs[(&d.JoyGiverDef{}).ProtoReflect().Descriptor().FullName()] {
		giver := m.(*d.JoyGiverDef)
		if !giver.GetCountsForRecRoom() {
			continue
		}
		for _, name := range giver.GetThingDefs() {
			out[name] = true
		}
	}
	return out, nil
}

// lifeStageBodySize is the body size factor of the humanlike child life
// stage: a bed whose limit is below it is a baby bed (the nursery worker).
func lifeStageBodySize(catalog *DefinitionCatalog) (float32, bool) {
	stage := DefRow[*d.LifeStageDef](catalog, lifeStageHumanlikeChild)
	return stage.GetBodySizeFactor(), stage != nil
}

type stationBuilder struct {
	catalog   *DefinitionCatalog
	name      string
	row       *d.ThingDef
	recipes   []*d.RecipeDef
	rec       map[string]bool
	childSize float32
	hasChild  bool
}

func (b stationBuilder) is(class string) (bool, error) {
	return b.catalog.ClassIsA(b.row.GetThingClass(), class)
}

// score is the policy weight of the role under the condition as a
// contribution.
func score(role policy.RoomRole, when policy.RoleCondition) (policy.RoleContribution, bool) {
	s, ok := policy.RoomRoleScore(role, when)
	return s, ok
}

// contributions are the role scores the building earns, by the membership
// each RoomRoleWorker tests.
func (b stationBuilder) contributions() ([]policy.RoleContribution, error) {
	var out []policy.RoleContribution
	add := func(role policy.RoomRole, when policy.RoleCondition) {
		if c, ok := score(role, when); ok {
			out = append(out, c)
		}
	}
	building := b.row.GetBuilding()
	if b.row.GetDesignationCategory() == designationProduction {
		for _, recipe := range b.recipes {
			edible, err := b.makesHumanFood(recipe)
			if err != nil {
				return nil, err
			}
			if edible {
				add(policy.RoomRoleKitchen, policy.CondAlways)
				break
			}
		}
	}
	for _, role := range []policy.RoomRole{policy.RoomRoleWorkshop, policy.RoomRoleLaboratory} {
		if building.GetWorkTableRoomRole() == string(role) {
			add(role, policy.CondAlways)
		}
	}
	if b.row.GetSurfaceType() == d.SurfaceType_SURFACE_TYPE_EAT {
		add(policy.RoomRoleDiningRoom, policy.CondAlways)
	}
	if b.rec[b.name] {
		add(policy.RoomRoleRecRoom, policy.CondAlways)
	}
	for _, test := range []struct {
		class string
		role  policy.RoomRole
	}{{classStorage, policy.RoomRoleStoreroom}, {classSarcophagus, policy.RoomRoleTomb}, {classThrone, policy.RoomRoleThroneRoom}} {
		match, err := b.is(test.class)
		if err != nil {
			return nil, err
		}
		if match {
			add(test.role, policy.CondAlways)
		}
	}
	if err := b.bedContributions(add); err != nil {
		return nil, err
	}
	if b.row.GetIsAltar() {
		add(policy.RoomRoleWorshipRoom, policy.CondIdeoAltar)
	}
	if role := policy.GameRoomRoleDefs[b.name]; role == policy.RoleDesk || role == policy.RoleBoard {
		add(policy.RoomRoleClassroom, policy.CondAlways)
	}
	if b.statBase(statBabyPlayGain) > 1 {
		add(policy.RoomRolePlayroom, policy.CondAlways)
	}
	if role, ok := policy.RoomRoleNamedDefs[b.name]; ok {
		add(role, policy.CondAlways)
	} else if bindable, err := b.catalog.HasComp(b.row, ClassDeathrestComp); err != nil {
		return nil, err
	} else if bindable {
		add(policy.RoomRoleDeathrestChamber, policy.CondBindable)
	}
	slices.SortStableFunc(out, func(x, y policy.RoleContribution) int {
		if c := compareStrings(string(x.Role), string(y.Role)); c != 0 {
			return c
		}
		return compareStrings(string(x.When), string(y.When))
	})
	return out, nil
}

// bedContributions are the scores the humanlike and animal bed workers give.
func (b stationBuilder) bedContributions(add func(policy.RoomRole, policy.RoleCondition)) error {
	bed, err := b.is(ClassBed)
	if err != nil || !bed {
		return err
	}
	building := b.row.GetBuilding()
	if !building.GetBedHumanlike() {
		add(policy.RoomRoleBarn, policy.CondAlways)
		return nil
	}
	if building.GetBedCountsForBedroomOrBarracks() {
		add(policy.RoomRoleBedroom, policy.CondBedFree)
		add(policy.RoomRoleBarracks, policy.CondBedFree)
	}
	add(policy.RoomRoleHospital, policy.CondBedMedical)
	if b.hasChild && building.GetBedMaxBodySize() < b.childSize {
		add(policy.RoomRoleNursery, policy.CondBedFree)
	}
	add(policy.RoomRolePrisonCell, policy.CondBedPrisoner)
	add(policy.RoomRolePrisonCell, policy.CondBedPrisonerMedical)
	add(policy.RoomRolePrisonBarracks, policy.CondBedPrisoner)
	add(policy.RoomRolePrisonBarracks, policy.CondBedPrisonerMedical)
	return nil
}

// makesHumanFood is the kitchen worker's product test: the recipe makes a
// nutrition-giving ingestible a human can eat (foodType & OmnivoreHuman).
func (b stationBuilder) makesHumanFood(recipe *d.RecipeDef) (bool, error) {
	for _, product := range recipe.GetProducts() {
		row := b.catalog.ThingDef(product.GetValue().GetThingDef())
		if row == nil || row.GetIngestible() == nil {
			continue
		}
		if int32(row.GetIngestible().GetFoodType())&int32(d.FoodTypeFlags_FOOD_TYPE_FLAGS_OMNIVORE_HUMAN) == 0 {
			continue
		}
		nutrition, shown, err := b.catalog.ShownStatValue(row.GetDefName(), "", StatNutrition)
		if err != nil {
			return false, err
		}
		if shown && nutrition > 0 {
			return true, nil
		}
	}
	return false, nil
}

func (b stationBuilder) statBase(stat string) float32 {
	for _, sm := range b.row.GetStatBases() {
		if sm.GetValue().GetStat() == stat {
			return sm.GetValue().GetValue()
		}
	}
	return 0
}

// capacity is what one building serves.
func (b stationBuilder) capacity() (policy.StationCapacity, error) {
	bed, err := b.is(ClassBed)
	if err != nil {
		return policy.StationCapacity{}, err
	}
	if bed {
		return policy.StationCapacity{Kind: policy.CapacitySleepers, Slots: int(b.row.GetSize().GetX())}, nil
	}
	storage, err := b.is(classStorage)
	if err != nil {
		return policy.StationCapacity{}, err
	}
	if storage && b.row.GetBuilding().GetFixedStorageSettings() != nil {
		cells := int(b.row.GetSize().GetX() * b.row.GetSize().GetZ())
		return policy.StationCapacity{Kind: policy.CapacityItems, Slots: cells * int(b.row.GetBuilding().GetMaxItemsInCell())}, nil
	}
	table, err := b.is(classWorkTable)
	if err != nil || !table || len(b.recipes) == 0 {
		return policy.StationCapacity{}, err
	}
	capacity := policy.StationCapacity{Kind: policy.CapacityWorkers, Slots: 1}
	for _, recipe := range b.recipes {
		work, err := b.catalog.RecipeWorkAmount(recipe.GetDefName())
		if err != nil {
			return policy.StationCapacity{}, err
		}
		if amount, known := work.Value(); known && amount > 0 {
			capacity.Recipes = append(capacity.Recipes, policy.RecipeWorkAmount{Recipe: recipe.GetDefName(), Work: amount})
		}
	}
	return capacity, nil
}

// needs are the environment needs the defs state: the room stats and
// work-table stat parts of the stats the building is subject to, its place
// workers, and cold for a store that takes perishable things.
func (b stationBuilder) needs() ([]policy.EnvNeed, error) {
	var out []policy.EnvNeed
	subject := map[string]float32{}
	var order []string
	for _, sm := range b.row.GetStatBases() {
		stat := sm.GetValue().GetStat()
		if _, seen := subject[stat]; !seen {
			order = append(order, stat)
		}
		subject[stat] = sm.GetValue().GetValue()
	}
	table, err := b.is(classWorkTable)
	if err != nil {
		return nil, err
	}
	if table {
		if _, seen := subject[statWorkTableSpeed]; !seen {
			order = append(order, statWorkTableSpeed)
		}
	}
	constants, err := b.catalog.GameConstants()
	if err != nil {
		return nil, err
	}
	slices.Sort(order)
	for _, stat := range order {
		row := DefRow[*d.StatDef](b.catalog, stat)
		for _, part := range row.GetParts() {
			value := part.GetValue()
			switch {
			case value.GetStatPart_RoomStat() != nil:
				roomStat := value.GetStatPart_RoomStat().GetRoomStat()
				out = append(out, policy.EnvNeed{Kind: policy.NeedRoomStat, Stat: roomStat, Input: b.rootRoomStat(roomStat), Via: stat, Base: float64(subject[stat])})
			case value.GetStatPart_WorkTableTemperature() != nil && table:
				c := constants.GetStatPart_WorkTableTemperature()
				out = append(out, policy.EnvNeed{Kind: policy.NeedTemperature, Via: stat, Min: float64(c.GetMinTemp()), Max: float64(c.GetMaxTemp()), Factor: float64(c.GetWorkRateFactor())})
			case value.GetStatPart_WorkTableOutdoors() != nil && table:
				out = append(out, policy.EnvNeed{Kind: policy.NeedIndoors, Via: stat, Factor: float64(constants.GetStatPart_WorkTableOutdoors().GetWorkRateFactor())})
			case value.GetStatPart_WorkTableRoomRole() != nil && table:
				if role := b.row.GetBuilding().GetWorkTableRoomRole(); role != "" {
					out = append(out, policy.EnvNeed{Kind: policy.NeedRole, Via: stat, Role: policy.RoomRole(role), Factor: float64(b.row.GetBuilding().GetWorkTableNotInRoomRoleFactor())})
				}
			}
		}
	}
	if slices.Contains(b.row.GetPlaceWorkers(), placeMustBeIndoors) {
		out = append(out, policy.EnvNeed{Kind: policy.NeedIndoors, Via: placeMustBeIndoors})
	}
	if slices.Contains(b.row.GetPlaceWorkers(), placeNotUnderRoof) {
		out = append(out, policy.EnvNeed{Kind: policy.NeedOpenSky, Via: placeNotUnderRoof})
	}
	cold, err := b.storesPerishables()
	if err != nil {
		return nil, err
	}
	if cold {
		out = append(out, policy.EnvNeed{Kind: policy.NeedCold, Via: "fixedStorageSettings"})
	}
	return out, nil
}

// rootRoomStat follows a room stat's inputStat chain to its end.
func (b stationBuilder) rootRoomStat(name string) string {
	for seen := 0; seen < 8; seen++ {
		row := DefRow[*d.RoomStatDef](b.catalog, name)
		if row == nil || row.GetInputStat() == "" {
			return name
		}
		name = row.GetInputStat()
	}
	return name
}

// storesPerishables is whether the storage building's fixed filter takes a
// def with a rottable comp.
func (b stationBuilder) storesPerishables() (bool, error) {
	storage, err := b.is(classStorage)
	if err != nil || !storage {
		return false, err
	}
	settings := b.row.GetBuilding().GetFixedStorageSettings()
	if settings == nil || settings.GetFilter() == nil {
		return false, nil
	}
	for name := range b.catalog.ThingDefs {
		if _, perishable, err := b.catalog.RotDays(name); err != nil || !perishable {
			if err != nil {
				return false, err
			}
			continue
		}
		within, err := b.catalog.categoriesWithin(name, b.catalog.ThingDefs[name])
		if err != nil {
			return false, err
		}
		if filterAccepts(settings.GetFilter(), name, within) {
			return true, nil
		}
	}
	return false, nil
}

// cooling is the building's cooling: a temperature control whose
// energyPerSecond is negative (a cooler or air conditioner draws heat out; a
// heater's is positive), with the power comp's draw.
func (b stationBuilder) cooling() policy.StationCooling {
	control := compOf(b.row, (*d.CompPropertiesAny).GetCompProperties_TempControl)
	if control == nil || control.GetEnergyPerSecond() >= 0 {
		return policy.StationCooling{}
	}
	out := policy.StationCooling{HeatPerSecond: float64(-control.GetEnergyPerSecond())}
	if power := compOf(b.row, (*d.CompPropertiesAny).GetCompProperties_Power); power != nil {
		out.PowerDraw = float64(power.GetBasePowerConsumption())
	}
	return out
}
