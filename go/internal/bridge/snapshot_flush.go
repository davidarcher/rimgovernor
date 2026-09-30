package bridge

import (
	"context"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// methodFlushSnapshot makes native capture the next frame when an applied
// write is uncaptured (#1274); the reply does not wait for the capture.
const methodFlushSnapshot = "rimgovernor/observations_flush_snapshot"

type deferSnapshotKey struct{}

// WithDeferredSnapshot marks ctx's Actions/Apply calls defer_snapshot:
// native records the write without capturing a frame for it until
// FlushSnapshot, or its 1 s safety net. Read-after-write is unchanged: a
// later frame read still waits for a frame past the write.
func WithDeferredSnapshot(ctx context.Context) context.Context {
	return context.WithValue(ctx, deferSnapshotKey{}, true)
}

// DeferredSnapshot reports whether ctx carries WithDeferredSnapshot.
func DeferredSnapshot(ctx context.Context) bool {
	v, _ := ctx.Value(deferSnapshotKey{}).(bool)
	return v
}

type anyFrameKey struct{}

// WithAnyFrame lets a frame read skip the read-after-write wait: for
// facts no write changes (map bounds; the haul pawn read, used only
// for its Context).
func WithAnyFrame(ctx context.Context) context.Context {
	return context.WithValue(ctx, anyFrameKey{}, true)
}

func AnyFrame(ctx context.Context) bool {
	v, _ := ctx.Value(anyFrameKey{}).(bool)
	return v
}

// FlushSnapshot asks native to capture the writes deferred so far.
func (caller *Client) FlushSnapshot(ctx context.Context) error {
	reply := &o.FlushSnapshotReply{}
	raw, err := caller.protoCall(ctx, methodFlushSnapshot, &o.FlushSnapshotRequest{}, reply)
	if err != nil {
		return err
	}
	switch v := reply.Outcome.(type) {
	case *o.FlushSnapshotReply_Flushed:
		return nil
	case *o.FlushSnapshotReply_Failure:
		return failure(v.Failure, raw)
	}
	return contract("flush snapshot outcome missing")
}
