package policy

import (
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainStockpiles applies the stockpile zones the departments declare
// (#725, DeclareStores): a zone is created once at its store's size, a zone
// whose role's desired filter or priority changed is patched, a zone whose
// role's purpose is gone is deleted, and a store no zone serves is created
// (#724, #917). A zone is never grown, shrunk or
// merged: its empty cells are its headroom. It acts on the zones the colony
// created (store.OwnedZone), role-keyed; a role-less legacy claim stands as
// created. It is a Standard whose target is no outstanding work: no zone edit
// due (#1024).
const MaintainStockpiles ConcernID = "MaintainStockpiles"

// stockpilePriority ranks MaintainStockpiles with the other upkeep goals.
const stockpilePriority = 3

// stockpileDeficit is the deficit a standing edit ranks with: small, like
// the tidy's, so it never outranks real shortfalls.
const stockpileDeficit = 0.1

// StockpileFurtherRoomFill is the used-cell fraction at which a store's
// standing zones count as full and the department asks layout for further
// room.
const StockpileFurtherRoomFill = 0.85

// StockpileZone is one owned stockpile as the census holds it now: its
// cells (the planning cells naming it), the cells holding things (Stored:
// the census' storage-empty flag false) and the settings the autopilot last
// applied.
type StockpileZone struct {
	ID       string
	Role     string
	Cells    []domain.Cell
	Stored   []domain.Cell
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
}

// Used counts the cells holding things.
func (z StockpileZone) Used() int { return len(z.Stored) }

// Fill is the used-cell fraction; an empty zone is 0.
func (z StockpileZone) Fill() float64 {
	if len(z.Cells) == 0 {
		return 0
	}
	return float64(z.Used()) / float64(len(z.Cells))
}

type StockpileEditKind string

const (
	StockpileDelete   StockpileEditKind = "delete"
	StockpileRetarget StockpileEditKind = "retarget"
)

// StockpileEdit is one proposed edit of one zone. Cells are the cells a
// create zones; Filter/Priority/Role a retarget's settings.
type StockpileEdit struct {
	Kind StockpileEditKind
	Zone string
	Role string
	// After is the site role whose create must be admitted before this
	// delete of a moved zone; empty once the site is served.
	After       string `json:",omitempty"`
	Cells       []domain.Cell
	Filter      domain.StockpileFilter
	Priority    domain.StockpilePriority
	Explanation string
}

// StockpileRequest is the review's input.
type StockpileRequest struct {
	Tick      domain.Tick
	Zones     []StockpileZone
	Cells     []SiteCell
	Bounds    Bounds
	Protected []domain.Cell
	// Shelves are the built shelves inside the zones (#721): each carries
	// its zone's desired settings, patched until it does.
	Shelves []StockpileShelf
	// Stores are the declared stores of the departments that own stockpiles
	// (DeclareStores): each store's filter and priority are defined once, on
	// the store.
	Stores []Store
	// RoomDemand is the departments' room demand for layout (#1773); the
	// review itself does not read it.
	RoomDemand RoomDemand
	// SiteErr is the departments' report of stores they could not make usable
	// (StoreDeclaration.Err); the review itself does not read it.
	SiteErr error
	// Rooms are the planned storage rooms (storage, armory and wardrobe,
	// #1774; the materials yard's fence ring, #2215) not yet standing: the
	// planner raises their shells (StockpileReview.Rooms).
	Rooms []PlannedRole
}

// StockpileShelf is one built shelf serving an owned zone and the settings
// last patched onto it (Patched false: never patched, native defaults).
type StockpileShelf struct {
	Building string
	Zone     string
	Cells    int
	Patched  bool
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
}

// StockpileShelfPatch configures a shelf like its zone (#721): Zone names
// the shelf building, Role is shelf:<buildingID>.
const StockpileShelfPatch StockpileEditKind = "shelf"

// stockpileShelfEdits patches every shelf whose settings differ from its
// zone's desired ones: its declared store's, else the zone's own settings. A shelf of a zone that is gone or retiring waits.
func stockpileShelfEdits(stores []Store, zones []StockpileZone, shelves []StockpileShelf) []StockpileEdit {
	byID := map[string]StockpileZone{}
	for _, z := range zones {
		byID[z.ID] = z
	}
	sorted := append([]StockpileShelf(nil), shelves...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Building < sorted[j].Building })
	var out []StockpileEdit
	for _, s := range sorted {
		z, ok := byID[s.Zone]
		if !ok || s.Building == "" {
			continue
		}
		filter, priority := z.Filter, z.Priority
		if store, declared := storeOfZone(stores, z); declared {
			if store.Retired {
				continue
			}
			filter, priority = store.Filter, store.Priority
		}
		if s.Patched && s.Filter == filter && s.Priority == priority {
			continue
		}
		hauls := s.Cells * ShelfItemsPerCell
		out = append(out, StockpileEdit{Kind: StockpileShelfPatch, Zone: s.Building, Role: ShelfRole(s.Building), Filter: filter, Priority: priority,
			Explanation: fmt.Sprintf("shelf %s in stockpile %s (%s): configure like its zone (priority %s); up to %d stacks may rehome", s.Building, z.ID, z.Role, priority, hauls)})
	}
	return out
}

