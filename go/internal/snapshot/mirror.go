package snapshot

import (
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
)

// Mirror frames (#795 step 4). The recording takes the colony mirror's
// sections from the mirror itself: every table it publishes is a section
// line in the serve's stream, next to the review lines,
//
//	{"Tick": t, "Seq": 0, "Section": {"Name", "Version", "AsOf", "Scope",
//	  "Key": true, "Upserts": [[key, row], ...], "Removed": [key, ...]}}
//
// a keyframe (Key: the rows replace the section) on a section's first
// table and on a scope change, else the rows that changed since the
// section's last table and the keys it dropped (no line when nothing
// changed, unless a binding names the version). Keys and rows are Encode's
// JSON of the mirror's K and R; planning_cells rides as a wire CellGrid in
// "Grid" instead (grid.go). "Format" names the form: FormatRows (1) or
// FormatGrid (2); any other value, or none, fails the load. Any mirror section is recorded this way,
// whatever its type, as soon as it is a mirror section (#773's pawns, gear,
// acquisition and upkeep included).
//
// A review or step line whose tree holds a field a bound section
// materialises exactly (Bind) leaves the field out and names the section
// version it is rebuilt from in "Mirror": {section: version}. A field that
// differs from the section (a window not read through the mirror) stays in
// the tree, so every recorded line materialises exactly what was read.

// A binding says how a mirror section's rows lay out as one field of a
// recorded review or step tree.
type binding struct {
	section string
	path    []string
	// less orders the section's rows (their trees) as the field's slice.
	less func(a, b any) bool
}

var bindings = []binding{
	// The planning window's site cells, row order bridge.SortSiteCells.
	{section: string(facts.PlanningCells), path: []string{"Projection", "Cells"}, less: func(a, b any) bool {
		az, ax := cellCoord(a, "Z"), cellCoord(a, "X")
		bz, bx := cellCoord(b, "Z"), cellCoord(b, "X")
		if az != bz {
			return az < bz
		}
		return ax < bx
	}},
}

func cellCoord(row any, axis string) int64 {
	m, _ := row.(map[string]any)
	cell, _ := m["Cell"].(map[string]any)
	n, _ := cell[axis].(json.Number)
	v, _ := n.Int64()
	return v
}

// sectionFrame is one mirror table as a stream line records it.
// Section line formats (Format). Any other value is a load error, never a
// guess.
const (
	// FormatRows: the rows in Upserts and Removed.
	FormatRows = 1
	// FormatGrid: planning_cells as a wire CellGrid in Grid (grid.go).
	FormatGrid = 2
)

type sectionFrame struct {
	Name string
	// Format is the line's form: FormatRows or FormatGrid.
	Format  int
	Version uint64
	AsOf    facts.Watermark
	Scope   facts.Scope
	Key     bool                 `json:",omitempty"`
	Upserts [][2]json.RawMessage `json:",omitempty"`
	Removed []json.RawMessage    `json:",omitempty"`
	// Grid holds planning_cells as a wire CellGrid in place of Upserts and
	// Removed (grid.go).
	Grid []byte `json:",omitempty"`
}

// recSection is a mirror section as a stream holds it, writing or
// replaying: rows by canonical key.
type recSection struct {
	scope   facts.Scope
	version uint64
	asOf    facts.Watermark
	keys    map[string]json.RawMessage
	rows    map[string]json.RawMessage
	// prev is the persistent table version the section was last recorded
	// from (recordKeyed), the base of the next delta.
	prev facts.KeyedRows
	// field caches the bound field built at version.
	field      any
	fieldBuilt bool
	// grid is planning_cells as a grid, when its last line was one.
	grid *heldGrid
	// colony caches the decoded colony facts on the root colony section.
	colony *colonyDecoded
}

// MirrorRecorder records every table a mirror publishes into the serve's
// stream in dir (the same stream Record writes). A failed write drops the
// section, so its next table is a keyframe and no line refers to it
// meanwhile.
func MirrorRecorder(dir string) facts.Recorder {
	return func(p facts.Published) { recordSection(dir, p) }
}

