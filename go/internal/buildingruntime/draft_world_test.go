package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"google.golang.org/protobuf/proto"
)

func TestDraftWorldReadUsesFreshIdentityWithoutAuthority(t *testing.T) {
	t.Parallel()
	bound, native := draft.NewFixture(t)
	first, err := bound.ReadWorld(context.Background())
	if err != nil || first.Load != "load" {
		t.Fatal(first, err)
	}
	native.Ctx.Identity.LoadToken = proto.String("replacement")
	second, err := bound.ReadWorld(context.Background())
	if err != nil || second.Load != "replacement" || native.Identities != 2 {
		t.Fatal(second, err)
	}
	unavailable := errors.New("identity unavailable")
	native.ReadErr = unavailable
	if _, err = bound.ReadWorld(context.Background()); !errors.Is(err, unavailable) {
		t.Fatal(err)
	}
	if native.Leases != 0 || native.Writes != 0 || native.Releases != 0 {
		t.Fatal("world observation acquired permission or changed native state")
	}
}
