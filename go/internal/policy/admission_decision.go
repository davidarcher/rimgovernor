package policy

// NativeIneligible: native previewed the action and refused it.
const NativeIneligible Reason = "native_ineligible"

// AdmissionDecision is one admission verdict: admitted, or the refusals and
// the emergency facts behind them.
type AdmissionDecision struct {
	Admitted  bool
	Refused   []Refusal
	Emergency EmergencyDecision
}
