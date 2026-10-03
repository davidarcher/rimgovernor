package policy

import (
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockpileCreate places a missing fixed-role zone (#724).
const StockpileCreate StockpileEditKind = "create"

// stockpileCreateEdits proposes one zone for each fixed role
// (domain.GearAndDumpRoles) the colony has things for (Needs) and no zone
// of: the gear stockpiles on the free indoor roofed 2x2 patch cheapest to
// haul to from the general store (nearest the anchor without one), the
// dumps on the nearest free outdoor 2x2 patch clear of living rooms (never
// while the room census is unknown). The role's registered state, when
// published, supplies its settings; a retired role is never created. Its
// hauls are the things waiting for it. Cells taken are marked in open.
func stockpileCreateEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	if len(r.Needs) == 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	have := map[string]bool{}
	var store []domain.Cell
	for _, z := range r.Zones {
		have[z.Role] = true
		if z.Role == domain.GeneralRole {
			store = append(store, z.Cells...)
		}
	}
	store = stockpileSorted(store)
	anchor := r.Anchor
	if len(store) > 0 {
		anchor = store[0]
	}
	var out []StockpileEdit
	for _, spec := range domain.GearAndDumpRoles() {
		count := r.Needs[spec.Role]
		if count <= 0 || have[spec.Role] {
			continue
		}
		filter, priority := spec.Filter, spec.Priority
		if r.Roles != nil {
			if state, ok := r.Roles(spec.Role); ok {
				if state.Retired {
					continue
				}
				filter, priority = state.Filter, state.Priority
			}
		}
		var sites []Rectangle
		var err error
		dump := spec.Priority == domain.LowPriority
		if dump {
			rooms, known := r.Rooms.Value()
			if !known {
				continue
			}
			sites, err = OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: anchor, Cells: r.Cells, Rooms: rooms, Protected: r.Protected, Width: 2, Height: 2})
		} else {
			sites, err = stockpileGearSites(r, anchor, store, r.GearRooms[spec.Role])
		}
		if err != nil {
			continue
		}
		for _, site := range sites {
			cells := rectCells(site)
			free := true
			for _, c := range cells {
				free = free && open.ok(c)
			}
			if !free || spec.Role == domain.WeaponsRole && nearPrison(cells, r.Prisons) {
				continue
			}
			for _, c := range cells {
				open.taken[c] = true
			}
			where := "indoors near the store"
			if dump {
				where = "outdoors clear of living rooms"
			}
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: spec.Role, Cells: stockpileSorted(cells), Filter: filter, Priority: priority, Hauls: count,
				Explanation: fmt.Sprintf("stockpile role %s: %d things and no zone, create %dx%d at (%d,%d) %s", spec.Role, count, site.Width, site.Height, site.X, site.Z, where)})
			break
		}
	}
	return out
}

// StockpileSite is a role whose zone belongs in one room (#917): the meal
// stockpile where the meals keep (#936), the raw-food stock in the freezer.
// Its absence is a MaintainStockpiles deficit of its own: while no zone of
// the role's prefix has a cell in Room, the first Candidate whose cells are
// all open is created. A zone of the prefix with no cell in Room is deleted
// (the site moved), and one larger than Size (when set) shrinks to it. The
// role key carries the census room ID, which RimWorld renumbers, so a zone
// is matched by where it stands, never by the key.
type StockpileSite struct {
	Role       string
	Room       []domain.Cell
	Filter     domain.StockpileFilter
	Priority   domain.StockpilePriority
	Size       int
	Candidates [][]domain.Cell
	// Remainder makes the site a catch-all: Candidates[0] is the pool of cells it
	// may take, and the zone created is every pool cell still open once the
	// sites ahead of it have taken theirs.
	Remainder bool
	// Supersedes is a role prefix whose zones are deleted once the site is
	// served: the stand-in it replaces.
	Supersedes string
	// Keyed makes Role a stable identity (a bench ID, unlike a census room
	// ID that RimWorld renumbers): zones are matched to the site by the
	// whole role key, so several sites may share a prefix.
	Keyed bool
}

