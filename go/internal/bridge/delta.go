package bridge

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"sync"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// Changed-since reads of whole snapshots (#773, observations.proto
// SectionDelta). Every colony facts, pawn list and bundle reply carries its
// tracker and watermark; the next read of the same request asks since
// them, the native omits every part unchanged since and lists it as held,
// and readCall restores each held part from the reply it holds for that
// watermark before any caller sees it. Callers, the step cache and
// validators see exactly the full reply the native would have sent. A
// reply is a delta exactly when its delta names since; an ask the native
// cannot answer (another tracker, or older than its one-day tombstone
// window or count cap, #795) comes back full, in the same round trip, and
// replaces what is held. A full read happens every deltaResyncEvery-th
// read of a request (the backstop), and after a delta that cannot be
// completed (its base is gone, a held path does not resolve, or a
// tombstoned element is still listed): a genuine error.

// deltaResyncEvery is the backstop cadence: every this-many reads of one
// request go without an ask.
const deltaResyncEvery = 16

// deltaKeep is how many recent replies of one request stay mergeable, so
// concurrent reads of one request each find their own base.
const deltaKeep = 4

var deltaMethods = map[string]bool{
	"rimgovernor/observations_read_colony_facts": true,
	"rimgovernor/observations_list_pawns":        true,
	"rimgovernor/observations_read_bundle":       true,
}

type deltaStore struct {
	mu      sync.Mutex
	entries map[string]*deltaEntry
}

type deltaEntry struct {
	reads int
	held  []deltaHeld // newest last
}

// deltaHeld is one complete reply and the tracker watermark it describes.
type deltaHeld struct {
	mark     string
	ask      *o.SectionDeltaAsk
	snapshot proto.Message
}

func deltaMark(tracker string, at *o.Watermark) string {
	return fmt.Sprintf("%s@%d.%d", tracker, at.GetTick(), at.GetSeq())
}

// ask returns what to read key since (nil for a full read) and counts the
// read.
func (s *deltaStore) ask(key string) *o.SectionDeltaAsk {
	s.mu.Lock()
	defer s.mu.Unlock()
	entry := s.entries[key]
	if entry == nil || len(entry.held) == 0 {
		return nil
	}
	entry.reads++
	if entry.reads%deltaResyncEvery == 0 {
		return nil
	}
	return proto.Clone(entry.held[len(entry.held)-1].ask).(*o.SectionDeltaAsk)
}

func (s *deltaStore) base(key, mark string) proto.Message {
	s.mu.Lock()
	defer s.mu.Unlock()
	if entry := s.entries[key]; entry != nil {
		for _, held := range entry.held {
			if held.mark == mark {
				return held.snapshot
			}
		}
	}
	return nil
}

func (s *deltaStore) put(key string, delta *o.SectionDelta, snapshot proto.Message) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.entries == nil {
		s.entries = map[string]*deltaEntry{}
	}
	entry := s.entries[key]
	if entry == nil {
		entry = &deltaEntry{}
		s.entries[key] = entry
	}
	ask := &o.SectionDeltaAsk{Tracker: proto.String(delta.GetTracker()), Since: proto.Clone(delta.GetAsOf()).(*o.Watermark)}
	entry.held = append(entry.held, deltaHeld{deltaMark(delta.GetTracker(), delta.GetAsOf()), ask, snapshot})
	if len(entry.held) > deltaKeep {
		entry.held = append([]deltaHeld(nil), entry.held[len(entry.held)-deltaKeep:]...)
	}
}

func (s *deltaStore) drop(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.entries, key)
}

