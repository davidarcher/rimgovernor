package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A medicine:<roomID> stockpile keeps its settings while the room hosts a
// hospital and retires once the room census no longer shows it as one
// (the room gone, or its role changed); an unknown census or role holds it.
func init() {
	RegisterStockpileRole("medicine", func(in StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		_, id, _ := strings.Cut(role, ":")
		rooms, known := in.Projection.Rooms.Value()
		filter, err := medicineFilter()
		facility, ferr := policy.Facility(policy.RoomRoleHospital)
		if !known || id == "" || err != nil || ferr != nil {
			return policy.StockpileRoleState{}, false
		}
		state := policy.StockpileRoleState{Filter: filter, Priority: domain.ImportantPriority}
		room, found := rooms.Room(id)
		if !found {
			state.Retired = true
			return state, true
		}
		kind, known := room.Role.Value()
		if !known {
			return policy.StockpileRoleState{}, false
		}
		state.Retired = !facility.Hosts(kind)
		return state, true
	})
}

// medicineStorageAttempts bounds the medicine stockpile per hospital room:
// another attempt only after every earlier one ended failed.
const medicineStorageAttempts = 3

// medicineStorageSite is the hospital room whose medical beds a medicine
// stockpile serves and its candidate 2x2 patches, nearest the beds by
// traffic-weighted walking distance first (#723).
type medicineStorageSite struct {
	Room  string
	Sites [][]domain.Cell
}

// medicineStorageSites picks the hospital-hosting room with the most
// medical beds (room id order on a tie) and ranks free roofed 2x2 patches
// inside it by walking distance to those beds, each bed one unit of
// traffic. Empty when no hosted room holds a medical bed or nothing fits.
func medicineStorageSites(rooms policy.RoomObservation, sleeping policy.SleepingObservation, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (medicineStorageSite, error) {
	facility, err := policy.Facility(policy.RoomRoleHospital)
	if err != nil {
		return medicineStorageSite{}, err
	}
	beds := map[string][]domain.Cell{}
	for _, bed := range sleeping.Beds {
		medical, mk := bed.Medical.Value()
		room, rk := bed.Room.Value()
		if mk && rk && medical {
			beds[room] = append(beds[room], bed.Cell)
		}
	}
	var hosted []policy.Room
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if known && facility.Hosts(role) && len(beds[room.ID]) > 0 && len(room.Cells) > 0 {
			hosted = append(hosted, room)
		}
	}
	if len(hosted) == 0 {
		return medicineStorageSite{}, nil
	}
	sort.Slice(hosted, func(i, j int) bool {
		if a, b := len(beds[hosted[i].ID]), len(beds[hosted[j].ID]); a != b {
			return a > b
		}
		return hosted[i].ID < hosted[j].ID
	})
	room := hosted[0]
	inside := map[domain.Cell]bool{}
	for _, c := range room.Cells {
		inside[c] = true
	}
	var scoped []policy.SiteCell
	for _, c := range cells {
		if inside[c.Cell] {
			scoped = append(scoped, c)
		}
	}
	bedCells := beds[room.ID]
	if len(scoped) == 0 {
		return medicineStorageSite{Room: room.ID}, nil
	}
	sites, err := policy.CoveredStorageSites(policy.CoveredStorageRequest{Bounds: bounds, Anchor: bedCells[0], Cells: scoped, Protected: protected})
	if err != nil {
		return medicineStorageSite{}, err
	}
	consumers := make([]policy.HaulConsumer, 0, len(bedCells))
	for _, c := range bedCells {
		consumers = append(consumers, policy.HaulConsumer{Cells: []domain.Cell{c}, Weight: 1})
	}
	if len(consumers) > 64 {
		consumers = consumers[:64]
	}
	costs, err := policy.HaulCosts(cells, consumers)
	if err != nil {
		return medicineStorageSite{}, err
	}
	out := medicineStorageSite{Room: room.ID}
	for _, site := range policy.RankSitesByHaul(sites, costs) {
		var block []domain.Cell
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				block = append(block, domain.Cell{X: x, Z: z})
			}
		}
		out.Sites = append(out.Sites, block)
	}
	return out, nil
}

// medicineFilter stores every medicine and nothing else.
func medicineFilter() (domain.StockpileFilter, error) {
	return domain.NewStockpileFilter(domain.BaseNothing, []domain.FilterSelector{domain.CategoryDef("Medicine")}, nil)
}

// medicineStorage places one medicine stockpile beside the hospital's
// medical beds once the ward stands (#723), a MaintainMedicalCare method
// keyed by the room: role medicine:<roomID>. A zero reason means nothing to
// do this step.
func (r *RoutineHospitalPlanner) medicineStorage(call, epoch context.Context, state ControlState, goal store.GoalState, reading observation.RoutineReading, started time.Time) (RoutineBuildingReason, error) {
	p := r.reviewer.player
	native, ok := r.native.(zonePreviewer)
	if !ok {
		return "", nil
	}
	facts := reading.Projection
	token, tk := facts.ZoneMapToken.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !tk || !rk || !sk {
		return "", nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return "", err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	site, err := medicineStorageSites(rooms, sleeping, facts.Bounds, facts.Cells, layoutProtected(facts, protected))
	if err != nil || site.Room == "" {
		return "", err
	}
	role := "medicine:" + site.Room
	var id domain.PlanID
	var method domain.MethodID
	for attempt := 0; attempt < medicineStorageAttempts; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d", goal.Goal.ID, role, attempt)))
		candidate := domain.MethodID(fmt.Sprintf("medicine-storage-%x-%d", digest[:8], attempt))
		bound, err := p.journal.LatestMethodPlan(call, goal.Goal.ID, candidate)
		if errors.Is(err, store.ErrNotFound) {
			id, method = domain.MintPlanID("routine-medicine-storage"), candidate
			break
		}
		if err != nil {
			return "", err
		}
		plan, err := p.journal.LoadPlan(call, bound)
		if err != nil {
			return "", err
		}
		if !ingredientStorageFailed(plan.Progress) {
			return "", nil
		}
	}
	if id == "" || len(site.Sites) == 0 {
		return "", nil
	}
	filter, err := medicineFilter()
	if err != nil {
		return "", err
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	for i, cells := range site.Sites {
		if i >= maxSecureSuppliesZoneSites {
			break
		}
		value, err := domain.NewFilteredStockpileZone(filter, domain.ImportantPriority, cells)
		if err != nil {
			return "", err
		}
		if value, err = value.WithRole(role); err != nil {
			return "", err
		}
		reply, _, err := native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
		var refused *bridge.NativeFailure
		if errors.As(err, &refused) {
			continue
		}
		if err != nil {
			return "", err
		}
		v := reply.GetEvaluated()
		if v == nil || !v.GetAccepted() {
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != facts.Identity.Tick {
			return "", ErrControl
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return "", err
		}
		preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: facts.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return "", err
		}
		if err = p.current(call, epoch); err != nil {
			return "", err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", ErrControl
		}
		decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
		if err != nil {
			return "", err
		}
		if !decision.Admitted {
			return BuildingMethodRefused, nil
		}
		return BuildingMethodAdmitted, nil
	}
	return "", nil
}
