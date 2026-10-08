package boundary

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestDispatchStockpileCarriesEveryComponentAndKeepsItsReplayKey(t *testing.T) {
	z, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, domain.GroundRect{Origin: domain.Cell{X: 1, Z: 1}, Width: 3, Height: 1})
	if err != nil {
		t.Fatal(err)
	}
	a, _ := domain.NewZoneCreateAction("store", z)
	p := placementOf(t, a)
	firstKey := IntentKey(p)
	p.Attempt = 2
	if IntentKey(p) != firstKey {
		t.Fatal("retry changed placement identity")
	}
	evidence := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{Created: []*r.CreatedZone{{ZoneId: proto.String("Zone_1"), Cells: []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(1)}}}, {ZoneId: proto.String("Zone_2"), Cells: []*c.Cell{{X: proto.Int32(3), Z: proto.Int32(1)}}}}}}}
	receipts, err := DispatchIntents(context.Background(), fixedLease{}, []executor.Placement{p}, evidenceWriter{evidence: evidence})
	if err != nil || len(receipts) != 1 || len(receipts[0].Stockpiles) != 2 || receipts[0].Stockpiles[1].ID != "Zone_2" || receipts[0].Zone != "" {
		t.Fatal(receipts, err)
	}
	for _, bad := range []*r.EffectEvidence{{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("Zone_1")}}}, {Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{Created: []*r.CreatedZone{{ZoneId: proto.String("Zone_1"), Cells: []*c.Cell{{X: proto.Int32(4), Z: proto.Int32(1)}}}}}}}} {
		if _, err := DispatchIntents(context.Background(), fixedLease{}, []executor.Placement{p}, evidenceWriter{evidence: bad}); err == nil {
			t.Fatal("invalid stockpile receipt accepted")
		}
	}
}