// recordSection appends p to dir's stream as a section line.
func recordSection(dir string, p facts.Published) {
	if kr, ok := p.Rows.(facts.KeyedRows); ok {
		recordKeyed(dir, p, kr)
		return
	}
	keys, rows, err := encodeRows(p.Rows)
	streamsMu.Lock()
	defer streamsMu.Unlock()
	rec, oerr := openStream(dir, domain.Tick(p.AsOf.Tick))
	if err = errors.Join(err, oerr); err != nil {
		if rec != nil {
			delete(rec.sections, p.Section)
		}
		return
	}
	held := rec.sections[p.Section]
	frame := sectionFrame{Name: p.Section, Version: p.Version, AsOf: p.AsOf, Scope: p.Scope}
	var grid *heldGrid
	if p.Section == string(facts.PlanningCells) {
		var base *heldGrid
		if held != nil && held.scope == p.Scope {
			base = held.grid
		}
		if data, next, ok := gridFrame(base, p.Rows, rows); ok {
			frame.Grid, frame.Key, grid = data, base == nil || base.grid.Rect != next.grid.Rect, next
		}
	}
	if grid != nil {
		// The grid holds the rows.
	} else if held == nil || held.scope != p.Scope {
		frame.Key = true
		for _, k := range sortedKeys(rows) {
			frame.Upserts = append(frame.Upserts, [2]json.RawMessage{keys[k], rows[k]})
		}
	} else {
		for _, k := range sortedKeys(rows) {
			if old, ok := held.rows[k]; !ok || string(old) != string(rows[k]) {
				frame.Upserts = append(frame.Upserts, [2]json.RawMessage{keys[k], rows[k]})
			}
		}
		for _, k := range sortedKeys(held.rows) {
			if _, ok := rows[k]; !ok {
				frame.Removed = append(frame.Removed, held.keys[k])
			}
		}
	}
	if !frame.Key && frame.Grid == nil && len(frame.Upserts) == 0 && len(frame.Removed) == 0 && !bound(p.Section) {
		// Nothing changed and no line names the version: the stream keeps
		// the section as it was.
		return
	}
	frame.stamp()
	if err := rec.append(streamLine{Tick: domain.Tick(p.AsOf.Tick), Section: &frame}); err != nil {
		delete(rec.sections, p.Section)
		return
	}
	rec.sections[p.Section] = &recSection{scope: p.Scope, version: p.Version, asOf: p.AsOf, keys: keys, rows: rows, grid: grid}
}

