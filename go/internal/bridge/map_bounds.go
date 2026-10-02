package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// MapBounds preserves the native context of known map dimensions. It does not
// certify placement safety, resources or permission to write.
type MapBounds struct {
	Context *c.ObservationContext
	Bounds  policy.Bounds
}

// ReadMapBounds reads the one-cell grid at a caller-observed anchor for
// the map's dimensions. A failed, incomplete or mismatched observation
// never supplies default bounds.
func (client *Client) ReadMapBounds(ctx context.Context, identity *c.Identity, anchor domain.Cell) (MapBounds, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return MapBounds{}, Result{}, err
	}
	if anchor.X < 0 || anchor.Z < 0 {
		return MapBounds{}, Result{}, contract("negative bounds anchor")
	}
	// Bounds never change: the read takes any frame, not one past the
	// last write, so a dispatch after a deferred write (#1274) does not
	// wait out the capture safety net.
	read, raw, err := client.readCells(WithAnyFrame(ctx), identity, policy.Rectangle{X: anchor.X, Z: anchor.Z, Width: 1, Height: 1}, false, false)
	if err != nil {
		return MapBounds{}, raw, err
	}
	return MapBounds{Context: proto.Clone(read.Context).(*c.ObservationContext), Bounds: read.Bounds}, raw, nil
}
