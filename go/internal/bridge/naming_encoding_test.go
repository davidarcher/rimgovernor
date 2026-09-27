package bridge

import (
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A generated faction name outside ASCII (#600: "Coalition of Ñoa") must
// leave the native census decode and reach the ConfirmColonyNames request
// intact through an ASCII-only frame: the host GABP reader short-reads any frame with
// a multi-byte character, so the connection escapes them and the game's own
// JSON parse restores the exact name.
func TestNamingSuggestionRoundTripsNonASCII(t *testing.T) {
	const faction, settlement = "Coalition of Ñoa", "Red Çanga 🏹"
	// The native reply: gzipped binary protobuf inside the one-field
	// wrapper, exactly as ProtoBoundary.Encode writes it for the controller.
	inner, err := proto.Marshal(&o.ColonyNaming{WindowId: int32p(1), FactionName: stringp(faction), SettlementName: stringp(settlement)})
	if err != nil {
		t.Fatal(err)
	}
	wrapper, err := json.Marshal(map[string]string{"proto": packProto(inner)})
	if err != nil {
		t.Fatal(err)
	}
	wire, err := decodeWrapper(wrapper, maxProtoBytes)
	if err != nil {
		t.Fatal(err)
	}
	decoded := &o.ColonyNaming{}
	if err = unmarshalReply(wire, decoded); err != nil {
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
	args := encode(struct {
		Request string `json:"request"`
	}{string(request)})
	// The GABP connection escapes every frame to ASCII (gabp.ASCIIJSON);
	// the game's JSON parse restores the exact text.
	frame := gabp.ASCIIJSON(args)
	for _, b := range frame {
		if b >= 0x80 {
			t.Fatalf("frame carries a non-ASCII byte: %s", frame)
		}
	}
	var outer struct{ Request string }
	if err = json.Unmarshal(frame, &outer); err != nil {
		t.Fatal(err)
	}
	parsed := &op.PreviewRequest{}
	if err = protojson.Unmarshal([]byte(outer.Request), parsed); err != nil {
		t.Fatal(err)
	}
	if got := parsed.GetOperation().GetConfirmColonyNames(); got.GetFactionName() != faction || got.GetSettlementName() != settlement {
		t.Fatalf("native would parse %q/%q", got.GetFactionName(), got.GetSettlementName())
	}
}

func int32p(v int32) *int32    { return &v }
func stringp(v string) *string { return &v }
