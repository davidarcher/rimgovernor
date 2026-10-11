package telemetry

import (
	"context"
	"sync"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/metric"
)

// Metrics are the controller's counters and histograms, recorded from explicit
// measurements at the call site (docs/developers/contracts/otel-metrics.md).
// Every attribute is a Label from a closed Vocabulary, so a metric's series
// count is bounded by construction: pawns, request ids and free-form targets
// cannot be passed. Instruments are rebuilt when the global MeterProvider
// changes (serve installs it after package init; tests install their own) and
// record into the no-op provider, at no real cost, while none is installed.

// Other is the label a value outside its Vocabulary collapses to.
const Other = "other"

// Label is an attribute value that came from a Vocabulary.
type Label struct{ v string }

// String is the label's value.
func (l Label) String() string { return l.v }

// Vocabulary is a closed set of attribute values.
type Vocabulary struct {
	set map[string]struct{}
	ok  func(string) bool
}

// Words is a Vocabulary of exactly the given values.
func Words(values ...string) Vocabulary {
	set := make(map[string]struct{}, len(values))
	for _, v := range values {
		set[v] = struct{}{}
	}
	return Vocabulary{set: set}
}

// Where is a Vocabulary of the values ok accepts; ok must accept a finite set
// (an enum's membership test), never a pattern.
func Where(ok func(string) bool) Vocabulary { return Vocabulary{ok: ok} }

// Label is v when it belongs to the vocabulary, else Other.
func (voc Vocabulary) Label(v string) Label {
	if _, in := voc.set[v]; in || (voc.ok != nil && voc.ok(v)) {
		return Label{v}
	}
	return Label{Other}
}

// Fixed vocabularies of the metrics this package defines.
var (
	nativePhases   = Words(PhaseGateWait, PhaseCall, PhaseDecode, PhaseProtoDecode, PhaseNativeQueue, PhaseNativeExecute)
	nativeOutcomes = Words("ok", "refused", "error")
	verdicts       = Words("applied", "refused", "failed")
	admissions     = Words("admitted", "refused")
	// NoReason is the reason label of an outcome that has no refusal reason.
	NoReason = Label{"none"}
)

// Native-call phases (the phase attribute of NativePhase).
const (
	PhaseGateWait      = "gate_wait"
	PhaseCall          = "call"
	PhaseDecode        = "decode"
	PhaseProtoDecode   = "proto_decode"
	PhaseNativeQueue   = "native_queue"
	PhaseNativeExecute = "native_execute"
)

// Histogram boundaries, explicit because the file sink carries only explicit
// buckets. Milliseconds throughout.
var (
	// NativeBuckets spans a cached read (sub-millisecond) to a call that hit
	// its timeout (10 s).
	NativeBuckets = bucketsMs(500*time.Microsecond, ms, 2*ms, 5*ms, 10*ms, 25*ms, 50*ms, 100*ms, 250*ms, 500*ms,
		time.Second, 2*time.Second+500*ms, 5*time.Second, 10*time.Second)
	// SchedulerBuckets spans a quiet admission tail to a step that waited out
	// a long pause (5 min).
	SchedulerBuckets = bucketsMs(ms, 5*ms, 10*ms, 25*ms, 50*ms, 100*ms, 250*ms, 500*ms,
		time.Second, 2*time.Second+500*ms, 5*time.Second, 10*time.Second, 30*time.Second, time.Minute, 2*time.Minute, 5*time.Minute)
)

const ms = time.Millisecond

// bucketsMs is the boundaries as milliseconds.
func bucketsMs(bounds ...time.Duration) []float64 {
	out := make([]float64, len(bounds))
	for i, b := range bounds {
		out[i] = float64(b) / float64(time.Millisecond)
	}
	return out
}

// Instrument names, the metrics contract's table.
const (
	NativePhaseMetric     = "rimgovernor.native.phase.duration"
	NativeCallsMetric     = "rimgovernor.native.calls"
	StepMetric            = "rimgovernor.scheduler.step.duration"
	StopLatencyMetric     = "rimgovernor.scheduler.stop_latency"
	ControllerPauseMetric = "rimgovernor.scheduler.controller_pause"
	StopPauseMetric       = "rimgovernor.scheduler.stop_pause"
	CriticalWaveMetric    = "rimgovernor.scheduler.critical_wave.duration"
	TicksPerSecondMetric  = "rimgovernor.game.ticks_per_second"
	OutcomesMetric        = "rimgovernor.action.outcomes"
	AdmissionsMetric      = "rimgovernor.admission.decisions"
)

type instruments struct {
	nativePhase     metric.Float64Histogram
	nativeCalls     metric.Int64Counter
	step            metric.Float64Histogram
	stopLatency     metric.Float64Histogram
	controllerPause metric.Float64Histogram
	stopPause       metric.Float64Histogram
	criticalWave    metric.Float64Histogram
	ticksPerSecond  metric.Float64Gauge
	outcomes        metric.Int64Counter
	admissions      metric.Int64Counter
}

