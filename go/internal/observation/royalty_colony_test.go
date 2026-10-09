package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// The Royalty colony section projects the neuroformer stock, ceremonies and
// thrones; WithRoyaltyColony joins them to the royalty read, and a section
// that is absent or unavailable leaves the royalty fact unknown.
func TestRoyaltyColonyProjection(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func() ColonyProjection {
		t.Helper()
		p, err := DecodeColony(r, id, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		return p
	}
	read := policy.RoyaltyFacts{Ladder: []policy.RoyalRung{{Title: "Knight"}}}
	for _, section := range []*o.RoyaltySection{nil, {Outcome: &o.RoyaltySection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}} {
		r.GetObserved().Royalty = section
		if _, known := decode().WithRoyaltyColony(read).Value(); known {
			t.Fatal("royalty known without a colony section")
		}
	}
	r.GetObserved().Royalty = &o.RoyaltySection{Outcome: &o.RoyaltySection_Observed{Observed: &o.RoyaltyColonyFacts{
		Neuroformers: []*o.NeuroformerStock{{DefName: proto.String("PsychicAmplifier"), Held: proto.Int32(2)}},
		Ceremonies:   []*o.BestowingCeremony{{Quest: proto.String("Quest_4"), Pawn: &c.Ref{Id: proto.String("Human12")}, FactionDef: proto.String("Empire")}},
		Thrones:      []*o.RoyalThrone{{Thing: &c.Ref{Id: proto.String("Throne_1")}, DefName: proto.String("Throne")}},
	}}}
	facts, known := decode().WithRoyaltyColony(read).Value()
	if !known || len(facts.Ladder) != 1 || len(facts.Ceremonies) != 1 || len(facts.Thrones) != 1 {
		t.Fatalf("joined royalty %+v %v", facts, known)
	}
	if held, ok := facts.Neuroformers["PsychicAmplifier"].Held.Value(); !ok || held != 2 {
		t.Fatalf("held %v %v", held, ok)
	}
}
