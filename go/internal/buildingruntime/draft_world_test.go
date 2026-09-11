package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"google.golang.org/protobuf/proto"
)

func TestDraftWorldReadUsesFreshIdentityWithoutAuthority(t *testing.T) {
	boundary, native := draftBoundaryFixture(t)
	first, err := boundary.ReadWorld(context.Background())
	if err != nil || first.Load != "load" {
		t.Fatal(first, err)
	}
	native.context.Identity.LoadToken = proto.String("replacement")
	second, err := boundary.ReadWorld(context.Background())
	if err != nil || second.Load != "replacement" || native.identities != 2 {
		t.Fatal(second, err)
	}
	unavailable := errors.New("identity unavailable")
	native.readErr = unavailable
	if _, err = boundary.ReadWorld(context.Background()); !errors.Is(err, unavailable) {
		t.Fatal(err)
	}
	if native.leases != 0 || native.writes != 0 || native.releases != 0 {
		t.Fatal("world observation acquired permission or changed native state")
	}
}
