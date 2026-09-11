package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestRoutineWorkReadbackRecoversInBothModesAndPreservesUnknown(t *testing.T) {
	r, _, _, _, n := routineFixture(t)
	r.native = &routineMedicalNative{routineNative: n}
	v := n.reply.GetObserved()
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	for _, phase := range []string{"numbered", "mismatch", "unknown", "checkbox"} {
		want := domain.NeedRecovered
		switch phase {
		case "mismatch":
			row.Settings.Work[0].Priority = proto.Int32(3)
			want = domain.NeedDeficit
		case "unknown":
			row.Settings.ManualWorkPriorities = nil
			want = domain.NeedUnknown
		case "checkbox":
			row.Settings.ManualWorkPriorities = proto.Bool(false)
			for _, w := range row.Settings.Work {
				if w.GetPriority() > 0 {
					w.Priority = proto.Int32(3)
				}
			}
		}
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(phase, err)
		}
		found := false
		for _, assessment := range out.Needs.Assessments {
			if assessment.ID == policy.EnsureWorkAssignments {
				found = true
				if assessment.Need != want {
					t.Fatal(phase, assessment, want)
				}
			}
		}
		if !found {
			t.Fatal("work assessment missing")
		}
	}
}
