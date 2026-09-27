package snapshot

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A combat recording (#853) is a fight in the serve's stream: the #858
// snapshot frames the fight decided from, and one line per stop that wrote
// to the journal (orders as evidence, a new formation, an admission). A
// stop that changes nothing writes nothing, as in the plan's evidence.
//
//	{"Tick": t, "CombatFrame": <BundleSnapshot ProtoJSON>}
//	{"Tick": t, "Combat": <CombatStop>}
//
// A frame line is the frame's combat part (bridge.DecodeCombat's input:
// context, emergency census, combat pawns, detail rows, building lines of
// fire), written before the first stop that decided from it; its events
// are only those past the previous frame line's, so the event log is
// lossless and each event is written once. A stop line holds no inputs:
// the replay rebuilds the view from the frame line before it, which must be
// at the stop's tick.

// CombatStop is one fight stop as recorded.
type CombatStop struct {
	Plan domain.PlanID
	Tick domain.Tick
	Stop policy.StopEvent
	// Orderable is the fight's owned drafts at the stop, from the plan's
	// progress; Layout the stored defense layout the view held, when one
	// was. Both are journal state no frame carries.
	Orderable []domain.PawnID      `json:",omitempty"`
	Layout    *policy.CombatLayout `json:",omitempty"`
	// Ask and Reply are the stop's one geometry round trip, when it had one.
	Ask                 *policy.GeometryRequest `json:",omitempty"`
	Reply               policy.GeometryReply
	MemoryIn, MemoryOut policy.CombatMemory
	// Evidence is set when the stop sent orders: the plan's evidence
	// record at Tick (store.CombatStopRecord) holds them and their
	// per-pawn results.
	Evidence bool `json:",omitempty"`
}

// CombatRecordingMaxStops and CombatRecordingMaxBytes cap a committed
// combat recording (#853).
const (
	CombatRecordingMaxStops = 40
	CombatRecordingMaxBytes = 300 << 10
)

// combatWriter is a stream's combat frame as last recorded: the frame
// without its events, and the newest event written.
type combatWriter struct {
	frame *o.BundleSnapshot
	mark  *mp.Watermark
}

// RecordCombatStop appends one fight stop to this process's stream in dir,
// after the frame it decided from when that frame is not the last one
// recorded.
func RecordCombatStop(dir string, frame *o.BundleSnapshot, s CombatStop) error {
	if frame == nil {
		return fmt.Errorf("combat stop at %d without a frame", s.Tick)
	}
	data, err := Encode(s)
	if err != nil {
		return err
	}
	var stop bytes.Buffer
	if err = json.Compact(&stop, data); err != nil {
		return err
	}
	streamsMu.Lock()
	defer streamsMu.Unlock()
	rec, err := openStream(dir, s.Tick)
	if err != nil {
		return err
	}
	bare := proto.Clone(frame).(*o.BundleSnapshot)
	bare.CombatEvents = nil
	var fresh []*mp.CombatEventRow
	for _, row := range frame.CombatEvents {
		if rec.combat.mark == nil || bridge.CombatBefore(rec.combat.mark, row.At) {
			fresh = append(fresh, row)
		}
	}
	if len(fresh) > 0 || rec.combat.frame == nil || !proto.Equal(bare, rec.combat.frame) {
		line := proto.Clone(bare).(*o.BundleSnapshot)
		line.CombatEvents = fresh
		raw, err := protojson.Marshal(line)
		if err != nil {
			return err
		}
		var compact bytes.Buffer
		if err = json.Compact(&compact, raw); err != nil {
			return err
		}
		if err = rec.append(streamLine{Tick: domain.Tick(frame.Context.GetTick()), CombatFrame: compact.Bytes()}); err != nil {
			return err
		}
		rec.combat.frame = bare
		if len(fresh) > 0 {
			rec.combat.mark = fresh[len(fresh)-1].At
		}
	}
	return rec.append(streamLine{Tick: s.Tick, Combat: stop.Bytes()})
}

// RecordedStop is a recorded fight stop with the frame it decided from,
// its events the whole log recorded up to it.
type RecordedStop struct {
	CombatStop
	Frame *o.BundleSnapshot
}

// combatLog replays a stream's combat lines: the newest frame and the
// event log so far.
type combatLog struct {
	frame  *o.BundleSnapshot
	events []*mp.CombatEventRow
}

// apply takes one frame line; the frame's events join the log.
func (l *combatLog) apply(raw json.RawMessage) error {
	frame := &o.BundleSnapshot{}
	if err := protojson.Unmarshal(raw, frame); err != nil {
		return err
	}
	for _, row := range frame.CombatEvents {
		if n := len(l.events); n > 0 && !bridge.CombatBefore(l.events[n-1].At, row.At) {
			return fmt.Errorf("combat event %s out of order", bridge.CombatEventID(row))
		}
		l.events = append(l.events, row)
	}
	l.frame = frame
	return nil
}

// current is the newest frame with the whole event log.
func (l *combatLog) current() *o.BundleSnapshot {
	if l.frame == nil {
		return nil
	}
	frame := proto.Clone(l.frame).(*o.BundleSnapshot)
	frame.CombatEvents = append([]*mp.CombatEventRow(nil), l.events...)
	return frame
}

// CombatStops is every fight stop the stream at path recorded, in order.
func CombatStops(path string) ([]RecordedStop, error) {
	var out []RecordedStop
	var log combatLog
	err := walk(path, func(line streamLine, _ *replayState) (bool, error) {
		switch {
		case line.CombatFrame != nil:
			return true, log.apply(line.CombatFrame)
		case line.Combat != nil:
			var s RecordedStop
			if err := Decode(line.Combat, &s.CombatStop); err != nil {
				return false, err
			}
			s.Frame = log.current()
			out = append(out, s)
		}
		return true, nil
	})
	return out, err
}

// TrimCombat is plan's fight in the stream at path as a committed
// recording: gzipped stream lines from the frame its first stop decided
// from (carrying the whole event log so far) through its last stop (the
// first CombatRecordingMaxStops), keeping only its frames and stops. An
// empty plan takes the first fight the stream recorded.
func TrimCombat(path string, plan domain.PlanID) ([]byte, error) {
	var lines [][]byte
	var log combatLog
	var pending *o.BundleSnapshot // the newest frame, not yet kept
	pendingTick := domain.Tick(0)
	end, stops := 0, 0
	err := walk(path, func(line streamLine, _ *replayState) (bool, error) {
		switch {
		case line.CombatFrame != nil:
			if err := log.apply(line.CombatFrame); err != nil {
				return false, err
			}
			if stops == 0 {
				pending, pendingTick = log.current(), line.Tick
				return true, nil
			}
			pending = nil
			return true, appendLine(&lines, line)
		case line.Combat != nil:
			var s CombatStop
			if err := Decode(line.Combat, &s); err != nil {
				return false, err
			}
			if plan == "" {
				plan = s.Plan
			}
			if s.Plan != plan {
				return true, nil
			}
			if stops == 0 {
				if pending == nil {
					return false, fmt.Errorf("stop at %d before any combat frame", s.Tick)
				}
				raw, err := protojson.Marshal(pending)
				if err != nil {
					return false, err
				}
				var compact bytes.Buffer
				if err = json.Compact(&compact, raw); err != nil {
					return false, err
				}
				if err = appendLine(&lines, streamLine{Tick: pendingTick, CombatFrame: compact.Bytes()}); err != nil {
					return false, err
				}
			}
			stops++
			if err := appendLine(&lines, line); err != nil {
				return false, err
			}
			end = len(lines)
			return stops < CombatRecordingMaxStops, nil
		}
		return true, nil
	})
	if err != nil {
		return nil, err
	}
	if stops == 0 {
		return nil, fmt.Errorf("%s: no combat stop recorded", path)
	}
	var out bytes.Buffer
	z := gzip.NewWriter(&out)
	for _, l := range lines[:end] {
		if _, err = z.Write(append(l, '\n')); err != nil {
			return nil, err
		}
	}
	if err = z.Close(); err != nil {
		return nil, err
	}
	if out.Len() > CombatRecordingMaxBytes {
		return nil, fmt.Errorf("%s: fight %s trims to %d bytes gzipped, over %d", path, plan, out.Len(), CombatRecordingMaxBytes)
	}
	return out.Bytes(), nil
}

func appendLine(lines *[][]byte, line streamLine) error {
	data, err := json.Marshal(line)
	if err == nil {
		*lines = append(*lines, data)
	}
	return err
}
