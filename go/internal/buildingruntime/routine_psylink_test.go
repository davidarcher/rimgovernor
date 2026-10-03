package buildingruntime

import (
	"context"
	"errors"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type fakePsylinkSource struct {
	items []string
	err   error
	reads int
}

func (f *fakePsylinkSource) ReadNeuroformerItems(context.Context, *c.Identity, string) ([]string, bridge.Result, error) {
	f.reads++
	return f.items, bridge.Result{}, f.err
}

func psylinkProjection(held int, pawns ...policy.WorkPawn) observation.ColonyProjection {
	royalty := policy.RoyaltyFacts{Neuroformers: map[string]policy.Neuroformer{
		policy.PsylinkNeuroformer: {Def: policy.PsylinkNeuroformer, Held: domain.Known(held), Tradeable: domain.Known(true)},
	}}
	p := observation.ColonyProjection{Royalty: domain.Known(royalty), WorkPawns: domain.Known(pawns)}
	p.Identity.Tick = 120
	return p
}

func bareColonist(id string) policy.WorkPawn {
	return policy.WorkPawn{ID: policy.PawnID(id), Available: domain.Known(true), Rest: domain.Known(0.7)}
}

func TestPsylinkReviewOwesAUseOnlyForAHeldItem(t *testing.T) {
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0}
	source := &fakePsylinkSource{items: []string{"PsychicAmplifier3"}}
	m := &psylinkMemory{native: source, who: domain.Unknown[[]policy.PawnID]()}

	who, owed := m.review(context.Background(), nil, snapshot, psylinkProjection(1, bareColonist("amy")))
	if got, _ := who.Value(); !slices.Equal(got, []policy.PawnID{"amy"}) {
		t.Fatal(got)
	}
	if v, known := owed.Value(); !known || !v {
		t.Fatal("held neuroformer and a candidate owe a use", owed)
	}
	candidates, items, ok := m.take(stockpileWorld(snapshot), 120)
	if !ok || !slices.Equal(candidates, []policy.PawnID{"amy"}) || !slices.Equal(items, []string{"PsychicAmplifier3"}) {
		t.Fatal(candidates, items, ok)
	}
	if _, _, ok := m.take(stockpileWorld(snapshot), 121); ok {
		t.Fatal("a later tick took the review's memory")
	}

	// The royalty count says none held: no live read, nothing owed.
	source.reads = 0
	_, owed = m.review(context.Background(), nil, snapshot, psylinkProjection(0, bareColonist("amy")))
	if v, known := owed.Value(); !known || v || source.reads != 0 {
		t.Fatal("no stock must owe nothing and read nothing", owed, source.reads)
	}

	// A failed item read leaves the use unknown instead of failing the review.
	source.err = errors.New("native unavailable")
	_, owed = m.review(context.Background(), nil, snapshot, psylinkProjection(1, bareColonist("amy")))
	if _, known := owed.Value(); known {
		t.Fatal("a failed read became evidence", owed)
	}

	// No willing colonist, no read.
	source.err, source.reads = nil, 0
	psycaster := bareColonist("zed")
	psycaster.PsylinkLevel = domain.Known(1)
	_, owed = m.review(context.Background(), nil, snapshot, psylinkProjection(1, psycaster))
	if v, known := owed.Value(); !known || v || source.reads != 0 {
		t.Fatal("a psycaster is no candidate", owed, source.reads)
	}
}
