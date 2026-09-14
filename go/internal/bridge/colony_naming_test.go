package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestColonyNamingWindowContract(t *testing.T) {
	for _, change := range []string{"present", "absent", "unknown", "missing-id", "negative-id", "missing-faction-name", "missing-settlement-name", "invalid-faction-name", "unreviewed-issues", "conflict"} {
		t.Run(change, func(t *testing.T) {
			v := colonyFixture(t).GetObserved()
			v.Naming = &o.ColonyNaming{WindowId: proto.Int32(0), FactionName: proto.String("Faction"), SettlementName: proto.String("Settlement")}
			switch change {
			case "absent", "unknown":
				v.Naming = nil
				if change == "absent" {
					v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
				}
			case "missing-id":
				v.Naming.WindowId = nil
			case "negative-id":
				v.Naming.WindowId = proto.Int32(-1)
			case "missing-faction-name":
				v.Naming.FactionName = nil
			case "missing-settlement-name":
				v.Naming.SettlementName = nil
			case "invalid-faction-name":
				v.Naming.FactionName = proto.String("")
			case "unreviewed-issues":
				v.Naming.Issues = append(v.Naming.Issues, &o.ReadIssue{Field: proto.String("naming")})
			case "conflict":
				v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}})
			}
			err := ValidateColonyFacts(v, v.Context.Identity)
			valid := change == "present" || change == "absent" || change == "unknown"
			if (err == nil) != valid {
				t.Fatal(change, err)
			}
		})
	}
}
