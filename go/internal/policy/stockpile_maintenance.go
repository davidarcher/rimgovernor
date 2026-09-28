package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainStockpiles keeps the autopilot's stockpiles fitted to what they
// hold (#725), every review cycle: a zone near capacity grows onto the open
// cells beside it, one that sat mostly empty sheds its empty edge cells, a
// zone whose role's desired filter or priority changed is patched, a zone
// whose role's purpose is gone is deleted, a same-role fragment is
// deleted into its larger sibling, and a fixed role the colony has things
// for but no zone of is created (#724), as is a room-bound role whose room
// stands without one (#917, StockpileSite). It acts on the zones the colony created
// (store.OwnedZone), role-keyed; a role-less legacy claim is resized and
// merged but never retargeted or deleted. Edits are rate-limited by the haul
// jobs each would trigger, not by how rarely the routine acts. It is a
// Standard whose target is no outstanding work: no zone edit due (#1024).
const MaintainStockpiles GoalID = "MaintainStockpiles"

// stockpilePriority ranks MaintainStockpiles with the other upkeep goals.
const stockpilePriority = 3

// stockpileDeficit is the deficit a standing edit ranks with: small, like
// the tidy's, so it never outranks real shortfalls.
const stockpileDeficit = 0.1

const (
	// StockpileGrowFill is the used-cell fraction at which a zone grows.
	StockpileGrowFill = 0.85
	// StockpileShrinkFill is the used-cell fraction at or under which a
	// zone counts as mostly empty.
	StockpileShrinkFill = 0.25
	// StockpileShrinkAfter is how long a zone must sit mostly empty
	// before it shrinks: one game day.
	StockpileShrinkAfter domain.Tick = 60000
	// StockpileMinCells is the floor a shrink never goes under.
	StockpileMinCells = 4
	// StockpileHaulsPerColonist bounds the haul jobs one review cycle's
	// edits may trigger, per colonist.
	StockpileHaulsPerColonist = 8
	stockpileEditCap          = 256
)

// StockpileRoleState is the desired state of one role, published by the
// planner that owns the role (#721 shelves, #723 siting, #724 filters and
// dumps): the filter and priority its zones should carry, or Retired once
// the role's purpose is gone (the bench demolished, the dump unneeded).
type StockpileRoleState struct {
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
	Retired  bool
	// Fixed zones keep the size they were sited at: never grown, shrunk
	// or merged (#917: a Critical shelf that grew would pull the whole
	// stock out of storage).
	Fixed bool
}

// StockpileRoles resolves a role key to its desired state; false leaves the
// zone's settings alone (no owner published the role).
type StockpileRoles func(role string) (StockpileRoleState, bool)

