package policy

import (
	"strings"
	"testing"
)

func TestRulesVetoByPauseAndEmergency(t *testing.T) {
	calm := RuleContext{Enabled: true}
	fire := RuleContext{Enabled: true, Emergency: []GoalID{MaintainFireSafety}}
	for _, c := range []struct {
		name string
		ctx  RuleContext
		p    RuleProposal
		want string
	}{
		{"calm admits", calm, RuleProposal{Need: MaintainResource, Priority: 3}, ""},
		{"pause vetoes routine work", RuleContext{}, RuleProposal{Need: MaintainResource, Priority: 3}, "control paused"},
		{"pause vetoes urgent work too", RuleContext{}, RuleProposal{Need: ActiveCombat, Priority: 0}, "control paused"},
		{"emergency vetoes priority 2", fire, RuleProposal{Need: EnsureCooking, Priority: 2}, "emergency MaintainFireSafety"},
		{"priority below 2 is exempt", fire, RuleProposal{Need: RestoreWorkers, Priority: 1}, ""},
		{"the emergency need is exempt", fire, RuleProposal{Need: MaintainFireSafety, Priority: 4}, ""},
	} {
		if got := VetoProposal(c.ctx, c.p); !strings.HasPrefix(got, c.want) || (c.want == "") != (got == "") {
			t.Errorf("%s: veto %q, want %q", c.name, got, c.want)
		}
	}
}
