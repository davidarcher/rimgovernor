package buildingruntime

import (
	"testing"

	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func moodReliefFactsRow(job *n.JobEvidence, schedule []*n.TimetableSlot) *n.PawnState {
	return &n.PawnState{
		Job:      job,
		Settings: &n.PawnSettings{Schedule: schedule},
	}
}

func TestMoodReliefDispatchFactsIdleAndScheduled(t *testing.T) {
	row := moodReliefFactsRow(
		&n.JobEvidence{Issues: []*n.ReadIssue{{Field: proto.String("current_job")}}},
		[]*n.TimetableSlot{{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Sleep")}},
	)
	job, def, ok := moodReliefDispatchFacts(row, 0, 0)
	if !ok || !job.Idle || job.JobID != nil || def != "Sleep" {
		t.Fatalf("expected idle job and Sleep schedule, got job=%+v def=%q ok=%v", job, def, ok)
	}
}

func TestMoodReliefDispatchFactsKnownJob(t *testing.T) {
	row := moodReliefFactsRow(
		&n.JobEvidence{LoadId: proto.String("17")},
		[]*n.TimetableSlot{{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Anything")}},
	)
	job, def, ok := moodReliefDispatchFacts(row, 0, 0)
	if !ok || job.Idle || job.JobID == nil || *job.JobID != 17 || def != "Anything" {
		t.Fatalf("expected job id 17 and Anything schedule, got job=%+v def=%q ok=%v", job, def, ok)
	}
}

func TestMoodReliefDispatchFactsUnknownCases(t *testing.T) {
	knownJob := &n.JobEvidence{LoadId: proto.String("17")}
	knownSchedule := []*n.TimetableSlot{{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Anything")}}
	cases := []*n.PawnState{
		nil,
		{Issues: []*n.ReadIssue{{Field: proto.String("job")}}, Job: knownJob, Settings: &n.PawnSettings{Schedule: knownSchedule}}, // job tracker unavailable
		moodReliefFactsRow(nil, knownSchedule), // no job evidence at all
		moodReliefFactsRow(knownJob, nil),      // no settings/schedule at all is unknown, not "no slots"
		{Job: knownJob},                        // settings entirely missing
	}
	for i, row := range cases {
		if _, _, ok := moodReliefDispatchFacts(row, 0, 0); ok {
			t.Fatalf("case %d: expected unknown, got ok", i)
		}
	}
}

func TestMoodReliefDispatchFactsScheduleIssueUnknown(t *testing.T) {
	row := &n.PawnState{
		Job:      &n.JobEvidence{LoadId: proto.String("17")},
		Settings: &n.PawnSettings{Issues: []*n.ReadIssue{{Field: proto.String("schedule")}}},
	}
	if _, _, ok := moodReliefDispatchFacts(row, 0, 0); ok {
		t.Fatalf("expected unknown when schedule issue present")
	}
}