// StockpileReview is the outcome: the zone edits this cycle proposes, the
// planned rooms whose shells are owed, and why none stands.
type StockpileReview struct {
	Known  bool
	Active bool
	Edits  []StockpileEdit `json:",omitempty"`
	// Rooms are the planned rooms (StockpileRequest.Rooms) the planner raises
	// before the zone edits go on.
	Rooms  []PlannedRole `json:",omitempty"`
	Reason string
}

// StockpileRoleCount is the owned zones of one role kind (the role key up
// to its colon: general, yard, medicine, meals ...; untagged for a zone no
// planner claimed): how many, their cells and the cells holding things.
type StockpileRoleCount struct {
	Role  string
	Zones int
	Cells int
	Used  int
}

// StockpileRoleCounts counts the owned zones by role kind, sorted by role.
func StockpileRoleCounts(zones []StockpileZone) []StockpileRoleCount {
	byRole := map[string]*StockpileRoleCount{}
	for _, z := range zones {
		kind, _, _ := strings.Cut(z.Role, ":")
		if kind == "" {
			kind = "untagged"
		}
		count := byRole[kind]
		if count == nil {
			count = &StockpileRoleCount{Role: kind}
			byRole[kind] = count
		}
		count.Zones++
		count.Cells += len(z.Cells)
		count.Used += z.Used()
	}
	out := make([]StockpileRoleCount, 0, len(byRole))
	for _, count := range byRole {
		out = append(out, *count)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Role < out[j].Role })
	return out
}

// PlanStockpileMaintenance proposes this cycle's edits, one per zone, in
// urgency order (delete, retarget and shelf patch, create; ties by zone id).
// Deterministic over its input.
func PlanStockpileMaintenance(r StockpileRequest) StockpileReview {
	review := StockpileReview{Known: true, Rooms: r.Rooms}
	zones := append([]StockpileZone(nil), r.Zones...)
	sort.Slice(zones, func(i, j int) bool { return zones[i].ID < zones[j].ID })
	open := newStockpileOpen(r)
	var candidates []StockpileEdit
	touched := map[string]bool{}
	take := func(e StockpileEdit, ok bool) bool {
		if ok && !touched[e.Zone] {
			touched[e.Zone] = true
			candidates = append(candidates, e)
			return true
		}
		return false
	}
	declared, owned := declaredStoreEdits(zones, r.Stores, open)
	for _, e := range declared {
		if e.Kind == StockpileCreate {
			candidates = append(candidates, e)
		} else {
			take(e, true)
		}
	}
	for id := range owned {
		touched[id] = true
	}
	for _, e := range stockpileShelfEdits(r.Stores, zones, r.Shelves) {
		take(e, true)
	}
	rank := map[StockpileEditKind]int{StockpileDelete: 0, StockpileRetarget: 1, StockpileShelfPatch: 1, StockpileCreate: 2}
	// A moved zone's delete follows its replacement's create.
	order := func(e StockpileEdit) int {
		if e.After != "" {
			return rank[StockpileCreate]*2 + 1
		}
		return rank[e.Kind] * 2
	}
	sort.SliceStable(candidates, func(i, j int) bool { return order(candidates[i]) < order(candidates[j]) })
	created := map[string]bool{}
	for _, e := range candidates {
		if e.After != "" && !created[e.After] {
			continue
		}
		review.Edits = append(review.Edits, e)
		if e.Kind == StockpileCreate {
			created[e.Role] = true
		}
	}
	review.Active = len(review.Edits) > 0 || len(review.Rooms) > 0
	if !review.Active {
		review.Reason = "stockpiles fit their contents"
	}
	return review
}