// StockpileZone is one owned stockpile as the census holds it now: its
// cells (the planning cells naming it), the cells holding things (Stored:
// the census' storage-empty flag false), the settings the autopilot last
// applied and since when it has sat mostly empty (zero while it has not).
type StockpileZone struct {
	ID       string
	Role     string
	Cells    []domain.Cell
	Stored   []domain.Cell
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
	LowSince domain.Tick
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

// Low reports a zone mostly empty now.
func (z StockpileZone) Low() bool { return len(z.Cells) > 0 && z.Fill() <= StockpileShrinkFill }

type StockpileEditKind string

const (
	StockpileDelete   StockpileEditKind = "delete"
	StockpileRetarget StockpileEditKind = "retarget"
	StockpileGrow     StockpileEditKind = "grow"
	StockpileMerge    StockpileEditKind = "merge"
	StockpileShrink   StockpileEditKind = "shrink"
)

// StockpileEdit is one proposed edit of one zone. Cells are the cells a
// grow adds or a shrink removes; Filter/Priority/Role a retarget's
// settings; Hauls the haul jobs the edit is estimated to trigger. A merge
// deletes the fragment Zone into Into.
type StockpileEdit struct {
	Kind        StockpileEditKind
	Zone        string
	Role        string
	Into        string `json:",omitempty"`
	Cells       []domain.Cell
	Filter      domain.StockpileFilter
	Priority    domain.StockpilePriority
	Hauls       int
	Explanation string
}

// StockpileRequest is the review's input.
type StockpileRequest struct {
	Tick      domain.Tick
	Zones     []StockpileZone
	Roles     StockpileRoles
	Cells     []SiteCell
	Bounds    Bounds
	Protected []domain.Cell
	// Colonists sizes the haul budget; unknown holds every edit.
	Colonists domain.Fact[int64]
	// Needs counts, per fixed role (#724: apparel, weapons, dumps), the
	// things waiting for it; a role with things and no zone is created.
	Needs map[string]int
	// Rooms sites the dumps clear of living rooms; unknown creates none.
	Rooms domain.Fact[[]Room]
	// Anchor sites the gear stockpiles when no general store stands.
	Anchor domain.Cell
	// Prisons are the planned prisons' cells (#1081): a weapons stockpile
	// never stands near one.
	Prisons []domain.Cell
	// Shelves are the built shelves inside the zones (#721): each carries
	// its zone's desired settings, patched until it does.
	Shelves []StockpileShelf
	// Sited are the room-bound roles (#917) created while absent.
	Sited []StockpileSite
	// Kitchen, when set, is the cooking spot the opening food stockpile
	// sits beside while no roofed floor is free.
	Kitchen *domain.Cell
	// Opening stands the opening stockpiles (general store, food, corpse
	// dump) while no owned zone of their kind stands; the runtime always
	// sets it.
	Opening bool
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
// zone's desired ones: the zone role's published state, else the zone's
// own settings. A shelf of a zone that is gone or retiring waits.
func stockpileShelfEdits(roles StockpileRoles, zones []StockpileZone, shelves []StockpileShelf) []StockpileEdit {
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
		if z.Role != "" && roles != nil {
			if want, published := roles(z.Role); published {
				if want.Retired {
					continue
				}
				filter, priority = want.Filter, want.Priority
			}
		}
		if s.Patched && s.Filter == filter && s.Priority == priority {
			continue
		}
		hauls := s.Cells * ShelfItemsPerCell
		out = append(out, StockpileEdit{Kind: StockpileShelfPatch, Zone: s.Building, Role: ShelfRole(s.Building), Filter: filter, Priority: priority, Hauls: hauls,
			Explanation: fmt.Sprintf("shelf %s in stockpile %s (%s): configure like its zone (priority %s); up to %d stacks may rehome", s.Building, z.ID, z.Role, priority, hauls)})
	}
	return out
}

// StockpileReview is the outcome: the edits this cycle admits within the
// haul budget, how many more stood over budget, and why none stands.
type StockpileReview struct {
	Known    bool
	Active   bool
	Edits    []StockpileEdit `json:",omitempty"`
	Deferred int
	Budget   int
	Reason   string
}

// PlanStockpileMaintenance proposes this cycle's edits, one per zone, in
// urgency order (delete, retarget and shelf patch, create, grow, merge,
// shrink; ties by
// zone id),
// admitting each while the haul jobs it triggers fit the cycle's budget
// (the first edit always fits, so an edit larger than the budget still
// lands, alone). Deterministic over its input.
func PlanStockpileMaintenance(r StockpileRequest) StockpileReview {
	colonists, known := r.Colonists.Value()
	if !known {
		return StockpileReview{Reason: "colonists unknown"}
	}
	budget := int(max(colonists, 1)) * StockpileHaulsPerColonist
	review := StockpileReview{Known: true, Budget: budget}
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
	for _, e := range stockpileSiteMoves(r) {
		take(e, true)
	}
	for _, z := range zones {
		take(stockpileSettingsEdit(r.Roles, z))
	}
	for _, e := range stockpileShelfEdits(r.Roles, zones, r.Shelves) {
		take(e, true)
	}
	opening := stockpileOpeningEdits(r, open)
	candidates = append(candidates, opening...)
	for _, e := range stockpileCreateEdits(r, open) {
		if !openingRole(opening, e.Role) {
			candidates = append(candidates, e)
		}
	}
	candidates = append(candidates, stockpileSiteEdits(r, open)...)
	for _, e := range stockpileSiteShrinks(r) {
		take(e, true)
	}
	for _, z := range zones {
		if state, ok := stockpileRoleState(r.Roles, z.Role); ok && state.Fixed {
			touched[z.ID] = true
		}
	}
	for _, z := range zones {
		if !touched[z.ID] {
			take(stockpileGrowEdit(open, z))
		}
	}
	for _, e := range stockpileMergeEdits(zones, touched) {
		if take(e, true) {
			touched[e.Into] = true
		}
	}
	for _, z := range zones {
		if !touched[z.ID] {
			take(stockpileShrinkEdit(r.Tick, z))
		}
	}
	rank := map[StockpileEditKind]int{StockpileDelete: 0, StockpileRetarget: 1, StockpileShelfPatch: 1, StockpileCreate: 2, StockpileGrow: 3, StockpileMerge: 4, StockpileShrink: 5}
	sort.SliceStable(candidates, func(i, j int) bool { return rank[candidates[i].Kind] < rank[candidates[j].Kind] })
	spent := 0
	for _, e := range candidates {
		if len(review.Edits) > 0 && spent+e.Hauls > budget {
			review.Deferred++
			continue
		}
		spent += e.Hauls
		review.Edits = append(review.Edits, e)
	}
	review.Active = len(review.Edits) > 0
	if !review.Active {
		review.Reason = "stockpiles fit their contents"
	}
	return review
}

// stockpileSettingsEdit deletes a zone whose role retired and retargets one
// whose role's desired settings differ from those last applied.
func stockpileSettingsEdit(roles StockpileRoles, z StockpileZone) (StockpileEdit, bool) {
	if z.Role == "" || roles == nil {
		return StockpileEdit{}, false
	}
	want, ok := roles(z.Role)
	if !ok {
		return StockpileEdit{}, false
	}
	if want.Retired {
		return StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role, Hauls: z.Used(),
			Explanation: fmt.Sprintf("stockpile %s (%s): role retired, delete; %d used cells rehome", z.ID, z.Role, z.Used())}, true
	}
	if want.Filter == z.Filter && want.Priority == z.Priority {
		return StockpileEdit{}, false
	}
	return StockpileEdit{Kind: StockpileRetarget, Zone: z.ID, Role: z.Role, Filter: want.Filter, Priority: want.Priority, Hauls: z.Used(),
		Explanation: fmt.Sprintf("stockpile %s (%s): desired settings changed (priority %s -> %s), patch; %d used cells may rehome", z.ID, z.Role, z.Priority, want.Priority, z.Used())}, true
}

