package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// DefenseBuilding is one serialisable placement of a stored layout tier;
// domain.Building keeps its fields private so the record carries them here.
type DefenseBuilding struct {
	Definition string
	Cell       domain.Cell
	Rotation   domain.Rotation
	Stuff      string
}

type DefenseTierRecord struct {
	Name      policy.DefenseTierName
	Buildings []DefenseBuilding
	Reserved  []domain.Cell
	// Attempts counts admitted methods for the tier; settled routine plans
	// retire out of the goal's method list, so the record keeps the count.
	Attempts int
	// Built records that every building of the tier was observed standing
	// in the latest census; a building lost since (a raider's wall breach, a
	// sprung spike trap) clears it so the planner re-admits the tier.
	Built bool
	// Reopened counts the times the census cleared Built after the tier
	// stood. Each repair restarts Attempts, and the tier's methods (and so
	// its plan ids) are keyed by both, so a repair never reuses the id of
	// the plan that built the tier the first time (#331).
	Reopened int
	// Remove marks a perimeter tier of buildings the replanned ring no
	// longer wants (#954): it is built once none of them stands, and then
	// leaves the record.
	Remove bool `json:",omitempty"`
}

// DefenseLayoutRecord is the one layout the colony committed to for one
// world and Project. The planner writes it when it first proposes a
// layout so later tiers keep the same geometry after earlier tiers changed
// the census, and marks Complete once every tier's method has finished;
// combat reads Firing only while Complete holds.
type DefenseLayoutRecord struct {
	World      World
	Project    domain.ProjectID
	Chokepoint domain.Cell
	Entry      domain.Cell
	Toward     domain.Rotation
	Width      int
	TrapLane   []domain.Cell
	// SafeLane is the civilian lane of records written before #1544;
	// layouts since leave it empty.
	SafeLane []domain.Cell
	Firing   []domain.Cell
	// Retreat is the inner line (#860): Retreat[i] is Firing[i]'s fall-back
	// cell. Empty on a record written before it; combat then has no line
	// to fall back to.
	Retreat []domain.Cell `json:",omitempty"`
	// Entrances are the colony doors whose access the audit keeps.
	Entrances []domain.Cell
	Tiers     []DefenseTierRecord
	Complete  bool
	// VerifiedTick is the tick of the last census that found every tier
	// standing, and VerifiedCombat the ActiveCombat Episode (goal/epoch)
	// whose aftermath that census covered. A Complete record is re-verified
	// after each combat and once per game hour of simulation; Complete
	// itself stays true while a lost building is being replaced, so combat
	// keeps holding the line on the proven geometry.
	VerifiedTick   domain.Tick `json:",omitempty"`
	VerifiedCombat string      `json:",omitempty"`
	// TurretsProbedTick is the tick the turret tier was last proposed
	// against the stored geometry while it had nothing to place; the
	// planner re-probes once per reverify interval, not every step.
	TurretsProbedTick domain.Tick `json:",omitempty"`
	// MortarsProbedTick is TurretsProbedTick for the mortar tier (#1206).
	MortarsProbedTick domain.Tick `json:",omitempty"`
	// FuelShortage is the fuel the tier's empty barrels need and the last
	// census found no stock of (#205), per definition; the rounds
	// raises it as a derived MaintainResource floor until a barrel is
	// rearmed or the stock returns.
	FuelShortage []policy.Amount `json:",omitempty"`
	// Anchored marks a layout proposed on the layout plan's killbox with
	// the perimeter's sections (#789); an older record is proposed afresh.
	Anchored bool `json:",omitempty"`
	// PerimeterKey digests the perimeter the tiers were last cut from, and
	// PerimeterRevision counts the times a changed plan or finished
	// research re-cut them (#954); it names the new tiers so their methods
	// never reuse an earlier section's plan ids.
	PerimeterKey      string `json:",omitempty"`
	PerimeterRevision int    `json:",omitempty"`
}

const maxDefenseLayoutBytes = 1024 * 1024

// Validate rejects a record that could not have come from a policy layout:
// bad world identity, no firing cells or tiers, or oversized tiers.
func (r DefenseLayoutRecord) Validate() error {
	if !validIdentity(string(r.World.Colony)) || !validIdentity(string(r.World.Load)) || r.World.Map < 0 || !validIdentity(string(r.Project)) {
		return errors.New("defense layout world or project identity invalid")
	}
	if len(r.Firing) == 0 || len(r.Tiers) == 0 || len(r.Firing) > 64 || len(r.Retreat) != 0 && len(r.Retreat) != len(r.Firing) || len(r.TrapLane) > 512 || len(r.SafeLane) > 64 || len(r.Entrances) > 64 {
		return errors.New("defense layout geometry out of bounds")
	}
	if r.VerifiedTick < 0 || r.TurretsProbedTick < 0 || r.MortarsProbedTick < 0 || len(r.VerifiedCombat) > 512 || len(r.PerimeterKey) > 128 || r.PerimeterRevision < 0 {
		return errors.New("defense layout verification invalid")
	}
	if len(r.FuelShortage) > 16 {
		return errors.New("defense layout fuel shortage out of bounds")
	}
	for _, a := range r.FuelShortage {
		if a.Resource == "" || len(a.Resource) > 256 || a.Count <= 0 || a.Count > 10000 {
			return errors.New("defense layout fuel shortage invalid")
		}
	}
	seen := map[policy.DefenseTierName]bool{}
	for _, tier := range r.Tiers {
		if tier.Name == "" || seen[tier.Name] || len(tier.Buildings) > 512 || len(tier.Reserved) > 512 || tier.Attempts < 0 || tier.Attempts > 64 || tier.Reopened < 0 || tier.Reopened > 4096 {
			return errors.New("defense layout tier invalid")
		}
		seen[tier.Name] = true
		for _, b := range tier.Buildings {
			if _, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff); err != nil {
				return err
			}
		}
	}
	return nil
}

