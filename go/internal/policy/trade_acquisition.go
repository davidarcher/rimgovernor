package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type TradeAcquisitionKind string

const (
	TradeAcquireCaravan    TradeAcquisitionKind = "request_caravan"
	TradeAcquireOrbital    TradeAcquisitionKind = "request_orbital"
	TradeAcquireSettlement TradeAcquisitionKind = "scout_settlement"
)

// TradeAcquisitionOption is an unpriced attempt, never a SupplyCandidate.
// Participant identifies an existing seller; requests identify faction and
// trader kind because the future seller does not exist yet.
type TradeAcquisitionOption struct {
	ID                                       string
	Kind                                     TradeAcquisitionKind
	Participant                              domain.TradeParticipant
	Faction, TraderKind, Console, Negotiator string
	LastRequestTick                          int64
	Compatible                               []ResourceKey
	Eligible                                 domain.Fact[bool]
	LeadDays, LaborTicks                     domain.Fact[float64]
	GoodwillCost                             domain.Fact[int32]
	RelationAfterPayment                     domain.Fact[string]
	CooldownTicks                            domain.Fact[int64]
	OrbitalAvailable                         domain.Fact[bool]
	PassingShips                             int
	// Trip facts are computed for the exact proposed crew and cargo. SafeCrew
	// includes retaining enough labor at home; Packable includes native gates.
	SafeCrew, Packable, RoutesReachable         domain.Fact[bool]
	OutboundDays, ReturnDays, FoodDays, RotDays domain.Fact[float64]
}

// TradeAcquisitionProposal holds an attempt for an unmet demand. Gap is
// explanatory demand, not expected stock, purchased goods or delivery credit.
type TradeAcquisitionProposal struct {
	Option TradeAcquisitionOption
	Demand SupplyDemand
	Gap    float64
}
type TradeAcquisitionRequest struct {
	Demands         []SupplyDemandResult
	Options         []TradeAcquisitionOption
	Silver, Reserve domain.Fact[int64]
	ActiveID        string
}
type TradeAcquisitionDecision struct {
	Option TradeAcquisitionOption
	Reason string
}
type TradeAcquisitionPlan struct {
	Proposal  *TradeAcquisitionProposal
	Decisions []TradeAcquisitionDecision
}

// PlanTradeAcquisition selects one attempt by known lead, known labor and
// stable ID. A pending attempt earns no supply credit and does not modify the
// caller's local/priced portfolio. ActiveID comes from the owning executor.
func PlanTradeAcquisition(in TradeAcquisitionRequest) TradeAcquisitionPlan {
	var out TradeAcquisitionPlan
	silver, sk := in.Silver.Value()
	reserve, rk := in.Reserve.Value()
	options := slices.Clone(in.Options)
	sort.Slice(options, func(i, j int) bool {
		a, ak := options[i].LeadDays.Value()
		b, bk := options[j].LeadDays.Value()
		if ak != bk {
			return ak
		}
		if a != b {
			return a < b
		}
		a, ak = options[i].LaborTicks.Value()
		b, bk = options[j].LaborTicks.Value()
		if ak != bk {
			return ak
		}
		if a != b {
			return a < b
		}
		return options[i].ID < options[j].ID
	})
	for _, o := range options {
		reason := acquisitionAttemptGate(o)
		if !sk || !rk || reserve < 0 || silver <= reserve {
			reason = "insufficient_silver"
		}
		if in.ActiveID != "" {
			reason = "active_attempt"
		}
		var matched *SupplyDemandResult
		lead, _ := o.LeadDays.Value()
		compatible := false
		for i := range in.Demands {
			d := &in.Demands[i]
			if d.Gap <= 0 || !slices.Contains(o.Compatible, d.Demand.Good) {
				continue
			}
			compatible = true
			if lead > d.Demand.HorizonDays {
				continue
			}
			if matched == nil || d.Demand.Priority > matched.Demand.Priority {
				matched = d
			}
		}
		if reason == "" && matched == nil {
			if compatible {
				reason = "late_arrival"
			} else {
				reason = "no_compatible_demand"
			}
		}
		if reason == "" && out.Proposal == nil {
			out.Proposal = &TradeAcquisitionProposal{Option: o, Demand: matched.Demand, Gap: matched.Gap}
			reason = "selected"
		} else if reason == "" {
			reason = "other_attempt_selected"
		}
		out.Decisions = append(out.Decisions, TradeAcquisitionDecision{o, reason})
	}
	return out
}

func acquisitionAttemptGate(o TradeAcquisitionOption) string {
	eligible, ek := o.Eligible.Value()
	lead, lk := o.LeadDays.Value()
	labor, wk := o.LaborTicks.Value()
	if !foodID(o.ID) || !ek || !lk || !wk || !foodNumber(lead) || !foodNumber(labor) {
		return "unknown_eligibility_or_cost"
	}
	if !eligible {
		return "ineligible"
	}
	switch o.Kind {
	case TradeAcquireCaravan, TradeAcquireOrbital:
		cost, ck := o.GoodwillCost.Value()
		relation, rk := o.RelationAfterPayment.Value()
		cooldown, dk := o.CooldownTicks.Value()
		if !ck || !rk || !dk || cost < 0 || cooldown < 0 || !foodID(o.Faction) || !foodID(o.TraderKind) || !foodID(o.Console) || !foodID(o.Negotiator) {
			return "unknown_request_facts"
		}
		if relation != "Ally" {
			return "alliance_loss"
		}
		if cooldown != 0 {
			return "cooldown"
		}
		if o.Kind == TradeAcquireOrbital {
			available, known := o.OrbitalAvailable.Value()
			if !known || !available {
				return "orbital_unavailable"
			}
			if o.PassingShips != 0 {
				return "passing_ships"
			}
		}
	case TradeAcquireSettlement:
		if o.Participant.Kind != domain.TradeParticipantSettlement || o.Participant.Validate() != nil {
			return "unknown_target"
		}
		safe, sk := o.SafeCrew.Value()
		pack, pk := o.Packable.Value()
		food, fk := o.FoodDays.Value()
		rot, rk := o.RotDays.Value()
		outbound, ok := o.OutboundDays.Value()
		home, hk := o.ReturnDays.Value()
		reachable, reachableKnown := o.RoutesReachable.Value()
		if !sk || !pk || !fk || !rk || !ok || !hk || !reachableKnown || !foodNumber(food) || !foodNumber(rot) || !foodNumber(outbound) || !foodNumber(home) || lead < outbound+home {
			return "unknown_trip_facts"
		}
		if !reachable {
			return "unreachable_route"
		}
		if !safe {
			return "unsafe_crew"
		}
		if !pack {
			return "cannot_pack"
		}
		if food < lead {
			return "insufficient_travel_food"
		}
		if rot < lead {
			return "spoiling_cargo"
		}
	default:
		return "unknown_kind"
	}
	return ""
}
