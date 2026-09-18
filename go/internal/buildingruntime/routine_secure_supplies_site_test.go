package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// siteZonePreviewer scripts one verdict per previewed patch, keyed by the
// patch's origin cell: a native refusal, an acceptance, or a transport error.
type siteZonePreviewer struct {
	verdicts  map[domain.Cell]error
	previewed []domain.Cell
}

var errSiteTransport = errors.New("transport")

func (n *siteZonePreviewer) PreviewZone(_ context.Context, _ *c.Identity, target bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	origin := target.Zone.Cells()[0]
	n.previewed = append(n.previewed, origin)
	switch err := n.verdicts[origin]; {
	case err == nil:
		return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
	default:
		return nil, bridge.Result{}, err
	}
}

func refusedSite() error {
	return &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("Zone creation requires fresh free ground: cell (134, 130) is not roofed, walkable, unzoned, empty storage ground.")}}
}

func coveredSites(origins ...domain.Cell) []policy.Rectangle {
	var sites []policy.Rectangle
	for _, o := range origins {
		sites = append(sites, policy.Rectangle{X: o.X, Z: o.Z, Width: 2, Height: 2})
	}
	return sites
}

// The #216 run: the nearest roofed 2x2 patch the census called free was
// refused natively five steps running, and the fallback never looked past
// it. A refused patch is that patch's verdict; the next one is previewed in
// the same step and its zone is what the method proposes.
func TestCoveredStorageSitesSkipARefusedPatchForTheNext(t *testing.T) {
	t.Parallel()
	first, second := domain.Cell{X: 134, Z: 130}, domain.Cell{X: 136, Z: 130}
	native := &siteZonePreviewer{verdicts: map[domain.Cell]error{first: refusedSite()}}
	value, cells, v, err := previewCoveredStorageSites(context.Background(), native, &c.Identity{}, "zone-token", "MedicineHerbal", coveredSites(first, second), "goal")
	if err != nil || v == nil || !v.GetAccepted() {
		t.Fatal(v, err)
	}
	if len(cells) != 4 || cells[0] != second || value.Cells()[0] != second {
		t.Fatal(cells, value.Cells())
	}
	if len(native.previewed) != 2 || native.previewed[0] != first || native.previewed[1] != second {
		t.Fatal(native.previewed)
	}
}

// Every previewed patch refused is "the fallback did not apply" (nil
// evaluation, nil error) so the caller moves on to the supply-room shell,
// and the search stops at the site bound rather than previewing the whole
// census.
func TestCoveredStorageSitesReportNoSiteWhenEveryPatchIsRefused(t *testing.T) {
	t.Parallel()
	var origins []domain.Cell
	verdicts := map[domain.Cell]error{}
	for i := int32(0); i < 6; i++ {
		o := domain.Cell{X: 100 + 2*i, Z: 100}
		origins = append(origins, o)
		verdicts[o] = refusedSite()
	}
	native := &siteZonePreviewer{verdicts: verdicts}
	_, cells, v, err := previewCoveredStorageSites(context.Background(), native, &c.Identity{}, "zone-token", "MedicineHerbal", coveredSites(origins...), "goal")
	if err != nil || v != nil || cells != nil {
		t.Fatal(cells, v, err)
	}
	if len(native.previewed) != maxSecureSuppliesZoneSites {
		t.Fatal(native.previewed)
	}
}

// A read that fails for any reason but a native refusal is the step's own
// failure: it is returned, not skipped, so a transport fault never reads as
// a refused site.
func TestCoveredStorageSitesReturnATransportFailure(t *testing.T) {
	t.Parallel()
	first, second := domain.Cell{X: 134, Z: 130}, domain.Cell{X: 136, Z: 130}
	native := &siteZonePreviewer{verdicts: map[domain.Cell]error{first: errSiteTransport}}
	_, _, v, err := previewCoveredStorageSites(context.Background(), native, &c.Identity{}, "zone-token", "MedicineHerbal", coveredSites(first, second), "goal")
	if !errors.Is(err, errSiteTransport) || v != nil || len(native.previewed) != 1 {
		t.Fatal(v, err, native.previewed)
	}
}
