package buildingruntime

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

type ideoligionSource struct {
	*roundsNative
	ideo policy.Ideoligion
}

func (n *ideoligionSource) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	frame, err := n.roundsNative.ReadRoundsFrame(ctx, id)
	if err != nil {
		return frame, err
	}
	frame.Ideology = &n.ideo
	frame.Colony.ColonistCount = proto.Uint32(1)
	frame.Colony.WorkerCount = proto.Uint32(1)
	frame.Emergency = bridge.EmergencyObservation{Context: frame.Context, Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}
	frame.Emergency.Facts.Colonists = []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), InBed: domain.Known(false)}}
	frame.Pawns = &o.PawnSnapshot{Context: frame.Context, Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn")}, Colonist: proto.Bool(true), FreeColonist: proto.Bool(true), Dead: proto.Bool(false), Standing: &o.PawnStanding{FactionDefName: proto.String("Player")}}}}
	frame.Pawns.Completeness = &o.Completeness{Filtered: proto.Uint64(0)}
	pawn := frame.Pawns.Pawns[0]
	pawn.Downed, pawn.Drafted = proto.Bool(false), proto.Bool(false)
	pawn.Issues = []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
	pawn.Social = &o.PawnSocial{}
	pawn.Settings = &o.PawnSettings{PolicyInputs: &o.PawnPolicyInputs{IdeoId: proto.String("Ideo_1")}, WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Construction"), Priority: proto.Int32(1), Disabled: proto.Bool(false)}}}
	add := func(message proto.Message, name string) {
		key := message.ProtoReflect().Descriptor().FullName()
		if frame.Catalog.Defs[key] == nil {
			frame.Catalog.Defs[key] = map[string]proto.Message{}
		}
		frame.Catalog.Defs[key][name] = message
	}
	add(&d.FactionDef{DefName: "Player"}, "Player")
	add(&d.MemeDef{DefName: "Structure", Category: d.MemeCategory_MEME_CATEGORY_STRUCTURE, ModPackageId: proto.String("ludeon.rimworld.ideology")}, "Structure")
	add(&d.MemeDef{DefName: "Normal", ModPackageId: proto.String("ludeon.rimworld.ideology")}, "Normal")
	add(&d.IssueDef{DefName: "Work"}, "Work")
	add(&d.PreceptDef{DefName: "Old", Issue: "Work", PreceptClass: "RimWorld.Precept", DefaultSelectionWeight: 1, OpposedWorkTypes: []string{"Construction"}, ModPackageId: proto.String("ludeon.rimworld.ideology")}, "Old")
	add(&d.PreceptDef{DefName: "New", Issue: "Work", PreceptClass: "RimWorld.Precept", DefaultSelectionWeight: 1, ModPackageId: proto.String("ludeon.rimworld.ideology")}, "New")
	return frame, nil
}

func TestReformMethodAdmittedOnceAndManualSuspends(t *testing.T) {
	base, db, _, request, native := roundsFixture(t)
	source := &ideoligionSource{roundsNative: native, ideo: policy.Ideoligion{Facts: policy.IdeoligionFacts{IdeoID: "Ideo_1", Believers: 1, Memes: []string{"Structure", "Normal"}, Precepts: []policy.HeldPrecept{{ID: "held", Def: "Old"}}, Development: domain.Known(policy.IdeoDevelopment{Fluid: true, CanReform: true, Points: 10, NextPoints: 10})}}}
	reviewer, err := NewRounder(base.player, source, testkit.NewManualClock(time.Now()), policy.DefaultRoundsPolicy(), time.Minute, RoundsCapabilities{Methods: []policy.ConcernID{policy.ImproveIdeoligion}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsIdeoligionPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	step := func() RoundsIdeoligionResult {
		call, epoch, done, err := reviewer.player.enter(context.Background(), "test", false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		result, err := planner.step(call, epoch, &stepArbiter{})
		if err != nil {
			t.Fatal(err)
		}
		return result
	}
	first := step()
	if first.Verdict != BuildingReasonAdmitted {
		t.Fatalf("not admitted: %+v", first)
	}
	if first.Comparison == nil || first.Comparison.Decision != policy.DesignImproves || !first.Comparison.WorkRelief || first.Comparison.Current.Restrictions <= first.Comparison.Candidate.Restrictions {
		t.Fatal("missing typed admission explanation", first.Comparison)
	}
	second := step()
	if second.Verdict != BuildingReasonExistingWork {
		t.Fatalf("duplicate method: %+v", second)
	}
	plan, err := db.LoadPlan(context.Background(), first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	intent, ok := plan.Spec.Actions()[0].IdeoligionReform()
	if !ok || intent.Design().Precepts[0] != "New" {
		t.Fatal("wrong journaled intent", intent)
	}
	rounds, err := db.LoadRounds(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	concern, _, err := db.Workable(context.Background(), rounds, policy.ImproveIdeoligion)
	if err != nil {
		t.Fatal(err)
	}
	saved, err := reformRecord(concern.Standard.Record)
	if err != nil || saved != intent {
		t.Fatal("pending save intent differs", err)
	}
	// A receipt is absent: recovery still follows the observed target/count.
	source.ideo.Facts.Precepts = []policy.HeldPrecept{{ID: "new-held", Def: "New"}}
	source.ideo.Facts.Development = domain.Known(policy.IdeoDevelopment{Fluid: true, ReformCount: 1, NextPoints: 12})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	settled, err := db.LoadStandard(context.Background(), concern.Standard.ID)
	if err != nil || settled.Standard.Record != "" || settled.Standard.Finding != domain.FindingMet {
		t.Fatalf("observation did not recover: %+v %v", settled.Standard, err)
	}
	request.Kind = store.PauseControl
	request.RequestID = "manual-reform"
	if _, err := reviewer.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	result, err := planner.step(context.Background(), context.Background(), nil)
	if err != nil || result.Verdict != BuildingReasonDisabled {
		t.Fatal("manual admitted reform", result, err)
	}
}

func TestReformCompletionRequiresObservedDesignAndProgression(t *testing.T) {
	before := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Old"}, Fluid: true}
	target := domain.IdeoligionDesign{Memes: before.Memes, Precepts: []string{"New"}, Fluid: true}
	intent, err := domain.NewIdeoligionReform("Ideo_1", before, target, 2)
	if err != nil {
		t.Fatal(err)
	}
	for _, tt := range []struct {
		name     string
		design   domain.IdeoligionDesign
		count    int
		complete bool
	}{{"before", before, 2, false}, {"target without progression", target, 2, false}, {"progression without target", before, 3, false}, {"completed", target, 3, true}, {"later unrelated reform", target, 4, false}} {
		t.Run(tt.name, func(t *testing.T) {
			ideo := policy.Ideoligion{Facts: policy.IdeoligionFacts{IdeoID: "Ideo_1", Development: domain.Known(policy.IdeoDevelopment{Fluid: true, ReformCount: tt.count})}}
			complete, known := observedReform(ideo, tt.design, intent).Value()
			if !known || complete != tt.complete {
				t.Fatalf("completion %v known %v", complete, known)
			}
		})
	}
	if _, known := observedReform(policy.Ideoligion{}, target, intent).Value(); known {
		t.Fatal("unknown world completed")
	}
	data, _ := json.Marshal(ideoligionRecord{intent.IdeoID(), intent.Expected(), intent.Design(), intent.Count()})
	resumed, err := reformRecord(string(data))
	if err != nil || resumed != intent {
		t.Fatal("save intent changed", err)
	}
}
