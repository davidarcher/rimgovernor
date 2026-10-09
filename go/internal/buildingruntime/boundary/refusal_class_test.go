package boundary

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// A native refusal's code, reason and class reach the receipt; a refusal
// that names no class is unknown.
func TestWriteIntentRefusalCarriesNativeClass(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		class c.RefusalClass
		want  domain.RefusalClass
	}{
		{c.RefusalClass_REFUSAL_CLASS_PERMANENT, domain.RefusalPermanent},
		{c.RefusalClass_REFUSAL_CLASS_TRANSIENT, domain.RefusalTransient},
		{c.RefusalClass_REFUSAL_CLASS_UNKNOWN, domain.RefusalUnknown},
		{c.RefusalClass_REFUSAL_CLASS_UNSPECIFIED, domain.RefusalUnknown},
	} {
		b, f := NewFixture(t)
		f.Refuse, f.RefuseClass = "cell blocked", tc.class
		out, err := writeOne(b, f.Placement)
		want := domain.NativeRefusal{Code: "FAILURE_CODE_INVALID_REQUEST", Reason: "cell blocked", Class: tc.want}
		if err != nil || out.Kind != domain.ReceiptRefused || out.Refusal == nil || *out.Refusal != want {
			t.Fatalf("class %v: receipt = %+v, %v; want refusal %+v", tc.class, out, err, want)
		}
	}
}
