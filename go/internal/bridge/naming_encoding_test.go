package bridge

import (
	"bytes"
	"encoding/json"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// A generated faction name outside ASCII (#600: "Coalition of Ñoa") must
// leave the native census decode and reach the ConfirmColonyNames request
// as an ASCII-only text: the host's GABP reader short-reads any frame with
// a multi-byte character, so the request escapes them and the game's own
// JSON parse restores the exact name.
func TestNamingSuggestionRoundTripsNonASCII(t *testing.T) {
	const faction, settlement = "Coalition of Ñoa", "Red Çanga 🏹"
	// The native reply: ProtoJSON inside the one-field wrapper, exactly as
	// ProtoBoundary.Encode writes it.
	inner, err := protojson.Marshal(&o.ColonyNaming{WindowId: int32p(1), FactionName: stringp(faction), SettlementName: stringp(settlement)})
	if err != nil {
		t.Fatal(err)
	}
	wrapper, err := json.Marshal(map[string]string{"payload": string(inner)})
	if err != nil {
		t.Fatal(err)
	}
	payload, err := decodePayload(wrapper, maxProtoBytes)
	if err != nil {
		t.Fatal(err)
	}
	decoded := &o.ColonyNaming{}
	if err = protojson.Unmarshal(payload, decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.GetFactionName() != faction || decoded.GetSettlementName() != settlement {
		t.Fatalf("decoded %q/%q", decoded.GetFactionName(), decoded.GetSettlementName())
	}
	// The request the worker builds from that census, as protoCall encodes
	// it into the games_call_tool argument.
	request, err := protojson.Marshal(&op.PreviewRequest{Operation: namingOperation(decoded.GetWindowId(), decoded.GetFactionName(), decoded.GetSettlementName())})
	if err != nil {
		t.Fatal(err)
	}
	request = asciiJSON(request)
	for _, b := range request {
		if b >= 0x80 {
			t.Fatalf("request text carries a non-ASCII byte: %s", request)
		}
	}
	if !bytes.Contains(request, []byte("Coalition of "+`\`+"u00d1oa")) || !bytes.Contains(request, []byte(`\`+"ud83c"+`\`+"udff9")) {
		t.Fatalf("request text lacks the escapes: %s", request)
	}
	args := encode(struct {
		Request string `json:"request"`
	}{string(request)})
	// gabs decodes the argument map and re-encodes it into the game's
	// frame; a string value keeps its backslashes, so the frame stays ASCII.
	var outer struct{ Request string }
	if err = json.Unmarshal(args, &outer); err != nil {
		t.Fatal(err)
	}
	frame, err := json.Marshal(outer.Request)
	if err != nil {
		t.Fatal(err)
	}
	for _, b := range frame {
		if b >= 0x80 {
			t.Fatalf("frame carries a non-ASCII byte: %s", frame)
		}
	}
	parsed := &op.PreviewRequest{}
	if err = protojson.Unmarshal([]byte(outer.Request), parsed); err != nil {
		t.Fatal(err)
	}
	if got := parsed.GetOperation().GetConfirmColonyNames(); got.GetFactionName() != faction || got.GetSettlementName() != settlement {
		t.Fatalf("native would parse %q/%q", got.GetFactionName(), got.GetSettlementName())
	}
}

func TestASCIIJSONLeavesASCIIAlone(t *testing.T) {
	in := []byte(`{"a":"plain \"text\" \\ 1"}`)
	if out := asciiJSON(in); !bytes.Equal(out, in) {
		t.Fatalf("%s", out)
	}
}

func int32p(v int32) *int32    { return &v }
func stringp(v string) *string { return &v }