// recordKeyed is recordSection for a section published as a persistent
// table version (facts.PutKeyed, #1578): after the keyframe, only the rows
// that changed since the held version are encoded, so a line costs the
// rows that changed, not the table. The lines are those recordSection
// writes for the same rows.
func recordKeyed(dir string, p facts.Published, kr facts.KeyedRows) {
	streamsMu.Lock()
	defer streamsMu.Unlock()
	rec, err := openStream(dir, domain.Tick(p.AsOf.Tick))
	if err != nil {
		if rec != nil {
			delete(rec.sections, p.Section)
		}
		return
	}
	held := rec.sections[p.Section]
	frame := sectionFrame{Name: p.Section, Version: p.Version, AsOf: p.AsOf, Scope: p.Scope}
	var keys, rows map[string]json.RawMessage
	var encodeErr error
	encoded := func(id string, row any) (key, enc json.RawMessage) {
		k, err := compactTree(reflect.ValueOf(id))
		if err != nil {
			encodeErr = errors.Join(encodeErr, err)
			return nil, nil
		}
		r, err := compactTree(reflect.ValueOf(row))
		if err != nil {
			encodeErr = errors.Join(encodeErr, err)
		}
		return k, r
	}
	if held == nil || held.scope != p.Scope || held.prev == nil {
		frame.Key = true
		keys, rows = map[string]json.RawMessage{}, map[string]json.RawMessage{}
		kr.Rows(func(id string, row any) {
			k, r := encoded(id, row)
			keys[string(k)], rows[string(k)] = k, r
		})
		for _, k := range sortedKeys(rows) {
			frame.Upserts = append(frame.Upserts, [2]json.RawMessage{keys[k], rows[k]})
		}
	} else {
		keys, rows = held.keys, held.rows
		var removed []json.RawMessage
		kr.ChangedSince(held.prev, func(id string, row any) {
			k, r := encoded(id, row)
			if old, ok := rows[string(k)]; !ok || string(old) != string(r) {
				frame.Upserts = append(frame.Upserts, [2]json.RawMessage{k, r})
				keys[string(k)], rows[string(k)] = k, r
			}
		}, func(id string) {
			k, _ := compactTree(reflect.ValueOf(id))
			removed = append(removed, k)
			delete(keys, string(k))
			delete(rows, string(k))
		})
		sort.Slice(frame.Upserts, func(i, j int) bool { return string(frame.Upserts[i][0]) < string(frame.Upserts[j][0]) })
		sort.Slice(removed, func(i, j int) bool { return string(removed[i]) < string(removed[j]) })
		frame.Removed = removed
	}
	if encodeErr != nil {
		delete(rec.sections, p.Section)
		return
	}
	if !frame.Key && len(frame.Upserts) == 0 && len(frame.Removed) == 0 && !bound(p.Section) {
		return
	}
	frame.stamp()
	if err := rec.append(streamLine{Tick: domain.Tick(p.AsOf.Tick), Section: &frame}); err != nil {
		delete(rec.sections, p.Section)
		return
	}
	rec.sections[p.Section] = &recSection{scope: p.Scope, version: p.Version, asOf: p.AsOf, keys: keys, rows: rows, prev: kr}
}

// encodeRows is a map[K]R's keys and rows as Encode's compact JSON, by
// canonical key.
func encodeRows(rows any) (map[string]json.RawMessage, map[string]json.RawMessage, error) {
	v := reflect.ValueOf(rows)
	if v.Kind() != reflect.Map {
		return nil, nil, fmt.Errorf("snapshot: mirror rows are %T, not a map", rows)
	}
	keys := make(map[string]json.RawMessage, v.Len())
	out := make(map[string]json.RawMessage, v.Len())
	iter := v.MapRange()
	for iter.Next() {
		k, err := compactTree(iter.Key())
		if err != nil {
			return nil, nil, err
		}
		row, err := compactTree(iter.Value())
		if err != nil {
			return nil, nil, err
		}
		keys[string(k)], out[string(k)] = k, row
	}
	return keys, out, nil
}

func compactTree(v reflect.Value) (json.RawMessage, error) {
	tree, err := encode(v, "$")
	if err != nil {
		return nil, err
	}
	return json.Marshal(tree)
}

func sortedKeys(m map[string]json.RawMessage) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// stamp sets the line's Format from the form it holds.
func (f *sectionFrame) stamp() {
	f.Format = FormatRows
	if f.Grid != nil {
		f.Format = FormatGrid
	}
}

// apply lays a section line over the held section (a replay).
func (s *recSection) apply(f sectionFrame) (*recSection, error) {
	switch f.Format {
	case FormatGrid:
		if f.Grid == nil {
			return nil, fmt.Errorf("snapshot: section %s line is Format %d with no Grid", f.Name, f.Format)
		}
		return s.applyGrid(f)
	case FormatRows:
		if f.Grid != nil {
			return nil, fmt.Errorf("snapshot: section %s line is Format %d with a Grid", f.Name, f.Format)
		}
	default:
		return nil, fmt.Errorf("snapshot: section %s line has unknown Format %d", f.Name, f.Format)
	}
	next := &recSection{scope: f.Scope, version: f.Version, asOf: f.AsOf, keys: map[string]json.RawMessage{}, rows: map[string]json.RawMessage{}}
	if !f.Key {
		if s == nil {
			return nil, fmt.Errorf("snapshot: section %s delta before its keyframe", f.Name)
		}
		for k, v := range s.rows {
			next.rows[k], next.keys[k] = v, s.keys[k]
		}
		for _, k := range f.Removed {
			delete(next.rows, string(k))
			delete(next.keys, string(k))
		}
	}
	// Keys are compact JSON as written, so their bytes are the canonical key.
	for _, kv := range f.Upserts {
		k := string(kv[0])
		next.keys[k], next.rows[k] = kv[0], kv[1]
	}
	return next, nil
}

