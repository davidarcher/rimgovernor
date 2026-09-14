package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// moodReliefDispatchFacts decodes the two native fencing values EnsureMood-*
// relief dispatch must supply immediately before commit (bridge.MoodReliefWriter's
// ExpectedJob and ExpectedScheduleDef): the pawn's exact current job load ID (or
// explicitly idle, boundary.JobEvidenceExpectedJob) and the def name of their
// current timetable assignment for the map-local hour absTicks resolves to at
// the given map longitude (boundary.ExpectedScheduleDef). Mirrors
// wasteCandidateFacts's role of turning one wire PawnState row into typed
// dispatch facts, but unlike waste's census-time policy.WastePawn these two
// values are dispatch-time fencing tokens rather than detection-time census
// facts, so they stay a sibling planner-local decode rather than an addition
// to policy.MoodPawn (which observation.routineMood already fully decodes for
// need detection and is persisted via store's mood history).
//
// job's tracker/queue itself being unavailable (PawnState's own "job" issue)
// is a coarser unavailability checked here via boundary.IssueField before
// decoding row.Job, per JobEvidenceExpectedJob's own doc comment. A missing or
// malformed schedule, or an unknown longitude, is reported the same way: false,
// never a guessed fencing value.
func moodReliefDispatchFacts(row *n.PawnState, absTicks int64, longitude float64) (bridge.MoodReliefExpectedJob, string, bool) {
	if row == nil || boundary.IssueField(row.Issues, "job") {
		return bridge.MoodReliefExpectedJob{}, "", false
	}
	job, ok := boundary.JobEvidenceExpectedJob(row.GetJob())
	if !ok {
		return bridge.MoodReliefExpectedJob{}, "", false
	}
	settings := row.GetSettings()
	if settings == nil || boundary.IssueField(settings.Issues, "schedule") {
		return bridge.MoodReliefExpectedJob{}, "", false
	}
	def, ok := boundary.ExpectedScheduleDef(settings.GetSchedule(), absTicks, longitude)
	if !ok {
		return bridge.MoodReliefExpectedJob{}, "", false
	}
	return job, def, true
}
