package bridge

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/base64"
	"encoding/json"
	"io"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The companion's reply wrapper carries the typed reply in exactly one of
// two fields (#757): "payload", the official ProtoJSON text as a JSON
// string, or "proto", base64 of the gzip-compressed binary protobuf
// message. The controller asks for the binary form with the encoding
// argument; every other caller (acceptance cases, flight-log tools) keeps
// ProtoJSON.
const (
	replyEncodingArgument = "proto-gzip"
	encodingProtoJSON     = "protojson"
)

// Per-client state of the binary reply form. An older companion refuses the
// encoding argument outright, so a client learns it from its first read:
// reads ask for binary until refused, writes only once a binary read has
// succeeded, so a write is never refused (or retried) for asking.
const (
	binaryUnknown int32 = iota
	binaryConfirmed
	binaryRefused
)

// unknownArgumentDetail is ProtoBoundary.TryParse's refusal of an argument it
// does not know, which is how a companion predating #757 answers encoding.
const unknownArgumentDetail = "The sole caller argument must be request."

// replyWire is one decoded wrapper: the reply bytes (ProtoJSON or binary),
// which form they are, and the length of the JSON value that carried them.
type replyWire struct {
	data   []byte
	binary bool
	wire   int
}

func (w replyWire) encoding() string {
	if w.binary {
		return replyEncodingArgument
	}
	return encodingProtoJSON
}

func decodeWrapper(raw []byte, limit int) (replyWire, error) {
	if len(raw) == 0 || len(raw) > maxResponseBytes {
		return replyWire{}, contract("invalid wrapper size")
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	token, err := d.Token()
	if err != nil || token != json.Delim('{') {
		return replyWire{}, contract("reply wrapper must be object")
	}
	fields := map[string]json.RawMessage{}
	for d.More() {
		token, err = d.Token()
		if err != nil {
			return replyWire{}, contract("wrapper key")
		}
		key, ok := token.(string)
		if !ok {
			return replyWire{}, contract("wrapper key")
		}
		if _, ok = fields[key]; ok {
			return replyWire{}, contract("duplicate wrapper key")
		}
		if key != "payload" && key != "proto" && key != "operation" && key != "timing" {
			return replyWire{}, contract("unknown wrapper field %s", key)
		}
		var value json.RawMessage
		if err = d.Decode(&value); err != nil {
			return replyWire{}, contract("wrapper value")
		}
		fields[key] = value
	}
	if _, err = d.Token(); err != nil {
		return replyWire{}, contract("wrapper end")
	}
	if _, err = d.Token(); err != io.EOF {
		return replyWire{}, contract("trailing wrapper data")
	}
	text, hasText := fields["payload"]
	packed, hasPacked := fields["proto"]
	switch {
	case hasText && hasPacked:
		return replyWire{}, contract("wrapper carries both payload and proto")
	case hasPacked:
		return decodePacked(packed, limit)
	}
	var value string
	if len(text) == 0 || text[0] != '"' || json.Unmarshal(text, &value) != nil {
		return replyWire{}, contract("payload must be string")
	}
	if len(value) > limit {
		return replyWire{}, contract("oversized payload")
	}
	return replyWire{data: []byte(value), wire: len(text)}, nil
}

// decodePacked gunzips a "proto" value, refusing more than limit
// decompressed bytes before reading them all.
func decodePacked(value json.RawMessage, limit int) (replyWire, error) {
	var text string
	if len(value) == 0 || value[0] != '"' || json.Unmarshal(value, &text) != nil {
		return replyWire{}, contract("proto must be string")
	}
	compressed, err := base64.StdEncoding.DecodeString(text)
	if err != nil {
		return replyWire{}, contract("proto is not base64")
	}
	reader, err := gzip.NewReader(bytes.NewReader(compressed))
	if err != nil {
		return replyWire{}, contract("proto is not gzip")
	}
	data, err := io.ReadAll(io.LimitReader(reader, int64(limit)+1))
	if err != nil {
		return replyWire{}, contract("proto gzip: %v", err)
	}
	if len(data) > limit {
		return replyWire{}, contract("oversized payload")
	}
	return replyWire{data: data, binary: true, wire: len(value)}, nil
}

// unmarshalReply decodes either form with the same strictness: unknown
// fields anywhere in the tree are refused, and nesting is bounded.
func unmarshalReply(w replyWire, reply proto.Message) error {
	if !w.binary {
		return protojson.UnmarshalOptions{DiscardUnknown: false, RecursionLimit: 64}.Unmarshal(w.data, reply)
	}
	if err := (proto.UnmarshalOptions{RecursionLimit: 64}).Unmarshal(w.data, reply); err != nil {
		return err
	}
	return refuseUnknown(reply.ProtoReflect())
}

func refuseUnknown(m protoreflect.Message) error {
	if len(m.GetUnknown()) != 0 {
		return contract("unknown fields in %s", m.Descriptor().FullName())
	}
	var err error
	m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		switch {
		case fd.IsList() && fd.Message() != nil:
			list := v.List()
			for i := 0; i < list.Len() && err == nil; i++ {
				err = refuseUnknown(list.Get(i).Message())
			}
		case fd.IsMap() && fd.MapValue().Message() != nil:
			v.Map().Range(func(_ protoreflect.MapKey, value protoreflect.Value) bool {
				err = refuseUnknown(value.Message())
				return err == nil
			})
		case !fd.IsList() && !fd.IsMap() && fd.Message() != nil:
			err = refuseUnknown(v.Message())
		}
		return err == nil
	})
	return err
}

