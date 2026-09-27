package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

// Serve composes planners from the real client by type assertion; a method
// dropped from the client used to surface only as a startup crash.
var (
	_ RoutineHospitalSource     = (*bridge.Client)(nil)
	_ observation.RoutineSource = (*bridge.Client)(nil)
)
