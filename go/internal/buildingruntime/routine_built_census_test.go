package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadConstructionBuildings is the fake colony construction census (#856).
// Until a test marks buildings built it is unavailable, so the census stays
// unknown and every applied building stays open work, as before the fake
// had a census at all.
func (n *routineNative) ReadConstructionBuildings(ctx context.Context, _ *c.Identity, ids []string) (*o.ListBuildingsReply, bridge.Result, error) {
	if n.built == nil || len(ids) != 0 {
		return &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}, bridge.Result{}, ctx.Err()
	}
	v := n.reply.GetObserved()
	count := uint64(len(n.built))
	snapshot := &o.BuildingsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
	for _, row := range n.built {
		snapshot.Buildings = append(snapshot.Buildings, proto.Clone(row).(*o.BuildingState))
	}
	return &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: snapshot}}, bridge.Result{}, ctx.Err()
}

// markBuilt lists every applied building intent of the journal's open plans
// as standing built in the fake census, keyed by its intent key, so the
// planners see it finished and census-aware retirement closes its plan.
func markBuilt(t *testing.T, db *store.Store, n *routineNative) {
	t.Helper()
	plans, err := db.LoadPlans(context.Background(), 256)
	if err != nil {
		t.Fatal(err)
	}
	if n.built == nil {
		n.built = map[domain.ActionID]*o.BuildingState{}
	}
	for _, plan := range plans {
		for _, p := range plan.Progress {
			b, ok := p.Action().Building()
			v := p.View()
			if !ok || v.Stage != domain.Completed || n.built[v.Action] != nil {
				continue
			}
			cell := &c.Cell{X: proto.Int32(b.Cell().X), Z: proto.Int32(b.Cell().Z)}
			row := &o.BuildingState{
				Building:      &o.EntityRef{Id: proto.String(fmt.Sprintf("built-%d", len(n.built))), DefName: proto.String(b.Definition()), MapId: proto.Int32(int32(v.Snapshot.Map)), Position: cell},
				OccupiedCells: []*c.Cell{cell},
				Status:        proto.String("built"),
				Rotation:      proto.String(strings.ToUpper(string(b.Rotation())[:1]) + string(b.Rotation())[1:]),
				IntentKey:     proto.String(fmt.Sprintf("%s/%d", v.Action, v.Attempt)),
			}
			if b.Stuff() != "" {
				row.Stuff = proto.String(b.Stuff())
			} else {
				row.Issues = []*o.ReadIssue{{Field: proto.String("stuff"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			}
			n.built[v.Action] = row
		}
	}
}
