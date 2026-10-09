package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"log/slog"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The build side of the reconciler as a shared library (#2109, epic #2101): an
// owning concern calls reconcileRoom for its own room and the reconciler decides
// how. It reads the room's ground (clearance census on the room's rectangle, the
// packed stock), asks the flooring review for the wanted floor, diffs
// (policy.ReconcileRoom) and commits the next wave: removals first (pack,
// deconstruct, floor out), then installs from packed stock, then everything
// built on site (doors, walls, floors, furniture), each wave one method. A
// placement the native preview refuses is not ready this pass: the room waits.
// The ring's removals (door and wall out, roof off) are the plan-wide clear side's.

// roomReconcile is one owner's room: the PlannedRoom, its furniture template
// and what the owner forbids in it. name prefixes the methods, reason is the
// admission's short why and tags the title's floor tags (the throne room's).
type roomReconcile struct {
	room      policy.PlannedRoom
	template  []policy.WantedPiece
	forbidden func(def string) bool
	tags      []string
	name      string
	reason    string
	// ringOnly reconciles the ring and its doors alone: no furniture template,
	// floors or wall-stuff swaps, and no clearance read, so a caller that holds
	// no packed stock (dining, kitchen, the gear rooms, the incinerator) can use
	// it. A ring cell the native refuses is no_space, as for an outdoor ring.
	ringOnly bool
	// stuff overrides the wall's and door's stuff (the incinerator's fireproof
	// choice); nil builds both from the one shared shell stuff.
	stuff shellStuff
}

// roomRingInput is the ring-only diff input for room: the plan, the colony's
// walls and doors with natural rock counted as wall, and obstructions on the
// ring alone; false while the construction census is unknown. Stored goods and
// furniture inside the room do not obstruct its shell.
func roomRingInput(facts observation.ColonyProjection, plan policy.LayoutPlan, room policy.PlannedRoom) (policy.ReconcileInput, bool) {
	buildings, known := colonyGround(facts)
	if !known {
		return policy.ReconcileInput{}, false
	}
	cells := cellsOn(facts.Cells, room.RoomGround())
	in := room.Interior
	cells = slices.DeleteFunc(cells, func(c policy.SiteCell) bool {
		return c.Cell.X >= in.X && c.Cell.X < in.X+in.Width && c.Cell.Z >= in.Z && c.Cell.Z < in.Z+in.Height
	})
	return policy.ReconcileInput{Plan: plan, Room: room, Ground: plan.GroundWithRock(buildings, naturalRock(facts)), Rooms: colonyRooms(facts), Cells: cells}, true
}

// cellsOn is the mirror cells inside ground: the room's foreign things are read
// from them (#2269).
func cellsOn(cells []policy.SiteCell, ground policy.Rectangle) []policy.SiteCell {
	var out []policy.SiteCell
	for _, c := range cells {
		if c.Cell.X >= ground.X && c.Cell.X < ground.X+ground.Width && c.Cell.Z >= ground.Z && c.Cell.Z < ground.Z+ground.Height {
			out = append(out, c)
		}
	}
	return out
}

// roomRingOwed is true while room's ring has a wall or door still to raise.
func roomRingOwed(facts observation.ColonyProjection, plan policy.LayoutPlan, room policy.PlannedRoom) bool {
	in, known := roomRingInput(facts, plan, room)
	if !known {
		return false
	}
	for _, op := range policy.Reconcile(in).Owed {
		if op.Kind == policy.OpWallIn || op.Kind == policy.OpDoorIn {
			return true
		}
	}
	return false
}

// flooringFacts is the flooring review's view of the colony's floors: every
// definition the mirror describes, the accessible stock and the tier style.
func flooringFacts(facts observation.ColonyProjection) policy.FlooringFacts {
	flooring := policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{}, Stock: facts.Resources, Style: floorStyle(facts), Layout: facts.LayoutPlan, Observation: facts.Facts.Upkeep.Flooring}
	if defs, known := facts.Facts.Containment.Defs.Value(); known {
		flooring.ContainmentFloor = defs.Floor
	}
	for _, d := range facts.Definitions {
		flooring.Definitions[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs, WorkToBuild: d.WorkToBuild, Tags: d.FloorTags}
	}
	return flooring
}

