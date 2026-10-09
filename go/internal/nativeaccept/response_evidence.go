package nativeaccept

import (
	"fmt"
	"os"
	"runtime"
)

// ResponseEvidence separates a native outcome from controller activity.
// Nil ticks mean the fixture or recording did not establish that phase.
// Detection and decision describe the controller's observed game tick, not
// an interpolated wall-clock timestamp. There is deliberately no latency gate.
type ResponseEvidence struct {
	Fixture           string    `json:"fixture"`
	Runner            string    `json:"runner"`
	Speed             string    `json:"speed"`
	OnsetTick         *int64    `json:"onsetTick"`
	DetectionTick     *int64    `json:"detectionTick"`
	DetectionSource   string    `json:"detectionSource"`
	DecisionTick      *int64    `json:"decisionTick"`
	DispatchTick      *int64    `json:"dispatchTick"`
	FirstEffectTick   *int64    `json:"firstEffectTick"`
	Effect            string    `json:"effect"`
	ControllerPauseMS []float64 `json:"controllerPauseMs"`
	ReviewInclusiveMS []float64 `json:"reviewInclusiveMs"`
}

func NewResponseEvidence(fixture, speed string, onset int64) (ResponseEvidence, error) {
	host, err := os.Hostname()
	if err != nil {
		return ResponseEvidence{}, err
	}
	return ResponseEvidence{Fixture: fixture, Speed: speed, OnsetTick: &onset,
		DetectionSource: "controller_review", Runner: fmt.Sprintf("%s %s/%s cpus=%d", host, runtime.GOOS, runtime.GOARCH, runtime.NumCPU())}, nil
}

// ObserveEffect accepts only the native postcondition the fixture has checked;
// a receipt or a clock-window admission must never be passed as an effect.
func (r *ResponseEvidence) ObserveEffect(tick int64, effect string) {
	if r.OnsetTick != nil && tick < *r.OnsetTick {
		return
	}
	if r.FirstEffectTick == nil || tick < *r.FirstEffectTick {
		r.FirstEffectTick, r.Effect = &tick, effect
	}
}

func (r *ResponseEvidence) ReadFlight(path string) error {
	rows, err := ReadFlight(path)
	if err != nil {
		return err
	}
	for _, row := range rows {
		if row.HasTick && r.OnsetTick != nil && row.Tick < *r.OnsetTick {
			continue
		}
		f := row.Fields()
		if row.Kind == "clock_step" {
			if value, ok := f["controller_pause_ms"]; ok {
				r.ControllerPauseMS = append(r.ControllerPauseMS, AsNumber(value))
			}
			if value, ok := f["critical_wave_ms"]; ok {
				r.ReviewInclusiveMS = append(r.ReviewInclusiveMS, AsNumber(value))
			}
		}
		if row.Kind == "planner_step" && AsString(f["scope"]) == "immediate" && AsString(f["verdict"]) != "failed" {
			if AsString(f["target"]) == "rounds" {
				if value, ok := f["observation_tick"]; ok && r.DetectionTick == nil && r.afterOnset(value) {
					tick := int64(AsNumber(value))
					r.DetectionTick = &tick
				}
			} else if r.DecisionTick == nil {
				if value, known := f["decision_tick"]; known && r.afterOnset(value) {
					tick := int64(AsNumber(value))
					r.DecisionTick = &tick
				} else if row.HasTick {
					tick := row.Tick
					r.DecisionTick = &tick
				}
			}
		}
		if row.Kind == "dispatch" && r.DispatchTick == nil {
			if value, ok := f["dispatch_tick"]; ok && r.afterOnset(value) {
				tick := int64(AsNumber(value))
				r.DispatchTick = &tick
			}
		}
	}
	return nil
}

func (r *ResponseEvidence) afterOnset(value any) bool {
	return r.OnsetTick == nil || int64(AsNumber(value)) >= *r.OnsetTick
}
