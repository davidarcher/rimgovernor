package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// memReplies is a reply ring in Go memory: seq -> payload, slots wide.
type memReplies struct {
	mu      sync.Mutex
	entries map[uint64][]byte
	slots   uint64
}

func (m *memReplies) Slots() uint64 { return m.slots }
func (m *memReplies) Close() error  { return nil }
func (m *memReplies) Read(n uint64) (snapshotshm.Frame, bool, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	data, ok := m.entries[n]
	return snapshotshm.Frame{Number: n, Payload: data}, ok, nil
}

func (m *memReplies) put(n uint64, data []byte) {
	m.mu.Lock()
	defer m.mu.Unlock()
	for seq := range m.entries {
		if seq%m.slots == n%m.slots {
			delete(m.entries, seq)
		}
	}
	m.entries[n] = data
}

func slotResult(seq uint64, slots uint64, length int) *callResult {
	return &callResult{StructuredContent: encode(map[string]any{
		"slot":   map[string]any{"ring": "ring-a", "slot": seq % slots, "seq": seq, "length": length},
		"timing": map[string]any{"queueMs": 1.0},
	})}
}

// A proto-shm client reads a slotted reply from the ring, and a ring it
// cannot open switches the session to proto-gzip.
func TestProtoCallReadsReplySlot(t *testing.T) {
	data, err := proto.Marshal(pbLoaded())
	if err != nil {
		t.Fatal(err)
	}
	ring := &memReplies{entries: map[uint64][]byte{}, slots: 4}
	ring.put(7, data)
	var mu sync.Mutex
	var encodings []string
	handler := func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var outer map[string]string
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			return nil, err
		}
		mu.Lock()
		encodings = append(encodings, outer["encoding"])
		mu.Unlock()
		if outer["encoding"] == replyEncodingShm {
			return slotResult(7, 4, len(data)), nil
		}
		return pbBinaryResult(t, pbLoaded()), nil
	}
	client := testClient(t, &testServer{schema: protoSchema, handler: handler}, testBudget)
	opens := 0
	client.replies = &replySlots{open: func(name string) (replySlotReader, error) {
		opens++
		if name != "ring-a" {
			t.Errorf("opened %q", name)
		}
		return ring, nil
	}}
	identity, _, err := client.Identity(context.Background())
	if err != nil || !proto.Equal(identity, pbLoaded()) {
		t.Fatalf("slotted read %v %v", identity, err)
	}
	// The entry is overwritten: a contract error, not a retry.
	ring.put(11, []byte("other"))
	if _, _, err = client.Identity(context.Background()); !errors.Is(err, ErrContract) {
		t.Fatalf("overwritten slot: %v", err)
	}
	if opens != 1 {
		t.Fatalf("ring opened %d times", opens)
	}
	// Another host: the ring will not open, and the session falls back.
	client.replies = &replySlots{open: func(string) (replySlotReader, error) { return nil, snapshotshm.ErrUnavailable }}
	if _, _, err = client.Identity(context.Background()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unopenable ring: %v", err)
	}
	if identity, _, err = client.Identity(context.Background()); err != nil || !proto.Equal(identity, pbLoaded()) {
		t.Fatalf("inline after fallback %v %v", identity, err)
	}
	mu.Lock()
	defer mu.Unlock()
	if got := strings.Join(encodings, ","); got != "proto-shm,proto-shm,proto-shm,proto-gzip" {
		t.Fatalf("calls asked for %q", got)
	}
}

// A kept receipt carries the slot's bytes as proto_raw, which decodes
// without the ring and through RecordedReply.
func TestKeptReceiptInlinesReplySlot(t *testing.T) {
	data, _ := proto.Marshal(pbLoaded())
	ring := &memReplies{entries: map[uint64][]byte{}, slots: 4}
	ring.put(3, data)
	s := &replySlots{open: func(string) (replySlotReader, error) { return ring, nil }}
	raw := receiptEnvelope(slotResult(3, 4, len(data)).StructuredContent, false)
	kept, slotted, err := s.resolveReplySlot(raw, true)
	if err != nil || !proto.Equal(mustUnmarshal(t, slotted), pbLoaded()) {
		t.Fatalf("resolve %v", err)
	}
	decoded, err := decodeReceipt("games_call_tool", kept)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(decoded.Structured), `"slot"`) {
		t.Fatalf("kept receipt still names the slot: %s", decoded.Structured)
	}
	wire, err := decodeWrapper(decoded.Structured, maxReplyProtoBytes, nil)
	if err != nil || !proto.Equal(mustUnmarshal(t, wire.data), pbLoaded()) {
		t.Fatalf("proto_raw decode %v", err)
	}
	var result map[string]any
	if err := json.Unmarshal(decoded.Structured, &result); err != nil {
		t.Fatal(err)
	}
	row := map[string]any{"reply_type": recordedReplyType(withRecordedReply(context.Background(), &l.IdentityReply{})), "result": result}
	if m, ok := RecordedReply(row); !ok || m["loaded"] == nil {
		t.Fatalf("recorded proto_raw row %v", m)
	}
	// Unkept, the receipt is left as received and the bytes ride beside it.
	same, slotted, err := s.resolveReplySlot(raw, false)
	if err != nil || string(same) != string(raw) || len(slotted) != len(data) {
		t.Fatalf("unkept resolve %v", err)
	}
}

func TestDecodeWrapperRefusesMixedReplies(t *testing.T) {
	for name, raw := range map[string]string{
		"slot unread":    `{"slot":{"ring":"r","slot":1,"seq":1,"length":1}}`,
		"slot and proto": `{"slot":{},"proto":"` + packProto(nil) + `"}`,
		"raw and proto":  `{"proto_raw":"","proto":"` + packProto(nil) + `"}`,
		"raw not base64": `{"proto_raw":"!!"}`,
		"raw not string": `{"proto_raw":1}`,
	} {
		if _, err := decodeWrapper([]byte(raw), maxReplyProtoBytes, nil); !errors.Is(err, ErrContract) {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func mustUnmarshal(t *testing.T, data []byte) *l.IdentityReply {
	t.Helper()
	reply := &l.IdentityReply{}
	if err := proto.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	return reply
}
