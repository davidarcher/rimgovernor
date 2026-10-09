package bridge

import (
	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// WithHazardThresholds sets the policy's Go-authored hazard thresholds
// (policy/hazard_thresholds.go) and returns p. Native applies exactly these
// and refuses a policy missing any.
func WithHazardThresholds(p *k.WatchPolicy) *k.WatchPolicy {
	p.SeriousSingleHitDamage = proto.Float32(policy.SeriousSingleHitDamage)
	p.SeriousSummaryHealthFloor = proto.Float32(policy.SeriousSummaryHealthFloor)
	p.SeriousBleedRateFloor = proto.Float32(policy.SeriousBleedRateFloor)
	p.SeriousVitalPartFloor = proto.Float32(policy.SeriousVitalPartFloor)
	p.ExplosiveNearMarginCells = proto.Float32(policy.ExplosiveNearMarginCells)
	p.MeleeReachCells = proto.Float32(policy.MeleeReachCells)
	p.InjurySeverityFloorTicks = proto.Uint32(policy.InjurySeverityFloorTicks)
	p.PredatorMarginCells = proto.Float32(policy.HuntPredatorMarginCells)
	return p
}