// clearFloors is the clear side's wanted floor per planned room (#2107): the
// flooring review's choice, with the throne room's title tags.
func (r *Rounder) clearFloors(facts observation.ColonyProjection) policy.RoomFloors {
	var throne policy.PlannedRoom
	var tags []string
	if need, owed := throneNeed(facts); owed && len(need.FloorTags) > 0 {
		if plan, known := facts.LayoutPlan.Value(); known {
			throne, _ = plan.ThroneRoomFor(need.MinArea)
			tags = need.FloorTags
		}
	}
	return policy.FlooringRoomFloors(func(room policy.PlannedRoom) []string {
		if room.Same(throne) {
			return tags
		}
		return nil
	}, flooringFacts(facts), r.policy.Flooring)
}

// naturalRock is the census cells that are natural rock.
func naturalRock(facts observation.ColonyProjection) []domain.Cell {
	var rock []domain.Cell
	for _, c := range facts.Cells {
		if c.NaturalRock() {
			rock = append(rock, c.Cell)
		}
	}
	return rock
}

// roomWaiting is the wait of a room whose next operation has no ready cell:
// a prerequisite is outstanding or the native preview refused the placements.
func roomWaiting(name string, refused ...refusedPlacement) RoundsBuildingResult {
	subject := name + "_reconcile"
	if len(refused) > 0 {
		subject += ":blocked:" + refused[0].key()
	}
	return RoundsBuildingResult{Verdict: waitFor(WaitExistingWork, subject)}
}

// refusedPlacement is a furniture cell the native preview refused and the
// thing it reported in the way (#2271).
type refusedPlacement struct {
	def     string
	cell    domain.Cell
	blocker string
}

func (r refusedPlacement) key() string { return fmt.Sprintf("%s@%d,%d", r.def, r.cell.X, r.cell.Z) }

// describeBlockers names what the preview reported in the way.
func describeBlockers(blockers []policy.PlacementBlocker) string {
	if len(blockers) == 0 {
		return "unreported"
	}
	parts := make([]string, 0, len(blockers))
	for _, b := range blockers {
		name := b.DefName
		if name == "" {
			name = b.Category
		}
		switch {
		case b.Blueprint:
			name += " blueprint"
		case b.Frame:
			name += " frame"
		}
		parts = append(parts, name)
	}
	return strings.Join(parts, ", ")
}

// reportRefused records a refused furniture cell in the service log and
// returns it for the wait key.
func reportRefused(ctx context.Context, build roomBuild, v policy.Preview) refusedPlacement {
	r := refusedPlacement{def: build.def, cell: build.cell, blocker: describeBlockers(v.Blockers)}
	slog.Default().InfoContext(ctx, "furniture cell refused: "+r.key()+" blocked by "+r.blocker, telemetry.ComponentKey, "building-planner", telemetry.KindKey, "placement_refused")
	return r
}

// roomWork is one room of a reconcile with the operations its diff leaves.
type roomWork struct {
	rr  roomReconcile
	ops []policy.Operation
	// holds are the foreign things the room leaves standing.
	holds []policy.ReconcileHold
}

// holdKey names the first held foreign thing for a wait key and logs every one
// (#2269): the blocker rides in the wait, not a new enum.
func holdKey(ctx context.Context, works []roomWork) string {
	var first string
	for _, w := range works {
		for _, h := range w.holds {
			key := fmt.Sprintf("%s@%d,%d:%s", h.Def, h.Cell.X, h.Cell.Z, h.Reason)
			slog.Default().InfoContext(ctx, "foreign thing held: "+key, telemetry.ComponentKey, "building-planner", telemetry.KindKey, "foreign_held")
			if first == "" {
				first = key
			}
		}
	}
	return first
}

// waitingHeld is roomWaiting with the first held foreign thing named.
func waitingHeld(ctx context.Context, name string, works []roomWork, refused ...refusedPlacement) RoundsBuildingResult {
	result := roomWaiting(name, refused...)
	if hold := holdKey(ctx, works); hold != "" && len(refused) == 0 {
		result.Verdict = waitFor(WaitExistingWork, name+"_reconcile:held:"+hold)
	}
	return result
}

