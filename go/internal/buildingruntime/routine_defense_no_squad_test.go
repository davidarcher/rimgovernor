package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// hiveTestNative is an equipTestNative whose census lists one hostile
// building and whose colonists are all downed.
type hiveTestNative struct{ *equipTestNative }

func (n *hiveTestNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, err := n.equipTestNative.ReadEmergency(ctx, id)
	for i := range v.Facts.Colonists {
		v.Facts.Colonists[i].Downed = domain.Known(true)
	}
	v.Facts.Threats = append(v.Facts.Threats, policy.EmergencyThreat{ID: "hive", Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), Distance: domain.Known(12.0), SnapshotToken: "cas", Definition: "Hive"})
	return v, r, err
}
func (n *hiveTestNative) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	reply, r, err := n.equipTestNative.ReadCombatPawns(ctx, id, ids)
	for _, row := range reply.GetObserved().GetPawns() {
		row.Downed = proto.Bool(true)
	}
	return reply, r, err
}

// A hostile building with every colonist downed is a deficit the planner
// cannot answer: it reports no eligible squad so the clock scheduler watches
// the building instead of holding on it (#326).
func TestRoutineDefenseReportsNoSquadForAnUnanswerableBuilding(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	native := &hiveTestNative{&equipTestNative{routineNative: n, ids: []string{"a"}}}
	planner, err := NewRoutineDefensePlanner(r, native)
	if err != nil {
		t.Fatal(err)
	}
	current, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoutineFacts{Workers: domain.Known(1), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
	if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: current.Revision, Current: session.State().Snapshot, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
		t.Fatal(err)
	}
	got, err := planner.Step(ctx)
	if err != nil || got.Reason != BuildingMethodNoSquad || got.Plan != "" {
		t.Fatal(got, err)
	}
}