// stockpileGrowEdit grows a zone at or over StockpileGrowFill by a quarter
// of its size (at least StockpileMinCells, twice that when full: the
// overflow waits on the floor) onto the open cells nearest it, breadth
// first from its edge so the zone stays contiguous.
func stockpileGrowEdit(open stockpileOpen, z StockpileZone) (StockpileEdit, bool) {
	if len(z.Cells) == 0 || z.Fill() < StockpileGrowFill {
		return StockpileEdit{}, false
	}
	want := max(len(z.Cells)/4, StockpileMinCells)
	if z.Used() >= len(z.Cells) {
		want *= 2
	}
	want = min(want, stockpileEditCap)
	own := map[domain.Cell]bool{}
	for _, c := range z.Cells {
		own[c] = true
	}
	frontier := stockpileSorted(z.Cells)
	seen := map[domain.Cell]bool{}
	var added []domain.Cell
	for len(frontier) > 0 && len(added) < want {
		var next []domain.Cell
		for _, c := range frontier {
			for _, n := range stockpileNeighbours(c) {
				if own[n] || seen[n] || !open.ok(n) {
					continue
				}
				seen[n] = true
				if len(added) < want {
					added = append(added, n)
					next = append(next, n)
				}
			}
		}
		frontier = stockpileSorted(next)
	}
	if len(added) == 0 {
		return StockpileEdit{}, false
	}
	for _, c := range added {
		open.taken[c] = true
	}
	return StockpileEdit{Kind: StockpileGrow, Zone: z.ID, Role: z.Role, Cells: stockpileSorted(added), Hauls: len(added),
		Explanation: fmt.Sprintf("stockpile %s (%s): %d/%d cells used, grow by %d", z.ID, z.Role, z.Used(), len(z.Cells), len(added))}, true
}

// stockpileMergeEdits deletes a same-role fragment (at most half its
// sibling's size) whose used cells fit in the sibling's free cells; the
// sibling grows back into the space on later cycles. Role-less zones merge
// only with each other.
func stockpileMergeEdits(zones []StockpileZone, edited map[string]bool) []StockpileEdit {
	touched := map[string]bool{}
	for id := range edited {
		touched[id] = true
	}
	var out []StockpileEdit
	for _, frag := range zones {
		if touched[frag.ID] || len(frag.Cells) == 0 {
			continue
		}
		var into *StockpileZone
		for i := range zones {
			other := &zones[i]
			if other.ID == frag.ID || other.Role != frag.Role || touched[other.ID] || len(other.Cells) < 2*len(frag.Cells) || len(other.Cells)-other.Used() < frag.Used() {
				continue
			}
			if into == nil || len(other.Cells) > len(into.Cells) {
				into = other
			}
		}
		if into == nil {
			continue
		}
		touched[frag.ID], touched[into.ID] = true, true
		out = append(out, StockpileEdit{Kind: StockpileMerge, Zone: frag.ID, Into: into.ID, Role: frag.Role, Hauls: frag.Used(),
			Explanation: fmt.Sprintf("stockpile %s (%s, %d cells) is a fragment of %s (%d cells, %d free): delete, %d used cells rehome", frag.ID, frag.Role, len(frag.Cells), into.ID, len(into.Cells), len(into.Cells)-into.Used(), frag.Used())})
	}
	return out
}

