package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validatedQuestWorkers(rows []*o.QuestWorker) ([]*o.QuestWorker, error) {
	out := make([]*o.QuestWorker, 0, len(rows))
	seen := map[string]bool{}
	for _, row := range rows {
		if row == nil || validID(row.GetPawnId()) != nil || seen[row.GetPawnId()] {
			return nil, contract("invalid quest worker")
		}
		seen[row.GetPawnId()] = true
		if row.GetSocialLevel() < 0 || row.GetSocialLevel() > 20 || row.NegotiationAbility != nil && !validNonnegative(row.GetNegotiationAbility()) {
			return nil, contract("invalid quest diplomat")
		}
		if row.CarryCapacity != nil && !validNonnegative(row.GetCarryCapacity()) || row.CarriedMass != nil && !validNonnegative(row.GetCarriedMass()) {
			return nil, contract("invalid quest worker carry capacity")
		}
		stats := map[string]bool{}
		for _, rate := range row.Rates {
			if rate == nil || rate.GetStat() == "" || stats[rate.GetStat()] || rate.Rate == nil || math.IsNaN(rate.GetRate()) || math.IsInf(rate.GetRate(), 0) || rate.GetRate() < 0 {
				return nil, contract("invalid quest work rate")
			}
			stats[rate.GetStat()] = true
		}
		out = append(out, proto.Clone(row).(*o.QuestWorker))
	}
	return out, nil
}
