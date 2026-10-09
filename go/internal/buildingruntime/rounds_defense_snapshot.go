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

// defenseRecorder is a RoundsDefenseSource that keeps the replies one
// defense step read, for its colony snapshot.
type defenseRecorder struct {
	RoundsDefenseSource
	emergency *bridge.EmergencyObservation
	pawns     *n.ListPawnsReply
	lines     *bridge.LinesOfFire
}

// ReadCombat keeps the frame's census, its pawn table rows and building
// lines of fire, in the shapes of the reads they replace.
func (d *defenseRecorder) ReadCombat(ctx context.Context, id *c.Identity) (bridge.Combat, error) {
	v, err := d.RoundsDefenseSource.ReadCombat(ctx, id)
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
func (r *RoundsDefensePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsDefenseResult, error) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return r.decide(call, epoch, arbiter)
	}
	recorder := &defenseRecorder{RoundsDefenseSource: r.native}
	planner := *r
	planner.native = recorder
	result, err := planner.decide(call, epoch, arbiter)
	if err == nil && recorder.emergency != nil {
		d, recErr := r.defenseSnapshot(call, recorder, result)
		if recErr == nil {
			recErr = snapshot.RecordDefense(dir, d)
		}
		if recErr != nil {
			defenseSnapshotSkip(call, "defense", "defense", recErr)
		}
	}
	return result, err
}

// recordLayoutSnapshot writes one layout decision's request when
// snapshot.DirEnv names a directory; a failed write is logged.
func recordLayoutSnapshot(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick, l snapshot.Layout) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return
	}
	l.Recorded = fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick)
	l.Snapshot, l.Tick = current, tick
	if err := snapshot.RecordLayout(dir, l); err != nil {
		defenseSnapshotSkip(ctx, "defense-layout", "layout", err)
	}
}

func (r *RoundsDefensePlanner) defenseSnapshot(ctx context.Context, rec *defenseRecorder, result RoundsDefenseResult) (snapshot.Defense, error) {
	p := r.reviewer.player
	current := p.session.State().Snapshot
	tick := domain.Tick(rec.emergency.Context.GetTick())
	d := snapshot.Defense{
		Recorded: fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick),
		Snapshot: current, Tick: tick, Emergency: rec.emergency.Facts,
		Reason: result.Verdict.String(), Plan: result.Plan,
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
	review, err := p.journal.LoadRounds(ctx)
	if err != nil {
		return d, err
	}
	incident, _, found, err := roundsIncident(ctx, p.journal, review, policy.ActiveCombat)
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
