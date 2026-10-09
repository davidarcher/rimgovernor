package buildingruntime

import (
	"context"
	"fmt"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	pp "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

// gatheringSource is a six-colonist colony whose every colonist loses the
// party's own mood effect, with a built PartySpot. Setting memory gives one
// colonist the AttendedParty memory; noSpot drops the spot.
type gatheringSource struct {
	*roundsNative
	memory, noSpot bool
}

const gatheringColonists = 6

func (n *gatheringSource) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	frame, err := n.roundsNative.ReadRoundsFrame(ctx, id)
	if err != nil {
		return frame, err
	}
	frame.Colony.ColonistCount = proto.Uint32(gatheringColonists)
	frame.Colony.WorkerCount = proto.Uint32(gatheringColonists)
	frame.IdeologyActive = domain.Known(false)
	frame.Emergency = bridge.EmergencyObservation{Context: frame.Context, Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}
	frame.Pawns = &o.PawnSnapshot{Context: frame.Context, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
	for i := 0; i < gatheringColonists; i++ {
		name := fmt.Sprintf("pawn%d", i)
		frame.Emergency.Facts.Colonists = append(frame.Emergency.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(name), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), InBed: domain.Known(false)})
		memories := []*o.Thought{{DefName: proto.String("NeedJoy"), MoodOffsetTotal: proto.Float64(-8)}}
		if n.memory && i == 0 {
			memories = append(memories, &o.Thought{DefName: proto.String(policy.PartyThought), MoodOffsetTotal: proto.Float64(8)})
		}
		frame.Pawns.Pawns = append(frame.Pawns.Pawns, &o.PawnState{
			Pawn: &o.EntityRef{Id: proto.String(name)}, Colonist: proto.Bool(true), FreeColonist: proto.Bool(true), Dead: proto.Bool(false),
			Downed: proto.Bool(false), Drafted: proto.Bool(false),
			Needs:  &o.PawnNeeds{Mood: proto.Float64(0.5)},
			Issues: []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}},
			Social: &o.PawnSocial{Memories: memories},
		})
	}
	if !n.noSpot {
		frame.Buildings = &bridge.BuildingCensus{Rows: bridge.NewBuildings(&o.BuildingState{
			Building: &o.EntityRef{Id: proto.String("Thing_spot"), DefName: proto.String(policy.PartySpotDefinition), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(3)}},
			Status:   o.BuildingStatus_BUILDING_STATUS_BUILT.Enum(), Stuff: proto.String(""), Rotation: pp.Rotation_ROTATION_NORTH.Enum(),
			Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(3), Z: proto.Int32(3)}, Maximum: &c.Cell{X: proto.Int32(3), Z: proto.Int32(3)}},
		})}
	}
	key := (&d.ThoughtDef{}).ProtoReflect().Descriptor().FullName()
	if frame.Catalog.Defs[key] == nil {
		frame.Catalog.Defs[key] = map[string]proto.Message{}
	}
	frame.Catalog.Defs[key][policy.PartyThought] = &d.ThoughtDef{DefName: policy.PartyThought, Stages: []*d.Opt_ThoughtStage{{Value: &d.ThoughtStage{BaseMoodEffect: 8}}}}
	return frame, nil
}

func gatheringFixture(t *testing.T, source *gatheringSource) (*Rounder, *RoundsGatheringPlanner, *store.Store, func() RoundsGatheringResult) {
	t.Helper()
	base, db, _, _, native := roundsFixture(t)
	source.roundsNative = native
	reviewer, err := NewRounder(base.player, source, testkit.NewManualClock(time.Now()), policy.DefaultRoundsPolicy(), time.Minute, RoundsCapabilities{Methods: []policy.ConcernID{policy.HoldGatherings}})
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsGatheringPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	step := func() RoundsGatheringResult {
		call, epoch, done, err := reviewer.player.enter(context.Background(), "test", false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		result, err := planner.step(call, epoch, newStepArbiter())
		if err != nil {
			t.Fatal(err)
		}
		return result
	}
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	return reviewer, planner, db, step
}

// A colony-wide dip with a PartySpot commits exactly one gathering action for
// the best-placed organizer, and a second step while it is open adds none.
func TestGatheringPlannerEmitsOnePartyAndNoDuplicate(t *testing.T) {
	_, _, db, step := gatheringFixture(t, &gatheringSource{})
	first := step()
	if first.Verdict != BuildingReasonAdmitted {
		t.Fatalf("not admitted: %+v", first)
	}
	plan, err := db.LoadPlan(context.Background(), first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	actions := plan.Spec.Actions()
	if len(actions) != 1 {
		t.Fatalf("%d actions", len(actions))
	}
	party, ok := actions[0].Gathering()
	if !ok || party.Def() != policy.PartyGatheringDef || party.Organizer() != "pawn0" {
		t.Fatalf("wrong gathering: %+v", party)
	}
	if second := step(); second.Verdict != BuildingReasonExistingWork || second.Plan != "" {
		t.Fatalf("duplicate while running: %+v", second)
	}
}

// The AttendedParty memory is the cooldown, and a missing PartySpot is a
// wait for EnsureComfort, not a failure: neither commits a party.
func TestGatheringPlannerWaitsOnCooldownAndOnTheSpot(t *testing.T) {
	for name, source := range map[string]*gatheringSource{"cooldown": {memory: true}, "no spot": {noSpot: true}} {
		_, _, db, step := gatheringFixture(t, source)
		if got := step(); got.Verdict != BuildingReasonNoDeficit || got.Plan != "" {
			t.Errorf("%s: %+v", name, got)
		}
		rounds, err := db.LoadRounds(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if goal, _, err := db.Workable(context.Background(), rounds, policy.HoldGatherings); err != nil || goal.Standard.Finding == domain.FindingUnmet {
			t.Errorf("%s: the concern should be recovered: %v %+v", name, err, goal.Standard.Finding)
		}
	}
}