// readCall is protoCall for a native read, through the delta store for
// the tracked methods.
func (caller *Client) readCall(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	if !deltaMethods[name] {
		return caller.protoCall(ctx, name, request, reply)
	}
	key := deltaKey(name, request)
	ask := caller.deltas.ask(key)
	result, err := caller.deltaCall(ctx, name, request, reply, ask)
	if err != nil {
		return result, err
	}
	snapshot, delta := trackedSnapshot(reply)
	if ask == nil || snapshot == nil || delta.GetSince() == nil {
		// A full reply, asked or not.
		if len(delta.GetHeld()) != 0 || len(delta.GetRemoved()) != 0 {
			return result, contract("full read held parts")
		}
		caller.deltaKeep(ctx, name, key, snapshot, delta, ask != nil)
		return result, nil
	}
	failure := ""
	switch {
	case len(delta.GetHeld()) != 0 || len(delta.GetRemoved()) != 0:
		base := caller.deltas.base(key, deltaMark(delta.GetTracker(), delta.GetSince()))
		switch {
		case base == nil || delta.GetTracker() != ask.GetTracker() || !proto.Equal(delta.GetSince(), ask.GetSince()):
			failure = "no base"
		default:
			if err := mergeHeld(snapshot, base, delta.GetHeld()); err != nil {
				failure = err.Error()
			} else if err := removedAbsent(snapshot, delta.GetRemoved()); err != nil {
				failure = err.Error()
			}
		}
	}
	if failure != "" {
		// The delta cannot be completed: read in full.
		caller.deltas.drop(key)
		caller.recordDelta(ctx, name, delta, "delta_refused", failure)
		proto.Reset(reply)
		if result, err = caller.deltaCall(ctx, name, request, reply, nil); err != nil {
			return result, err
		}
		snapshot, delta = trackedSnapshot(reply)
		if len(delta.GetHeld()) != 0 || len(delta.GetRemoved()) != 0 {
			return result, contract("full read held parts")
		}
	}
	caller.deltaKeep(ctx, name, key, snapshot, delta, false)
	return result, nil
}

func (caller *Client) deltaCall(ctx context.Context, name string, request, reply proto.Message, ask *o.SectionDeltaAsk) (Result, error) {
	if ask == nil {
		return caller.protoCall(ctx, name, request, reply)
	}
	asked := proto.Clone(request)
	message := asked.ProtoReflect()
	message.Set(message.Descriptor().Fields().ByName("changed_since"), protoreflect.ValueOfMessage(ask.ProtoReflect()))
	return caller.protoCall(ctx, name, asked, reply)
}

// deltaKeep stores a complete observed snapshot under its watermark and
// removes the delta field, so the reply is the one a full read returns.
// inline marks a full reply to a delta ask.
func (caller *Client) deltaKeep(ctx context.Context, name, key string, snapshot protoreflect.Message, delta *o.SectionDelta, inline bool) {
	if snapshot == nil {
		return
	}
	snapshot.Clear(snapshot.Descriptor().Fields().ByName("delta"))
	if delta.GetTracker() == "" || delta.GetAsOf() == nil {
		return
	}
	caller.deltas.put(key, delta, proto.Clone(snapshot.Interface()))
	outcome, detail := "full", ""
	if delta.GetSince() != nil {
		outcome = "delta"
	} else if inline {
		detail = "inline"
	}
	caller.recordDelta(ctx, name, delta, outcome, detail)
}

func (caller *Client) recordDelta(ctx context.Context, name string, delta *o.SectionDelta, outcome, detail string) {
	if caller.recorder == nil {
		return
	}
	payload := map[string]any{"native_tool": name, "outcome": outcome, "held": len(delta.GetHeld()), "removed": len(delta.GetRemoved()), "digest_ms": delta.GetDigestMs(), "tracked": delta.GetTracked()}
	if detail != "" {
		payload["detail"] = detail
	}
	caller.recorder.Event("native_delta", caller.snapshotRecordingContext(ctx), false, payload)
}

// deltaKey is the store key of a tracked request: its bytes without the
// ask and, for the bundle, without the parts that move every read.
func deltaKey(name string, request proto.Message) string {
	shape := proto.Clone(request)
	message := shape.ProtoReflect()
	message.Clear(message.Descriptor().Fields().ByName("changed_since"))
	if bundle, ok := shape.(*o.BundleRequest); ok {
		bundle.Events = nil
		if bundle.PlanningWindow != nil {
			bundle.PlanningWindow.ChangedSinceTick = nil
		}
	}
	encoded, _ := proto.MarshalOptions{Deterministic: true}.Marshal(shape)
	return name + "\x00" + string(encoded)
}