// reconcileRoom reconciles one owner's room (see reconcileRooms).
func (b *RoundsBuildingPlanner) reconcileRoom(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, stock *packedStock, rr roomReconcile) (RoundsBuildingResult, error) {
	return b.reconcileRooms(call, epoch, state, review, goal, reading, stock, []roomReconcile{rr})
}

// reconcileRooms reconciles several rooms of one owner together (#2133, a
// wing's bedrooms): each room is diffed on its own, then the next wave is
// committed across all of them in plan order, so twelve rooms' walls are one
// wall batch. The first room names the methods. A removal is the first room's
// that owes one (it ends the pass), installs from packed stock are one method
// for every room (each stored piece goes to one room), and the builds are one
// method that admits what the stock funds (commitBuilds).
func (b *RoundsBuildingPlanner) reconcileRooms(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, stock *packedStock, rrs []roomReconcile) (RoundsBuildingResult, error) {
	facts := reading.Projection
	plan, known := facts.LayoutPlan.Value()
	if !known || len(rrs) == 0 {
		return RoundsBuildingResult{Verdict: fieldUnavailable("room_ground")}, nil
	}
	// A ring-only room whose wave the owner still has open is not diffed again:
	// its owner goes on to its own placement (#2303).
	for _, rr := range rrs {
		if rr.ringOnly {
			open, err := b.ringWaveOpen(call, goal)
			if err != nil || open {
				return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, rr.name+"_build")}, err
			}
		}
	}
	// The packed stock the rooms share: a room takes what its predecessors left.
	var left map[string]int
	works := make([]roomWork, 0, len(rrs))
	for _, rr := range rrs {
		in, ground := roomRingInput(facts, plan, rr.room)
		if !ground {
			return RoundsBuildingResult{Verdict: fieldUnavailable("room_ground")}, nil
		}
		if !rr.ringOnly {
			in.Cells = cellsOn(facts.Cells, rr.room.RoomGround())
			source, ok := b.native.(observation.ClearanceSource)
			if !ok {
				return RoundsBuildingResult{Verdict: fieldUnavailable("room_ground")}, nil
			}
			read, err := observation.ObserveClearanceCensusOnGround(call, source, facts.Identity, false, []policy.Rectangle{rr.room.RoomGround()})
			if err != nil {
				return RoundsBuildingResult{}, err
			}
			census, known := read.Value()
			if !known {
				return RoundsBuildingResult{Verdict: fieldUnavailable("room_ground")}, nil
			}
			_, player := policy.SplitGroundRows(census.Targets)
			in.Rows, in.Floors, in.Furniture = policy.OwnRows(stampPacking(player, facts), rr.template, rr.forbidden), census.Floors, rr.template
			if want := shellStyle(facts).WallStuff(domain.ShellRun); want != "" && !rr.room.Outdoor {
				in.WallUpgrade = func(have string) bool { return facts.StuffUpgrade(policy.ShellWallDefinition, have, want) }
			}
			if !rr.room.Outdoor {
				in.WantedFloor, in.FloorKept = policy.FlooringRoomFloors(func(policy.PlannedRoom) []string { return rr.tags }, flooringFacts(facts), b.reviewer.policy.Flooring)(rr.room)
			}
			if left == nil {
				if items, readable, err := stock.Items(call, policy.PackedFurnitureDefinition); err != nil {
					return RoundsBuildingResult{}, err
				} else if readable {
					left = map[string]int{}
					for _, item := range items {
						left[item.InnerDef]++
					}
				}
			}
			in.Stock = left
		}
		ops, holds := policy.ReconcileRoomHolds(in)
		for _, op := range ops {
			if op.Kind != policy.OpInstall || left == nil {
				continue
			}
			for _, piece := range op.Pieces {
				left[piece.DefName] = max(0, left[piece.DefName]-1)
			}
		}
		works = append(works, roomWork{rr: rr, ops: ops, holds: holds})
	}
	// Foreign obstructions first (claim, cut, haul), one wave across the rooms,
	// then removals, then installs from stock, then what is built on site.
	if result, done, err := b.commitObstructions(call, epoch, state, goal, works); done || err != nil {
		return result, err
	}
	for _, kind := range []policy.OpKind{policy.OpFurnitureOut, policy.OpPack, policy.OpPackInUse, policy.OpFloorOut} {
		for _, w := range works {
			for _, op := range w.ops {
				if op.Kind == kind {
					return b.commitRemoval(call, epoch, state, goal, w.rr, op)
				}
			}
		}
	}
	var installs []policy.WantedPiece
	for _, w := range works {
		for _, op := range w.ops {
			if op.Kind == policy.OpInstall {
				installs = append(installs, op.Pieces...)
			}
		}
	}
	if len(installs) > 0 {
		if result, done, err := b.commitInstalls(call, epoch, state, goal, stock, works[0].rr, installs); done || err != nil {
			return result, err
		}
	}
	return b.commitBuilds(call, epoch, state, review, goal, reading, plan, works)
}

