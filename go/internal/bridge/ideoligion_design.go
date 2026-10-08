package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func WireIdeoligionDesign(v domain.IdeoligionDesign) *c.IdeoligionDesign {
	return &c.IdeoligionDesign{Memes: v.Memes, Precepts: v.Precepts, Fluid: proto.Bool(v.Fluid)}
}

func ideoligionReformAction(action domain.Action) (*o.Action, error) {
	v, ok := action.IdeoligionReform()
	if !ok {
		return nil, contract("not an ideoligion reform action")
	}
	return &o.Action{Intent: &o.Action_IdeoligionReform{IdeoligionReform: &o.IdeoligionReformIntent{IdeoId: proto.String(v.IdeoID()), Expected: WireIdeoligionDesign(v.Expected()), Design: WireIdeoligionDesign(v.Design()), ExpectedReformCount: proto.Int32(int32(v.Count()))}}}, nil
}

// ValidateIdeoligionDesign checks shape only. Native owns compatibility,
// faction restrictions, required issues and the game's selection limits.
func ValidateIdeoligionDesign(design *c.IdeoligionDesign) error {
	if design == nil || design.Fluid == nil || len(design.Memes) == 0 || len(design.Memes) > 5 || len(design.Precepts) > 256 {
		return contract("invalid ideoligion design")
	}
	for _, names := range [][]string{design.Memes, design.Precepts} {
		seen := map[string]bool{}
		for _, name := range names {
			if validID(name) != nil || seen[name] {
				return contract("invalid or duplicate ideoligion selection")
			}
			seen[name] = true
		}
	}
	return nil
}
