package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSleepingProjectionPreservesUnassignedAndUnknown(t *testing.T) {
	u := &o.UpkeepFacts{People: []*o.UpkeepPerson{{Pawn: &commonpb.Ref{Id: proto.String("pawn")}}}}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := sleepingOf(t, v, bridge.Buildings{}, nil).Value()
	if !known || len(r.People) != 1 {
		t.Fatal(r, known)
	}
	if bed, known := r.People[0].OwnedBed.Value(); !known || bed != "" {
		t.Fatal("unassigned became unknown")
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("beds")}}
	if _, known := sleepingOf(t, v, bridge.Buildings{}, nil).Value(); known {
		t.Fatal("failed bed census became empty")
	}
}

// sleepingOf is colonySleeping that fails the test on an error.
func sleepingOf(t *testing.T, v *o.ColonyFactsSnapshot, buildings bridge.Buildings, catalog *bridge.DefinitionCatalog) domain.Fact[policy.SleepingObservation] {
	t.Helper()
	r, err := colonySleeping(v, buildings, catalog)
	if err != nil {
		t.Fatal(err)
	}
	return r
}