// ringWaveOpen is true when the owner has a ring wave of any planned room still
// open: the walls it admitted are not all done. The store holds one open ring
// wave per owner, so another room's ring waits for it as well.
func (b *RoundsBuildingPlanner) ringWaveOpen(call context.Context, goal store.WorkOwner) (bool, error) {
	journal := b.reviewer.player.journal
	for _, m := range goal.OwnerMethods() {
		if !store.IsRoomShellMethod(m.Method) {
			continue
		}
		plan, err := journal.LoadPlan(call, m.Plan)
		if err != nil {
			return false, err
		}
		if store.PlanOpen(plan) {
			return true, nil
		}
	}
	return false, nil
}

// methodOnce is true when the owner has not committed method yet.
func (b *RoundsBuildingPlanner) methodOnce(call context.Context, goal store.WorkOwner, method domain.MethodID) (bool, error) {
	if _, err := b.reviewer.player.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return false, err
	}
	return true, nil
}

// commitOwnerActions commits actions as one owner method.
func (b *RoundsBuildingPlanner) commitOwnerActions(call, epoch context.Context, state ControlState, goal store.WorkOwner, method domain.MethodID, id domain.PlanID, actions []domain.Action) (RoundsBuildingResult, error) {
	p := b.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoundsBuildingResult{}, fmt.Errorf("%w: commitOwnerActions: p.session.State() != state", ErrControl)
	}
	if err := p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}

// commitRemoval takes down what the room holds that it should not: packable
// buildings are packed (uninstalled), the rest deconstructed one at a time, the
// wrong floors removed, one method per wave.
func (b *RoundsBuildingPlanner) commitRemoval(call, epoch context.Context, state ControlState, goal store.WorkOwner, rr roomReconcile, op policy.Operation) (RoundsBuildingResult, error) {
	step := policy.GroundStep{Phase: op.Label, Targets: op.Targets, Floors: op.Floors}
	if op.Kind == policy.OpFurnitureOut {
		step.Targets = step.Targets[:1]
	}
	id := domain.MintPlanID()
	prefix, actions, err := groundStepMethod(id, step)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	method := domain.MethodID(rr.name + "-" + strings.TrimSuffix(prefix, "-"))
	if once, err := b.methodOnce(call, goal, method); err != nil || !once {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, rr.name+"_removal")}, err
	}
	return b.commitOwnerActions(call, epoch, state, goal, method, id, actions)
}

// obstructionKinds are the foreign-thing waves in the order they are committed.
var obstructionKinds = []policy.OpKind{policy.OpClaim, policy.OpCut, policy.OpHaulOut}