// Tier returns the named tier's buildings as domain placements.
func (r DefenseLayoutRecord) Tier(name policy.DefenseTierName) (DefenseTierRecord, []domain.Building, bool) {
	for _, tier := range r.Tiers {
		if tier.Name != name {
			continue
		}
		out := make([]domain.Building, 0, len(tier.Buildings))
		for _, b := range tier.Buildings {
			building, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff)
			if err != nil {
				return DefenseTierRecord{}, nil, false
			}
			out = append(out, building)
		}
		return tier, out, true
	}
	return DefenseTierRecord{}, nil, false
}

// SetPolicyTier replaces the named tier (turrets, mortars) with a fresh pending
// record of the policy tier, appending it when the record lacks one.
func (r *DefenseLayoutRecord) SetPolicyTier(tier policy.DefenseTier) {
	t := DefenseTierRecord{Name: tier.Name, Reserved: append([]domain.Cell{}, tier.Reserved...)}
	for _, b := range tier.Buildings {
		t.Buildings = append(t.Buildings, DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()})
	}
	for i := range r.Tiers {
		if r.Tiers[i].Name == tier.Name {
			r.Tiers[i] = t
			return
		}
	}
	r.Tiers = append(r.Tiers, t)
}

// Standing reports whether the layout was verified complete and every tier
// placed since still stands in the last census: the state in which the
// goal has no deficit for development arbitration.
func (r DefenseLayoutRecord) Standing() bool {
	if !r.Complete {
		return false
	}
	for _, tier := range r.Tiers {
		if len(tier.Buildings) > 0 && !tier.Built {
			return false
		}
	}
	return true
}

// SetTier replaces the named tier's record in place; unknown names are ignored.
func (r *DefenseLayoutRecord) SetTier(tier DefenseTierRecord) {
	for i := range r.Tiers {
		if r.Tiers[i].Name == tier.Name {
			r.Tiers[i] = tier
		}
	}
}

func validIdentity(s string) bool { return s != "" && len(s) <= 256 }

// NewDefenseLayoutRecord captures a policy layout for one Project.
func NewDefenseLayoutRecord(w World, project domain.ProjectID, l policy.DefenseLayout, entrances []domain.Cell) (DefenseLayoutRecord, error) {
	r := DefenseLayoutRecord{World: w, Project: project, Chokepoint: l.Chokepoint, Entry: l.Entry, Toward: l.Toward, Width: l.Width, TrapLane: append([]domain.Cell{}, l.TrapLane...), Entrances: append([]domain.Cell{}, entrances...)}
	for _, f := range l.Firing {
		r.Firing = append(r.Firing, f.Cell)
		r.Retreat = append(r.Retreat, f.Retreat)
	}
	for _, tier := range l.Tiers {
		t := DefenseTierRecord{Name: tier.Name, Reserved: append([]domain.Cell{}, tier.Reserved...)}
		for _, b := range tier.Buildings {
			t.Buildings = append(t.Buildings, DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()})
		}
		r.Tiers = append(r.Tiers, t)
	}
	return r, r.Validate()
}

func loadDefenseLayout(ctx context.Context, tx *sql.Tx) (DefenseLayoutRecord, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM defense_layout WHERE singleton=1").Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return DefenseLayoutRecord{}, false, nil
		}
		return DefenseLayoutRecord{}, false, err
	}
	if len(data) > maxDefenseLayoutBytes {
		return DefenseLayoutRecord{}, false, ErrCapacity
	}
	var r DefenseLayoutRecord
	if err := json.Unmarshal(data, &r); err != nil {
		return DefenseLayoutRecord{}, false, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Validate() != nil {
		return DefenseLayoutRecord{}, false, errors.New("invalid stored defense layout")
	}
	return r, true, nil
}

// LoadDefenseLayout returns the stored layout for w's colony and map, false
// when none or when the stored record belongs to another colony or map. The
// record's World.Load may differ from w.Load: the geometry outlives a reload
// of the same colony (the walls and traps are on the map either way), and
// the layout planner adopts it under the new load after re-observing the
// tiers, and combat holds a Complete record's line under any load.
func (s *Store) LoadDefenseLayout(ctx context.Context, w World) (DefenseLayoutRecord, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return DefenseLayoutRecord{}, false, err
	}
	defer tx.Rollback()
	r, ok, err := loadDefenseLayout(ctx, tx)
	if err != nil {
		return DefenseLayoutRecord{}, false, err
	}
	if !ok || r.World.Colony != w.Colony || r.World.Map != w.Map {
		return DefenseLayoutRecord{}, false, tx.Commit()
	}
	return r, true, tx.Commit()
}

// SaveDefenseLayout replaces the stored layout. The singleton holds one
// colony's layout: a new colony overwrites, so a stale layout never survives
// a switch of colony or map.
func (s *Store) SaveDefenseLayout(ctx context.Context, r DefenseLayoutRecord) error {
	if err := r.Validate(); err != nil {
		return err
	}
	data, err := json.Marshal(r)
	if err != nil {
		return err
	}
	if len(data) > maxDefenseLayoutBytes {
		return ErrCapacity
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT INTO defense_layout(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return err
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	s.notifyStandardsWritten()
	return nil
}