// serves reports whether a zone's role belongs to the site: the whole key
// for a keyed site, else the prefix.
func (s StockpileSite) serves(role string) bool {
	if role == "" {
		return false
	}
	if s.Keyed {
		return role == s.Role
	}
	return stockpileRolePrefix(role) == stockpileRolePrefix(s.Role)
}

// stockpileSiteMoves deletes the zones of a site's prefix standing outside
// its room.
func stockpileSiteMoves(r StockpileRequest) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		room := cellSet(site.Room)
		for _, z := range r.Zones {
			if !site.serves(z.Role) || stockpileTouches(z.Cells, room) {
				continue
			}
			out = append(out, StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role, Hauls: z.Used(),
				Explanation: fmt.Sprintf("stockpile %s (%s): its site moved to %s, delete; %d used cells rehome", z.ID, z.Role, site.Role, z.Used())})
		}
	}
	return out
}

// stockpileSiteShrinks trims a zone serving a site down to the site's Size,
// keeping stored cells, then those nearest the site's first candidate.
func stockpileSiteShrinks(r StockpileRequest) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		if site.Size <= 0 {
			continue
		}
		room := cellSet(site.Room)
		anchor := domain.Cell{}
		if len(site.Candidates) > 0 && len(site.Candidates[0]) > 0 {
			anchor = site.Candidates[0][0]
		}
		for _, z := range r.Zones {
			if !site.serves(z.Role) || !stockpileTouches(z.Cells, room) || len(z.Cells) <= site.Size {
				continue
			}
			stored := cellSet(z.Stored)
			order := stockpileSorted(z.Cells)
			distance := func(c domain.Cell) int32 { return absInt32(c.X-anchor.X) + absInt32(c.Z-anchor.Z) }
			sort.SliceStable(order, func(i, j int) bool {
				if stored[order[i]] != stored[order[j]] {
					return stored[order[i]]
				}
				return distance(order[i]) < distance(order[j])
			})
			removed := order[site.Size:]
			hauls := 0
			for _, c := range removed {
				if stored[c] {
					hauls++
				}
			}
			out = append(out, StockpileEdit{Kind: StockpileShrink, Zone: z.ID, Role: z.Role, Cells: stockpileSorted(removed), Hauls: hauls,
				Explanation: fmt.Sprintf("stockpile %s (%s): %d cells over its site's %d, shrink", z.ID, z.Role, len(z.Cells), site.Size)})
		}
	}
	return out
}

func stockpileRolePrefix(role string) string {
	prefix, _, _ := strings.Cut(role, ":")
	return prefix
}

func cellSet(cells []domain.Cell) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		out[c] = true
	}
	return out
}

func stockpileTouches(cells []domain.Cell, room map[domain.Cell]bool) bool {
	for _, c := range cells {
		if room[c] {
			return true
		}
	}
	return false
}

// stockpileSiteEdits proposes a zone for every site no zone serves yet.
// Cells taken are marked in open.
func stockpileSiteEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		if stockpileSiteServed(r.Zones, site) {
			continue
		}
		if site.Remainder {
			var cells []domain.Cell
			if len(site.Candidates) > 0 {
				for _, c := range site.Candidates[0] {
					if open.ok(c) {
						cells = append(cells, c)
					}
				}
			}
			if len(cells) == 0 {
				continue
			}
			for _, c := range cells {
				open.taken[c] = true
			}
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: stockpileSorted(cells), Filter: site.Filter, Priority: site.Priority, Hauls: len(cells),
				Explanation: fmt.Sprintf("stockpile role %s: no zone in its room, create the remaining %d cells", site.Role, len(cells))})
			continue
		}
		for _, cells := range site.Candidates {
			free := len(cells) > 0
			for _, c := range cells {
				free = free && open.ok(c)
			}
			if !free {
				continue
			}
			for _, c := range cells {
				open.taken[c] = true
			}
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: stockpileSorted(cells), Filter: site.Filter, Priority: site.Priority, Hauls: len(cells),
				Explanation: fmt.Sprintf("stockpile role %s: no zone in its room, create %d cells at (%d,%d)", site.Role, len(cells), cells[0].X, cells[0].Z)})
			break
		}
	}
	return out
}

