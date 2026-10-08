package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const consumptionMethod = "rimgovernor/observations_read_consumption"

// ReadConsumption reads native's saved realized-consumption ring (#2441):
// every completed hour after sinceHour (negative: the whole 60-day window),
// sparse. The ring is game-scoped, not map-scoped, so the request carries no
// identity; the caller keys what it keeps by the load it read under.
func (client *Client) ReadConsumption(ctx context.Context, sinceHour int) (policy.ConsumptionPage, error) {
	request := &o.ConsumptionRequest{}
	if sinceHour >= 0 {
		request.SinceHour = proto.Int32(int32(sinceHour))
	}
	reply := &o.ConsumptionReply{}
	raw, err := client.protoRead(ctx, consumptionMethod, request, reply)
	if err != nil {
		return policy.ConsumptionPage{}, err
	}
	var snapshot *o.ConsumptionSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ConsumptionReply_Observed:
		snapshot = v.Observed
	case *o.ConsumptionReply_Unavailable:
		return policy.ConsumptionPage{}, unavailable(v.Unavailable, raw)
	case *o.ConsumptionReply_Failure:
		return policy.ConsumptionPage{}, failure(v.Failure, raw)
	default:
		return policy.ConsumptionPage{}, contract("missing consumption outcome")
	}
	if snapshot == nil || snapshot.CurrentHour == nil || snapshot.FirstHour == nil || snapshot.GetCurrentHour() < 0 || snapshot.GetFirstHour() < 0 {
		return policy.ConsumptionPage{}, contract("invalid consumption hours")
	}
	page := policy.ConsumptionPage{CurrentHour: int(snapshot.GetCurrentHour()), FirstHour: int(snapshot.GetFirstHour())}
	previous := -1
	for _, hour := range snapshot.Hours {
		if hour == nil || hour.Hour == nil || int(hour.GetHour()) <= previous || int(hour.GetHour()) >= page.CurrentHour || int(hour.GetHour()) <= sinceHour {
			return policy.ConsumptionPage{}, contract("invalid consumption hour")
		}
		previous = int(hour.GetHour())
		entry := policy.ConsumptionHour{Hour: previous}
		for _, row := range hour.Rows {
			if row == nil || validID(row.GetDefinition()) != nil || row.Count == nil || !policy.KnownConsumptionReason(row.GetReason()) {
				return policy.ConsumptionPage{}, contract("invalid consumption row")
			}
			entry.Rows = append(entry.Rows, policy.ConsumptionRow{Resource: policy.Resource(row.GetDefinition()), Reason: row.GetReason(), Count: row.GetCount()})
		}
		page.Hours = append(page.Hours, entry)
	}
	return page, nil
}
