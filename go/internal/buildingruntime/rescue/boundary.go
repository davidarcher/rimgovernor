package rescue

import (
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func NewRescuerFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RescuerFacts {
	facts := policy.RescuerFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") && !boundary.IssueField(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}
func NewRescuePatientFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RescuePatientFacts {
	facts := policy.RescuePatientFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), InBed: boundary.FactBool(row.InBed)}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") && row.Job.DefName != nil {
		facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") && health.BedId != nil {
		facts.BedID = domain.Known(health.GetBedId())
	}
	return facts
}
