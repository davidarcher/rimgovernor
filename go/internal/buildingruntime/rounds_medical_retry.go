package buildingruntime

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// medicalWaitTicks bounds one clock window lent when a standing CriticalMedical
// deficit has no method to run: no doctor/rescuer-patient pair the policy will
// select, or a patient native has refused (the shared refusal budget). The emergency freezes development
// (every goal "not selected: emergency") and the tend planner contributes no
// plan, so without a lent window the step reports no work and the clock parks
// on no_work for as long as the emergency stands. Progress needs game time: a doctor
// finishing the job it is on, a patient reaching a bed, or the injury tending
// itself out. Lending the same bound the stock waits use keeps the world moving
// under the emergency without pretending a method ran.
const medicalWaitTicks = stockWaitTicks

// nextWaveMethod is nextEquipWaveMethod for any wave prefix.
func nextWaveMethod(goal store.StandardState, prefix string) domain.MethodID {
	bound := map[domain.MethodID]bool{}
	for _, m := range goal.History {
		bound[m.Method] = true
	}
	for _, m := range goal.Methods {
		bound[m.Method] = true
	}
	for i := 0; ; i++ {
		if method := domain.MethodID(fmt.Sprintf("%s%d", prefix, i)); !bound[method] {
			return method
		}
	}
}
