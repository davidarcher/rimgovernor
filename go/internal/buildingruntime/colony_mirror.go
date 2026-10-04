package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type colonyFactsReader interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
}

// publishColony publishes every colony facts sub-section
// (bridge.SplitColonyFacts, keyed by row path) of the review frame's colony
// facts (planning, no extra definitions) and returns the tables' mirror
// versions.
func publishColony(m *facts.Store, scope facts.Scope, observed *o.ColonyFactsSnapshot) map[string]uint64 {
	split := bridge.SplitColonyFacts(observed)
	tick := observed.GetContext().GetTick()
	versions := make(map[string]uint64, len(split))
	for _, name := range bridge.ColonySections() {
		versions[name] = facts.PutTable(m, scope, name, split[name], facts.At(tick)).Version
	}
	return versions
}

// colonyTables rebuilds the colony facts the mirror holds at versions,
// false when any section moved on or is gone.
func colonyTables(m *facts.Store, scope facts.Scope, versions map[string]uint64) (*o.ColonyFactsSnapshot, bool) {
	tables := make(map[string]map[string]bridge.ColonyRow, len(versions))
	for name, version := range versions {
		table, ok := facts.GetTable[string, bridge.ColonyRow](m, scope, name)
		if !ok || table.Version != version {
			return nil, false
		}
		tables[name] = table.Rows
	}
	joined, err := bridge.JoinColonyFacts(tables)
	return joined, err == nil
}

// colonyFacts is a planner's colony facts read (no extra definitions):
// the review's mirrored census while it serves the planner's current
// identity under the part-1 reuse rule (sameObservedIdentity: the same
// world and generation, within the pawn cadence after the review, no clock
// invalidation since), otherwise native. A read without planning gets the
// planning section the native answers it with.
func (r *Rounder) colonyFacts(ctx context.Context, native colonyFactsReader, identity *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	if r == nil || !sameNativeSource(native, r.native) {
		return native.ReadColonyFacts(ctx, identity, planning)
	}
	return r.servedColonyFacts(ctx, native, identity, planning)
}

func (r *Rounder) servedColonyFacts(ctx context.Context, native colonyFactsReader, identity *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	if v, ok := r.mirroredColony(ctx, identity); ok {
		if !planning {
			v.Planning = bridge.NotRequestedPlanning()
		}
		return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}, bridge.Result{}, nil
	}
	return native.ReadColonyFacts(ctx, identity, planning)
}

func (r *Rounder) mirroredColony(ctx context.Context, identity *c.Identity) (*o.ColonyFactsSnapshot, bool) {
	if r == nil || r.store == nil {
		return nil, false
	}
	r.census.mu.Lock()
	census, generation := r.census.latest, r.census.generation
	r.census.mu.Unlock()
	if census == nil || census.colony == nil || census.generation != generation {
		return nil, false
	}
	expected, err := stepScope(ctx, r.native)
	if err != nil || string(expected.Colony) != identity.GetColonyId() || string(expected.Load) != identity.GetLoadToken() || int32(expected.Map) != identity.GetMapId() {
		return nil, false
	}
	if !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return nil, false
	}
	generationValue, _ := expected.NativeGeneration.Value()
	scope := facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generationValue)}
	return colonyTables(r.store, scope, census.colony)
}