// stockpileSettingsEdit deletes a zone whose store retired and retargets one
// whose store's filter or priority differ from those last applied.
func stockpileSettingsEdit(s Store, z StockpileZone) (StockpileEdit, bool) {
	if s.Retired {
		return StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role,
			Explanation: fmt.Sprintf("stockpile %s (%s): role retired, delete; %d used cells rehome", z.ID, z.Role, z.Used())}, true
	}
	if s.Filter == z.Filter && s.Priority == z.Priority {
		return StockpileEdit{}, false
	}
	return StockpileEdit{Kind: StockpileRetarget, Zone: z.ID, Role: z.Role, Filter: s.Filter, Priority: s.Priority,
		Explanation: fmt.Sprintf("stockpile %s (%s): desired settings changed (priority %s -> %s), patch; %d used cells may rehome", z.ID, z.Role, z.Priority, s.Priority, z.Used())}, true
}

func stockpileNeighbours(c domain.Cell) []domain.Cell {
	return []domain.Cell{{X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z - 1}, {X: c.X, Z: c.Z + 1}, {X: c.X + 1, Z: c.Z}}
}

func stockpileSorted(cells []domain.Cell) []domain.Cell {
	out := append([]domain.Cell(nil), cells...)
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

func newStockpileOpen(r StockpileRequest) stockpileOpen {
	s := stockpileOpen{cells: make(map[domain.Cell]SiteCell, len(r.Cells)), protected: map[domain.Cell]bool{}, taken: reservedGround{}, bounds: r.Bounds}
	for _, c := range r.Cells {
		s.cells[c.Cell] = c
	}
	for _, c := range r.Protected {
		s.protected[c] = true
	}
	return s
}

func (s stockpileOpen) ok(p domain.Cell) bool {
	c, ok := s.cells[p]
	if !ok || s.protected[p] || s.taken[p] || s.only != nil && !s.only[p] || p.X < 0 || p.Z < 0 || p.X >= s.bounds.Width || p.Z >= s.bounds.Height {
		return false
	}
	walkable, wk := c.Walkable.Value()
	occupied, ok := c.Occupied.Value()
	zone, zk := c.Zone.Value()
	// Native refuses a zone on ground holding things (the crash-site supplies),
	// so a site there is retried every pass forever (#1581).
	empty, ek := c.StorageEmpty.Value()
	return wk && walkable && ok && !occupied && zk && !zone && (!ek || empty)
}

// stockpileOpen indexes the cells a new zone may take: observed, walkable,
// unoccupied, unzoned, unprotected, not taken by another edit this cycle and
// inside the map.
type stockpileOpen struct {
	cells     map[domain.Cell]SiteCell
	protected map[domain.Cell]bool
	// taken holds the ground earlier edits of this pass claimed.
	taken  reservedGround
	bounds Bounds
	// only, when set, limits the cells to a site's room.
	only map[domain.Cell]bool
}