// trackedSnapshot is the observed snapshot of a tracked reply and its delta.
func trackedSnapshot(reply proto.Message) (protoreflect.Message, *o.SectionDelta) {
	message := reply.ProtoReflect()
	field := message.Descriptor().Fields().ByName("observed")
	if field == nil || !message.Has(field) {
		return nil, nil
	}
	snapshot := message.Get(field).Message()
	deltaField := snapshot.Descriptor().Fields().ByName("delta")
	if deltaField == nil || !snapshot.Has(deltaField) {
		return snapshot, nil
	}
	delta, _ := snapshot.Get(deltaField).Message().Interface().(*o.SectionDelta)
	return snapshot, delta
}

// removedAbsent checks the tombstones against the merged reply: an element
// removed since the ask is no longer in its list (an absent list or parent
// lists nothing).
func removedAbsent(root protoreflect.Message, removed []string) error {
	for _, path := range removed {
		split := strings.LastIndexByte(path, '[')
		if split <= 0 || !strings.HasSuffix(path, "]") {
			return fmt.Errorf("tombstone %q", path)
		}
		list, ok, err := resolveList(root, path[:split])
		if err != nil {
			return fmt.Errorf("tombstone %s: %w", path, err)
		}
		if ok {
			if _, present := findKeyed(list, path[split+1:len(path)-1]); present {
				return fmt.Errorf("tombstone %s still listed", path)
			}
		}
	}
	return nil
}

// resolveList is the repeated field at path in root, false when a message
// on the way is absent.
func resolveList(root protoreflect.Message, path string) (protoreflect.List, bool, error) {
	segments := strings.Split(path, ".")
	message := root
	for i, segment := range segments {
		number, key, keyed, err := parseSegment(segment)
		if err != nil {
			return nil, false, err
		}
		field := message.Descriptor().Fields().ByNumber(number)
		if field == nil || field.Kind() != protoreflect.MessageKind || field.IsMap() || keyed && !field.IsList() {
			return nil, false, fmt.Errorf("field %d", number)
		}
		if i == len(segments)-1 {
			if keyed || !field.IsList() {
				return nil, false, fmt.Errorf("field %d is no list", number)
			}
			return message.Get(field).List(), true, nil
		}
		if keyed {
			at, ok := findKeyed(message.Get(field).List(), key)
			if !ok {
				return nil, false, nil
			}
			message = message.Get(field).List().Get(at).Message()
			continue
		}
		if field.IsList() || !message.Has(field) {
			return nil, false, nil
		}
		message = message.Get(field).Message()
	}
	return nil, false, fmt.Errorf("empty path")
}

const observationContextName protoreflect.FullName = "rimgovernor.common.v1.ObservationContext"

// mergeHeld restores every held path of dst from base (the reply its
// delta was asked since), restamping the contexts base's root carried
// with dst's.
func mergeHeld(dst protoreflect.Message, base proto.Message, held []string) error {
	src := base.ProtoReflect()
	contextField := dst.Descriptor().Fields().ByName("context")
	var from, to proto.Message
	if contextField != nil && src.Has(contextField) && dst.Has(contextField) {
		from, to = src.Get(contextField).Message().Interface(), dst.Get(contextField).Message().Interface()
	}
	for _, path := range held {
		if err := mergePath(dst, src, path, from, to); err != nil {
			return fmt.Errorf("held %s: %w", path, err)
		}
	}
	return nil
}

func mergePath(dst, src protoreflect.Message, path string, from, to proto.Message) error {
	segments := strings.Split(path, ".")
	for i, segment := range segments {
		number, key, keyed, err := parseSegment(segment)
		if err != nil {
			return err
		}
		last := i == len(segments)-1
		field := dst.Descriptor().Fields().ByNumber(number)
		if field == nil || field.Kind() != protoreflect.MessageKind || field.IsMap() || keyed && !field.IsList() || !keyed && field.IsList() && !last {
			return fmt.Errorf("field %d", number)
		}
		if !field.IsList() {
			if !src.Has(field) {
				return fmt.Errorf("base lacks field %d", number)
			}
			if last {
				if dst.Has(field) {
					return fmt.Errorf("held field %d present", number)
				}
				dst.Set(field, protoreflect.ValueOfMessage(restamp(src.Get(field).Message(), from, to)))
				return nil
			}
			if !dst.Has(field) {
				return fmt.Errorf("field %d absent", number)
			}
			dst, src = dst.Mutable(field).Message(), src.Get(field).Message()
			continue
		}
		dstList, srcList := dst.Mutable(field).List(), src.Get(field).List()
		if !keyed {
			if dstList.Len() != 0 || srcList.Len() == 0 {
				return fmt.Errorf("held list %d", number)
			}
			for j := 0; j < srcList.Len(); j++ {
				dstList.Append(protoreflect.ValueOfMessage(restamp(srcList.Get(j).Message(), from, to)))
			}
			return nil
		}
		at, ok := findKeyed(dstList, key)
		if !ok {
			return fmt.Errorf("element %s absent", key)
		}
		source, ok := findKeyed(srcList, key)
		if !ok {
			return fmt.Errorf("base lacks element %s", key)
		}
		if last {
			dstList.Set(at, protoreflect.ValueOfMessage(restamp(srcList.Get(source).Message(), from, to)))
			return nil
		}
		dst, src = dstList.Get(at).Message(), srcList.Get(source).Message()
	}
	return fmt.Errorf("empty path")
}

