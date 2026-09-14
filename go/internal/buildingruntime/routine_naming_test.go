package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestRoutineNamingRecoveryUnknownAndRenewedDialog(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := routineFixture(t)
	base := append([]*o.ReadIssue(nil), n.reply.GetObserved().Issues...)
	var previous uint64
	for _, phase := range []string{"absent", "present", "unknown", "absent", "renewed"} {
		v := n.reply.GetObserved()
		v.Naming = nil
		v.Issues = append([]*o.ReadIssue(nil), base...)
		want := domain.NeedDeficit
		switch phase {
		case "absent":
			want = domain.NeedRecovered
			v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
		case "unknown":
			want = domain.NeedUnknown
		default:
			v.Naming = &o.ColonyNaming{WindowId: proto.Int32(42), FactionName: proto.String("Faction"), SettlementName: proto.String("Settlement")}
		}
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		found := false
		for _, binding := range out.Review.Goals {
			if binding.Need != policy.ConfirmColonyNames {
				continue
			}
			found = true
			g, err := db.LoadGoal(context.Background(), binding.Goal)
			if err != nil || g.Goal.Need != want {
				t.Fatal(phase, g, err)
			}
			if phase == "present" {
				previous = g.Goal.Epoch
			}
			if phase == "unknown" && g.Goal.Epoch != previous {
				t.Fatal("unknown reopened naming", g)
			}
			if phase == "renewed" && g.Goal.Epoch <= previous {
				t.Fatal("renewed dialog did not reopen", g)
			}
		}
		if !found {
			t.Fatal("naming goal missing")
		}
	}
}
