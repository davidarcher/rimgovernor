package bridge

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"testing"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

func gzipBase64(t *testing.T, data []byte) string {
	t.Helper()
	return packProto(data)
}

// packProto is a companion's "proto" value: base64 of gzipped bytes.
func packProto(data []byte) string {
	var buffer bytes.Buffer
	w := gzip.NewWriter(&buffer)
	if _, err := w.Write(data); err != nil {
		panic(err)
	}
	if err := w.Close(); err != nil {
		panic(err)
	}
	return base64.StdEncoding.EncodeToString(buffer.Bytes())
}

// pbBinaryResult is the wrapper a companion writes for encoding=proto-gzip.
func pbBinaryResult(t *testing.T, message proto.Message) *callResult {
	t.Helper()
	data, err := proto.Marshal(message)
	if err != nil {
		t.Fatal(err)
	}
	return &callResult{StructuredContent: encode(map[string]string{"proto": gzipBase64(t, data)})}
}

func TestDecodeWrapperBinaryForm(t *testing.T) {
	data, err := proto.Marshal(pbLoaded())
	if err != nil {
		t.Fatal(err)
	}
	raw := encode(map[string]any{"proto": gzipBase64(t, data), "timing": map[string]float64{"queueMs": 1}})
	wire, err := decodeWrapper(raw, maxReplyProtoBytes)
	if err != nil || wire.wire == 0 {
		t.Fatalf("binary wrapper: %+v %v", wire, err)
	}
	reply := &l.IdentityReply{}
	if err = unmarshalReply(wire, reply); err != nil || !proto.Equal(reply, pbLoaded()) {
		t.Fatalf("binary reply %v %v", reply, err)
	}

	for name, raw := range map[string][]byte{
		"payload":    encode(map[string]string{"payload": "{}"}),
		"both":       encode(map[string]string{"payload": "{}", "proto": gzipBase64(t, nil)}),
		"neither":    []byte(`{"timing":{}}`),
		"not base64": []byte(`{"proto":"!!"}`),
		"not gzip":   encode(map[string]string{"proto": base64.StdEncoding.EncodeToString([]byte("plain"))}),
		"not string": []byte(`{"proto":1}`),
		// A gzip bomb: a few KB that inflate far past the limit.
		"oversized": encode(map[string]string{"proto": gzipBase64(t, make([]byte, 4<<20))}),
	} {
		if _, err := decodeWrapper(raw, 1<<20); !errors.Is(err, ErrContract) {
			t.Errorf("%s accepted: %v", name, err)
		}
	}
}

func TestBinaryReplyRefusesUnknownFields(t *testing.T) {
	top := pbLoaded()
	top.ProtoReflect().SetUnknown(protowire.AppendVarint(protowire.AppendTag(nil, 999, protowire.VarintType), 1))
	nested := pbLoaded()
	nested.GetLoaded().GetContext().ProtoReflect().SetUnknown(protowire.AppendVarint(protowire.AppendTag(nil, 999, protowire.VarintType), 1))
	for name, message := range map[string]proto.Message{"top": top, "nested": nested} {
		data, err := proto.Marshal(message)
		if err != nil {
			t.Fatal(err)
		}
		if err = unmarshalReply(replyWire{data: data}, &l.IdentityReply{}); !errors.Is(err, ErrContract) {
			t.Errorf("%s unknown field accepted: %v", name, err)
		}
	}
}

// encodingServer records the encoding argument each call sent.
type encodingServer struct {
	mu        sync.Mutex
	encodings []string
}

func (b *encodingServer) handler(t *testing.T) func(context.Context, nativeArgument) (*callResult, error) {
	return func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var outer map[string]string
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			return nil, err
		}
		b.mu.Lock()
		b.encodings = append(b.encodings, outer["encoding"])
		b.mu.Unlock()
		return pbBinaryResult(t, pbLoaded()), nil
	}
}

// Reads and writes alike always ask for the binary reply form.
func TestProtoCallAlwaysAsksForBinaryReplies(t *testing.T) {
	b := &encodingServer{}
	client := testClient(t, &testServer{schema: protoSchema, handler: b.handler(t)}, testBudget)
	if _, err := client.protoCall(context.Background(), "rimgovernor/operations_execute", &l.IdentityRequest{}, &l.IdentityReply{}); err != nil {
		t.Fatal(err)
	}
	identity, _, err := client.Identity(context.Background())
	if err != nil || !proto.Equal(identity, pbLoaded()) {
		t.Fatalf("binary read %v %v", identity, err)
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if got := strings.Join(b.encodings, ","); got != replyEncodingArgument+","+replyEncodingArgument {
		t.Fatalf("calls asked for %q", got)
	}
}

// The flight recorder keeps a binary reply as received (#774); readers
// decode it by the row's reply_type.
func TestRecordedReplyDecodesBinaryRow(t *testing.T) {
	data, _ := proto.Marshal(pbLoaded())
	ctx := withRecordedReply(context.Background(), &l.IdentityReply{})
	row := map[string]any{
		"reply_type": recordedReplyType(ctx),
		"result":     map[string]any{"proto": gzipBase64(t, data), "timing": map[string]any{"queueMs": 1.0}},
	}
	text, ok := RecordedReplyJSON(row)
	reply := &l.IdentityReply{}
	if !ok || protojson.Unmarshal([]byte(text), reply) != nil || !proto.Equal(reply, pbLoaded()) {
		t.Fatalf("decoded %q %v", text, ok)
	}
	if m, ok := RecordedReply(row); !ok || m["loaded"] == nil {
		t.Fatalf("map %v", m)
	}
	delete(row, "reply_type")
	if _, ok := RecordedReplyJSON(row); ok {
		t.Fatal("untyped binary row decoded")
	}
	legacy := map[string]any{"result": map[string]any{"payload": `{"loaded":{}}`}}
	if text, ok := RecordedReplyJSON(legacy); !ok || text != `{"loaded":{}}` {
		t.Fatalf("payload row %q", text)
	}
}
