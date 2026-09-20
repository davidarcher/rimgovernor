package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"io"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Colony extent history is an append-only journal of established regions
// and explicit expansion areas, scoped to one world (colony, map) and to
// the saved timeline the current load belongs to. Every load token is one
// timeline segment; a segment forks from the segment whose observed span
// covered its first tick, and only entries at or before the fork tick are
// visible through it. Loading an older save therefore restores exactly
// what that save's timeline had recorded by that tick, and territory
// established after the save, or on another branch, never leaks back.
// Another colony or map has its own segments and starts empty.

type EstablishedExtent struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Region   policy.ExtentRegion
}

// ExpansionArea is an explicitly selected area with the reason recorded
// when it was added. Removal is a later journal entry, never an edit.
type ExpansionArea struct {
	ID       string
	Cells    []domain.Cell
	Reason   string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}

// ExtentReconciliation reports what a load's first observation kept and
// discarded so the caller can log it.
type ExtentReconciliation struct {
	// Parent is the load token the current load's timeline forked from;
	// empty when no recorded segment of this world covered the tick.
	Parent domain.LoadID
	// ForkTick is the tick the current load was first observed at.
	ForkTick domain.Tick
	// Visible counts the established regions and expansion entries restored
	// through the lineage; Beyond counts the parent lineage's entries past
	// the fork that this timeline does not see.
	Visible, Beyond int
	// Discarded counts entries of the same load deleted by a tick rewind
	// within it, which is a dev-mode edit rather than a save.
	Discarded int
}

const (
	extentKindEstablished = "established"
	extentKindExpand      = "expand"
	extentKindContract    = "contract"
	extentPayloadLimit    = 4 << 20
	extentLineageLimit    = 256
	extentReasonLimit     = 256
)

