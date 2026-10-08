package policy

import (
	"strings"
	"testing"
)

func TestSafeguardsVetoByPause(t *testing.T) {
	calm := SafeguardContext{Enabled: true}
	fire := SafeguardContext{Enabled: true}
	for _, c := range []struct {
		name string
		ctx  SafeguardContext
		p    SafeguardProposal
		want string
	}{
		{"calm admits", calm, SafeguardProposal{Need: MaintainResource, Priority: 3}, ""},
		{"pause vetoes routine work", SafeguardContext{}, SafeguardProposal{Need: MaintainResource, Priority: 3}, "control paused"},
		{"pause vetoes urgent work too", SafeguardContext{}, SafeguardProposal{Need: ActiveCombat, Priority: 0}, "control paused"},
		{"ordinary work admits", fire, SafeguardProposal{Need: EnsureCooking, Priority: 2}, ""},
		{"urgent work admits", fire, SafeguardProposal{Need: RestoreWorkers, Priority: 1}, ""},
		{"fire response admits", fire, SafeguardProposal{Need: MaintainFireSafety, Priority: 4}, ""},
	} {
		if got := VetoProposal(c.ctx, c.p); !strings.HasPrefix(got, c.want) || (c.want == "") != (got == "") {
			t.Errorf("%s: veto %q, want %q", c.name, got, c.want)
		}
	}
}
