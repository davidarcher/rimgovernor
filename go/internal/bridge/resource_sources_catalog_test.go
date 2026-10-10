package bridge

import (
	"context"

	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// resourceCatalogHandler answers the definition catalog read the way the game
// does for the defs the resource source tests use (a mineable rock class and
// Steel with its stack limit) and every other call with next.
func resourceCatalogHandler(next func(context.Context, nativeArgument) (*callResult, error)) func(context.Context, nativeArgument) (*callResult, error) {
	return func(ctx context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != methodDefinitionCatalog {
			return next(ctx, arg)
		}
		reply := catalogReply(resourceSourcesContext())
		v := reply.GetObserved()
		v.ThingDefs = []*d.ThingDef{{DefName: "Steel", ThingClass: "Verse.ThingWithComps", StackLimit: 75}, {DefName: "MineableSteel", ThingClass: "RimWorld.Mineable"}}
		v.ThingFacts = []*o.ThingDefFacts{{DefName: "Steel"}, {DefName: "MineableSteel"}}
		v.ClassChains = []*o.ClassChain{{Name: "Verse.ThingWithComps"}, {Name: "RimWorld.Mineable"}}
		return pbResult(reply), nil
	}
}

// TestResourceSourceMethodsReadTheDefRows is the parity check against the
// native census switch it replaced (thing is Mineable, plant.IsTree): over the
// recorded vanilla defs rock is mined, a tree is cut and a crop is harvested.
func TestResourceSourceMethodsReadTheDefRows(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	for def, want := range map[string]policy.ResourceSourceMethod{"MineableSteel": policy.ResourceSourceMine, "MineableGold": policy.ResourceSourceMine, "Plant_TreeOak": "cut", "Plant_Rice": "harvest", "Plant_Berry": "harvest"} {
		if got, err := catalog.ResourceSourceMethodOf(def); err != nil || got != want {
			t.Errorf("%s is %q (%v), want %q", def, got, err, want)
		}
	}
	if limit := catalog.ThingDef("Steel").GetStackLimit(); limit != 75 {
		t.Errorf("Steel stack limit %d", limit)
	}
}
