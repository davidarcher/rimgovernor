package buildingruntime

import (
	"context"
	"errors"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// setFloors makes the fake colony spend enough of each resource that its
// runway target is at least floor. A resource whose stock covers fewer
// than ProjectionHorizonDays of the daily spend is short and its target is the
// spend over the horizon, so a floor of N is a daily spend of N over the
// horizon.
func (n *roundsNative) setFloors(floors map[policy.Resource]int64) {
	n.ring = map[policy.Resource]int64{}
	for resource, floor := range floors {
		n.ring[resource] = int64(math.Ceil(float64(floor) / policy.ProjectionHorizonDays))
	}
}

// ReadConsumption serves the ring setFloors built: one completed hour holding
// the day's spend, which a window shorter than a day reads as a day. Without
// floors the ring is unavailable, as it was before this fake served one.
func (n *roundsNative) ReadConsumption(_ context.Context, _ int) (policy.ConsumptionPage, error) {
	if n.ring == nil {
		return policy.ConsumptionPage{}, errors.New("no consumption ring")
	}
	var rows []policy.ConsumptionRow
	for resource, rate := range n.ring {
		rows = append(rows, policy.ConsumptionRow{Resource: resource, Reason: "bill_ingredient", Count: rate})
	}
	return policy.ConsumptionPage{CurrentHour: 1, FirstHour: 0, Hours: []policy.ConsumptionHour{{Hour: 0, Rows: rows}}}, nil
}
