package bridge

import (
	"context"
	"math"
	"slices"
	"sync"
	"sync/atomic"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The definition catalog: the static planning facts of every
// buildable or sowable definition and every research project. They hold
// for a whole load, so the client reads the catalog on the first read
// under a load token and keeps it until the token changes; frames carry
// only the per-map, per-tick values (availability is derived from the
// research prerequisites and the finished projects).

const methodDefinitionCatalog = "rimgovernor/observations_read_definition_catalog"

// DefinitionCatalog is one load's decoded catalog.
type DefinitionCatalog struct {
	LoadToken string
	// Research is every research project's static facts by name.
	Research map[string]policy.ResearchProjectFacts
	// Biotech is the Biotech defs; nil without Biotech.
	Biotech *BiotechCatalog
	// Odyssey is the Odyssey defs; nil without Odyssey.
	Odyssey *OdysseyCatalog
	// ThingDefs and TerrainDefs are every def with all its fields
	// by defName: the generated messages of defs.proto, unfiltered.
	ThingDefs   map[string]*d.ThingDef
	TerrainDefs map[string]*d.TerrainDef
	// Defs are the defs of every other concrete Def class: the
	// generated rows of defs.proto's DefSets by message full name, then
	// defName. Read them with DefRow.
	Defs map[protoreflect.FullName]map[string]proto.Message
	// classBases is each class the rows name by CLR full name with its base
	// classes, nearest first (ClassIsA, RowIsA).
	classBases map[string][]string
	// spawnForbidden caches SpawnForbiddenProducts.
	spawnForbidden atomic.Pointer[map[string]bool]
	// Constants are the game constants the native read took from the game
	// assemblies.
	Constants *o.CatalogConstants
	// statValues are the game's own stat values per (def, stuff); nil
	// in a reply that carries none.
	statValues *statTable
	// thingFacts are the game-computed flags of every ThingDef by name.
	thingFacts map[string]*o.ThingDefFacts
	// items is the planner-facing item facts, built once on first use.
	// recipes are the recipe facts derived from the rows on first use.
	recipes   recipeCache
	itemsOnce sync.Once
	items     policy.ItemFacts
	itemsErr  error
	// powerSources is PowerSources, built once on first use.
	powerOnce    sync.Once
	powerSources map[string]policy.PowerSourceProfile
	powerErr     error
	// floorTerrains is FloorTerrains, built once on first use.
	floorOnce     sync.Once
	floorTerrains map[string]policy.FloorTerrain
	floorErr      error
	// shapes is PieceShapes, built once on first use.
	shapesOnce sync.Once
	shapes     policy.PieceShapes
	shapesErr  error
	// races is the animal race catalog, built once on first use (catalog_races.go).
	racesOnce sync.Once
	races     policy.AnimalRaceCatalog
	racesErr  error
	// ideology is IdeologyDefs, built once on first use.
	ideologyOnce sync.Once
	ideology     *policy.IdeologyDefs
	ideologyErr  error
	// disarm is CreepJoinerDisarm, built once on first use.
	disarmOnce sync.Once
	disarm     policy.CreepJoinerDisarm
	disarmErr  error
}

// NotMirrored is the error for a fact the game computes in code that the
// mirror does not carry: the stat evaluator returns it for a StatWorker or
// StatPart class that cmd/stataudit lists as unowned, never a default value.
type NotMirrored struct {
	Class string // the StatWorker or StatPart class
	Fact  string // what the class computes that the mirror lacks
}

func (e *NotMirrored) Error() string {
	return "stat class " + e.Class + ": " + e.Fact + " is computed in game code and not mirrored"
}

// defStuff keys a stat row: stuff is empty for a def not made from stuff.
type defStuff struct{ def, stuff string }

// StatValue is the game's GetStatValueAbstract(stat, stuff) of def, with stuff
// empty for a def not made from stuff. A catalog without the stat table, a
// (def, stuff) pair without a row and a stat the game does not show for the
// def are errors, never a default.
func (catalog *DefinitionCatalog) StatValue(def, stuff, stat string) (float32, error) {
	value, shown, err := catalog.ShownStatValue(def, stuff, stat)
	if err == nil && !shown {
		return 0, contract("stat %s is not shown for def %s with stuff %q", stat, def, stuff)
	}
	return value, err
}

// ShownStatValue is StatValue for a stat that is legitimately absent from a
// def's row: shown is false when the row exists and the game does not show
// the stat for the def (the def has no such property). A catalog without the
// stat table and a pair without a row are still errors.
func (catalog *DefinitionCatalog) ShownStatValue(def, stuff, stat string) (value float32, shown bool, err error) {
	if catalog == nil || catalog.statValues == nil {
		return 0, false, contract("definition catalog carries no stat values")
	}
	row, ok := catalog.statValues.things[defStuff{def, stuff}]
	if !ok {
		return 0, false, contract("no stat values for def %s with stuff %q", def, stuff)
	}
	value, shown = row.values[stat]
	return value, shown, nil
}

// ThingDef is name's generated def row, nil when the catalog has none.
func (catalog *DefinitionCatalog) ThingDef(name string) *d.ThingDef {
	if catalog == nil {
		return nil
	}
	return catalog.ThingDefs[name]
}

// Packable is the building defs that pack into a minified item: those
// with a minifiedDef, which vanilla uninstalls instead of destroying.
func (catalog *DefinitionCatalog) Packable() map[string]bool {
	out := map[string]bool{}
	if catalog == nil {
		return out
	}
	for name, def := range catalog.ThingDefs {
		if def.GetCategory() == d.ThingCategory_THING_CATEGORY_BUILDING && def.GetMinifiedDef() != "" {
			out[name] = true
		}
	}
	return out
}

// SowableCrops are the sowable plants that harvest a thing, sorted: the
// crops a resource deficit can be planted for, whatever they are eaten as.
func (catalog *DefinitionCatalog) SowableCrops() []string {
	if catalog == nil {
		return nil
	}
	var out []string
	for name, def := range catalog.ThingDefs {
		if plant := def.GetPlant(); len(plant.GetSowTags()) > 0 && plant.GetHarvestedThingDef() != "" {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return out
}

// TerrainDef is name's generated def row, nil when the catalog has none.
func (catalog *DefinitionCatalog) TerrainDef(name string) *d.TerrainDef {
	if catalog == nil {
		return nil
	}
	return catalog.TerrainDefs[name]
}

// DefRow is name's generated row of def class T (a message of defs.proto
// other than ThingDef and TerrainDef), nil when the catalog has none.
func DefRow[T proto.Message](catalog *DefinitionCatalog, name string) T {
	var zero T
	if catalog == nil {
		return zero
	}
	row, _ := catalog.Defs[zero.ProtoReflect().Descriptor().FullName()][name].(T)
	return row
}

// catalogCache holds the catalog of the newest load token read.
type catalogCache struct {
	mu      sync.Mutex
	catalog *DefinitionCatalog
}

// DefinitionCatalog is the catalog of identity's load, read over GABP the
// first time the load token is seen.
func (caller *Client) DefinitionCatalog(ctx context.Context, identity *c.Identity) (*DefinitionCatalog, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	noteNativeRead(ctx, methodDefinitionCatalog)
	if held := caller.heldCatalog(identity); held != nil {
		return held, nil
	}
	wire, err := caller.readCatalogWire(ctx, identity)
	if err != nil {
		return nil, err
	}
	catalog, err := DecodeDefinitionCatalog(wire, identity)
	if err != nil {
		return nil, err
	}
	caller.catalog.mu.Lock()
	caller.catalog.catalog = catalog
	caller.catalog.mu.Unlock()
	return catalog, nil
}

// RecordDefinitionCatalog is the wire catalog of identity's load, read fresh
// (the held catalog is not consulted) and checked by DecodeDefinitionCatalog;
// cmd/recordcatalog writes it as the recorded catalog.
func (caller *Client) RecordDefinitionCatalog(ctx context.Context, identity *c.Identity) (*o.DefinitionCatalog, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	wire, err := caller.readCatalogWire(ctx, identity)
	if err != nil {
		return nil, err
	}
	if _, err := DecodeDefinitionCatalog(wire, identity); err != nil {
		return nil, err
	}
	return wire, nil
}

func (caller *Client) readCatalogWire(ctx context.Context, identity *c.Identity) (*o.DefinitionCatalog, error) {
	request := &o.DefinitionCatalogRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.DefinitionCatalogReply{}
	raw, err := caller.protoRead(ctx, methodDefinitionCatalog, request, reply)
	if err != nil {
		return nil, err
	}
	switch v := reply.Outcome.(type) {
	case *o.DefinitionCatalogReply_Failure:
		return nil, failure(v.Failure, raw)
	case *o.DefinitionCatalogReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	case *o.DefinitionCatalogReply_Observed:
		return v.Observed, nil
	}
	return nil, contract("missing definition catalog outcome")
}

// defRows keys generated def rows by defName; no rows, a row without a
// valid name or a repeated name is a contract violation (the game defines
// both kinds, so an empty list is a reply from a build without the def
// mirror).
func defRows[T any](rows []*T, name func(*T) string, kind string) (map[string]*T, error) {
	if len(rows) == 0 {
		return nil, contract("catalog carries no %s defs", kind)
	}
	out := make(map[string]*T, len(rows))
	for _, row := range rows {
		if row == nil || validID(name(row)) != nil {
			return nil, contract("invalid catalog %s def", kind)
		}
		if out[name(row)] != nil {
			return nil, contract("duplicate catalog %s def %s", kind, name(row))
		}
		out[name(row)] = row
	}
	return out, nil
}

// defSetRows keys every row of the DefSets fields by message and defName,
// reading the repeated fields by protobuf reflection so a class added to the
// generator needs no code here. An absent set, a row without a valid name or
// a repeated name is a contract violation.
func defSetRows(sets *d.DefSets) (map[protoreflect.FullName]map[string]proto.Message, error) {
	if sets == nil {
		return nil, contract("catalog carries no def sets")
	}
	out := map[protoreflect.FullName]map[string]proto.Message{}
	var err error
	sets.ProtoReflect().Range(func(fd protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		class := fd.Message().FullName()
		nameField := fd.Message().Fields().ByName("defName")
		if !fd.IsList() || nameField == nil || nameField.Kind() != protoreflect.StringKind {
			err = contract("def set %s is not a list of rows with a defName", fd.Name())
			return false
		}
		list := value.List()
		rows := make(map[string]proto.Message, list.Len())
		for i := range list.Len() {
			row := list.Get(i).Message()
			name := row.Get(nameField).String()
			if !row.IsValid() || validID(name) != nil {
				err = contract("invalid catalog %s def", class)
				return false
			}
			if rows[name] != nil {
				err = contract("duplicate catalog %s def %s", class, name)
				return false
			}
			rows[name] = row.Interface()
		}
		out[class] = rows
		return true
	})
	if err == nil && len(out) == 0 {
		err = contract("catalog carries no def sets")
	}
	return out, err
}

// classBases keys the class chains by class name; an unnamed or repeated
// chain, or one with an empty base name, is a contract violation.
func classBases(chains []*o.ClassChain) (map[string][]string, error) {
	out := make(map[string][]string, len(chains))
	for _, chain := range chains {
		if chain == nil || chain.GetName() == "" || slices.Contains(chain.GetBases(), "") {
			return nil, contract("invalid catalog class chain")
		}
		if _, exists := out[chain.GetName()]; exists {
			return nil, contract("duplicate catalog class chain %s", chain.GetName())
		}
		out[chain.GetName()] = chain.GetBases()
	}
	return out, nil
}

// ClassIsA reports whether the class named by its CLR full name is base or
// derives from it, by the base chains the catalog carries (every def class
// and every System.Type value of a row, a mod's classes included): a family
// such as the no-sunlight game conditions is a base class, not a list of
// names. A class the catalog has no chain for is an error, never false.
func (catalog *DefinitionCatalog) ClassIsA(class, base string) (bool, error) {
	if catalog == nil {
		return false, contract("no definition catalog")
	}
	bases, ok := catalog.classBases[class]
	if !ok {
		return false, contract("catalog has no class chain for %s", class)
	}
	return class == base || slices.Contains(bases, base), nil
}

// RowIsA is ClassIsA for the class a generated def row mirrors (its
// message's clr_type), so a row of a subclass matches its base class.
func (catalog *DefinitionCatalog) RowIsA(row proto.Message, base string) (bool, error) {
	descriptor := row.ProtoReflect().Descriptor()
	class, _ := proto.GetExtension(descriptor.Options(), d.E_ClrType).(string)
	if class == "" {
		return false, contract("message %s mirrors no CLR class", descriptor.FullName())
	}
	return catalog.ClassIsA(class, base)
}

// validateConstants refuses an absent constants block and one with a
// non-positive or non-finite value.
func validateConstants(v *o.CatalogConstants) (*o.CatalogConstants, error) {
	if v == nil {
		return nil, contract("catalog carries no constants")
	}
	for name, n := range map[string]int32{"ticks_per_hour": v.TicksPerHour, "ticks_per_day": v.TicksPerDay, "days_per_year": v.DaysPerYear, "bill_stack_max": v.BillStackMax, "skill_max_level": v.SkillMaxLevel} {
		if n <= 0 {
			return nil, contract("catalog constant %s is %d", name, n)
		}
	}
	// Go states the calendar once (domain.TicksPerDay); a game that differs
	// is refused, never planned against.
	if v.TicksPerHour != domain.TicksPerHour || v.TicksPerDay != domain.TicksPerDay || v.DaysPerYear != domain.DaysPerYear {
		return nil, contract("catalog calendar %d ticks per hour, %d per day, %d days per year differs from the one Go plans with", v.TicksPerHour, v.TicksPerDay, v.DaysPerYear)
	}
	if v.CurrencyDef == "" {
		return nil, contract("catalog constant currency_def is empty")
	}
	if v.WortDef == "" {
		return nil, contract("catalog constant wort_def is empty")
	}
	if g := float64(v.LitGlowThreshold); math.IsNaN(g) || math.IsInf(g, 0) || g <= 0 {
		return nil, contract("catalog constant lit_glow_threshold is %v", g)
	}
	if r := float64(v.FullRotRateC); math.IsNaN(r) || math.IsInf(r, 0) || r <= 0 {
		return nil, contract("catalog constant full_rot_rate_c is %v", r)
	}
	if r := float64(v.RoofMaxSupportDistance); math.IsNaN(r) || math.IsInf(r, 0) || r <= 0 {
		return nil, contract("catalog constant roof_max_support_distance is %v", r)
	}
	return v, nil
}

// DecodeDefinitionCatalog validates a catalog read under identity.
func DecodeDefinitionCatalog(v *o.DefinitionCatalog, identity *c.Identity) (*DefinitionCatalog, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid definition catalog context")
	}
	if err := buildingUnknown(v); err != nil {
		return nil, err
	}
	out := &DefinitionCatalog{LoadToken: identity.GetLoadToken(), Research: make(map[string]policy.ResearchProjectFacts, len(v.Research))}
	var err error
	if out.Odyssey, err = DecodeOdysseyCatalog(v.Odyssey); err != nil {
		return nil, err
	}
	if out.ThingDefs, err = defRows(v.ThingDefs, (*d.ThingDef).GetDefName, "thing"); err != nil {
		return nil, err
	}
	if out.TerrainDefs, err = defRows(v.TerrainDefs, (*d.TerrainDef).GetDefName, "terrain"); err != nil {
		return nil, err
	}
	if out.Defs, err = defSetRows(v.Defs); err != nil {
		return nil, err
	}
	if out.classBases, err = classBases(v.ClassChains); err != nil {
		return nil, err
	}
	if out.Constants, err = validateConstants(v.Constants); err != nil {
		return nil, err
	}
	if out.thingFacts, err = decodeThingFacts(v.ThingFacts, out.ThingDefs); err != nil {
		return nil, err
	}
	if out.statValues, err = decodeStatTable(v.StatValues, out.ThingDefs, out.TerrainDefs); err != nil {
		return nil, err
	}
	if out.Biotech, err = buildBiotech(out, v.Biotech); err != nil {
		return nil, err
	}
	for _, row := range v.Research {
		if row == nil || row.Project == nil || validID(row.Project.GetDefName()) != nil {
			return nil, contract("invalid catalog research project")
		}
		name := row.Project.GetDefName()
		if _, exists := out.Research[name]; exists {
			return nil, contract("duplicate catalog research project %s", name)
		}
		for _, list := range [][]string{row.Prerequisites, row.HiddenPrerequisites} {
			for _, prerequisite := range list {
				if validID(prerequisite) != nil {
					return nil, contract("invalid catalog research prerequisite")
				}
			}
		}
		// The catalog does not carry native ResearchProjectDef.hidden (an
		// anomaly codex state, not a static fact): every project starts as
		// not hidden and the research read marks a codex-hidden one from
		// its "hidden" lock reason.
		// A project with no prerequisites is an absent repeated field on
		// the wire, which is known-empty, not unread.
		if row.GetCategory() != "" && (row.ApparentCost == nil || badNonNegative(row.ApparentCost) || row.GetApparentCost() <= 0) {
			return nil, contract("catalog knowledge project %s lacks a finite cost", name)
		}
		out.Research[name] = policy.ResearchProjectFacts{Name: policy.ResearchProjectID(name), Hidden: domain.Known(false), KnowledgeCategory: row.GetCategory(), Cost: row.GetApparentCost(),
			Prerequisites: domain.Known(toProjectIDs(row.GetPrerequisites())), HiddenPrerequisites: domain.Known(toProjectIDs(row.GetHiddenPrerequisites())), RequiredBuilding: row.GetRequiredBuilding()}
	}
	return out, nil
}
