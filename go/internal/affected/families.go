package affected

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

// buildingruntimeDir is the package holding the routine planners, relative
// to go/.
const buildingruntimeDir = "internal/buildingruntime"

// roundsFamilyFiles maps each family-owned buildingruntime source file to
// the serve routine families (RIMGOVERNOR_ROUTINE_FAMILIES, the
// roundsFamilies table in cmd/rimgovernor/serve.go) that compose it. A
// change confined to these files reaches the areas whose cases compose one
// of the families, not every area (#361); roundsFamilyScope widens the set
// through the files that use a changed file's declarations. Every other
// buildingruntime file (the clock, worker, scheduler, review and boundary
// code, rounds_sleeping.go's shared building planner) is an input to every
// window and stays all-areas. TestRoundsFamilyFilesCoverFamilies keeps the
// table equal to serve's family list and to the routine_*.go files.
var roundsFamilyFiles = map[string][]string{
	"rounds_acquisition.go":            {"acquisition"},
	"rounds_acquisition_fields.go":     {"acquisition"},
	"rounds_animal_containment.go":     {"animal-containment"},
	"rounds_herd_rooms.go":             {"animal-containment"},
	"rounds_animal_feed.go":            {"animal-feed"},
	"rounds_assignments.go":            {"work"},
	"rounds_mech.go":                   {"work"},
	"rounds_disease.go":                {"work"},
	"rounds_meals.go":                  {"bill", "cooking"},
	"rounds_paste.go":                  {"cooking"},
	"rounds_cooking_campfire.go":       {"cooking", "temperature"},
	"rounds_bill.go":                   {"bill", "art"},
	"rounds_bill_art.go":               {"art"},
	"rounds_bill_mech.go":              {"mechs"},
	"rounds_blight.go":                 {"blight"},
	"rounds_pollution.go":              {"pollution"},
	"rounds_mech_charger.go":           {"mechcharger"},
	"rounds_gene_bank.go":              {"genebank"},
	"rounds_building_selection.go":     {"cooking", "bill"},
	"rounds_clean.go":                  {"clean"},
	"rounds_clearance.go":              {"clearance"},
	"rounds_shrine.go":                 {"shrine"},
	"rounds_comfort.go":                {"comfort"},
	"rounds_defense.go":                {"defense"},
	"rounds_defense_postfight.go":      {"defense"},
	"rounds_defense_entities.go":       {"defense"},
	"rounds_combat.go":                 {"defense"},
	"rounds_combat_loadout.go":         {"defense"},
	"rounds_combat_permit.go":          {"defense"},
	"rounds_defense_snapshot.go":       {"defense", "defensive-layout"},
	"rounds_break.go":                  {"defense"},
	"rounds_incident.go":               {"defense", "defensive-layout", "rescue", "tend"},
	"rounds_defense_layout.go":         {"defensive-layout"},
	"rounds_defense_cover.go":          {"defensive-layout"},
	"rounds_defense_wait.go":           {"defensive-layout"},
	"rounds_defense_burn.go":           {"defensive-layout", "defense"},
	"rounds_defense_perimeter.go":      {"defensive-layout"},
	"rounds_dialog.go":                 {"dialog"},
	"rounds_equip.go":                  {"equip"},
	"rounds_equip_pawn.go":             {"equip", "gear", "defense"},
	"rounds_excavation.go":             {"shelter", "expansion"},
	"rounds_field.go":                  {"field"},
	"rounds_social_fields.go":          {"field"},
	"rounds_shell_interiors.go":        {"field"},
	"rounds_fishing.go":                {"field", "work", "research"},
	"rounds_fire_safety.go":            {"fire"},
	"rounds_flooring.go":               {"flooring"},
	"rounds_food_reserve.go":           {"bill", "food-storage-upkeep"},
	"rounds_baby_feeding.go":           {"bill"},
	"rounds_food_storage_upkeep.go":    {"food-storage-upkeep"},
	"rounds_corpse_larder.go":          {"food-storage-upkeep"},
	"rounds_gear.go":                   {"gear"},
	"rounds_armory.go":                 {"armory", "gear"},
	"rounds_armory_weapons.go":         {"armory", "gear", "equip"},
	"rounds_armory_shells.go":          {"armory"},
	"rounds_home_coverage.go":          {"home-coverage"},
	"rounds_hospital.go":               {"hospital"},
	"rounds_husbandry.go":              {"husbandry"},
	"rounds_hay.go":                    {"animal-feed"},
	"rounds_lighting.go":               {"lighting"},
	"rounds_medical.go":                {"medical"},
	"rounds_surgery.go":                {"medical"},
	"rounds_bill_surgery.go":           {"medical", "trade"},
	"rounds_medical_retry.go":          {"medical"},
	"rounds_mood_relief.go":            {"mood"},
	"rounds_mood_cast.go":              {"mood"},
	"rounds_naming.go":                 {"naming"},
	"rounds_population_custody.go":     {"population-custody"},
	"rounds_population_lance.go":       {"population-custody"},
	"rounds_population_containment.go": {"population-custody"},
	"rounds_shrine_arrest.go":          {"population-custody"},
	"rounds_population_joiner.go":      {"population-joiner"},
	"rounds_power.go":                  {"power"},
	"rounds_power_dig.go":              {"power"},
	"rounds_route_dig.go":              {"routes"},
	"rounds_planned_rooms.go":          {"cooking", "refrigeration", "prisoner-interaction"},
	"rounds_prisoner_interaction.go":   {"prisoner-interaction"},
	"rounds_recovery.go":               {"recovery"},
	"rounds_areas.go":                  {"recovery"},
	"rounds_refrigeration.go":          {"refrigeration"},
	"rounds_plan_dig.go":               {"shelter", "expansion", "sleeping", "waste", "refrigeration"},
	"rounds_repair.go":                 {"repair"},
	"rounds_repair_stale.go":           {"repair"},
	"rounds_rescue.go":                 {"rescue"},
	"rounds_research.go":               {"research"},
	"rounds_resource.go":               {"resource"},
	"rounds_resource_tunnel.go":        {"resource"},
	"rounds_deep_drill.go":             {"resource", "power", "research"},
	"rounds_routes.go":                 {"routes"},
	"rounds_shelter.go":                {"shelter", "expansion"},
	"rounds_maintain_shelter.go":       {"sheltering"},
	"rounds_firebreak.go":              {"firebreak"},
	"rounds_creepjoiner.go":            {"creepjoiner"},
	"rounds_psylink.go":                {"psylink"},
	"rounds_permits.go":                {"permits"},
	"rounds_ideo_roles.go":             {"ideo-roles"},
	"rounds_rituals.go":                {"rituals"},
	"rounds_shelter_bunks.go":          {"shelter", "sleeping"},
	"rounds_sleeping_couple.go":        {"sleeping"},
	"rounds_sleeping_upkeep.go":        {"sleeping"},
	"rounds_sleeping_bedroom.go":       {"sleeping"},
	"rounds_sleeping_sculpture.go":     {"sleeping"},
	"rounds_throne.go":                 {"sleeping"},
	"rounds_throne_refuel.go":          {"sleeping"},
	"rounds_child_rooms.go":            {"sleeping"},
	"rounds_sleeping_upgrade.go":       {"sleeping"},
	"rounds_sleeping_shell_bed.go":     {"sleeping"},
	"rounds_stone_shell.go":            {"stone-shell"},
	"rounds_stockpiles.go":             {"stockpiles"},
	"stockpile_roles.go":               {"stockpiles"},
	"stockpile_sited_roles.go":         {"stockpiles"},
	"rounds_storage_shelves.go":        {"stockpiles"},
	"rounds_supplies.go":               {"supply"},
	"rounds_temperature.go":            {"temperature"},
	"rounds_tend.go":                   {"tend"},
	"rounds_tier_style.go":             {"shelter", "expansion", "flooring", "lighting"},
	"rounds_trade.go":                  {"trade"},
	"rounds_waste.go":                  {"waste"},
	"rounds_waste_tomb.go":             {"waste"},
	"rounds_tomb_facts.go":             {"waste"},
	"rounds_incineration.go":           {"incineration"},
	"rounds_incineration_room.go":      {"incineration"},
	"rounds_incineration_burn.go":      {"incineration"},
	"rounds_workshop.go":               {"workshop"},
}