// built is the bound field b lays the section out as, cached per version.
func (s *recSection) built(b binding) (any, error) {
	if s.fieldBuilt {
		return s.field, nil
	}
	rows := make([]any, 0, len(s.rows))
	for _, raw := range s.rows {
		tree, err := parseTree(raw)
		if err != nil {
			return nil, err
		}
		rows = append(rows, tree)
	}
	sort.SliceStable(rows, func(i, j int) bool { return b.less(rows[i], rows[j]) })
	s.field, s.fieldBuilt = rows, true
	return rows, nil
}

// elide drops from tree every bound field its section materialises
// exactly, naming the section versions it is rebuilt from.
func elide(tree any, sections map[string]*recSection) (any, map[string]uint64) {
	var refs map[string]uint64
	for _, b := range bindings {
		s := sections[b.section]
		if s == nil {
			continue
		}
		field, ok := getPath(tree, b.path)
		if !ok {
			continue
		}
		built, err := s.built(b)
		if err != nil || !reflect.DeepEqual(field, built) {
			continue
		}
		tree = setPath(tree, b.path, nil, true)
		if refs == nil {
			refs = map[string]uint64{}
		}
		refs[b.section] = s.version
	}
	if elided, version, ok := elideColony(tree, sections); ok {
		tree = elided
		if refs == nil {
			refs = map[string]uint64{}
		}
		refs[bridge.ColonySection] = version
	}
	return tree, refs
}

// restore puts back the fields elide dropped.
func restore(tree any, refs map[string]uint64, sections map[string]*recSection) (any, error) {
	if version, ok := refs[bridge.ColonySection]; ok {
		var err error
		if tree, err = restoreColony(tree, version, sections); err != nil {
			return nil, err
		}
	}
	for _, b := range bindings {
		version, ok := refs[b.section]
		if !ok {
			continue
		}
		s := sections[b.section]
		if s == nil || s.version != version {
			held := uint64(0)
			if s != nil {
				held = s.version
			}
			return nil, fmt.Errorf("snapshot: line wants mirror section %s at version %d, stream holds %d", b.section, version, held)
		}
		built, err := s.built(b)
		if err != nil {
			return nil, err
		}
		tree = setPath(tree, b.path, built, false)
	}
	for name := range refs {
		if name != bridge.ColonySection && !bound(name) {
			return nil, fmt.Errorf("snapshot: line refers to unbound mirror section %s", name)
		}
	}
	return tree, nil
}

func bound(section string) bool {
	for _, b := range bindings {
		if b.section == section {
			return true
		}
	}
	return false
}

func getPath(tree any, path []string) (any, bool) {
	for _, k := range path {
		m, ok := tree.(map[string]any)
		if !ok {
			return nil, false
		}
		if tree, ok = m[k]; !ok {
			return nil, false
		}
	}
	return tree, true
}

// setPath is tree with the value at path set (or dropped), copying the
// objects along the path so tree is not modified.
func setPath(tree any, path []string, v any, drop bool) any {
	m, _ := tree.(map[string]any)
	out := make(map[string]any, len(m)+1)
	for k, x := range m {
		out[k] = x
	}
	if len(path) == 1 {
		if drop {
			delete(out, path[0])
		} else {
			out[path[0]] = v
		}
		return out
	}
	out[path[0]] = setPath(out[path[0]], path[1:], v, drop)
	return out
}

// Section is one mirror section as a stream recorded it at a line: rows
// (Encode's JSON of the mirror's R) by their key's JSON.
type Section struct {
	Name    string
	Version uint64
	AsOf    facts.Watermark
	Scope   facts.Scope
	Rows    map[string]json.RawMessage
}
