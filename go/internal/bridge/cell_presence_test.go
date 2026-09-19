package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A planning cell whose reply declares the roof/zone fields applied decodes a
// missing value as a known absence without a per-cell issue row; a reply that
// did not apply the field, or that reports it unavailable for another reason,
// leaves it unknown; a "not applicable" issue row still decodes as absent.
func TestCellPresenceDecodesAbsentAppliedFieldsAsKnown(t *testing.T) {
	notApplicable := []*o.ReadIssue{{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
	failed := []*o.ReadIssue{{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	cases := []struct {
		name    string
		value   *string
		issues  []*o.ReadIssue
		applied bool
		known   bool
		present bool
	}{
		{"value present", proto.String("RoofConstructed"), nil, true, true, true},
		{"applied, absent", nil, nil, true, true, false},
		{"not applied, absent", nil, nil, false, false, false},
		{"issue not applicable", nil, notApplicable, false, true, false},
		{"issue read failed under applied", nil, failed, true, false, false},
	}
	for _, tc := range cases {
		got := CellPresence(tc.value, tc.issues, "roof", tc.applied)
		v, known := got.Value()
		if known != tc.known || known && v != tc.present {
			t.Fatalf("%s: got (%v, known=%v), want (%v, known=%v)", tc.name, v, known, tc.present, tc.known)
		}
	}
}
