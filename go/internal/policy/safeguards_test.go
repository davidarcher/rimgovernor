package policy

import (
	"strings"
	"testing"
)

func TestSafeguardsVetoByPauseAndEmergency(t *testing.T) {
	calm := SafeguardContext{Enabled: true}
	fire := SafeguardContext{Enabled: true, Emergency: []GoalID{MaintainFireSafety}}
	for _, c := range []struct {
		name string
		ctx  SafeguardContext
		p    SafeguardProposal
		want string
	}{
		{"calm admits", calm, SafeguardProposal{Need: MaintainResource, Priority: 3}, ""},
		{"pause vetoes routine work", SafeguardContext{}, SafeguardProposal{Need: MaintainResource, Priority: 3}, "control paused"},
		{"pause vetoes urgent work too", SafeguardContext{}, SafeguardProposal{Need: ActiveCombat, Priority: 0}, "control paused"},
		{"emergency vetoes priority 2", fire, SafeguardProposal{Need: EnsureCooking, Priority: 2}, "emergency MaintainFireSafety"},
		{"priority below 2 is exempt", fire, SafeguardProposal{Need: RestoreWorkers, Priority: 1}, ""},
		{"the emergency need is exempt", fire, SafeguardProposal{Need: MaintainFireSafety, Priority: 4}, ""},
	} {
		if got := VetoProposal(c.ctx, c.p); !strings.HasPrefix(got, c.want) || (c.want == "") != (got == "") {
			t.Errorf("%s: veto %q, want %q", c.name, got, c.want)
		}
	}
}
