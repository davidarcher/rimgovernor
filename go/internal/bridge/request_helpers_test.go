package bridge

import (
	"encoding/json"
	"testing"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// protoTestRequest checks a call's request argument decodes to expected.
func protoTestRequest(t *testing.T, arg nativeArgument, expected proto.Message) {
	t.Helper()
	var outer struct {
		Request string `json:"request"`
	}
	if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
		t.Fatal(err)
	}
	actual := expected.ProtoReflect().New().Interface()
	if err := protojson.Unmarshal([]byte(outer.Request), actual); err != nil || !proto.Equal(actual, expected) {
		t.Fatal(actual, err)
	}
}
