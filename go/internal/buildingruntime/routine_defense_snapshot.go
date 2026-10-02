package buildingruntime

import (
	"context"
	"fmt"
	"os"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// defenseRecorder is a RoutineDefenseSource that keeps the replies one
// defense step read, for its colony snapshot (#744).
type defenseRecorder struct {
	RoutineDefenseSource
	emergency *bridge.EmergencyObservation
	pawns     *n.ListPawnsReply
	lines     *bridge.LinesOfFire
}

// ReadCombat keeps the frame's census, its pawn table rows and building
// lines of fire, in the shapes of the reads they replace (#853).
func (d *defenseRecorder) ReadCombat(ctx context.Context, id *c.Identity) (bridge.Combat, error) {
	v, err := d.RoutineDefenseSource.ReadCombat(ctx, id)
	if err != nil || v.Emergency.Context == nil {
		return v, err
	}
	d.emergency = &v.Emergency
	if detail := v.Frame.GetPawns(); detail != nil {
		d.pawns = &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: detail}}
	}
	if lines := v.Frame.GetCombatLinesOfFire(); lines != nil {
		d.lines = &bridge.LinesOfFire{Context: lines.Context, Lines: v.Lines}
	}
	return v, nil
}

// step runs the defense decision and, when snapshot.DirEnv names a
// directory, records every step that read the emergency census as
// defense-<tick>-<reason>.json; a failed write is logged, never the step's error.
func (r *RoutineDefensePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineDefenseResult, error) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return r.decide(call, epoch, arbiter)
	}
	recorder := &defenseRecorder{RoutineDefenseSource: r.native}
	planner := *r
	planner.native = recorder
	result, err := planner.decide(call, epoch, arbiter)
	if err == nil && recorder.emergency != nil {
		d, recErr := r.defenseSnapshot(call, recorder, result)
		if recErr == nil {
			recErr = snapshot.RecordDefense(dir, d)
		}
		if recErr != nil {
			clockEvent(call, "defense", "snapshot", "defense snapshot not recorded: "+recErr.Error())
		}
	}
	return result, err
}

// recordLayoutSnapshot writes one layout decision's request when
// snapshot.DirEnv names a directory (#744); a failed write is logged.
func recordLayoutSnapshot(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick, l snapshot.Layout) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return
	}
	l.Recorded = fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick)
	l.Snapshot, l.Tick = current, tick
	if err := snapshot.RecordLayout(dir, l); err != nil {
		clockEvent(ctx, "defense-layout", "snapshot", "layout snapshot not recorded: "+err.Error())
	}
}

func (r *RoutineDefensePlanner) defenseSnapshot(ctx context.Context, rec *defenseRecorder, result RoutineDefenseResult) (snapshot.Defense, error) {
	p := r.reviewer.player
	current := p.session.State().Snapshot
	tick := domain.Tick(rec.emergency.Context.GetTick())
	d := snapshot.Defense{
		Recorded: fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick),
		Snapshot: current, Tick: tick, Emergency: rec.emergency.Facts,
		Reason: string(result.Reason), Plan: result.Plan,
	}
	var err error
	if d.EmergencyContext, err = protojson.Marshal(rec.emergency.Context); err != nil {
		return d, err
	}
	if rec.pawns != nil {
		if d.CombatPawns, err = protojson.Marshal(rec.pawns); err != nil {
			return d, err
		}
	}
	if rec.lines != nil {
		d.Lines = rec.lines.Lines
		if d.LinesContext, err = protojson.Marshal(rec.lines.Context); err != nil {
			return d, err
		}
	}
	layout, ok, err := p.journal.LoadDefenseLayout(ctx, store.World{Colony: current.Colony, Load: current.Load, Map: current.Map})
	if err != nil {
		return d, err
	}
	if ok {
		d.Layout = &layout
	}
	if result.Plan == "" {
		return d, nil
	}
	review, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		return d, err
	}
	incident, _, found, err := routineIncident(ctx, p.journal, review, policy.ActiveCombat)
	if err != nil || !found {
		return d, err
	}
	for _, m := range incident.Methods {
		if m.Plan == result.Plan {
			d.Method = m.Method
		}
	}
	return d, nil
}
