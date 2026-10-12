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

// DefenseTierRecord is one tier of the layout and what became of it.
//
// A tier cut from the layout plan's site nodes (the ring, its gates, the
// fence, the pumps, the geothermal enclosure) names them in Nodes and stores
// no Buildings: the plan owns the geometry, and the runtime fills Buildings in
// from it after every load. Every other tier stores its Buildings: the
// census-derived ones (the hallway's firing line, funnel and trap corridor,
// the IEDs, turrets, mortars and prison turrets, the lights) and, until the
// plan carries them as nodes, the pockets. A removal tier
// holds the node ids it replaced and the cells it was cut from, frozen when
// the perimeter was re-cut, since the plan no longer holds the old ring.
type DefenseTierRecord struct {
	Name policy.DefenseTierName
	// Nodes are the plan's site nodes the tier is cut from.
	Nodes     []string `json:",omitempty"`
	Buildings []DefenseBuilding
	// Refused are the cells of a node-backed tier the game refused to build
	// on; the tier leaves them out of its Buildings.
	Refused  []domain.Cell `json:",omitempty"`
	Reserved []domain.Cell
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
	// the plan that built the tier the first time.
	Reopened int
	// Remove marks a perimeter tier of buildings the replanned ring no
	// longer wants: it is built once none of them stands, and then
	// leaves the record.
	Remove bool `json:",omitempty"`
}

// NodeBacked reports whether the tier's buildings come from the plan.
func (t DefenseTierRecord) NodeBacked() bool { return len(t.Nodes) > 0 && !t.Remove }

// DefenseLayoutRecord is the one layout the colony committed to for one
// world and Episode of its Standard. The planner writes it when it first proposes a
// layout so later tiers keep the same geometry after earlier tiers changed
// the census, and marks Complete once every tier's method has finished;
// combat reads Firing only while Complete holds.
//
// The killbox it was proposed on is the plan's node, named by Killbox with a
// digest of its footprint; the opening's entry, heading and width are read
// from the plan at use. Firing, Retreat and TrapLane are what the proposal
// observed against the live census, and stay here.
type DefenseLayoutRecord struct {
	World    World
	Standard domain.StandardID
	Episode  uint64
	// Killbox is the plan's killbox node id and KillboxDigest a digest of its
	// footprint when the layout was proposed: a plan that moved the opening
	// proposes the layout afresh. An empty Killbox is a layout to propose
	// afresh.
	Killbox       string
	KillboxDigest string
	TrapLane      []domain.Cell
	Firing        []domain.Cell
	// Retreat is the inner line: Retreat[i] is Firing[i]'s fallback cell.
	// It can be empty, leaving combat without a fallback line.
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
	// MortarsProbedTick is TurretsProbedTick for the mortar tier.
	MortarsProbedTick domain.Tick `json:",omitempty"`
	// PerimeterKey digests the perimeter the tiers were last cut from, and
	// PerimeterRevision counts the times a changed plan or finished
	// research re-cut them; it names the new tiers so their methods
	// never reuse an earlier section's plan ids.
	PerimeterKey      string `json:",omitempty"`
	PerimeterRevision int    `json:",omitempty"`
	// PerimeterPlanTick is the tick of the plan the node tiers were last cut
	// from and PerimeterBridge the bridge definition they were cut with: a
	// re-cut resolves the old tiers' cells from that plan.
	PerimeterPlanTick domain.Tick `json:",omitempty"`
	PerimeterBridge   string      `json:",omitempty"`
}

const maxDefenseLayoutBytes = 1024 * 1024

// Validate rejects a record that could not have come from a policy layout:
// bad world identity, no firing cells or tiers, or oversized tiers.
func (r DefenseLayoutRecord) Validate() error {
	if !validIdentity(string(r.World.Colony)) || !validIdentity(string(r.World.Load)) || r.World.Map < 0 || !validIdentity(string(r.Standard)) {
		return errors.New("defense layout world or standard identity invalid")
	}
	if len(r.Firing) == 0 || len(r.Tiers) == 0 || len(r.Firing) > 64 || len(r.Retreat) != 0 && len(r.Retreat) != len(r.Firing) || len(r.TrapLane) > 512 || len(r.Entrances) > 64 {
		return errors.New("defense layout geometry out of bounds")
	}
	if r.VerifiedTick < 0 || r.TurretsProbedTick < 0 || r.MortarsProbedTick < 0 || len(r.VerifiedCombat) > 512 || len(r.PerimeterKey) > 128 || r.PerimeterRevision < 0 ||
		r.PerimeterPlanTick < 0 || len(r.PerimeterBridge) > 64 || len(r.Killbox) > 128 || len(r.KillboxDigest) > 64 {
		return errors.New("defense layout verification invalid")
	}
	seen := map[policy.DefenseTierName]bool{}
	for _, tier := range r.Tiers {
		if tier.Name == "" || seen[tier.Name] || len(tier.Buildings) > 512 || len(tier.Reserved) > 512 || len(tier.Nodes) > 256 || len(tier.Refused) > 512 || tier.Attempts < 0 || tier.Attempts > 64 || tier.Reopened < 0 || tier.Reopened > 4096 {
			return errors.New("defense layout tier invalid")
		}
		seen[tier.Name] = true
		for _, id := range tier.Nodes {
			if !validIdentity(id) {
				return errors.New("defense layout tier node invalid")
			}
		}
		for _, b := range tier.Buildings {
			if _, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff); err != nil {
				return err
			}
		}
	}
	return nil
}

// stored is the record as it is persisted: a node-backed tier carries no
// buildings, the plan holds them.
func (r DefenseLayoutRecord) stored() DefenseLayoutRecord {
	tiers := make([]DefenseTierRecord, len(r.Tiers))
	for i, t := range r.Tiers {
		if t.NodeBacked() {
			t.Buildings = nil
		}
		tiers[i] = t
	}
	r.Tiers = tiers
	return r
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
		if (len(tier.Buildings) > 0 || tier.NodeBacked()) && !tier.Built {
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

// NewDefenseLayoutRecord captures a policy layout for one Standard Episode,
// proposed on the plan's killbox node (its id and footprint digest).
func NewDefenseLayoutRecord(w World, standard domain.StandardID, episode uint64, l policy.DefenseLayout, entrances []domain.Cell, killbox, digest string) (DefenseLayoutRecord, error) {
	r := DefenseLayoutRecord{World: w, Standard: standard, Episode: episode, Killbox: killbox, KillboxDigest: digest, TrapLane: append([]domain.Cell{}, l.TrapLane...), Entrances: append([]domain.Cell{}, entrances...)}
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

func loadDefenseLayout(ctx context.Context, tx *Tx) (DefenseLayoutRecord, bool, error) {
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
	canonical, err := json.Marshal(r.stored())
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
// tiers, and combat holds a Complete record's line under any load. A
// node-backed tier comes back without Buildings: the caller resolves them
// from the plan.
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
// a switch of colony or map. A node-backed tier's buildings are not stored.
func (s *Store) SaveDefenseLayout(ctx context.Context, r DefenseLayoutRecord) error {
	if err := r.Validate(); err != nil {
		return err
	}
	data, err := json.Marshal(r.stored())
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
	return nil
}
