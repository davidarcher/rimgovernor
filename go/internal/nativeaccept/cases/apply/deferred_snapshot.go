package apply

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	obs "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func init() {
	cases.Register(cases.Case{
		Name: "apply/deferred_snapshot",
		Scope: "Deferred snapshot capture (#1274): a burst of defer_snapshot applies captures no frame per write, " +
			"observations_flush_snapshot makes the next frame capture them, and without a flush the 1 s safety net " +
			"captures them. The capture schedule is native; a Go test cannot see it.",
		Start:  cases.LabStart(),
		Budget: 3 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runDeferredSnapshot,
	})
}

// deferredBurst is how many applies each burst sends.
const deferredBurst = 8

func runDeferredSnapshot(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	if !na.Contains(s.Names(), "rimgovernor/observations_flush_snapshot") {
		return fmt.Errorf("missing rimgovernor/observations_flush_snapshot in discovery")
	}
	encoded, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	identity := &c.Identity{}
	if err := protojson.Unmarshal(encoded, identity); err != nil {
		return err
	}
	opened := &obs.SnapshotStreamReply{}
	if err := wireTyped(ctx, h, "open-stream", "observations_open_snapshot_stream", &obs.SnapshotStreamRequest{}, opened); err != nil {
		return err
	}
	if opened.GetOpened().GetName() == "" {
		return fmt.Errorf("snapshot stream not opened: %v", opened)
	}
	reader, err := snapshotshm.Open(opened.GetOpened().GetName())
	if err != nil {
		return err
	}
	defer reader.Close()
	// burst sends deferredBurst refused applies (a refusal is still an
	// applied write to the stream) and reports the frames captured while
	// it ran and the write count it ends at.
	burst := func(label string, deferred bool) (frames uint64, writes int64, err error) {
		if !waitFrame(ctx, reader, reader.Writes(), 3*time.Second) {
			return 0, 0, fmt.Errorf("%s: no frame caught up before the burst", label)
		}
		start := reader.Head()
		for i := 0; i < deferredBurst; i++ {
			request := &o.ApplyRequest{Identity: identity, Actions: []*o.Action{deferredProbe(fmt.Sprintf("%s-%d", label, i))}}
			if deferred {
				request.DeferSnapshot = proto.Bool(true)
			}
			if err := wireTyped(ctx, h, fmt.Sprintf("%s-%d", label, i), "operations_apply", request, &o.ApplyReply{}); err != nil {
				return 0, 0, err
			}
		}
		return reader.Head() - start, reader.Writes(), nil
	}
	plain, _, err := burst("plain", false)
	if err != nil {
		return err
	}
	deferredFrames, writes, err := burst("deferred", true)
	if err != nil {
		return err
	}
	report["plain_burst_frames"], report["deferred_burst_frames"] = plain, deferredFrames
	// A paused game still captures once per wall second, so one periodic
	// frame may land inside the burst; one per write may not.
	if deferredFrames > 2 || deferredFrames >= plain {
		return fmt.Errorf("deferred burst captured %d frames (plain burst %d)", deferredFrames, plain)
	}
	flushed := &obs.FlushSnapshotReply{}
	flushedAt := time.Now()
	if err := wireTyped(ctx, h, "flush", "observations_flush_snapshot", &obs.FlushSnapshotRequest{}, flushed); err != nil {
		return err
	}
	if _, ok := flushed.Outcome.(*obs.FlushSnapshotReply_Flushed); !ok {
		return fmt.Errorf("flush: %v", flushed)
	}
	if !waitFrame(ctx, reader, writes, 3*time.Second) {
		return fmt.Errorf("no frame captured the flushed writes")
	}
	report["flush_to_frame_ms"] = time.Since(flushedAt).Milliseconds()
	// Without a flush the safety net captures the deferred writes.
	_, writes, err = burst("unflushed", true)
	if err != nil {
		return err
	}
	unflushedAt := time.Now()
	if !waitFrame(ctx, reader, writes, 3*time.Second) {
		return fmt.Errorf("the safety net never captured unflushed deferred writes")
	}
	report["unflushed_to_frame_ms"] = time.Since(unflushedAt).Milliseconds()
	return nil
}

// waitFrame waits up to limit for a committed frame captured at or past
// writes.
func waitFrame(ctx context.Context, reader *snapshotshm.Reader, writes int64, limit time.Duration) bool {
	deadline := time.Now().Add(limit)
	for time.Now().Before(deadline) && ctx.Err() == nil {
		frame, ok, err := reader.Latest()
		if err != nil {
			return false
		}
		if ok && frame.Writes >= writes {
			return true
		}
		after := uint64(0)
		if ok {
			after = frame.Number
		}
		reader.Wait(ctx, after, 50*time.Millisecond)
	}
	return false
}

// deferredProbe is an apply native refuses: a trade with a trader that
// does not exist.
func deferredProbe(key string) *o.Action {
	return &o.Action{Key: proto.String(key), Intent: &o.Action_Trade{Trade: &o.TradeIntent{Target: &c.TradeTarget{Kind: &c.TradeTarget_MapTrader{MapTrader: &c.MapTradeTarget{TraderId: proto.String("probe-trader")}}}, NegotiatorId: proto.String("probe-negotiator"),
		Step: &o.TradeIntent_End{End: &o.EndTrade{Kind: o.EndTradeKind_END_TRADE_KIND_CANCEL.Enum()}}}}}
}

// wireTyped calls a ProtoJSON tool with typed messages.
func wireTyped(ctx context.Context, h *na.Harness, label, method string, request, reply proto.Message) error {
	encoded, err := protojson.Marshal(request)
	if err != nil {
		return err
	}
	message, err := h.Wire(ctx, label, method, json.RawMessage(encoded))
	if err != nil {
		return err
	}
	raw, err := json.Marshal(message)
	if err != nil {
		return err
	}
	return protojson.Unmarshal(raw, reply)
}
