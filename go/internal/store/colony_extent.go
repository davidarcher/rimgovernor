package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"io"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Colony extent history is an append-only journal of established regions,
// scoped to one world (colony, map) and load. A new load starts empty and
// re-establishes its extent from the live world (#1009); another colony or
// map has its own journal.

type EstablishedExtent struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Region   policy.ExtentRegion
}

const extentPayloadLimit = 4 << 20

func initializeColonyExtent(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_extent_events(colony TEXT NOT NULL, map_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(colony,map_id,ordinal)) STRICT;`)
	return err
}
func checkColonyExtentSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,ordinal,tick,native_generation,payload FROM colony_extent_events LIMIT 0")
	return err
}

type extentRegionPayload struct{ Cells []policy.ExtentCell }

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
func extentScope(s domain.GenerationSnapshot, tick domain.Tick) error {
	if err := s.Validate(); err != nil {
		return err
	}
	if tick < 0 {
		return errors.New("negative colony extent tick")
	}
	return nil
}

type extentEvent struct {
	tick       domain.Tick
	generation domain.NativeGeneration
	payload    []byte
}

// extentEvents lists the world's journal at or before tick, oldest first.
func extentEvents(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) ([]extentEvent, error) {
	rows, err := tx.QueryContext(ctx, "SELECT tick,native_generation,payload FROM colony_extent_events WHERE colony=? AND map_id=? AND tick<=? ORDER BY ordinal", s.Colony, s.Map, tick)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []extentEvent
	for rows.Next() {
		var e extentEvent
		if err = rows.Scan(&e.tick, &e.generation, &e.payload); err != nil {
			return nil, err
		}
		out = append(out, e)
	}
	return out, rows.Err()
}

func appendExtentEvent(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, e extentEvent) error {
	var ordinal int64
	if err := tx.QueryRowContext(ctx, "SELECT coalesce(max(ordinal),0)+1 FROM colony_extent_events WHERE colony=? AND map_id=?", s.Colony, s.Map).Scan(&ordinal); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "INSERT INTO colony_extent_events(colony,map_id,ordinal,tick,native_generation,payload) VALUES(?,?,?,?,?,?)",
		s.Colony, s.Map, ordinal, e.tick, e.generation, e.payload)
	return err
}

// EstablishColonyExtent appends regions observed established at tick under
// snapshot. A region already in the world's journal is not repeated; the
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
	events, err := extentEvents(ctx, tx, snapshot, tick)
	if err != nil {
		return 0, err
	}
	seen := map[string]bool{}
	for _, e := range events {
		seen[string(e.payload)] = true
	}
	added := 0
	for _, data := range encoded {
		if seen[string(data)] {
			continue
		}
		seen[string(data)] = true
		if err = appendExtentEvent(ctx, tx, snapshot, extentEvent{tick: tick, generation: snapshot.Native, payload: data}); err != nil {
			return 0, err
		}
		added++
	}
	return added, tx.Commit()
}

// EstablishedColonyExtent lists the world's established regions visible
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
		var payload extentRegionPayload
		if err = extentDecode(e.payload, &payload); err != nil {
			return nil, err
		}
		origin := domain.GenerationSnapshot{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Plan: snapshot.Plan, Revision: snapshot.Revision, Native: e.generation}
		out = append(out, EstablishedExtent{Snapshot: origin, Tick: e.tick, Region: policy.ExtentRegion{Cells: payload.Cells}})
	}
	return out, nil
}