// commitObstructions commits the next foreign-thing wave across every room
// (#2269): ruins claimed as wall, impassable plants cut and haulable items
// moved, one method per kind whose name carries the targets, so an order the
// game has not finished is not repeated. Packing and deconstruction of foreign
// buildings go through commitRemoval like the room's own. A wave already
// committed is skipped, not waited on: the cells it covers are blocked in the
// diff, so the rest of the room proceeds. done is false when nothing was
// committed.
func (b *RoundsBuildingPlanner) commitObstructions(call, epoch context.Context, state ControlState, goal store.WorkOwner, works []roomWork) (RoundsBuildingResult, bool, error) {
	for _, kind := range obstructionKinds {
		var targets []policy.ClearanceTarget
		seen := map[string]bool{}
		for _, w := range works {
			for _, op := range w.ops {
				if op.Kind != kind {
					continue
				}
				for _, t := range op.Targets {
					key := fmt.Sprintf("%s@%d,%d", t.EntityID, t.Minimum.X, t.Minimum.Z)
					if !seen[key] {
						seen[key] = true
						targets = append(targets, t)
					}
				}
			}
		}
		if len(targets) == 0 {
			continue
		}
		result, done, err := b.commitObstructionWave(call, epoch, state, goal, works[0].rr.name, kind, targets)
		if err != nil || done {
			return result, done, err
		}
	}
	return RoundsBuildingResult{}, false, nil
}

// commitObstructionWave commits one wave of kind over targets as an owner
// method named for them; done is false when that wave was committed before.
func (b *RoundsBuildingPlanner) commitObstructionWave(call, epoch context.Context, state ControlState, goal store.WorkOwner, name string, kind policy.OpKind, targets []policy.ClearanceTarget) (RoundsBuildingResult, bool, error) {
	id := domain.MintPlanID()
	var actions []domain.Action
	var key strings.Builder
	for _, t := range targets {
		action, err := obstructionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), kind, t)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		actions = append(actions, action)
		fmt.Fprintf(&key, "%s@%d,%d;", t.EntityID, t.Minimum.X, t.Minimum.Z)
	}
	digest := sha256.Sum256([]byte(key.String()))
	method := domain.MethodID(fmt.Sprintf("%s-%s-%x", name, kind, digest[:8]))
	if once, err := b.methodOnce(call, goal, method); err != nil {
		return RoundsBuildingResult{}, false, err
	} else if !once {
		return RoundsBuildingResult{}, false, nil
	}
	result, err := b.commitOwnerActions(call, epoch, state, goal, method, id, actions)
	return result, true, err
}

// obstructionAction maps a foreign-thing target to an existing action: a claim,
// or a cover clearance in the cut-plant or haul mode.
func obstructionAction(id domain.ActionID, kind policy.OpKind, t policy.ClearanceTarget) (domain.Action, error) {
	if kind == policy.OpClaim {
		value, err := domain.NewClaimBuilding(t.EntityID)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewClaimBuildingAction(id, value)
	}
	mode := domain.CoverClearanceHaul
	if kind == policy.OpCut {
		mode = domain.CoverClearanceCutPlant
	}
	value, err := domain.NewCoverClearance(t.EntityID, t.DefName, mode, t.Minimum)
	if err != nil {
		return domain.Action{}, err
	}
	return domain.NewCoverClearanceAction(id, value)
}

// commitInstalls moves the pieces the packed stock holds to their slots; done
// is false when none is stored after all, so the pieces are built on site.
func (b *RoundsBuildingPlanner) commitInstalls(call, epoch context.Context, state ControlState, goal store.WorkOwner, stock *packedStock, rr roomReconcile, pieces []policy.WantedPiece) (RoundsBuildingResult, bool, error) {
	var actions []domain.Action
	var key strings.Builder
	id := domain.MintPlanID()
	for _, piece := range pieces {
		move, stored, err := stock.Install(call, policy.PackedFurnitureDefinition, piece.DefName, piece.Anchor(), piece.Rot)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		if !stored {
			continue
		}
		action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), move)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		actions = append(actions, action)
		fmt.Fprintf(&key, "%s@%d,%d;", move.Thing(), piece.Anchor().X, piece.Anchor().Z)
	}
	if len(actions) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	digest := sha256.Sum256([]byte(key.String()))
	method := domain.MethodID(fmt.Sprintf("%s-install-%x", rr.name, digest[:8]))
	if once, err := b.methodOnce(call, goal, method); err != nil || !once {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, rr.name+"_install")}, true, err
	}
	result, err := b.commitOwnerActions(call, epoch, state, goal, method, id, actions)
	return result, true, err
}