// stockpileShrinkEdit sheds the empty cells of a zone that sat mostly empty
// for StockpileShrinkAfter, down to twice its used cells (at least
// StockpileMinCells), removing the empty cells farthest from its middle
// first and never one whose removal splits the zone. Removing empty cells
// triggers no hauling.
func stockpileShrinkEdit(tick domain.Tick, z StockpileZone) (StockpileEdit, bool) {
	if !z.Low() || z.LowSince <= 0 || tick-z.LowSince < StockpileShrinkAfter {
		return StockpileEdit{}, false
	}
	keep := max(2*z.Used(), StockpileMinCells)
	if len(z.Cells) <= keep {
		return StockpileEdit{}, false
	}
	cells := stockpileSorted(z.Cells)
	var cx, cz int64
	for _, c := range cells {
		cx += int64(c.X)
		cz += int64(c.Z)
	}
	mid := domain.Cell{X: int32(cx / int64(len(cells))), Z: int32(cz / int64(len(cells)))}
	distance := func(c domain.Cell) int32 { return absInt32(c.X-mid.X) + absInt32(c.Z-mid.Z) }
	order := append([]domain.Cell(nil), cells...)
	sort.SliceStable(order, func(i, j int) bool { return distance(order[i]) > distance(order[j]) })
	remaining := map[domain.Cell]bool{}
	for _, c := range cells {
		remaining[c] = true
	}
	stored := map[domain.Cell]bool{}
	for _, c := range z.Stored {
		stored[c] = true
	}
	var removed []domain.Cell
	for _, c := range order {
		if len(remaining) <= keep || len(removed) >= stockpileEditCap {
			break
		}
		if stored[c] {
			continue
		}
		delete(remaining, c)
		if !stockpileContiguous(remaining) {
			remaining[c] = true
			continue
		}
		removed = append(removed, c)
	}
	if len(removed) == 0 {
		return StockpileEdit{}, false
	}
	return StockpileEdit{Kind: StockpileShrink, Zone: z.ID, Role: z.Role, Cells: stockpileSorted(removed),
		Explanation: fmt.Sprintf("stockpile %s (%s): %d/%d cells used since tick %d, shrink by %d empty cells", z.ID, z.Role, z.Used(), len(z.Cells), z.LowSince, len(removed))}, true
}

func stockpileContiguous(cells map[domain.Cell]bool) bool {
	if len(cells) == 0 {
		return true
	}
	var start domain.Cell
	first := true
	for c := range cells {
		if first || cellLess(c, start) {
			start, first = c, false
		}
	}
	reached := map[domain.Cell]bool{start: true}
	queue := []domain.Cell{start}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range stockpileNeighbours(c) {
			if cells[n] && !reached[n] {
				reached[n] = true
				queue = append(queue, n)
			}
		}
	}
	return len(reached) == len(cells)
}

func stockpileNeighbours(c domain.Cell) []domain.Cell {
	return []domain.Cell{{X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z - 1}, {X: c.X, Z: c.Z + 1}, {X: c.X + 1, Z: c.Z}}
}

func stockpileSorted(cells []domain.Cell) []domain.Cell {
	out := append([]domain.Cell(nil), cells...)
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

// stockpileOpen indexes the cells a grow may take: observed, walkable,
// unoccupied, unzoned, unprotected, not taken by another grow this cycle
// and inside the map.
type stockpileOpen struct {
	cells     map[domain.Cell]SiteCell
	protected map[domain.Cell]bool
	// taken holds the cells an earlier grow of this cycle added.
	taken  map[domain.Cell]bool
	bounds Bounds
}

func newStockpileOpen(r StockpileRequest) stockpileOpen {
	s := stockpileOpen{cells: make(map[domain.Cell]SiteCell, len(r.Cells)), protected: map[domain.Cell]bool{}, taken: map[domain.Cell]bool{}, bounds: r.Bounds}
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
	if !ok || s.protected[p] || s.taken[p] || p.X < 0 || p.Z < 0 || p.X >= s.bounds.Width || p.Z >= s.bounds.Height {
		return false
	}
	walkable, wk := c.Walkable.Value()
	occupied, ok := c.Occupied.Value()
	zone, zk := c.Zone.Value()
	return wk && walkable && ok && !occupied && zk && !zone
}