func initializeColonyExtent(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_extent_timelines(sequence INTEGER PRIMARY KEY AUTOINCREMENT, colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, parent_load_token TEXT, fork_tick INTEGER NOT NULL CHECK(fork_tick>=0), last_tick INTEGER NOT NULL CHECK(last_tick>=fork_tick), UNIQUE(colony,map_id,load_token)) STRICT;
CREATE TABLE colony_extent_events(colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, ordinal INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('established','expand','contract')), area_id TEXT, reason TEXT, payload BLOB, PRIMARY KEY(colony,map_id,load_token,ordinal), CHECK((kind='established' AND area_id IS NULL AND reason IS NULL AND payload IS NOT NULL) OR (kind='expand' AND area_id IS NOT NULL AND reason IS NOT NULL AND payload IS NOT NULL) OR (kind='contract' AND area_id IS NOT NULL AND reason IS NOT NULL AND payload IS NULL))) STRICT;`)
	return err
}
func checkColonyExtentSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT sequence,colony,map_id,load_token,parent_load_token,fork_tick,last_tick FROM colony_extent_timelines LIMIT 0"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,load_token,ordinal,tick,native_generation,kind,area_id,reason,payload FROM colony_extent_events LIMIT 0")
	return err
}

type extentRegionPayload struct{ Cells []policy.ExtentCell }
type extentAreaPayload struct{ Cells []domain.Cell }

func extentCellsValid(cells []domain.Cell) bool {
	if len(cells) == 0 || len(cells) > 65536 {
		return false
	}
	for i, c := range cells {
		if c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 {
			return false
		}
		if i > 0 && !extentCellBefore(cells[i-1], c) {
			return false
		}
	}
	return true
}
func extentCellBefore(a, b domain.Cell) bool { return a.X < b.X || a.X == b.X && a.Z < b.Z }

func extentRegionEncode(region policy.ExtentRegion) ([]byte, error) {
	invalid := errors.New("invalid established extent region")
	cells := make([]domain.Cell, 0, len(region.Cells))
	for _, c := range region.Cells {
		if len(c.Provenance) == 0 {
			return nil, invalid
		}
		for i, p := range c.Provenance {
			switch p.Origin {
			case policy.ExtentFacility, policy.ExtentEnclosedInterior, policy.ExtentCorridor, policy.ExtentMargin:
			default:
				return nil, invalid
			}
			if i > 0 && (c.Provenance[i-1].Origin > p.Origin || c.Provenance[i-1].Origin == p.Origin && c.Provenance[i-1].Facility > p.Facility) {
				return nil, invalid
			}
		}
		cells = append(cells, c.Cell)
	}
	if !extentCellsValid(cells) {
		return nil, invalid
	}
	data, err := json.Marshal(extentRegionPayload{Cells: region.Cells})
	if err != nil {
		return nil, err
	}
	if len(data) > extentPayloadLimit {
		return nil, invalid
	}
	return data, nil
}
func extentDecode(data []byte, payload any) error {
	if len(data) > extentPayloadLimit {
		return errors.New("colony extent payload exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(payload); err != nil {
		return err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return errors.New("trailing colony extent data")
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	if !bytes.Equal(data, canonical) {
		return errors.New("noncanonical colony extent record")
	}
	return nil
}
func extentAreaEncode(cells []domain.Cell) ([]byte, error) {
	if !extentCellsValid(cells) {
		return nil, errors.New("invalid expansion area cells")
	}
	return json.Marshal(extentAreaPayload{Cells: cells})
}
func extentReasonValid(reason string) bool {
	return strings.TrimSpace(reason) != "" && len(reason) <= extentReasonLimit && !strings.ContainsRune(reason, '\x00')
}
func extentScope(s domain.GenerationSnapshot, tick domain.Tick) error {
	if err := s.Validate(); err != nil {
		return err
	}
	if tick < 0 {
		return errors.New("negative colony extent tick")
	}
	return nil
}

type extentSegment struct {
	load domain.LoadID
	// limit is the highest tick of this segment visible from the reader.
	limit domain.Tick
}

// extentLineage walks from the current load back through its parents,
// tightening the visible tick at every fork. A load without a recorded
// segment sees only its own entries at or before tick.
func extentLineage(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) ([]extentSegment, error) {
	lineage := []extentSegment{{load: s.Load, limit: tick}}
	load, limit := s.Load, tick
	for range extentLineageLimit {
		var parent sql.NullString
		var fork domain.Tick
		err := tx.QueryRowContext(ctx, "SELECT parent_load_token,fork_tick FROM colony_extent_timelines WHERE colony=? AND map_id=? AND load_token=?", s.Colony, s.Map, load).Scan(&parent, &fork)
		if errors.Is(err, sql.ErrNoRows) || err == nil && !parent.Valid {
			return lineage, nil
		}
		if err != nil {
			return nil, err
		}
		if fork < limit {
			limit = fork
		}
		load = domain.LoadID(parent.String)
		lineage = append(lineage, extentSegment{load: load, limit: limit})
	}
	return nil, errors.New("colony extent lineage exceeds bound")
}

// reconcileColonyExtent records the current load's timeline segment on its
// first observation and advances it afterwards. Same-load rewinds discard
// the load's entries past the tick.
func reconcileColonyExtent(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) (ExtentReconciliation, error) {
	report := ExtentReconciliation{ForkTick: tick}
	var parent sql.NullString
	var fork, last domain.Tick
	err := tx.QueryRowContext(ctx, "SELECT parent_load_token,fork_tick,last_tick FROM colony_extent_timelines WHERE colony=? AND map_id=? AND load_token=?", s.Colony, s.Map, s.Load).Scan(&parent, &fork, &last)
	switch {
	case errors.Is(err, sql.ErrNoRows):
		// The segment that was live at this tick is the save's origin; among
		// branches that all covered it, the most recently played one is the
		// save the player is likeliest to have taken. A tick past every
		// recorded span continues the latest segment that ended before it.
		err = tx.QueryRowContext(ctx, `SELECT load_token FROM colony_extent_timelines WHERE colony=? AND map_id=? AND fork_tick<=? ORDER BY (last_tick>=?) DESC, CASE WHEN last_tick>=? THEN sequence ELSE last_tick END DESC, sequence DESC LIMIT 1`, s.Colony, s.Map, tick, tick, tick).Scan(&parent)
		if err != nil && !errors.Is(err, sql.ErrNoRows) {
			return report, err
		}
		if parent.Valid {
			report.Parent = domain.LoadID(parent.String)
			if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM colony_extent_events WHERE colony=? AND map_id=? AND load_token=? AND tick>?", s.Colony, s.Map, parent.String, tick).Scan(&report.Beyond); err != nil {
				return report, err
			}
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO colony_extent_timelines(colony,map_id,load_token,parent_load_token,fork_tick,last_tick) VALUES(?,?,?,?,?,?)", s.Colony, s.Map, s.Load, parent, tick, tick); err != nil {
			return report, err
		}
	case err != nil:
		return report, err
	default:
		report.ForkTick = fork
		if parent.Valid {
			report.Parent = domain.LoadID(parent.String)
		}
		if tick < last {
			result, err := tx.ExecContext(ctx, "DELETE FROM colony_extent_events WHERE colony=? AND map_id=? AND load_token=? AND tick>?", s.Colony, s.Map, s.Load, tick)
			if err != nil {
				return report, err
			}
			n, err := result.RowsAffected()
			if err != nil {
				return report, err
			}
			report.Discarded = int(n)
		}
		if tick < fork {
			fork = tick
		}
		if _, err = tx.ExecContext(ctx, "UPDATE colony_extent_timelines SET fork_tick=?,last_tick=? WHERE colony=? AND map_id=? AND load_token=?", fork, tick, s.Colony, s.Map, s.Load); err != nil {
			return report, err
		}
	}
	lineage, err := extentLineage(ctx, tx, s, tick)
	if err != nil {
		return report, err
	}
	for _, segment := range lineage {
		var n int
		if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM colony_extent_events WHERE colony=? AND map_id=? AND load_token=? AND tick<=?", s.Colony, s.Map, segment.load, segment.limit).Scan(&n); err != nil {
			return report, err
		}
		report.Visible += n
	}
	return report, nil
}

// ReconcileColonyExtent binds the current load to its timeline. Call it on
// restart and on every world change; writes reconcile implicitly.
func (s *Store) ReconcileColonyExtent(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (ExtentReconciliation, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return ExtentReconciliation{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ExtentReconciliation{}, err
	}
	defer tx.Rollback()
	report, err := reconcileColonyExtent(ctx, tx, snapshot, tick)
	if err != nil {
		return ExtentReconciliation{}, err
	}
	return report, tx.Commit()
}

type extentEvent struct {
	load       domain.LoadID
	tick       domain.Tick
	generation domain.NativeGeneration
	kind       string
	area       string
	reason     string
	payload    []byte
}

// extentEvents lists the visible journal oldest first: ancestors before
// descendants, then tick and ordinal.
func extentEvents(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) ([]extentEvent, error) {
	lineage, err := extentLineage(ctx, tx, s, tick)
	if err != nil {
		return nil, err
	}
	var out []extentEvent
	for i := len(lineage) - 1; i >= 0; i-- {
		segment := lineage[i]
		rows, err := tx.QueryContext(ctx, "SELECT tick,native_generation,kind,area_id,reason,payload FROM colony_extent_events WHERE colony=? AND map_id=? AND load_token=? AND tick<=? ORDER BY ordinal", s.Colony, s.Map, segment.load, segment.limit)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			e := extentEvent{load: segment.load}
			var area, reason sql.NullString
			if err = rows.Scan(&e.tick, &e.generation, &e.kind, &area, &reason, &e.payload); err != nil {
				rows.Close()
				return nil, err
			}
			e.area, e.reason = area.String, reason.String
			out = append(out, e)
		}
		err = rows.Err()
		rows.Close()
		if err != nil {
			return nil, err
		}
	}
	return out, nil
}

func appendExtentEvent(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, e extentEvent) error {
	var ordinal int64
	if err := tx.QueryRowContext(ctx, "SELECT coalesce(max(ordinal),0)+1 FROM colony_extent_events WHERE colony=? AND map_id=? AND load_token=?", s.Colony, s.Map, s.Load).Scan(&ordinal); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "INSERT INTO colony_extent_events(colony,map_id,load_token,ordinal,tick,native_generation,kind,area_id,reason,payload) VALUES(?,?,?,?,?,?,?,?,?,?)",
		s.Colony, s.Map, s.Load, ordinal, e.tick, e.generation, e.kind, sql.NullString{String: e.area, Valid: e.area != ""}, sql.NullString{String: e.reason, Valid: e.reason != ""}, e.payload)
	return err
}

// EstablishColonyExtent appends regions observed established at tick under
// snapshot. A region already visible in the timeline is not repeated; the
// count returned is the number of new entries.
func (s *Store) EstablishColonyExtent(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, regions []policy.ExtentRegion) (int, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return 0, err
	}
	if len(regions) > 4096 {
		return 0, errors.New("too many established extent regions")
	}
	encoded := make([][]byte, 0, len(regions))
	for _, region := range regions {
		data, err := extentRegionEncode(region)
		if err != nil {
			return 0, err
		}
		encoded = append(encoded, data)
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return 0, err
	}
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return 0, err
	}
	seen := map[string]bool{}
	for _, e := range events {
		if e.kind == extentKindEstablished {
			seen[string(e.payload)] = true
		}
	}
	added := 0
	for _, data := range encoded {
		if seen[string(data)] {
			continue
		}
		seen[string(data)] = true
		if err = appendExtentEvent(ctx, tx, snapshot, extentEvent{tick: tick, generation: snapshot.Native, kind: extentKindEstablished, payload: data}); err != nil {
			return 0, err
		}
		added++
	}
	return added, tx.Commit()
}

// EstablishedColonyExtent lists the timeline's established regions visible
// at tick, oldest first, each with the generation that first observed it.
func (s *Store) EstablishedColonyExtent(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) ([]EstablishedExtent, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return nil, err
	}
	out := []EstablishedExtent{}
	for _, e := range events {
		if e.kind != extentKindEstablished {
			continue
		}
		var payload extentRegionPayload
		if err = extentDecode(e.payload, &payload); err != nil {
			return nil, err
		}
		origin := domain.GenerationSnapshot{Colony: snapshot.Colony, Map: snapshot.Map, Load: e.load, Plan: snapshot.Plan, Revision: snapshot.Revision, Native: e.generation}
		out = append(out, EstablishedExtent{Snapshot: origin, Tick: e.tick, Region: policy.ExtentRegion{Cells: payload.Cells}})
	}
	return out, nil
}

func extentAreas(events []extentEvent, s domain.GenerationSnapshot) (map[string]ExpansionArea, error) {
	areas := map[string]ExpansionArea{}
	for _, e := range events {
		switch e.kind {
		case extentKindExpand:
			var payload extentAreaPayload
			if err := extentDecode(e.payload, &payload); err != nil {
				return nil, err
			}
			origin := domain.GenerationSnapshot{Colony: s.Colony, Map: s.Map, Load: e.load, Plan: s.Plan, Revision: s.Revision, Native: e.generation}
			areas[e.area] = ExpansionArea{ID: e.area, Cells: payload.Cells, Reason: e.reason, Snapshot: origin, Tick: e.tick}
		case extentKindContract:
			delete(areas, e.area)
		}
	}
	return areas, nil
}

// AddExpansionArea records an explicitly selected area and why. Repeating
// an ID with the same cells is a no-op; different cells under a live ID
// conflict.
func (s *Store) AddExpansionArea(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, id string, cells []domain.Cell, reason string) error {
	if err := extentScope(snapshot, tick); err != nil {
		return err
	}
	if submissionID(id) != nil || !extentReasonValid(reason) {
		return errors.New("invalid expansion area id or reason")
	}
	data, err := extentAreaEncode(cells)
	if err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return err
	}
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return err
	}
	areas, err := extentAreas(events, snapshot)
	if err != nil {
		return err
	}
	if existing, live := areas[id]; live {
		current, err := extentAreaEncode(existing.Cells)
		if err != nil {
			return err
		}
		if bytes.Equal(current, data) {
			return tx.Commit()
		}
		return ErrConflict
	}
	if err = appendExtentEvent(ctx, tx, snapshot, extentEvent{tick: tick, generation: snapshot.Native, kind: extentKindExpand, area: id, reason: reason, payload: data}); err != nil {
		return err
	}
	return tx.Commit()
}

// RemoveExpansionArea journals the removal of a live area with its reason.
func (s *Store) RemoveExpansionArea(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, id, reason string) error {
	if err := extentScope(snapshot, tick); err != nil {
		return err
	}
	if submissionID(id) != nil || !extentReasonValid(reason) {
		return errors.New("invalid expansion area id or reason")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return err
	}
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return err
	}
	areas, err := extentAreas(events, snapshot)
	if err != nil {
		return err
	}
	if _, live := areas[id]; !live {
		return ErrNotFound
	}
	if err = appendExtentEvent(ctx, tx, snapshot, extentEvent{tick: tick, generation: snapshot.Native, kind: extentKindContract, area: id, reason: reason}); err != nil {
		return err
	}
	return tx.Commit()
}

// ExpansionAreas lists the areas live in the timeline at tick, by ID.
func (s *Store) ExpansionAreas(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) ([]ExpansionArea, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return nil, err
	}
	areas, err := extentAreas(events, snapshot)
	if err != nil {
		return nil, err
	}
	out := make([]ExpansionArea, 0, len(areas))
	for _, area := range areas {
		out = append(out, area)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out, nil
}
