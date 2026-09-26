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
	"github.com/modelcontextprotocol/go-sdk/mcp"
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
func pbBinaryResult(t *testing.T, message proto.Message) *mcp.CallToolResult {
	t.Helper()
	data, err := proto.Marshal(message)
	if err != nil {
		t.Fatal(err)
	}
	return &mcp.CallToolResult{StructuredContent: encode(map[string]string{"proto": gzipBase64(t, data)})}
}

func TestDecodeWrapperBinaryForm(t *testing.T) {
	data, err := proto.Marshal(pbLoaded())
	if err != nil {
		t.Fatal(err)
	}
	raw := encode(map[string]any{"proto": gzipBase64(t, data), "timing": map[string]float64{"queueMs": 1}})
	wire, err := decodeWrapper(raw, maxProtoBytes)
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

func (b *encodingServer) handler(t *testing.T) func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
	return func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
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

// The flight recorder keeps ProtoJSON text for a binary reply: acceptance
// cases and flight-log tools read "payload" from native_response rows.
func TestRecordableResultRendersBinaryAsPayload(t *testing.T) {
	data, _ := proto.Marshal(pbLoaded())
	raw := encode(map[string]any{"proto": gzipBase64(t, data), "timing": map[string]float64{"queueMs": 1}})
	ctx := withRecordedReply(context.Background(), &l.IdentityReply{})
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(recordableResult(ctx, raw), &fields); err != nil {
		t.Fatal(err)
	}
	var text string
	if fields["proto"] != nil || fields["timing"] == nil || json.Unmarshal(fields["payload"], &text) != nil {
		t.Fatalf("recorded %v", fields)
	}
	reply := &l.IdentityReply{}
	if err := protojson.Unmarshal([]byte(text), reply); err != nil || !proto.Equal(reply, pbLoaded()) {
		t.Fatalf("recorded payload %q %v", text, err)
	}
	if got := recordableResult(context.Background(), raw); string(got) != string(raw) {
		t.Fatal("untyped context rewrote the result")
	}
}
