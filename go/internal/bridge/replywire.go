package bridge

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/base64"
	"encoding/json"
	"io"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"
)

// The companion answers the controller in one wrapper field (#757): "proto",
// base64 of the gzip-compressed binary protobuf reply. Every call asks for
// it with the encoding argument; a companion that answers ProtoJSON
// "payload" instead is refused as a contract error. Callers that omit the
// argument (acceptance cases reading the companion directly) still receive
// ProtoJSON "payload", which this package never decodes.
const replyEncodingArgument = "proto-gzip"

// replyWire is one decoded wrapper: the binary reply bytes and the length
// of the JSON value that carried them.
type replyWire struct {
	data []byte
	wire int
}

func decodeWrapper(raw []byte, limit int, slotted []byte) (replyWire, error) {
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
		if key != "proto" && key != "proto_raw" && key != "slot" && key != "operation" && key != "timing" {
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
	_, hasSlot := fields["slot"]
	rawValue, hasRaw := fields["proto_raw"]
	packed, hasPacked := fields["proto"]
	switch {
	case hasSlot && hasRaw, (hasSlot || hasRaw) && hasPacked:
		return replyWire{}, contract("wrapper carries more than one reply")
	case hasSlot:
		if slotted == nil {
			return replyWire{}, contract("reply slot was not read")
		}
		return replyWire{data: slotted, wire: len(fields["slot"])}, nil
	case hasRaw:
		if slotted != nil { // the bytes resolveReplySlot inlined
			return replyWire{data: slotted, wire: len(rawValue)}, nil
		}
		return decodeRaw(rawValue, limit)
	case hasPacked:
		return decodePacked(packed, limit)
	}
	return replyWire{}, contract("wrapper carries no proto")
}

// decodeRaw decodes a "proto_raw" value: base64 of the raw proto.
func decodeRaw(value json.RawMessage, limit int) (replyWire, error) {
	var text string
	if len(value) == 0 || value[0] != '"' || json.Unmarshal(value, &text) != nil {
		return replyWire{}, contract("proto_raw must be string")
	}
	if base64.StdEncoding.DecodedLen(len(text)) > limit+3 {
		return replyWire{}, contract("oversized payload")
	}
	data, err := base64.StdEncoding.DecodeString(text)
	if err != nil {
		return replyWire{}, contract("proto_raw is not base64")
	}
	if len(data) > limit {
		return replyWire{}, contract("oversized payload")
	}
	return replyWire{data: data, wire: len(value)}, nil
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
	return replyWire{data: data, wire: len(value)}, nil
}

// unmarshalReply decodes a binary reply strictly: unknown
// fields anywhere in the tree are refused, and nesting is bounded.
func unmarshalReply(w replyWire, reply proto.Message) error {
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

// The reply type a protoCall decodes into rides the call's context so the
// recorded native_call row names it ("reply_type"): the row keeps the
// wrapper's binary "proto" as received (#774) and readers decode it with
// RecordedReply.
type recordedReplyKey struct{}

func withRecordedReply(ctx context.Context, reply proto.Message) context.Context {
	return context.WithValue(ctx, recordedReplyKey{}, string(reply.ProtoReflect().Descriptor().FullName()))
}

func recordedReplyType(ctx context.Context) string {
	name, _ := ctx.Value(recordedReplyKey{}).(string)
	return name
}

// RecordedReplyJSON renders a recorded native_call row payload's reply
// as ProtoJSON text: the binary "proto" decoded by the row's "reply_type",
// (or "proto_raw", a reply read from the reply ring, #1344), or the
// ProtoJSON "payload" a call without the encoding argument received.
func RecordedReplyJSON(row map[string]any) (string, bool) {
	result, ok := row["result"].(map[string]any)
	if !ok {
		return "", false
	}
	if payload, ok := result["payload"].(string); ok {
		return payload, true
	}
	typeName, _ := row["reply_type"].(string)
	if typeName == "" {
		return "", false
	}
	field, decode := "proto", decodePacked
	if _, ok := result["proto_raw"]; ok {
		field, decode = "proto_raw", decodeRaw
	}
	packed, ok := result[field].(string)
	if !ok {
		return "", false
	}
	kind, err := protoregistry.GlobalTypes.FindMessageByName(protoreflect.FullName(typeName))
	if err != nil {
		return "", false
	}
	quoted, err := json.Marshal(packed)
	if err != nil {
		return "", false
	}
	w, err := decode(quoted, maxReplyProtoBytes)
	if err != nil {
		return "", false
	}
	reply := kind.New().Interface()
	if (proto.UnmarshalOptions{RecursionLimit: 64, DiscardUnknown: true}).Unmarshal(w.data, reply) != nil {
		return "", false
	}
	text, err := protojson.Marshal(reply)
	if err != nil {
		return "", false
	}
	return string(text), true
}

// RecordedReply is RecordedReplyJSON parsed into a generic map.
func RecordedReply(row map[string]any) (map[string]any, bool) {
	text, ok := RecordedReplyJSON(row)
	if !ok {
		return nil, false
	}
	var reply map[string]any
	if json.Unmarshal([]byte(text), &reply) != nil {
		return nil, false
	}
	return reply, true
}