// refusedEncoding reports whether reply is an older companion's refusal of
// the encoding argument: its top-level failure names the unknown argument.
func refusedEncoding(reply proto.Message) bool {
	m := reply.ProtoReflect()
	fd := m.Descriptor().Fields().ByName("failure")
	if fd == nil || fd.Message() == nil || fd.Message().FullName() != (&c.Failure{}).ProtoReflect().Descriptor().FullName() || !m.Has(fd) {
		return false
	}
	failure, ok := m.Get(fd).Message().Interface().(*c.Failure)
	return ok && failure.GetCode() == c.FailureCode_FAILURE_CODE_INVALID_REQUEST && failure.GetDetail() == unknownArgumentDetail
}

// The reply type a protoCall decodes into rides the call's context so a
// recorded native_response keeps ProtoJSON text: acceptance cases and
// flight-log tools read "payload" from those rows.
type recordedReplyKey struct{}

func withRecordedReply(ctx context.Context, reply proto.Message) context.Context {
	return context.WithValue(ctx, recordedReplyKey{}, reply.ProtoReflect().Type())
}

// recordableResult renders a binary wrapper as the ProtoJSON wrapper it
// replaces, for the flight recorder only; anything it cannot render is
// recorded as received.
func recordableResult(ctx context.Context, structured json.RawMessage) json.RawMessage {
	kind, _ := ctx.Value(recordedReplyKey{}).(protoreflect.MessageType)
	if kind == nil || !bytes.Contains(structured, []byte(`"proto"`)) {
		return structured
	}
	var fields map[string]json.RawMessage
	if json.Unmarshal(structured, &fields) != nil || fields["proto"] == nil {
		return structured
	}
	w, err := decodePacked(fields["proto"], maxMediaProtoBytes)
	if err != nil {
		return structured
	}
	reply := kind.New().Interface()
	if (proto.UnmarshalOptions{RecursionLimit: 64, DiscardUnknown: true}).Unmarshal(w.data, reply) != nil {
		return structured
	}
	text, err := protojson.Marshal(reply)
	if err != nil {
		return structured
	}
	quoted, err := json.Marshal(string(text))
	if err != nil {
		return structured
	}
	delete(fields, "proto")
	fields["payload"] = quoted
	out, err := json.Marshal(fields)
	if err != nil {
		return structured
	}
	return out
}
