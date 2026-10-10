package buildingruntime

import (
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The fakes of these tests serve the game's own recorded rows
// (observation/testdata/full_catalog.pb.gz): a small slice of them, edited by
// the test where it needs a row the game does not have, and decoded on demand.

// weaponNames are the Core weapons the planning tests name and the mortar
// shells of Core, Biotech and Anomaly.
var weaponNames = []string{
	"Weapon_GrenadeFrag", "Weapon_GrenadeMolotov", "Weapon_GrenadeEMP", "Gun_EmpLauncher", "Gun_IncendiaryLauncher", "Gun_SmokeLauncher",
	"Gun_TripleRocket", "Gun_DoomsdayRocket", "Gun_AssaultRifle", "Gun_Minigun", "Gun_LMG", "Gun_PumpShotgun", "Gun_SniperRifle",
	"Gun_BoltActionRifle", "Gun_Revolver", "Gun_ChargeRifle", "Bow_Short", "MeleeWeapon_Club", "MeleeWeapon_Mace", "MeleeWeapon_Warhammer",
	"MeleeWeapon_Knife", "MeleeWeapon_Spear", "MeleeWeapon_LongSword", "WoodLog",
	"Shell_HighExplosive", "Shell_Incendiary", "Shell_EMP", "Shell_Smoke", "Shell_Firefoam", "Shell_AntigrainWarhead", "Shell_Toxic", "Shell_Deadlife",
}

// furnitureNames are the Core furniture the interior templates plan.
var furnitureNames = []string{
	"Bed", "Bedroll", "SleepingSpot", "DoubleBed", "BedrollDouble", "RoyalBed", "DoubleSleepingSpot", "HospitalBed", "Door", "AnimalFlap",
	"AnimalSleepingSpot", "AnimalBed", "EndTable", "Dresser", "StandingLamp", "Heater", "Cooler", "ToolCabinet", "ShelfSmall", "Campfire",
	"CraftingSpot", "PartySpot", "PassiveCooler", "VitalsMonitor", "Sarcophagus", "FueledStove", "ElectricStove", "TableStonecutter",
	"ElectricSmithy", "HandTailoringBench", "FabricationBench", "TableButcher", "SimpleResearchBench", "HiTechResearchBench",
}

// garmentNames are the garments a gear census's loadout model may name.
var garmentNames = []string{"Apparel_BasicShirt", "Apparel_Parka", "Apparel_PowerArmor", "Apparel_ArmorRecon", "Apparel_FlakVest"}

// catalogSets are the def sets every fake catalog takes whole from the recording.
var catalogSets = []string{
	"stat_defs", "room_stat_defs", "thing_category_defs", "weather_defs", "game_condition_defs", "biome_defs", "damage_defs",
	"maneuver_defs", "job_defs", "joy_giver_defs", "work_giver_defs",
}

// newCatalogRows is the base of the planner fakes' catalog: the foothold pin,
// the dining chair and table, the garments, the Core weapons and the Core
// furniture, with the def sets their rules read.
func newCatalogRows() *recordedrows.Slice {
	names := slices.Concat([]string{"HorseshoesPin", "DiningChair", "Table1x2c", "Silver"}, garmentNames, weaponNames, furnitureNames)
	return recordedrows.Take(recordedrows.Panic, recordedrows.Named(names...), catalogSets...)
}

// recordedWork are the rows of the def classes a fake catalog takes from the
// recording after the decode: the traits, thoughts, work types and needs a
// pawn's trait effects and work rows read.
func overlayRecorded(catalog *bridge.DefinitionCatalog) error {
	recorded, err := fullCatalogRows()
	if err != nil {
		return err
	}
	for class, rows := range recorded {
		catalog.Defs[class] = rows
	}
	return nil
}

// decodeRows decodes the slice as the catalog of a load, with the recorded
// trait, thought, work type and need rows.
func decodeRows(s *recordedrows.Slice, loadToken string) (*bridge.DefinitionCatalog, error) {
	catalog, err := recordedcatalog.FromSlice(s, loadToken)
	if err != nil {
		return nil, err
	}
	return catalog, overlayRecorded(catalog)
}

// baseCatalog is the decoded base catalog per load token, built once and
// never mutated: a fake whose test edited no rows serves it.
var baseCatalog sync.Map

func sharedBaseCatalog(loadToken string) (*bridge.DefinitionCatalog, error) {
	if cached, ok := baseCatalog.Load(loadToken); ok {
		return cached.(*bridge.DefinitionCatalog), nil
	}
	catalog, err := decodeRows(newCatalogRows(), loadToken)
	if err != nil {
		return nil, err
	}
	actual, _ := baseCatalog.LoadOrStore(loadToken, catalog)
	return actual.(*bridge.DefinitionCatalog), nil
}

// catalogRows is the fake's slice of recorded rows, copied from the base on the
// first edit; a test edits the copy's rows and the fake decodes it again.
func (n *roundsNative) catalogRows() *recordedrows.Slice {
	if n.rows == nil {
		n.rows = newCatalogRows()
	}
	n.edited, n.decoded = true, nil
	return n.rows
}

// def is the fake's copy of a recorded thing def, added from the recording
// when the base lacks it, for the test to edit.
func (n *roundsNative) def(name string) *d.ThingDef {
	rows := n.catalogRows()
	rows.Add(name)
	return rows.Thing(name)
}

// buildable is def with the given construction skill and size and no
// research: a plain buildable of that size for a planner to choose.
func (n *roundsNative) buildable(name string, skill, width, height int32) *d.ThingDef {
	row := n.def(name)
	row.ConstructionSkillPrerequisite = skill
	row.ResearchPrerequisites = nil
	row.Size = &d.IntVec2{X: width, Z: height}
	if row.DesignationCategory == "" {
		row.DesignationCategory = "Misc"
	}
	return row
}

// definitions serves the fake's catalog under the asked load: the shared base
// while no test edited a row or set recipes, else a decode of the edited rows
// (and the def mirror's recipes), kept until the next edit.
func (n *roundsNative) definitions(id *c.Identity) (*bridge.DefinitionCatalog, error) {
	if !n.edited && len(n.recipes) == 0 {
		return sharedBaseCatalog(id.GetLoadToken())
	}
	if n.decoded != nil && n.decodedLoad == id.GetLoadToken() && slices.EqualFunc(n.decodedRecipes, n.recipes, func(a, b *d.RecipeDef) bool { return proto.Equal(a, b) }) {
		return n.decoded, nil
	}
	rows := n.rows
	if rows == nil {
		rows = newCatalogRows()
	}
	// A catalog is decoded from a copy, so a row pointer a test still holds
	// cannot change a catalog already served.
	copied := &recordedrows.Slice{T: rows.T, Wire: proto.Clone(rows.Wire).(*o.DefinitionCatalog)}
	if len(n.recipes) > 0 {
		copied.Wire.Defs.RecipeDefs = nil
		for _, recipe := range n.recipes {
			copied.Wire.Defs.RecipeDefs = append(copied.Wire.Defs.RecipeDefs, proto.Clone(recipe).(*d.RecipeDef))
		}
	}
	catalog, err := decodeRows(copied, id.GetLoadToken())
	if err != nil {
		return nil, err
	}
	n.decoded, n.decodedLoad, n.decodedRecipes = catalog, id.GetLoadToken(), slices.Clone(n.recipes)
	return catalog, nil
}

// invent adds a copy of a recorded def under a name the game does not have,
// for a test that proves a rule reads the row and not the name.
func (n *roundsNative) invent(name, like string) *d.ThingDef {
	rows := n.catalogRows()
	rows.Add(like)
	return rows.CopyThing(like, name)
}

// takeCatalog starts the fake from a copy of another fake's catalog rows.
func (n *roundsNative) takeCatalog(from *roundsNative) {
	n.rows, n.edited, n.decoded = nil, from.edited, nil
	if from.rows != nil {
		n.rows = from.rows.Clone()
	}
}

// onlyStuff leaves a stuffed def one allowed stuff: the other stuffs' stat
// rows go.
func (n *roundsNative) onlyStuff(name, stuff string) {
	rows := n.catalogRows()
	rows.Add(name)
	keep := rows.Wire.StatValues.Rows[:0]
	for _, row := range rows.Wire.StatValues.Rows {
		if row.GetDefName() != name || row.GetStuffName() == stuff {
			keep = append(keep, row)
		}
	}
	rows.Wire.StatValues.Rows = keep
}

// madeOf leaves a stuffed def one stuff that its recorded rows do not allow:
// the first stuff's row is kept under the new stuff, its cost item renamed.
func (n *roundsNative) madeOf(name, stuff string) {
	rows := n.catalogRows()
	rows.Add(name, stuff)
	rows.Thing(name).StuffCategories = slices.Clone(rows.Thing(stuff).GetStuffProps().GetCategories())
	keep := rows.Wire.StatValues.Rows[:0]
	done := false
	for _, row := range rows.Wire.StatValues.Rows {
		if row.GetDefName() == name && row.GetStuffName() != "" {
			if done {
				continue
			}
			done = true
			row.StuffName = stuff
			for _, cost := range row.Costs {
				cost.DefName = &stuff
			}
		}
		keep = append(keep, row)
	}
	rows.Wire.StatValues.Rows = keep
}

// rice is the recorded rice plant tuned to the colony these tests draw: it
// grows at the glow of a lit cell and one harvest is a nutrition unit (the
// recorded yield is 6 of a 0.05 nutrition product).
func (n *roundsNative) rice() *d.PlantProperties {
	plant := n.def("Plant_Rice").Plant
	plant.GrowMinGlow = 0.5
	plant.HarvestYield = 20
	// The census reads no soil pollution, so a crop that wants clean soil would never find any.
	plant.Pollution = d.Pollution_POLLUTION_ANY
	return plant
}

// setPowerW sets the watts the def's power comp draws (negative generates),
// adding the comp when the row has none.
func setPowerW(row *d.ThingDef, watts float32) {
	for _, comp := range row.Comps {
		if power := comp.GetValue().GetCompProperties_Power(); power != nil {
			power.BasePowerConsumption = watts
			power.PowerUpgrades = nil
			return
		}
	}
	row.Comps = append(row.Comps, &d.Opt_CompPropertiesAny{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{BasePowerConsumption: watts}}}})
}
