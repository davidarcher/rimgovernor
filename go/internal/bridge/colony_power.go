package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateColonyPower(v *o.DevelopmentFacts, identity *c.Identity) error {
	if v == nil || !proto.Equal(v, &o.DevelopmentFacts{Power: v.Power, Completeness: v.Completeness}) {
		return contract("unsupported development facts")
	}
	if err := colonyCounts(v.Completeness, len(v.Power), 256); err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, row := range v.Power {
		if row == nil || row.Building == nil {
			return contract("missing power building")
		}
		b := row.Building
		ref := b.Building
		if ref == nil || validID(ref.GetId()) != nil || ref.MapId == nil || ref.GetMapId() != identity.GetMapId() || seen[ref.GetId()] || !proto.Equal(ref, &o.EntityRef{Id: ref.Id, MapId: ref.MapId}) {
			return contract("invalid power building identity")
		}
		seen[ref.GetId()] = true
		if !proto.Equal(b, &o.BuildingState{Building: ref, Service: b.Service, Settings: b.Settings}) {
			return contract("unsupported power building detail")
		}
		s := b.Service
		if s == nil || !proto.Equal(s, &o.BuildingServiceState{Connected: s.Connected, PowerOn: s.PowerOn, PowerOutputW: s.PowerOutputW, SwitchedOn: s.SwitchedOn, PowerNetId: s.PowerNetId}) {
			return contract("unsupported power service detail")
		}
		if b.Settings == nil || !proto.Equal(b.Settings, &o.BuildingSettings{Forbidden: b.Settings.Forbidden}) {
			return contract("unsupported power settings detail")
		}
		for _, value := range []*float64{row.BaseW, s.PowerOutputW} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e12) {
				return contract("invalid power wattage")
			}
		}
		if s.PowerNetId != nil && (validID(s.GetPowerNetId()) != nil || s.Connected != nil && !s.GetConnected()) {
			return contract("invalid power network identity")
		}
	}
	return nil
}
