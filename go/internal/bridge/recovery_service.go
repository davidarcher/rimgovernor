package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

var recoveryServiceJobs = map[domain.RecoveryMethod]string{
	domain.RecoveryServiceRepair:    JobRepair,
	domain.RecoveryServiceBreakdown: JobFixBrokenDownBuilding,
	domain.RecoveryServiceRefuel:    JobRefuel,

	domain.RecoveryServiceInvestigateMonolith: JobInvestigateMonolith,
	domain.RecoveryServiceActivateMonolith:    JobActivateMonolith,
}

// recoverAction is the GiveJobIntent of one pawn servicing one colony
// building (repair, breakdown, refuel). Native checks the pawn, the building
// and whether it still needs the service live, and the game's own WorkGiver
// builds the job (NativeRepairOperations.cs, NativeRecoveryOperations.cs).
func recoverAction(action domain.Action) (*o.Action, error) {
	v, ok := action.RecoveryService()
	if !ok {
		return nil, contract("not a recovery service action")
	}
	job, known := recoveryServiceJobs[v.Method()]
	if !known {
		return nil, contract("recover intent requires a service method")
	}
	return giveJob(v.Pawn(), job, v.Thing())
}
