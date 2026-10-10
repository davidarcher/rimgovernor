package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type projectSource struct {
	*colonySource
	// rows are the recorded rows of the definition catalog the frame carries,
	// nil for none.
	rows    *recordedrows.Slice
	onFrame func()
	frame   bridge.RoundsFrame
}

// diningRows are the recorded rows the dining furniture rules need: the
// chair, the table and the foothold pin, with the def sets they read.
func diningRows(t testing.TB, names ...string) *recordedrows.Slice {
	t.Helper()
	s := recordedrows.Take(t, recordedrows.Named("DiningChair", "Table1x2c", "HorseshoesPin",
		"Bed", "Bedroll", "SleepingSpot", "DoubleBed", "BedrollDouble", "RoyalBed", "DoubleSleepingSpot", "HospitalBed", "Door", "AnimalFlap",
		"AnimalSleepingSpot", "AnimalBed", "EndTable", "Dresser", "StandingLamp", "Heater", "Cooler", "ToolCabinet", "ShelfSmall", "Campfire",
		"CraftingSpot", "PartySpot", "PassiveCooler", "VitalsMonitor", "Sarcophagus", "FueledStove", "ElectricStove",
		"TableStonecutter", "ElectricSmithy", "HandTailoringBench", "FabricationBench", "TableButcher", "SimpleResearchBench", "HiTechResearchBench"),
		"stat_defs", "room_stat_defs", "thing_category_defs", "joy_giver_defs", "job_defs", "work_giver_defs", "damage_defs", "maneuver_defs")
	s.Add(names...)
	return s
}

// ReadRoundsFrame is the frame over the colony reply, with the sections
// the test set in frame and the catalog of extra; the emergency census is
// empty by default.
func (s *projectSource) ReadRoundsFrame(context.Context, *c.Identity) (bridge.RoundsFrame, error) {
	if s.onFrame != nil {
		s.onFrame()
	}
	frame := s.frame
	observed := s.reply.GetObserved()
	frame.Context, frame.Colony = observed.Context, observed
	if frame.Emergency.Context == nil {
		frame.Emergency = bridge.EmergencyObservation{Context: observed.Context}
	}
	if s.rows != nil {
		frame.Catalog = decodeRows(s.rows)
	}
	return frame, nil
}

func TestRoundsProjectDefinitionsStayInsideObservationBracket(t *testing.T) {
	for _, phase := range []string{"supplement", "default-only", "expired", "cancelled", "uncataloged"} {
		t.Run(phase, func(t *testing.T) {
			data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
			if err != nil {
				t.Fatal(err)
			}
			base := &o.ColonyFactsReply{}
			if err := protojson.Unmarshal(data, base); err != nil {
				t.Fatal(err)
			}
			identity := func() *l.IdentityReply {
				return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}
			}
			rows := diningRows(t)
			rows.Add("Wall", "WoodLog")
			// A furniture row may have pulled Wall in with every stuff: leave it wood.
			// The wall takes a stuff category only wood has.
			rows.Thing("WoodLog").StuffProps.Categories = append(rows.Thing("WoodLog").StuffProps.Categories, "OnlyWood")
			rows.Thing("Wall").StuffCategories = []string{"OnlyWood"}
			if phase != "uncataloged" {
				rows.CopyThing("HospitalBed", "SurgeryTable").ResearchPrerequisites = []string{"Medicine"}
			}
			s := &projectSource{colonySource: &colonySource{reply: base}, rows: rows}
			expected, err := DecodeIdentity(identity())
			if err != nil {
				t.Fatal(err)
			}
			clock := testkit.NewManualClock(time.Now())
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			names := []string{"Wall", "SurgeryTable"}
			switch phase {
			case "default-only":
				names = []string{"Wall"}
			case "expired":
				s.onFrame = func() { clock.Advance(2 * time.Second) }
			case "cancelled":
				s.onFrame = cancel
			}
			out, err := observeRoundsUnowned(ctx, s, clock, expected, time.Second, names...)
			if phase == "expired" || phase == "cancelled" {
				if err == nil {
					t.Fatal("unsafe extra read accepted")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			// The starter definitions resolve first; a frame without
			// research leaves a definition behind research unknown, and a
			// name the catalog lacks is unavailable.
			var wallRow, bedRow PlanningDefinition
			for _, d := range out.Projection.Definitions {
				switch d.Name {
				case "Wall":
					wallRow = d
				case "SurgeryTable":
					bedRow = d
				}
			}
			if wallRow.Available != domain.Known(true) || len(wallRow.StuffOptions) != 1 || wallRow.StuffOptions[0].Stuff != "WoodLog" {
				t.Fatal(wallRow)
			}
			want := domain.Unknown[bool]()
			if phase == "uncataloged" {
				want = domain.Known(false)
			}
			if len(names) == 2 && (bedRow.Name != "SurgeryTable" || bedRow.Available != want) {
				t.Fatal(bedRow)
			}
			if len(names) == 2 && !reflect.DeepEqual(out.Projection.Definitions[len(out.Projection.Definitions)-1].Name, names[len(names)-1]) {
				t.Fatal(out.Projection.Definitions)
			}
		})
	}
}

// observeRoundsUnowned reads the routine census with no construction claims.
func observeRoundsUnowned(ctx context.Context, source RoundsSource, clock Clock, expected Identity, maxAge time.Duration, definitions ...string) (RoundsReading, error) {
	return ObserveRoundsOwned(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
}
