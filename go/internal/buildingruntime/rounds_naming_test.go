package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoundsNamingRecoveryUnknownAndRenewedDialog(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, _, _, _, n := roundsFixture(t)
	base := append([]*o.ReadIssue(nil), n.reply.GetObserved().Issues...)
	var previous domain.IncidentID
	for _, phase := range []string{"absent", "present", "unknown", "absent", "renewed"} {
		v := n.reply.GetObserved()
		v.Naming = nil
		v.Issues = append([]*o.ReadIssue(nil), base...)
		want := domain.FindingUnmet
		switch phase {
		case "absent":
			want = domain.FindingMet
			v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
		case "unknown":
			want = domain.FindingUnclear
		default:
			v.Naming = &o.ColonyNaming{WindowId: proto.Int32(42), FactionName: proto.String("Faction"), SettlementName: proto.String("Settlement")}
		}
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		// ConfirmColonyNames is an incident (#1078): recovered closes it,
		// unknown keeps the open one, a renewed dialog opens a new one.
		b, found := out.Review.Incident(policy.ConfirmColonyNames)
		if want == domain.FindingMet {
			if found {
				t.Fatal(phase, "recovered naming kept its occurrence", b)
			}
			continue
		}
		if !found || b.Situation != want.Situation() {
			t.Fatal(phase, b, found)
		}
		switch phase {
		case "present":
			previous = b.Incident
		case "unknown":
			if b.Incident != previous {
				t.Fatal("unknown reopened naming", b)
			}
		case "renewed":
			if b.Incident == previous {
				t.Fatal("renewed dialog did not reopen", b)
			}
		}
	}
}
