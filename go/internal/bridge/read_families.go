package bridge

import "context"

// The recording facts reader: every native state read goes through
// protoRead, which notes the fact family the read draws from on the
// context's read note. The scheduler hangs a note on each planner's context
// and compares what it saw with the planner's declared families
// (buildingruntime plannerEntry.declared), so a planner that starts reading
// a family it never declared is caught by a test instead of silently
// planning on facts its wake step does not track.

type readNoteKey struct{}

// ReadNote receives one family read: the family and the native method (or
// held section) the read came from.
type ReadNote func(family FactFamily, source string)

// WithReadNote returns ctx carrying note; a nil note returns ctx unchanged.
func WithReadNote(ctx context.Context, note ReadNote) context.Context {
	if note == nil {
		return ctx
	}
	return context.WithValue(ctx, readNoteKey{}, note)
}

// NoteRead reports a read of family from source to the context's note, if
// it carries one. Reads of held store sections call it too (facts.Read), so
// a read served without a native call still counts.
func NoteRead(ctx context.Context, family FactFamily, source string) {
	if note, ok := ctx.Value(readNoteKey{}).(ReadNote); ok {
		note(family, source)
	}
}

// nativeReadFamily is the family of the state a reviewed native read
// returns. The assignments of geometry and census reads to colony follow
// facts.Section.Family (planning cells, zones, buildings, bills are colony
// sections); a read that is not state at all (clock, presentation,
// previews, lifecycle) is in nonFactReads instead.
var nativeReadFamily = map[string]FactFamily{
	"rimgovernor/observations_list_supplies":           FactColony,
	"rimgovernor/observations_read_colony_facts":       FactColony,
	"rimgovernor/observations_list_buildings":          FactColony,
	"rimgovernor/observations_list_wall_upgrade_sites": FactColony,
	"rimgovernor/observations_list_zones":              FactColony,
	"rimgovernor/observations_read_defense_site":       FactColony,
	"rimgovernor/observations_read_lines_of_fire":      FactColony,
	"rimgovernor/observations_read_spatial_access":     FactColony,
	"rimgovernor/observations_read_husbandry":          FactColony,
	"rimgovernor/observations_read_consumption":        FactColony,
	"rimgovernor/observations_read_bills":              FactColony,
	"rimgovernor/observations_list_resource_sources":   FactColony,
	"rimgovernor/observations_read_excavation_site":    FactColony,
	"rimgovernor/observations_get_cells":               FactColony,
	clearanceTool:                                      FactColony,
	shrinesTool:                                        FactColony,
	combatGeometryMethod:                               FactColony,
	"rimgovernor/observations_list_rooms":              FactRooms,
	"rimgovernor/observations_read_research":           FactResearch,
	methodDefinitionCatalog:                            FactDefinitions,
	"rimgovernor/observations_read_recipes":            FactDefinitions,
	"rimgovernor/observations_list_pawns":              FactPawns,
	"rimgovernor/observations_read_population":         FactPawns,
	"rimgovernor/observations_read_status":             FactEmergency,
	"rimgovernor/observations_read_world":              FactWorld,
	"rimgovernor/observations_read_world_progression":  FactWorld,
	"rimgovernor/observations_read_trade_sheet":        FactWorld,
	tradeSessionTool:                                   FactWorld,
	"rimgovernor/observations_read_trade_acquisition":  FactWorld,
	"rimgovernor/observations_list_traders":            FactWorld,
	"rimgovernor/lifecycle_read_identity":              FactIdentity,
}

// nonFactReads are the reviewed read methods that return no observation
// fact family: the clock and authority machinery, presentation, previews
// (a computation over the colony, not a read of it), the tick and the
// governor's own state.
var nonFactReads = map[string]bool{
	"rimgovernor/clock_read_events":             true,
	"rimgovernor/clock_read_status":             true,
	"rimgovernor/clock_read_attempt":            true,
	"rimgovernor/zones_preview":                 true,
	"rimgovernor/placement_preview":             true,
	"rimgovernor/lifecycle_read_tick":           true,
	"rimgovernor/lifecycle_read_governor_state": true,
	"rimgovernor/lifecycle_wait_save_signal":    true,
	"rimgovernor/authority_read_status":         true,
	rulesStatusMethod:                           true,
	"rimgovernor/receipts_lookup":               true,
	"rimgovernor/presentation_camera":           true,
	"rimgovernor/presentation_selection":        true,
	"rimgovernor/presentation_colonists":        true,
	"rimgovernor/presentation_notifications":    true,
	"rimgovernor/presentation_render_state":     true,
	methodOpenSnapshotStream:                    true,
}

// noteNativeRead notes the read of name on ctx's read note.
func noteNativeRead(ctx context.Context, name string) {
	if family, ok := nativeReadFamily[name]; ok {
		NoteRead(ctx, family, name)
	}
}
