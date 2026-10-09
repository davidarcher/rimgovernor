package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Route evidence names each prey once and is either an evaluated verdict or a skip, never both or neither.
func TestHuntRouteEvidenceIsExplicit(t *testing.T) {
	census := &o.HuntCensus{Hunters: []*o.HunterFacts{{PawnId: proto.String("ann"), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}, Downed: proto.Bool(false), InMentalState: proto.Bool(false), HuntingActive: proto.Bool(true), CookingActive: proto.Bool(false), HuntingPriority: proto.Int32(3),
		Routes: []*o.HuntRoute{{PreyId: proto.String("a"), Safe: proto.Bool(true)}, {PreyId: proto.String("b"), Safe: proto.Bool(false)}, {PreyId: proto.String("c"), Skipped: proto.Bool(true)}}}}}
	if err := validateHuntCensus(census); err != nil {
		t.Fatal(err)
	}
	for name, routes := range map[string][]*o.HuntRoute{
		"neither":    {{PreyId: proto.String("a")}},
		"both":       {{PreyId: proto.String("a"), Safe: proto.Bool(true), Skipped: proto.Bool(true)}},
		"false skip": {{PreyId: proto.String("a"), Skipped: proto.Bool(false)}},
		"duplicate":  {{PreyId: proto.String("a"), Safe: proto.Bool(true)}, {PreyId: proto.String("a"), Safe: proto.Bool(false)}},
		"no id":      {{Safe: proto.Bool(true)}},
	} {
		v := proto.Clone(census).(*o.HuntCensus)
		v.Hunters[0].Routes = routes
		if validateHuntCensus(v) == nil {
			t.Fatal("accepted", name)
		}
	}
}

// Go asks for the herd radius and the hunt route budget on every colony facts read.
func TestColonyFactsRequestCarriesHerdRadiusAndHuntBudget(t *testing.T) {
	q := colonyFactsRequest(&c.Identity{}, true)
	if q.HerdRadius == nil || q.HuntRouteBudgetMs == nil || q.GetHerdRadius() != HerdRadius || q.GetHuntRouteBudgetMs() != HuntRouteBudgetMS {
		t.Fatal(q)
	}
}
