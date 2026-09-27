package observation

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineMirror is the reviewer's colony mirror (#795), attached to the
// routine reading's context: when set, its reads replace the source's so
// the mirror publishes the rows read and answers from them.
type RoutineMirror struct {
	// Pawns is the colonists' pawn detail read in place of the source's
	// ReadRoutinePawns.
	Pawns func(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	// Colony is the colony facts census read (no extra definitions) in
	// place of the source's.
	Colony func(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
}

type routineMirrorKey struct{}

// WithRoutineMirror attaches mirror to ctx for the routine reading under it.
func WithRoutineMirror(ctx context.Context, mirror RoutineMirror) context.Context {
	return context.WithValue(ctx, routineMirrorKey{}, mirror)
}

// RoutineMirrorFrom returns the mirror ctx carries; a zero value reads
// through the source.
func RoutineMirrorFrom(ctx context.Context) RoutineMirror {
	mirror, _ := ctx.Value(routineMirrorKey{}).(RoutineMirror)
	return mirror
}
