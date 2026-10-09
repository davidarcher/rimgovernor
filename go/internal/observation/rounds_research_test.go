package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func researchSource(base *o.ColonyFactsReply, read bridge.ResearchRead) *projectSource {
	return &projectSource{colonySource: &colonySource{reply: base}, frame: bridge.RoundsFrame{Research: &read}}
}

func TestRoundsResearchAndResourceFactsReadTheFrame(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	base := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, base); err != nil {
		t.Fatal(err)
	}
	base.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	identity := func() *l.IdentityReply {
		return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}
	}
	expected, err := DecodeIdentity(identity())
	if err != nil {
		t.Fatal(err)
	}
	newSource := func() *projectSource {
		return &projectSource{colonySource: &colonySource{reply: base}}
	}
	clock := testkit.NewManualClock(time.Now())
	ctx := context.Background()

	// A source without a research read leaves the fact unknown; the generic
	// resource census still projects the shared stock rows.
	out, err := observeRoundsUnowned(ctx, newSource(), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := out.Projection.Facts.Research.Value(); known {
		t.Fatal("research known without a source")
	}
	if _, known := out.Projection.TechTier.Value(); known {
		t.Fatal("tech tier known without a research census")
	}
	if rows, known := out.Projection.Facts.Resources.Value(); !known || !reflect.DeepEqual(rows, []policy.Amount{{Resource: "WoodLog", Count: 40}}) {
		t.Fatal(out.Projection.Facts.Resources)
	}

	read := bridge.ResearchRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), CurrentProject: "Electricity", Finished: []string{"Stonecutting"}, Projects: map[string]policy.ResearchProjectFacts{"Stonecutting": {}, "Electricity": {}, "Batteries": {}}}
	out, err = observeRoundsUnowned(ctx, researchSource(base, read), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	want := policy.ResearchFacts{Current: "Electricity", Finished: []policy.ResearchProjectID{"Stonecutting"}, Projects: []policy.ResearchProjectID{"Batteries", "Electricity", "Stonecutting"}}
	if facts := out.Projection.Facts.Research; !reflect.DeepEqual(facts, domain.Known(want)) {
		t.Fatal(facts)
	}
	// The tech tier follows the census (#604): Stonecutting finished on
	// the fixture's Neolithic faction is Masonry, filed with the section.
	if tier := out.Projection.TechTier; tier != domain.Known(policy.TechTierMasonry) || out.Sections.Colony.Value.TechTier != tier {
		t.Fatal(tier, out.Sections.Colony.Value.TechTier)
	}
}