func parseSegment(segment string) (protoreflect.FieldNumber, string, bool, error) {
	key, keyed := "", false
	if open := strings.IndexByte(segment, '['); open >= 0 {
		if !strings.HasSuffix(segment, "]") || open == len(segment)-2 {
			return 0, "", false, fmt.Errorf("segment %q", segment)
		}
		key, keyed, segment = segment[open+1:len(segment)-1], true, segment[:open]
	}
	var number int
	if _, err := fmt.Sscanf(segment, "%d", &number); err != nil || number <= 0 || fmt.Sprint(number) != segment {
		return 0, "", false, fmt.Errorf("segment %q", segment)
	}
	return protoreflect.FieldNumber(number), key, keyed, nil
}

// findKeyed is the index of the one element of list whose key (the
// SectionDelta rule) is key.
func findKeyed(list protoreflect.List, key string) (int, bool) {
	found := -1
	for i := 0; i < list.Len(); i++ {
		if elementKey(list.Get(i).Message()) == key {
			if found >= 0 {
				return 0, false
			}
			found = i
		}
	}
	return found, found >= 0
}

// elementKey is a repeated element's key: its string id, else the id of
// its first (by number) singular message field whose type has one.
func elementKey(element protoreflect.Message) string {
	if id := idField(element.Descriptor()); id != nil {
		return element.Get(id).String()
	}
	fields := element.Descriptor().Fields()
	numbers := make([]int, 0, fields.Len())
	for i := 0; i < fields.Len(); i++ {
		numbers = append(numbers, int(fields.Get(i).Number()))
	}
	sort.Ints(numbers)
	for _, number := range numbers {
		field := fields.ByNumber(protoreflect.FieldNumber(number))
		if field.Kind() != protoreflect.MessageKind || field.IsList() || field.IsMap() {
			continue
		}
		id := idField(field.Message())
		if id == nil {
			continue
		}
		if !element.Has(field) {
			return ""
		}
		return element.Get(field).Message().Get(id).String()
	}
	return ""
}

func idField(message protoreflect.MessageDescriptor) protoreflect.FieldDescriptor {
	field := message.Fields().ByName("id")
	if field == nil || field.Kind() != protoreflect.StringKind || field.IsList() {
		return nil
	}
	return field
}

// restamp is a copy of part with every ObservationContext equal to from
// replaced by to.
func restamp(part protoreflect.Message, from, to proto.Message) protoreflect.Message {
	copied := proto.Clone(part.Interface()).ProtoReflect()
	if from != nil && !proto.Equal(from, to) {
		restampIn(copied, from, to)
	}
	return copied
}

func restampIn(message protoreflect.Message, from, to proto.Message) {
	var stamped []protoreflect.FieldDescriptor
	defer func() {
		for _, field := range stamped {
			message.Set(field, protoreflect.ValueOfMessage(proto.Clone(to).ProtoReflect()))
		}
	}()
	message.Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		if field.Kind() != protoreflect.MessageKind || field.IsMap() {
			return true
		}
		if field.IsList() {
			list := value.List()
			for i := 0; i < list.Len(); i++ {
				restampIn(list.Get(i).Message(), from, to)
			}
			return true
		}
		child := value.Message()
		if child.Descriptor().FullName() == observationContextName {
			if proto.Equal(child.Interface(), from) {
				stamped = append(stamped, field)
			}
			return true
		}
		restampIn(child, from, to)
		return true
	})
}
