package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Remote work (loot recovery, ruin salvage, surface mining outside the base
// extent) is held for one of four explicit reasons (#525). Selection reports
// them on the review's hold rows; dispatch mirrors them as the pending
// action's HeldReason through the executor's refusal table. Every other
// selection reason (the reach stage, demand) keeps its own text.
const (
	// RemoteHoldThreat: a known hostile is on the map. The threat explains
	// the hold even where it also makes the route read unsafe.
	RemoteHoldThreat = "threat_present"
	// RemoteHoldRouteUnsafe: native walked no safe colonist route to the
	// target and back to storage, or the target itself is unsafe.
	RemoteHoldRouteUnsafe = "route_unsafe"
	// RemoteHoldRoofSupport: removing the target would drop a roof, or the
	// deposit is not an open-surface rock.
	RemoteHoldRoofSupport = "roof_support_risk"
	// RemoteHoldMissingStorage: no accepting storage headroom for the yield.
	RemoteHoldMissingStorage = "missing_storage"
	// RemoteHoldUrgentWork: urgent colony work outranks acquisition.
	RemoteHoldUrgentWork = "urgent_competing_work"
)

// The dispatch-time mirrors: a pending remote action's inspection refuses
// with these and the executor records them as the action's hold reason.
const (
	UnsafeRoute         Reason = "unsafe_route"
	RoofSupportRisk     Reason = "roof_support_risk"
	StorageMissing      Reason = "missing_storage"
	UrgentCompetingWork Reason = "urgent_competing_work"
)

type RemoteWorkKind string

const (
	RemoteLoot    RemoteWorkKind = "loot"
	RemoteSalvage RemoteWorkKind = "salvage"
	RemoteMining  RemoteWorkKind = "mining"
)

// RemoteWorkHold names one candidate a remote selection kept back.
type RemoteWorkHold struct {
	Kind   RemoteWorkKind
	Target string
	Reason string
}

// RemoteWorkRequest carries what every remote selection filters against: the
// reach readiness (#520), the demand (#521) and the urgent work competing for
// the colonists (#525). Extent geometry decides which candidates are base
// scope; the rest are remote.
type RemoteWorkRequest struct {
	Reach       ResourceReachRequest
	Demand      domain.Fact[[]ResourceDemand]
	Competition AcquisitionCompetition
}

// UrgentWorkPriority outranks every demand priority (1-100) so urgent work
// holds acquisition whatever the shortage.
const UrgentWorkPriority = 100

// UrgentWorkCompeting is known true while a colonist is an urgent patient
// (bleeding, or downed with a tend outstanding) or a disaster still disrupts
// a colony service. Unknown patients leave it unknown; the dispatch-time
// emergency check holds on the same unknown facts.
func UrgentWorkCompeting(f RoutineFacts) domain.Fact[bool] {
	patients, known := f.UrgentPatients.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if patients > 0 {
		return domain.Known(true)
	}
	if f.Disaster != nil {
		switch f.Disaster.Phase {
		case DisasterDisrupted, DisasterSurvival:
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// RemoteCompetition turns the urgent work verdict into the scoring
// competition: unknown urgency competes with nothing, since the emergency
// path holds dispatch on unknown facts and reach never widens on them.
func RemoteCompetition(f RoutineFacts) AcquisitionCompetition {
	if urgent, known := UrgentWorkCompeting(f).Value(); known && urgent {
		return AcquisitionCompetition{UrgentPriority: UrgentWorkPriority}
	}
	return AcquisitionCompetition{}
}

// RemoteHoldReason folds a reach, eligibility or demand reason into the
// explicit vocabulary above and returns any other reason unchanged. An
// "ineligible" salvage or loot candidate failed native route safety; an
// ineligible mining deposit failed the open-surface roof check.
func RemoteHoldReason(kind RemoteWorkKind, reason string) string {
	base := reason
	if strings.HasPrefix(base, "outside_") || strings.HasPrefix(base, "demand:") {
		if i := strings.Index(base, ":"); i >= 0 {
			base = base[i+1:]
		}
	}
	switch base {
	case "threat_present":
		return RemoteHoldThreat
	case "no_storage", "storage_unknown", "no_storage_headroom":
		return RemoteHoldMissingStorage
	case "competing_urgent_work":
		return RemoteHoldUrgentWork
	case "route_impassable", "route_unknown":
		return RemoteHoldRouteUnsafe
	case "roof_blocker":
		return RemoteHoldRoofSupport
	case "ineligible":
		if kind == RemoteMining {
			return RemoteHoldRoofSupport
		}
		return RemoteHoldRouteUnsafe
	}
	return reason
}

// remoteThreatHold reports the threat hold ahead of any route verdict: a
// hostile on the map is what the operator needs to know, and the reach
// stage collapses to base on the same fact.
func remoteThreatHold(r ResourceReachRequest) string {
	if threat, known := r.Threat.Value(); known && threat {
		return RemoteHoldThreat
	}
	return ""
}