var (
	instMu       sync.Mutex
	instProvider metric.MeterProvider
	instCurrent  *instruments
)

// current is the instruments for the global MeterProvider.
func current() *instruments {
	provider := otel.GetMeterProvider()
	instMu.Lock()
	defer instMu.Unlock()
	if instCurrent != nil && instProvider == provider {
		return instCurrent
	}
	meter := provider.Meter(scopePrefix + "telemetry")
	// Creation errors leave a nil instrument, which every recorder skips.
	hist := func(name, desc string, bounds []float64) metric.Float64Histogram {
		h, _ := meter.Float64Histogram(name, metric.WithUnit("ms"), metric.WithDescription(desc), metric.WithExplicitBucketBoundaries(bounds...))
		return h
	}
	in := &instruments{
		nativePhase:     hist(NativePhaseMetric, "Native call time by phase and native tool.", NativeBuckets),
		step:            hist(StepMetric, "Scheduler step wall time by cause.", SchedulerBuckets),
		stopLatency:     hist(StopLatencyMetric, "Native clock stop to the step it woke.", SchedulerBuckets),
		controllerPause: hist(ControllerPauseMetric, "Wall time the controller held the game paused in a step.", SchedulerBuckets),
		stopPause:       hist(StopPauseMetric, "Window stop to the admission that resumed the game.", SchedulerBuckets),
		criticalWave:    hist(CriticalWaveMetric, "Critical planner wave wall time, the controller's leg of hazard to protective order.", SchedulerBuckets),
	}
	in.nativeCalls, _ = meter.Int64Counter(NativeCallsMetric, metric.WithUnit("{call}"), metric.WithDescription("Completed native calls by native tool and outcome."))
	in.ticksPerSecond, _ = meter.Float64Gauge(TicksPerSecondMetric, metric.WithUnit("{tick}/s"), metric.WithDescription("Game ticks per wall second the clock status last reported."))
	in.outcomes, _ = meter.Int64Counter(OutcomesMetric, metric.WithUnit("{action}"), metric.WithDescription("Dispatched actions by kind, verdict and refusal reason."))
	in.admissions, _ = meter.Int64Counter(AdmissionsMetric, metric.WithUnit("{decision}"), metric.WithDescription("Method admissions by verdict and refusal reason."))
	instProvider, instCurrent = provider, in
	return in
}

func with(kv ...attribute.KeyValue) metric.MeasurementOption { return metric.WithAttributes(kv...) }

func label(key string, l Label) attribute.KeyValue { return attribute.String(key, l.v) }

// NativePhase records one phase of a native call. tool must come from a
// Vocabulary of native tool names.
func NativePhase(tool Label, phase string, ms float64) {
	if h := current().nativePhase; h != nil {
		h.Record(context.Background(), ms, with(label("native_tool", tool), label("phase", nativePhases.Label(phase))))
	}
}

// NativeCall counts one completed native call; outcome is ok, refused or error.
func NativeCall(tool Label, outcome string) {
	if c := current().nativeCalls; c != nil {
		c.Add(context.Background(), 1, with(label("native_tool", tool), label("outcome", nativeOutcomes.Label(outcome))))
	}
}

// SchedulerStep records one step's wall time under its cause.
func SchedulerStep(cause Label, ms float64) {
	if h := current().step; h != nil {
		h.Record(context.Background(), ms, with(label("step_cause", cause)))
	}
}

// StopLatency records the native stop stamp to the step it woke.
func StopLatency(ms float64) { recordPlain(current().stopLatency, ms) }

// ControllerPause records the wall time the controller held the game paused.
func ControllerPause(ms float64) { recordPlain(current().controllerPause, ms) }

// StopPause records the window stop to readmit pause.
func StopPause(ms float64) { recordPlain(current().stopPause, ms) }

// CriticalWave records one critical planner wave's wall time.
func CriticalWave(ms float64) { recordPlain(current().criticalWave, ms) }

func recordPlain(h metric.Float64Histogram, ms float64) {
	if h != nil {
		h.Record(context.Background(), ms)
	}
}

// TicksPerSecond sets the game ticks per wall second gauge.
func TicksPerSecond(tps float64) {
	if g := current().ticksPerSecond; g != nil {
		g.Record(context.Background(), tps)
	}
}

// ActionOutcome counts one dispatched action: verdict applied, refused or
// failed, and for a refusal its reason (NoReason otherwise).
func ActionOutcome(kind Label, verdict string, reason Label) {
	if c := current().outcomes; c != nil {
		c.Add(context.Background(), 1, with(label("action_kind", kind), label("verdict", verdicts.Label(verdict)), label("reason", reason)))
	}
}

// Admission counts one method admission: verdict admitted or refused, and for
// a refusal its reason (NoReason otherwise).
func Admission(verdict string, reason Label) {
	if c := current().admissions; c != nil {
		c.Add(context.Background(), 1, with(label("verdict", admissions.Label(verdict)), label("reason", reason)))
	}
}
