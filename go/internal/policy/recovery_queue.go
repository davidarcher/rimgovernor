package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The recovery queue (#2297, epic #2291): one ranking over every ruin,
// foreign obstruction and loot-capable stack on the map, whatever its
// distance from Home. Rank is tier, then distance: tier 1 a room obstruction,
// tier 2 anything yielding at least one currently short resource, tier 3 the
// rest; within a tier nearest first. Safety holds apply per thing; demand
// never holds one. Admission is one thing per development slot of the
// ClearHomeObstructions labor profile. The review journals the queue
// (Rounds.RecoveryQueue); the clearance planner executes its removals as a
// roof-first batch (PlanRecoveryBatch) and loot is released by FilterLootReach
// under the same hold vocabulary.

// RecoveryTier orders the queue: lower is first.
type RecoveryTier int

const (
	RecoveryRoomObstruction RecoveryTier = 1
	RecoveryShortYield      RecoveryTier = 2
	RecoveryRest            RecoveryTier = 3
)

// RecoveryStatus is what the queue does with a thing this review.
type RecoveryStatus string

const (
	// RecoveryAdmitted is the one thing the slot works now.
	RecoveryAdmitted RecoveryStatus = "admitted"
	// RecoveryQueued waits behind the admitted thing; the slot is granted.
	RecoveryQueued RecoveryStatus = "queued"
	// RecoveryDeferred waits on labor or storage and stays in the queue.
	RecoveryDeferred RecoveryStatus = "deferred"
	// RecoveryHeld is refused by a safety hold (RemoteHoldReason words).
	RecoveryHeld RecoveryStatus = "held"
)

// RecoveryReasonLaborExhausted defers every ready thing while the
// ClearHomeObstructions profile has no development slot.
const RecoveryReasonLaborExhausted = "labor_exhausted"

// RecoveryThing is one candidate. Hold is a thing-level refusal already in
// the RemoteHoldReason vocabulary (not_deconstructible, ancient_danger,
// casket, roof_support_risk, ...); RouteSafe is native's route verdict. A
// thing with no yield evidence has Evidenced false and is held
// "salvage_unknown".
type RecoveryThing struct {
	ID, Def         string
	Kind            RemoteWorkKind
	Cell            domain.Cell
	RoomObstruction bool
	Evidenced       bool
	Candidate       SupplyCandidate
	Hold            string
	RouteSafe       domain.Fact[bool]
}

// RecoveryRequest is the review's input. Short are the resources currently
// below demand (unknown demand leaves none short: nothing is held for it).
// Origins are the cells distance is measured from (the nearest wins);
// Center stands in when there are none. Slot is the development slot of the
// ClearHomeObstructions profile.
type RecoveryRequest struct {
	Things  []RecoveryThing
	Short   map[Resource]bool
	Origins []domain.Cell
	Center  domain.Cell
	Threat  domain.Fact[bool]
	Urgent  domain.Fact[bool]
	Slot    bool
}

// RecoveryEntry is one ranked thing as journaled.
type RecoveryEntry struct {
	ID       string
	Def      string `json:",omitempty"`
	Kind     RemoteWorkKind
	Tier     RecoveryTier
	Distance float64
	Status   RecoveryStatus
	Reason   string     `json:",omitempty"`
	Short    []Resource `json:",omitempty"`
}

// RecoveryQueue is the ranked result: entries in rank order, held ones in
// place, and the admitted thing ("" when none).
type RecoveryQueue struct {
	Entries  []RecoveryEntry
	Admitted string `json:",omitempty"`
}

func recoveryShort(c SupplyCandidate, short map[Resource]bool) []Resource {
	var out []Resource
	for _, y := range c.Yields {
		if n, known := y.StockCap.Value(); known && n > 0 && short[y.Good.Def] && !slices.Contains(out, y.Good.Def) {
			out = append(out, y.Good.Def)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

func recoveryDistance(t RecoveryThing, r RecoveryRequest) float64 {
	if d, known := t.Candidate.PathDistance.Value(); known && d >= 0 {
		return d
	}
	origins := r.Origins
	if len(origins) == 0 {
		origins = []domain.Cell{r.Center}
	}
	best := math.Inf(1)
	for _, o := range origins {
		best = min(best, math.Hypot(float64(t.Cell.X-o.X), float64(t.Cell.Z-o.Z)))
	}
	return best
}

// recoveryHold is the per-thing safety verdict: "" when the thing is free to
// work. Threat explains the hold first, then the thing's own refusal, missing
// evidence, route safety and urgent colony work.
func recoveryHold(t RecoveryThing, r RecoveryRequest) string {
	if threat, known := r.Threat.Value(); known && threat {
		return RemoteHoldThreat
	}
	switch {
	case t.Hold != "":
		return t.Hold
	case !t.Evidenced:
		return "salvage_unknown"
	}
	if safe, known := t.RouteSafe.Value(); !known || !safe {
		return RemoteHoldRouteUnsafe
	}
	if urgent, known := r.Urgent.Value(); known && urgent {
		return RemoteHoldUrgentWork
	}
	return ""
}

// recoveryStorageMissing: a thing with yields none of which has known storage
// headroom. It throttles; it never holds, and no storage is planned for it.
func recoveryStorageMissing(c SupplyCandidate) bool {
	if len(c.Yields) == 0 {
		return false
	}
	for _, y := range c.Yields {
		if n, known := y.Headroom.Value(); known && n > 0 {
			return false
		}
	}
	return true
}

// RankRecovery ranks and admits the request's things.
func RankRecovery(r RecoveryRequest) RecoveryQueue {
	entries := make([]RecoveryEntry, len(r.Things))
	ready := make([]bool, len(r.Things))
	throttled := make([]bool, len(r.Things))
	for i, t := range r.Things {
		short := recoveryShort(t.Candidate, r.Short)
		tier := RecoveryRest
		switch {
		case t.RoomObstruction:
			tier = RecoveryRoomObstruction
		case len(short) > 0:
			tier = RecoveryShortYield
		}
		e := RecoveryEntry{ID: t.ID, Def: t.Def, Kind: t.Kind, Tier: tier, Distance: recoveryDistance(t, r), Short: short}
		if reason := recoveryHold(t, r); reason != "" {
			e.Status, e.Reason = RecoveryHeld, reason
		} else {
			ready[i] = true
			throttled[i] = recoveryStorageMissing(t.Candidate)
		}
		entries[i] = e
	}
	order := make([]int, len(entries))
	for i := range order {
		order[i] = i
	}
	sort.SliceStable(order, func(a, b int) bool {
		x, y := entries[order[a]], entries[order[b]]
		if x.Tier != y.Tier {
			return x.Tier < y.Tier
		}
		if x.Distance != y.Distance {
			return x.Distance < y.Distance
		}
		return x.ID < y.ID
	})
	q := RecoveryQueue{Entries: make([]RecoveryEntry, 0, len(entries))}
	for _, i := range order {
		e := entries[i]
		switch {
		case !ready[i]:
		case throttled[i]:
			e.Status, e.Reason = RecoveryDeferred, RemoteHoldMissingStorage
		case !r.Slot:
			e.Status, e.Reason = RecoveryDeferred, RecoveryReasonLaborExhausted
		case q.Admitted == "":
			e.Status, q.Admitted = RecoveryAdmitted, e.ID
		default:
			e.Status = RecoveryQueued
		}
		q.Entries = append(q.Entries, e)
	}
	return q
}

// RecoveryClearanceThing prices one census row. Player buildings are the
// colony's own and are no recovery thing (ok false). The row's native
// verdicts (ancient, casket, not deconstructible) fold into Hold. Its native
// per-building roof verdict does not (#2301): the mirror's joint roof check
// supersedes it, so a ruin holding up a roof is queued and PlanRecoveryBatch
// takes the thin roofs down before it; what the mirror check still refuses
// after that is held roof_support_risk there. Native re-checks at admission.
func RecoveryClearanceThing(row ClearanceTarget, roomObstruction bool) (RecoveryThing, bool) {
	if row.Player {
		return RecoveryThing{}, false
	}
	own := row
	own.RoofBlocker = ""
	t := RecoveryThing{
		ID: row.EntityID, Def: row.DefName, Kind: RemoteSalvage, RoomObstruction: roomObstruction,
		Cell: domain.Cell{X: (row.Minimum.X + row.Maximum.X) / 2, Z: (row.Minimum.Z + row.Maximum.Z) / 2},
		Hold: RemoteHoldReason(RemoteSalvage, ClearanceHoldReason(own)), RouteSafe: domain.Unknown[bool](),
	}
	if row.Salvage != nil {
		t.Candidate, _ = SalvagePriced(row.EntityID, *row.Salvage)
		t.Evidenced = true
		t.RouteSafe = row.Salvage.Safe
	}
	return t, true
}

// RecoveryLootThing is a forbidden safe-to-haul stack's candidate. A stack a
// native spawner forbids on purpose is never recovery (ok false).
func RecoveryLootThing(row LootItem) (RecoveryThing, bool) {
	if row.SpawnForbidden || !row.Forbidden {
		return RecoveryThing{}, false
	}
	t := RecoveryThing{
		ID: row.Supply.Thing, Def: row.Supply.Definition, Kind: RemoteLoot, Cell: row.Supply.Cell, Evidenced: true,
		Candidate: lootCandidate(row), RouteSafe: domain.Unknown[bool](),
	}
	if row.SafetyKnown {
		t.RouteSafe = domain.Known(row.SafeToHaul)
	}
	return t, true
}

// RoomObstructionIDs is tier 1's source: the census ids of the foreign rows
// standing on the ground of a room the plan has not built yet (PlannedGround).
// The room owners' own claim of a ring ruin is not consulted.
func RoomObstructionIDs(rows []ClearanceTarget, ground []Rectangle) map[string]bool {
	out := map[string]bool{}
	for _, row := range rows {
		for _, g := range ground {
			if row.Minimum.X < g.X+g.Width && row.Maximum.X >= g.X && row.Minimum.Z < g.Z+g.Height && row.Maximum.Z >= g.Z {
				out[row.EntityID] = true
				break
			}
		}
	}
	return out
}

// recoveryShortResources are the resources below their effective stock target
// (the targets carry the derived resource needs): the short set tier 2 ranks
// by. An unknown demand leaves none short.
func recoveryShortResources(p RoundsPolicy, f RoundsFacts) (map[Resource]bool, error) {
	in := ResourceDemandInput{EconomicFloors: map[string]int64{}}
	for resource, count := range f.ResourceNeeds {
		if count > 0 {
			in.Targets = append(in.Targets, ResourceDemand{Key: ResourceKey{Def: resource}, Count: count, Priority: 2})
		}
	}
	if stock, known := f.Resources.Value(); known {
		rows := make([]ResourceQuantity, 0, len(stock))
		for _, amount := range stock {
			if amount.Count > 0 {
				rows = append(rows, ResourceQuantity{Key: ResourceKey{Def: amount.Resource}, Count: amount.Count})
			}
		}
		in.Stock = domain.Known(rows)
	}
	demand, err := BuildResourceDemand(in)
	if err != nil {
		return nil, err
	}
	short := map[Resource]bool{}
	if rows, known := demand.Value(); known {
		for _, d := range rows {
			if d.Count > 0 {
				short[d.Key.Def] = true
			}
		}
	}
	return short, nil
}

// ReviewRecoveryRequest assembles the review's request from the clearance
// census, the loot census and the review facts: demand from the effective
// targets, distance from the colony's facility cells (stockpile zones among
// them), the nearest of which is the origin. roomObstructions names the census
// ids a planned room waits on (tier 1). The caller sets Slot.
func ReviewRecoveryRequest(p RoundsPolicy, f RoundsFacts, rows []ClearanceTarget, roomObstructions map[string]bool) (RecoveryRequest, error) {
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: f.MapBounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage})
	if err != nil {
		return RecoveryRequest{}, err
	}
	short, err := recoveryShortResources(p, f)
	if err != nil {
		return RecoveryRequest{}, err
	}
	r := RecoveryRequest{Short: short, Urgent: UrgentWorkCompeting(f), Threat: domain.Unknown[bool]()}
	if hostiles, known := f.Hostiles.Value(); known {
		r.Threat = domain.Known(hostiles > 0)
	}
	if e, known := extent.Value(); known {
		var sx, sz, n int64
		for _, region := range e.Regions {
			for _, c := range region.Cells {
				sx, sz, n = sx+int64(c.Cell.X), sz+int64(c.Cell.Z), n+1
				if slices.ContainsFunc(c.Provenance, func(o ExtentProvenance) bool { return o.Origin == ExtentFacility }) {
					r.Origins = append(r.Origins, c.Cell)
				}
			}
		}
		if n > 0 {
			r.Center = domain.Cell{X: int32(sx / n), Z: int32(sz / n)}
		}
	}
	for _, row := range rows {
		if t, ok := RecoveryClearanceThing(row, roomObstructions[row.EntityID]); ok {
			r.Things = append(r.Things, t)
		}
	}
	if loot, known := f.EventLoot.Value(); known {
		for _, row := range loot {
			if t, ok := RecoveryLootThing(row); ok {
				r.Things = append(r.Things, t)
			}
		}
	}
	return r, nil
}
