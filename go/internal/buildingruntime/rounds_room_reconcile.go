package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
}

// flooringFacts is the flooring review's view of the colony's floors: every
// definition the mirror describes, the accessible stock and the tier style.
func flooringFacts(facts observation.ColonyProjection) policy.FlooringFacts {
	flooring := policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{}, Stock: facts.Resources, Style: floorStyle(facts)}
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
		if natural, known := c.NaturalRock.Value(); known && natural {
			rock = append(rock, c.Cell)
		}
	}
	return rock
}

// roomWaiting is the wait of a room whose next operation has no ready cell:
// a prerequisite is outstanding or the native preview refused the placements.
func roomWaiting(name string) RoundsBuildingResult {
	return RoundsBuildingResult{Verdict: waitFor(WaitExistingWork, name+"_reconcile")}
}

func (b *RoundsBuildingPlanner) reconcileRoom(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, stock *packedStock, rr roomReconcile) (RoundsBuildingResult, error) {
	facts := reading.Projection
	plan, known := facts.LayoutPlan.Value()
	buildings, bk := colonyGround(facts)
	if !known || !bk {
		return RoundsBuildingResult{Verdict: fieldUnavailable("room_ground")}, nil
	}
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
	in := policy.ReconcileInput{
		Plan: plan, Room: rr.room, Ground: plan.GroundWithRock(buildings, naturalRock(facts)),
		Rows:   policy.OwnRows(stampPacking(player, facts), rr.template, rr.forbidden),
		Floors: census.Floors, Rooms: colonyRooms(facts), Furniture: rr.template,
	}
	in.WantedFloor, in.FloorKept = policy.FlooringRoomFloors(func(policy.PlannedRoom) []string { return rr.tags }, flooringFacts(facts), b.reviewer.policy.Flooring)(rr.room)
	if items, readable, err := stock.Items(call, policy.PackedFurnitureDefinition); err != nil {
		return RoundsBuildingResult{}, err
	} else if readable {
		in.Stock = map[string]int{}
		for _, item := range items {
			in.Stock[item.InnerDef]++
		}
	}
	ops := policy.ReconcileRoom(in)
	// Removals first, then installs from stock, then what is built on site.
	for _, kind := range []policy.OpKind{policy.OpFurnitureOut, policy.OpPack, policy.OpPackInUse, policy.OpFloorOut} {
		for _, op := range ops {
			if op.Kind == kind {
				return b.commitRemoval(call, epoch, state, goal, rr, op)
			}
		}
	}
	for _, op := range ops {
		if op.Kind == policy.OpInstall {
			if result, done, err := b.commitInstalls(call, epoch, state, goal, stock, rr, op); done || err != nil {
				return result, err
			}
		}
	}
	return b.commitBuilds(call, epoch, state, review, goal, reading, plan, rr, ops)
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

// commitInstalls moves the pieces the packed stock holds to their slots; done
// is false when none is stored after all, so the pieces are built on site.
func (b *RoundsBuildingPlanner) commitInstalls(call, epoch context.Context, state ControlState, goal store.WorkOwner, stock *packedStock, rr roomReconcile, op policy.Operation) (RoundsBuildingResult, bool, error) {
	var actions []domain.Action
	var key strings.Builder
	id := domain.MintPlanID()
	for _, piece := range op.Pieces {
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

// roomBuild is one building the room is built from on site.
type roomBuild struct {
	def, stuff string
	cell       domain.Cell
	rot        domain.Rotation
}

// commitBuilds previews every ready on-site building of the room, doors first
// then walls, floors and furniture, and admits those native accepts as one
// method; a refused placement is left for a later pass.
func (b *RoundsBuildingPlanner) commitBuilds(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, plan policy.LayoutPlan, rr roomReconcile, ops []policy.Operation) (RoundsBuildingResult, error) {
	facts := reading.Projection
	var wantWalls bool
	for _, op := range ops {
		wantWalls = wantWalls || op.Kind == policy.OpWallIn || op.Kind == policy.OpDoorIn
	}
	var builds []roomBuild
	var wallStuff, doorStuff string
	if wantWalls {
		var refusal Verdict
		var ok bool
		if wallStuff, doorStuff, refusal, ok = shellMaterials(facts); !ok {
			return RoundsBuildingResult{Verdict: refusal}, nil
		}
	}
	for _, kind := range []policy.OpKind{policy.OpDoorIn, policy.OpWallIn, policy.OpFloorIn, policy.OpBuild} {
		for _, op := range ops {
			if op.Kind != kind {
				continue
			}
			switch kind {
			case policy.OpDoorIn:
				for _, c := range op.Cells {
					builds = append(builds, roomBuild{def: policy.ShellDoorDefinition, stuff: doorStuff, cell: c, rot: domain.North})
				}
			case policy.OpWallIn:
				for _, c := range op.Cells {
					builds = append(builds, roomBuild{def: policy.ShellWallDefinition, stuff: wallStuff, cell: c, rot: domain.North})
				}
			case policy.OpFloorIn:
				for _, f := range op.Floors {
					builds = append(builds, roomBuild{def: f.DefName, cell: f.Cell, rot: domain.North})
				}
			case policy.OpBuild:
				for _, piece := range op.Pieces {
					builds = append(builds, roomBuild{def: piece.DefName, stuff: facts.BuildStuff(piece.DefName), cell: piece.Anchor(), rot: piece.Rot})
				}
			}
		}
	}
	if len(builds) == 0 {
		return roomWaiting(rr.name), nil
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
	// A room planned into rock is mined out before its ring (#836).
	if wantWalls && rr.room.Dug {
		if result, handled, err := b.digPlannedRoom(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading.ColonyReading}, plan, rr.room, check); err != nil || handled {
			return result, err
		}
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var selected []policy.Preview
	var key strings.Builder
	for _, build := range builds {
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
			continue
		}
		if err := mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoundsBuildingResult{}, err
		}
		selected = append(selected, v)
		fmt.Fprintf(&key, "%s@%d,%d;", build.def, build.cell.X, build.cell.Z)
	}
	if len(selected) == 0 {
		return roomWaiting(rr.name), nil
	}
	digest := sha256.Sum256([]byte(key.String()))
	method := domain.MethodID(fmt.Sprintf("%s-build-%x", rr.name, digest[:8]))
	if once, err := b.methodOnce(call, goal, method); err != nil || !once {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, rr.name+"_build")}, err
	}
	return b.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, reason: rr.reason, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}

// shellMaterials chooses the wall's and the door's stuff from the one stuff the
// colony can raise a shell from; refusal names what is unavailable.
func shellMaterials(facts observation.ColonyProjection) (wallStuff, doorStuff string, refusal Verdict, ok bool) {
	wallDef, wok := animalContainmentDefinition(facts.Definitions, policy.ShellWallDefinition)
	doorDef, dok := animalContainmentDefinition(facts.Definitions, policy.ShellDoorDefinition)
	if !wok || !dok {
		return "", "", fieldUnavailable("wall_door_definitions"), false
	}
	wa, wak := wallDef.Available.Value()
	da, dak := doorDef.Available.Value()
	if !wak || !dak || !wa || !da {
		return "", "", fieldUnavailable("wall_door_availability"), false
	}
	return sharedShellStuff(facts, wallDef, doorDef)
}