// roundsDispatchFiles compose or dispatch to the family planners without
// sharing their behaviour: the scheduler constructs and steps every
// planner, the catalog logs each step, rounds.go holds each family's review
// memory and feeds its owed facts, and the shared building planner in
// rounds_sleeping.go selects a family's method behind the family's own
// gate. A reference from one of these to a family file does not widen the
// file's scope; a change to one of them is all-areas like any other shared
// file.
var roundsDispatchFiles = map[string]bool{
	"clock_scheduler.go":       true,
	"clock_planner_catalog.go": true,
	"rounds_sleeping.go":       true,
	"rounds.go":                true,
}

// roundsFamilyNames is every family the table names, sorted.
func roundsFamilyNames() []string {
	set := map[string]bool{}
	for _, families := range roundsFamilyFiles {
		for _, family := range families {
			set[family] = true
		}
	}
	names := make([]string, 0, len(set))
	for name := range set {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}

// familyScope is the routine families a changed file reaches: All when
// the file is shared (not family-owned, or used by a shared file).
type familyScope struct {
	Families []string
	All      bool
}

// roundsFamilyScope scopes a repo-relative changed file to routine
// families. Only a non-test file of roundsFamilyFiles scopes; the scope is
// the file's own families plus, transitively, those of every family file
// in the package that references one of its top-level declarations. A
// reference from a file outside the table (other than a dispatch file)
// makes the scope All: that file's behaviour, shared by every window,
// depends on the change.
func roundsFamilyScope(goDir, file string) (familyScope, error) {
	file = filepath.ToSlash(file)
	dir, base := strings.TrimPrefix(filepath.ToSlash(filepath.Dir(file)), "go/"), filepath.Base(file)
	if dir != buildingruntimeDir || strings.HasSuffix(base, "_test.go") {
		return familyScope{All: true}, nil
	}
	if _, ok := roundsFamilyFiles[base]; !ok {
		return familyScope{All: true}, nil
	}
	users, err := buildingruntimeUsers(filepath.Join(goDir, filepath.FromSlash(buildingruntimeDir)))
	if err != nil {
		return familyScope{}, err
	}
	set := map[string]bool{}
	seen := map[string]bool{}
	var visit func(name string) bool
	visit = func(name string) bool {
		if seen[name] {
			return true
		}
		seen[name] = true
		for _, family := range roundsFamilyFiles[name] {
			set[family] = true
		}
		for _, user := range users[name] {
			if roundsDispatchFiles[user] {
				continue
			}
			if _, ok := roundsFamilyFiles[user]; !ok {
				return false
			}
			if !visit(user) {
				return false
			}
		}
		return true
	}
	if !visit(base) {
		return familyScope{All: true}, nil
	}
	scope := familyScope{}
	for family := range set {
		scope.Families = append(scope.Families, family)
	}
	sort.Strings(scope.Families)
	return scope, nil
}

var (
	usersMu    sync.Mutex
	usersByDir = map[string]map[string][]string{} // package dir -> file -> files referencing its declarations
)

// buildingruntimeUsers maps each non-test file of the package to the other
// non-test files that reference one of its top-level declarations, read
// once per process.
func buildingruntimeUsers(dir string) (map[string][]string, error) {
	usersMu.Lock()
	defer usersMu.Unlock()
	if users, ok := usersByDir[dir]; ok {
		return users, nil
	}
	files, err := parseDir(dir, false)
	if err != nil {
		return nil, err
	}
	owner := map[string]string{} // top-level identifier -> file
	for base, file := range files {
		for _, decl := range file.Decls {
			switch decl := decl.(type) {
			case *ast.FuncDecl:
				// init is never referenced; every file may declare one.
				if decl.Recv == nil && decl.Name.Name != "init" {
					owner[decl.Name.Name] = base
				}
			case *ast.GenDecl:
				for _, spec := range decl.Specs {
					switch spec := spec.(type) {
					case *ast.TypeSpec:
						owner[spec.Name.Name] = base
					case *ast.ValueSpec:
						for _, name := range spec.Names {
							owner[name.Name] = base
						}
					}
				}
			}
		}
	}
	set := map[string]map[string]bool{}
	for base, file := range files {
		ast.Inspect(file, func(n ast.Node) bool {
			ident, ok := n.(*ast.Ident)
			if !ok {
				return true
			}
			if declared, ok := owner[ident.Name]; ok && declared != base {
				if set[declared] == nil {
					set[declared] = map[string]bool{}
				}
				set[declared][base] = true
			}
			return true
		})
	}
	users := map[string][]string{}
	for declared, bases := range set {
		for base := range bases {
			users[declared] = append(users[declared], base)
		}
		sort.Strings(users[declared])
	}
	usersByDir[dir] = users
	return users, nil
}

// parseDir parses a directory's Go files by base name, the tests too when
// tests is set; build tags are not consulted.
func parseDir(dir string, tests bool) (map[string]*ast.File, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	fset := token.NewFileSet()
	files := map[string]*ast.File{}
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, ".go") || !tests && strings.HasSuffix(name, "_test.go") {
			continue
		}
		file, err := parser.ParseFile(fset, filepath.Join(dir, name), nil, 0)
		if err != nil {
			return nil, err
		}
		files[name] = file
	}
	return files, nil
}
