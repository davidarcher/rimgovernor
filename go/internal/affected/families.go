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

// routineFamilyFiles maps each family-owned buildingruntime source file to
// the serve routine families (RIMGOVERNOR_ROUTINE_FAMILIES, the
// routineFamilies table in cmd/rimgovernor/serve.go) that compose it. A
// change confined to these files reaches the areas whose cases compose one
// of the families, not every area (#361); routineFamilyScope widens the set
// through the files that use a changed file's declarations. Every other
// buildingruntime file (the clock, worker, scheduler, review and boundary
// code, routine_sleeping.go's shared building planner) is an input to every
// window and stays all-areas. TestRoutineFamilyFilesCoverFamilies keeps the
// table equal to serve's family list and to the routine_*.go files.
var routineFamilyFiles = map[string][]string{
	"routine_acquisition.go":          {"acquisition"},
	"routine_animal_containment.go":   {"animal-containment"},
	"routine_animal_feed.go":          {"animal-feed"},
	"routine_assignments.go":          {"work"},
	"routine_meals.go":                {"bill", "cooking"},
	"routine_paste.go":                {"cooking"},
	"routine_bill.go":                 {"bill"},
	"routine_blight.go":               {"blight"},
	"routine_building_selection.go":   {"cooking", "bill"},
	"routine_clean.go":                {"clean"},
	"routine_clearance.go":            {"clearance"},
	"routine_comfort.go":              {"comfort"},
	"routine_defense.go":              {"defense"},
	"routine_defense_layout.go":       {"defensive-layout"},
	"routine_dialog.go":               {"dialog"},
	"routine_equip.go":                {"equip"},
	"routine_excavation.go":           {"shelter", "expansion"},
	"routine_field.go":                {"field"},
	"routine_fire_safety.go":          {"fire"},
	"routine_flooring.go":             {"flooring"},
	"routine_food_storage.go":         {"food-storage"},
	"routine_food_storage_upkeep.go":  {"food-storage-upkeep"},
	"routine_corpse_larder.go":        {"food-storage-upkeep"},
	"routine_gear.go":                 {"gear"},
	"routine_haul.go":                 {"haul"},
	"routine_haul_stale.go":           {"haul"},
	"routine_home_coverage.go":        {"home-coverage"},
	"routine_hospital.go":             {"hospital"},
	"routine_husbandry.go":            {"husbandry"},
	"routine_ingredient_storage.go":   {"ingredient-storage"},
	"routine_lighting.go":             {"lighting"},
	"routine_medical.go":              {"medical"},
	"routine_medical_retry.go":        {"medical"},
	"routine_mood_relief.go":          {"mood"},
	"routine_naming.go":               {"naming"},
	"routine_population_custody.go":   {"population-custody"},
	"routine_population_joiner.go":    {"population-joiner"},
	"routine_power.go":                {"power"},
	"routine_prisoner_interaction.go": {"prisoner-interaction"},
	"routine_production_policy.go":    {"production-policy"},
	"routine_recovery.go":             {"recovery"},
	"routine_refrigeration.go":        {"refrigeration"},
	"routine_repair.go":               {"repair"},
	"routine_repair_stale.go":         {"repair"},
	"routine_rescue.go":               {"rescue"},
	"routine_research.go":             {"research"},
	"routine_resource.go":             {"resource"},
	"routine_routes.go":               {"routes"},
	"routine_secure_supplies.go":      {"secure-supplies"},
	"routine_shelter.go":              {"shelter", "expansion"},
	"routine_sleeping_upkeep.go":      {"sleeping"},
	"routine_stone_shell.go":          {"stone-shell"},
	"routine_supplies.go":             {"supply"},
	"routine_temperature.go":          {"temperature"},
	"routine_tend.go":                 {"tend"},
	"routine_trade.go":                {"trade"},
	"routine_waste.go":                {"waste"},
	"routine_workshop.go":             {"workshop"},
}

// routineDispatchFiles compose or dispatch to the family planners without
// sharing their behaviour: the scheduler constructs and steps every
// planner, the catalog logs each step, and the shared building planner in
// routine_sleeping.go selects a family's method behind the family's own
// gate. A reference from one of these to a family file does not widen the
// file's scope; a change to one of them is all-areas like any other shared
// file.
var routineDispatchFiles = map[string]bool{
	"clock_scheduler.go":       true,
	"clock_planner_catalog.go": true,
	"routine_sleeping.go":      true,
}

// routineFamilyNames is every family the table names, sorted.
func routineFamilyNames() []string {
	set := map[string]bool{}
	for _, families := range routineFamilyFiles {
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

// routineFamilyScope scopes a repo-relative changed file to routine
// families. Only a non-test file of routineFamilyFiles scopes; the scope is
// the file's own families plus, transitively, those of every family file
// in the package that references one of its top-level declarations. A
// reference from a file outside the table (other than a dispatch file)
// makes the scope All: that file's behaviour, shared by every window,
// depends on the change.
func routineFamilyScope(goDir, file string) (familyScope, error) {
	file = filepath.ToSlash(file)
	dir, base := strings.TrimPrefix(filepath.ToSlash(filepath.Dir(file)), "go/"), filepath.Base(file)
	if dir != buildingruntimeDir || strings.HasSuffix(base, "_test.go") {
		return familyScope{All: true}, nil
	}
	if _, ok := routineFamilyFiles[base]; !ok {
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
		for _, family := range routineFamilyFiles[name] {
			set[family] = true
		}
		for _, user := range users[name] {
			if routineDispatchFiles[user] {
				continue
			}
			if _, ok := routineFamilyFiles[user]; !ok {
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
				if decl.Recv == nil {
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