// roomBuild is one building a room is built from on site.
type roomBuild struct {
	def, stuff string
	cell       domain.Cell
	rot        domain.Rotation
	// ring marks a wall, fence, door or gate of the room's ring.
	ring bool
	// work is the room's index in the batch.
	work int
}

// roomBuildKinds is the order a wave's buildings are admitted in: doors first,
// then walls, floors and furniture.
var roomBuildKinds = []policy.OpKind{policy.OpDoorIn, policy.OpWallIn, policy.OpWallUp, policy.OpFloorIn, policy.OpBuild}

// commitBuilds previews every ready on-site building of the rooms, one kind at
// a time across the rooms in plan order (twelve rooms' walls are one batch),
// and admits those native accepts as one method; a refused placement is left
// for a later pass. The first room with work to do is admitted whole, as a
// single room always was (a shell short of a material records the shortfall,
// #602); the rooms after it take only what the stock still funds after it,
// walls, doors, floors and furniture alike, and the rest follows in the next
// wave (#2133). There is no cap on rooms or cells: the stock is the limit.
func (b *RoundsBuildingPlanner) commitBuilds(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, plan policy.LayoutPlan, works []roomWork) (RoundsBuildingResult, error) {
	facts := reading.Projection
	wantWalls := make([]bool, len(works))
	for i, w := range works {
		for _, op := range w.ops {
			wantWalls[i] = wantWalls[i] || op.Kind == policy.OpWallIn || op.Kind == policy.OpWallUp || op.Kind == policy.OpDoorIn
		}
	}
	var builds []roomBuild
	for _, kind := range roomBuildKinds {
		for i, w := range works {
			rr := w.rr
			wallDef, doorDef := rr.room.RingDefs()
			var wallStuff, doorStuff string
			if wantWalls[i] {
				var refusal Verdict
				var ok bool
				if wallStuff, doorStuff, refusal, ok = shellMaterials(facts, wallDef, doorDef, rr.stuff); !ok {
					return RoundsBuildingResult{Verdict: refusal}, nil
				}
			}
			for _, op := range w.ops {
				if op.Kind != kind {
					continue
				}
				switch kind {
				case policy.OpDoorIn:
					flaps := plan.FlapCells(rr.room)
					for _, c := range op.Cells {
						if slices.Contains(flaps, c) {
							// The shared wall of a pen and its barn takes the animal flap (#2122).
							flap := facts.Shapes.Furniture.AnimalFlap
							if flap == "" {
								return RoundsBuildingResult{}, fmt.Errorf("%w: commitBuilds: the catalog names no animal flap", ErrControl)
							}
							builds = append(builds, roomBuild{def: flap, stuff: facts.BuildStuff(flap), cell: c, rot: domain.North, ring: true, work: i})
							continue
						}
						builds = append(builds, roomBuild{def: doorDef, stuff: doorStuff, cell: c, rot: domain.North, ring: true, work: i})
					}
				case policy.OpWallIn, policy.OpWallUp:
					for _, c := range op.Cells {
						builds = append(builds, roomBuild{def: wallDef, stuff: wallStuff, cell: c, rot: domain.North, ring: true, work: i})
					}
				case policy.OpFloorIn:
					for _, f := range op.Floors {
						builds = append(builds, roomBuild{def: f.DefName, cell: f.Cell, rot: domain.North, work: i})
					}
				case policy.OpBuild:
					for _, piece := range op.Pieces {
						builds = append(builds, roomBuild{def: piece.DefName, stuff: facts.BulkBuildStuff(piece.DefName, 1), cell: piece.Anchor(), rot: piece.Rot, work: i})
					}
				}
			}
		}
	}
	if len(builds) == 0 {
		if works[0].rr.ringOnly {
			// Nothing of the ring is ready (a door awaits the clear side's wall
			// removal): the owner goes on to its own placement.
			return RoundsBuildingResult{Verdict: noSpace("room_ring")}, nil
		}
		return waitingHeld(call, works[0].rr.name, works), nil
	}
	p := b.reviewer.player
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: commitBuilds: p.session.State() != state", ErrControl)
		}
		return nil
	}
	// A room planned into rock is mined out before its ring (#836), a dug
	// store room first: its zone waits on the dig (#2190).
	for _, stores := range []bool{true, false} {
		for i, w := range works {
			if wantWalls[i] && w.rr.room.Dug && policy.IsStoreRoom(w.rr.room.Role) == stores {
				if result, handled, err := b.digPlannedRoom(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading.ColonyReading}, plan, w.rr.room, check); err != nil || handled {
					return result, err
				}
			}
		}
	}
	// The first room with work goes first and whole; the rest follow in the
	// order the kinds were collected.
	head := builds[0].work
	ordered := make([]roomBuild, 0, len(builds))
	for _, build := range builds {
		if build.work == head {
			ordered = append(ordered, build)
		}
	}
	for _, build := range builds {
		if build.work != head {
			ordered = append(ordered, build)
		}
	}
	ledger := newFundingLedger(policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick})
	var selected []policy.Preview
	var tiers []domain.Fact[domain.ConstructionTier]
	var key strings.Builder
	previewed := 0
	var refused []refusedPlacement
	for _, build := range ordered {
		rr := works[build.work].rr
		priceKey := build.def + "/" + build.stuff
		// A building priced already and unfunded is not previewed again.
		if costs, seen := ledger.priceOf(priceKey); seen && build.work != head && !ledger.funded(costs) {
			continue
		}
		if err := check(); err != nil {
			return RoundsBuildingResult{}, err
		}
		building, err := domain.NewBuilding(build.def, build.cell, build.rot, build.stuff)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(selected))), building)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		preview, _, err := b.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		v := preview.Preview
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !ck || !can || !sk || !safe {
			if build.ring && (rr.room.Outdoor || rr.ringOnly) {
				// The planner sited this ring: a cell the native refuses is
				// reported and left to the plan's replan, never moved to
				// another site (#2120).
				return RoundsBuildingResult{Verdict: noSpace("pen_enclosure")}, nil
			}
			if !build.ring {
				r := reportRefused(call, build, v)
				if len(refused) == 0 {
					refused = append(refused, r)
				}
			}
			continue
		}
		if err := ledger.merge(preview.Stock, previewed == 0); err != nil {
			return RoundsBuildingResult{}, err
		}
		previewed++
		costs, priceKnown := v.Costs.Value()
		if priceKnown {
			ledger.price(priceKey, costs)
		}
		if build.work != head && (!priceKnown || !ledger.funded(costs)) {
			continue
		}
		ledger.claim(costs)
		selected = append(selected, v)
		tiers = append(tiers, domain.Known(policy.RoomTier(rr.room.Role)))
		fmt.Fprintf(&key, "%s@%d,%d;", build.def, build.cell.X, build.cell.Z)
	}
	if len(selected) == 0 {
		return waitingHeld(call, works[0].rr.name, works, refused...), nil
	}
	rr := works[0].rr
	digest := sha256.Sum256([]byte(key.String()))
	method := domain.MethodID(fmt.Sprintf("%s-build-%x", rr.name, digest[:8]))
	if once, err := b.methodOnce(call, goal, method); err != nil || !once {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, rr.name+"_build")}, err
	}
	return b.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, reason: rr.reason, snapshot: snapshot, selected: selected, stock: ledger.stock, purpose: policy.Shelter, tiers: tiers})
}

// shellMaterials chooses the wall's and the door's stuff from the one stuff the
// colony can raise a shell from; refusal names what is unavailable.
func shellMaterials(facts observation.ColonyProjection, wall, door string, choose shellStuff) (wallStuff, doorStuff string, refusal Verdict, ok bool) {
	wallDef, wok := animalContainmentDefinition(facts.Definitions, wall)
	doorDef, dok := animalContainmentDefinition(facts.Definitions, door)
	if !wok || !dok {
		return "", "", fieldUnavailable("wall_door_definitions"), false
	}
	wa, wak := wallDef.Available.Value()
	da, dak := doorDef.Available.Value()
	if !wak || !dak || !wa || !da {
		return "", "", fieldUnavailable("wall_door_availability"), false
	}
	if choose != nil {
		return choose(wallDef, doorDef)
	}
	return sharedShellStuff(facts, wallDef, doorDef)
}