func stockpileSiteServed(zones []StockpileZone, site StockpileSite) bool {
	room := cellSet(site.Room)
	for _, z := range zones {
		if site.serves(z.Role) && stockpileTouches(z.Cells, room) {
			return true
		}
	}
	return false
}

// stockpileRoleState is a role's published state; false for a role-less
// zone or one no owner publishes.
func stockpileRoleState(roles StockpileRoles, role string) (StockpileRoleState, bool) {
	if role == "" || roles == nil {
		return StockpileRoleState{}, false
	}
	return roles(role)
}

// stockpileGearSites are the free indoor roofed 2x2 patches nearest anchor,
// cheapest walking distance to the general store first.
func stockpileGearSites(r StockpileRequest, anchor domain.Cell, store, room []domain.Cell) ([]Rectangle, error) {
	// The role's own standing room takes the zone before the walk to the
	// general store decides; no free patch there falls back to anywhere indoors.
	cells := r.Cells
	if inRoom := cellSet(room); len(inRoom) > 0 {
		cells = nil
		for _, c := range r.Cells {
			if inRoom[c.Cell] {
				cells = append(cells, c)
			}
		}
	}
	covered, err := CoveredStorageSites(CoveredStorageRequest{Bounds: r.Bounds, Anchor: anchor, Cells: cells, Protected: r.Protected})
	if err != nil {
		return nil, err
	}
	if len(cells) < len(r.Cells) && len(covered) == 0 {
		return stockpileGearSites(r, anchor, store, nil)
	}
	indoors := map[domain.Cell]bool{}
	for _, c := range r.Cells {
		indoors[c.Cell] = positive(c.Indoors)
	}
	var sites []Rectangle
	for _, site := range covered {
		ok := true
		for _, c := range rectCells(site) {
			ok = ok && indoors[c]
		}
		if ok {
			sites = append(sites, site)
		}
	}
	if len(store) == 0 || len(sites) < 2 {
		return sites, nil
	}
	costs, err := HaulCosts(r.Cells, []HaulConsumer{{Cells: store[:min(len(store), 256)], Weight: 1}})
	if err != nil {
		return nil, err
	}
	return RankSitesByHaul(sites, costs), nil
}

// stockpileGearMoves deletes a gear zone standing outside its role's room
// (the storage room or barracks) once a free patch in that room can take
// its replacement, which the create step then raises there; the things
// stored in it rehome like a moved meal site's.
func stockpileGearMoves(r StockpileRequest) []StockpileEdit {
	var out []StockpileEdit
	var store []domain.Cell
	for _, z := range r.Zones {
		if z.Role == domain.GeneralRole {
			store = append(store, z.Cells...)
		}
	}
	store = stockpileSorted(store)
	anchor := r.Anchor
	if len(store) > 0 {
		anchor = store[0]
	}
	for _, spec := range domain.GearAndDumpRoles() {
		room := r.GearRooms[spec.Role]
		if spec.Priority == domain.LowPriority || len(room) == 0 {
			continue
		}
		inRoom := cellSet(room)
		sites, err := stockpileGearSites(r, anchor, store, room)
		fits := false
		for _, site := range sites {
			all := true
			for _, c := range rectCells(site) {
				all = all && inRoom[c]
			}
			fits = fits || all
		}
		if err != nil || !fits {
			continue
		}
		for _, z := range r.Zones {
			if z.Role != spec.Role || stockpileTouches(z.Cells, inRoom) {
				continue
			}
			out = append(out, StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role, Hauls: z.Used(),
				Explanation: fmt.Sprintf("stockpile %s (%s): outside its room, delete; %d used cells rehome", z.ID, z.Role, z.Used())})
		}
	}
	return out
}
