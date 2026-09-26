package bridge

import (
	"math"
	"sort"
)

// Observation capture accounting (#642): the companion reports, beside each
// main-thread hop's queue and execute time, where that hop's execute time
// went -- reading game state (capture), ProtoJSON formatting and the UTF-8
// size checks that precede it (format), the bytes it returned and the rows
// each requested section produced -- and, cumulatively for the loaded game
// session, the update-to-update intervals its frame recorder measured.
//
// Every field here is additive and optional. A recording written by a
// companion that predates the account carries none of it, which the report
// shows as unknown (sample counts of zero, "-" in the text report), never as
// zero work. Units are milliseconds on a monotonic clock (Stopwatch), bytes
// are UTF-8 bytes of the returned ProtoJSON payload, and row counts are rows
// the section returned (Candidates being what it could have returned, where
// the section's page info knows).

// Declared slow-update thresholds, in milliseconds. The companion counts an
// update-to-update interval against every threshold it exceeds and reports
// the counts; these are the buckets the report names. 16.7 and 33.3 ms are
// 60 and 30 updates per second.
var SlowFrameThresholdsMs = []float64{16.7, 33.3, 100, 250}

// Quantiles is a nearest-rank quantile set over one phase's per-hop samples:
// with n samples sorted ascending, the q quantile is the sample at 1-based
// index ceil(q*n). Samples of 0 means the recording carried none of this
// phase, so P50/P95/P99/Max are unknown rather than zero.
type Quantiles struct {
	Samples uint64  `json:"samples"`
	P50     float64 `json:"p50_ms"`
	P95     float64 `json:"p95_ms"`
	P99     float64 `json:"p99_ms"`
	Max     float64 `json:"max_ms"`
	Sum     float64 `json:"sum_ms"`
}

// Mean is Sum over Samples, or 0 without any.
func (q Quantiles) Mean() float64 {
	if q.Samples == 0 {
		return 0
	}
	return q.Sum / float64(q.Samples)
}

// quantiles reduces one phase's samples. It sorts in place.
func quantiles(samples []float64) Quantiles {
	if len(samples) == 0 {
		return Quantiles{}
	}
	sort.Float64s(samples)
	out := Quantiles{Samples: uint64(len(samples)), Max: samples[len(samples)-1]}
	for _, sample := range samples {
		out.Sum += sample
	}
	out.P50, out.P95, out.P99 = nearestRank(samples, 0.50), nearestRank(samples, 0.95), nearestRank(samples, 0.99)
	return out
}

// nearestRank is the q quantile of an ascending slice at 1-based index
// ceil(q*n), clamped to the slice.
func nearestRank(sorted []float64, q float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	rank := int(math.Ceil(q * float64(len(sorted))))
	if rank < 1 {
		rank = 1
	}
	if rank > len(sorted) {
		rank = len(sorted)
	}
	return sorted[rank-1]
}

// SectionPhases is one requested bundle section's cost across the hops that
// read it: how many hops asked for it, the main-thread wall it took in total
// and at most, and the rows it returned. Candidates is what the section
// could have returned where its page info knows the total, so a section
// whose rows are capped is visible as such; it is omitted where the section
// reports no total, rather than serialized as a 0 that would read as "it
// could have returned nothing".
type SectionPhases struct {
	Section    string  `json:"section"`
	Hops       uint64  `json:"hops"`
	Ms         float64 `json:"ms"`
	MaxMs      float64 `json:"max_ms"`
	Rows       uint64  `json:"rows"`
	Candidates uint64  `json:"candidates,omitempty"`
}

// ObservationSample aggregates the companion's per-hop observation account
// over one recording (#642): how many hops reported one, the capture and
// format phases as quantiles and sums, the formatting passes and payload
// bytes they paid for, the per-section split, and the outcomes -- so a slow
// bundle can be attributed to reading the colony or to encoding the reply.
// Hops of 0 means no recorded reply carried the account.
type ObservationSample struct {
	Hops         uint64  `json:"hops"`
	FormatPasses uint64  `json:"format_passes"`
	PayloadBytes uint64  `json:"payload_bytes"`
	Dropped      uint64  `json:"dropped_sections"`
	CaptureMs    float64 `json:"capture_ms"`
	FormatMs     float64 `json:"format_ms"`
	// Capture, Format, Queue and Execute are the per-hop distributions over
	// the hops that reported each. Queue and Execute come from the queueMs
	// and executeMs the companion has reported since #631, so they cover
	// every timed hop, not only the ones carrying an observation account.
	Capture  Quantiles         `json:"capture"`
	Format   Quantiles         `json:"format"`
	Queue    Quantiles         `json:"queue"`
	Execute  Quantiles         `json:"execute"`
	Sections []SectionPhases   `json:"sections,omitempty"`
	Outcomes map[string]uint64 `json:"outcomes,omitempty"`
}

// SlowFrameBucket is one declared threshold and how many update-to-update
// intervals of the recording exceeded it.
type SlowFrameBucket struct {
	ThresholdMs float64 `json:"threshold_ms"`
	Count       uint64  `json:"count"`
}

// SlowFrame is one of the widest update-to-update intervals the companion's
// bounded ring kept for the loaded game session: the update's own id, the
// interval that preceded it, the observation work that ran inside it, the
// game tick it saw and the trace of the most expensive observation it ran,
// where one was traced.
type SlowFrame struct {
	Frame         uint64  `json:"frame"`
	IntervalMs    float64 `json:"interval_ms"`
	ObservationMs float64 `json:"observation_ms"`
	Tick          int64   `json:"tick"`
	Trace         string  `json:"trace,omitempty"`
}

// FrameSample is the companion's frame recorder as one recording sees it
// (#642). Updates, ElapsedMs, ObservationMs, Observations, Cancelled,
// RecorderMs and the Slow counts are cumulative for the loaded game session
// and differenced between the recording's first and last sample, so they
// describe this recording; MaxIntervalMs is the widest the samples reported
// and Worst the ring the last sample carried, both of which are
// session-wide and so may name an update outside the recording.
//
// The intervals are Unity update-to-update wall times on a monotonic clock.
// They are not GPU presentation intervals: a stalled present or a dropped
// frame is not distinguishable here, and an unrendered (batch mode) launch
// still updates. Hooked is false when the recorder never installed, in which
// case every counter is absent rather than zero. Samples counts the replies
// that carried the block.
type FrameSample struct {
	Hooked        bool              `json:"hooked"`
	Samples       uint64            `json:"samples"`
	Updates       uint64            `json:"updates"`
	ElapsedMs     float64           `json:"elapsed_ms"`
	MaxIntervalMs float64           `json:"max_interval_ms"`
	ObservationMs float64           `json:"observation_ms"`
	Observations  uint64            `json:"observations"`
	Cancelled     uint64            `json:"cancelled_hops"`
	RecorderMs    float64           `json:"recorder_ms"`
	Slow          []SlowFrameBucket `json:"slow,omitempty"`
	Worst         []SlowFrame       `json:"worst,omitempty"`
}

// UpdatesPerSecond is Updates over the measured interval wall, or 0 without
// a measured interval. It is the recording's own update rate, independent of
// the game's tick rate.
func (f FrameSample) UpdatesPerSecond() float64 {
	if f.ElapsedMs <= 0 {
		return 0
	}
	return float64(f.Updates) * 1000 / f.ElapsedMs
}

// ObservationShare is the share of the measured update wall that ran
// observation work on the main thread, or 0 without a measured interval.
// It is not the share of a single update: a hop can outlast one update.
func (f FrameSample) ObservationShare() float64 {
	if f.ElapsedMs <= 0 {
		return 0
	}
	return f.ObservationMs / f.ElapsedMs
}

// observationRecord is one recorded reply's observation account, as the
// aggregator reads it out of a flight row's timing block.
type observationRecord struct {
	captureMs, formatMs float64
	hasCapture          bool
	formatPasses        uint64
	payloadBytes        uint64
	dropped             uint64
	outcome             string
	sections            []SectionPhases
}

// readObservation reads the "native_observation" block the service copies
// out of the companion's timing. A row without one, or with a negative
// duration (a clock that moved backwards), reports nothing.
func readObservation(timing map[string]any) (observationRecord, bool) {
	raw, ok := timing["native_observation"].(map[string]any)
	if !ok {
		return observationRecord{}, false
	}
	out := observationRecord{}
	capture, hasCapture := number(raw["captureMs"])
	format, hasFormat := number(raw["formatMs"])
	if capture < 0 || format < 0 {
		return observationRecord{}, false
	}
	out.captureMs, out.hasCapture, out.formatMs = capture, hasCapture, format
	out.formatPasses = countOf(raw["formatPasses"])
	out.payloadBytes = countOf(raw["payloadBytes"])
	out.dropped = countOf(raw["droppedSections"])
	out.outcome, _ = raw["outcome"].(string)
	if sections, ok := raw["sections"].(map[string]any); ok {
		for name, entry := range sections {
			body, ok := entry.(map[string]any)
			if !ok {
				continue
			}
			ms, _ := number(body["ms"])
			if ms < 0 {
				ms = 0
			}
			out.sections = append(out.sections, SectionPhases{Section: name, Hops: 1, Ms: ms, MaxMs: ms,
				Rows: countOf(body["rows"]), Candidates: countOf(body["candidates"])})
		}
	}
	if !hasCapture && !hasFormat && out.payloadBytes == 0 && len(out.sections) == 0 {
		return observationRecord{}, false
	}
	return out, true
}

// frameRecord is one recorded reply's frame-recorder sample.
type frameRecord struct {
	hooked                                              bool
	updates, observations, cancelled                    uint64
	elapsedMs, maxIntervalMs, observationMs, recorderMs float64
	slow                                                map[float64]uint64
	worst                                               []SlowFrame
}

// readFrames reads the "native_frames" block out of a flight row's timing.
// A block without an update count reports nothing, so an older companion
// leaves the frame report unknown.
func readFrames(timing map[string]any) (frameRecord, bool) {
	raw, ok := timing["native_frames"].(map[string]any)
	if !ok {
		return frameRecord{}, false
	}
	updates, ok := number(raw["updates"])
	if !ok || updates < 0 {
		return frameRecord{}, false
	}
	out := frameRecord{updates: uint64(updates)}
	out.hooked, _ = raw["hooked"].(bool)
	out.elapsedMs, _ = number(raw["elapsedMs"])
	out.maxIntervalMs, _ = number(raw["maxUpdateMs"])
	out.observationMs, _ = number(raw["observationMs"])
	out.recorderMs, _ = number(raw["recorderMs"])
	out.observations = countOf(raw["observations"])
	out.cancelled = countOf(raw["cancelled"])
	if buckets, ok := raw["slow"].([]any); ok {
		out.slow = map[float64]uint64{}
		for _, entry := range buckets {
			body, ok := entry.(map[string]any)
			if !ok {
				continue
			}
			threshold, ok := number(body["thresholdMs"])
			if !ok {
				continue
			}
			out.slow[threshold] = countOf(body["count"])
		}
	}
	if worst, ok := raw["worst"].([]any); ok {
		for _, entry := range worst {
			body, ok := entry.(map[string]any)
			if !ok {
				continue
			}
			frame := SlowFrame{Frame: countOf(body["update"])}
			frame.IntervalMs, _ = number(body["intervalMs"])
			frame.ObservationMs, _ = number(body["observationMs"])
			frame.Tick, _ = tickValue(body["tick"])
			frame.Trace, _ = body["trace"].(string)
			out.worst = append(out.worst, frame)
		}
	}
	return out, true
}

// countOf reads a non-negative count out of a recorded JSON number, 0 for
// anything else.
func countOf(value any) uint64 {
	n, ok := number(value)
	if !ok || n < 0 {
		return 0
	}
	return uint64(n)
}

// observationAccumulator collects the per-hop samples SummarizePhases needs
// to reduce into an ObservationSample.
type observationAccumulator struct {
	sample                          ObservationSample
	capture, format, queue, execute []float64
	sections                        map[string]*SectionPhases
}

func (a *observationAccumulator) hop(record observationRecord) {
	a.sample.Hops++
	a.sample.CaptureMs += record.captureMs
	a.sample.FormatMs += record.formatMs
	a.sample.FormatPasses += record.formatPasses
	a.sample.PayloadBytes += record.payloadBytes
	a.sample.Dropped += record.dropped
	if record.hasCapture {
		a.capture = append(a.capture, record.captureMs)
	}
	a.format = append(a.format, record.formatMs)
	if record.outcome != "" {
		if a.sample.Outcomes == nil {
			a.sample.Outcomes = map[string]uint64{}
		}
		a.sample.Outcomes[record.outcome]++
	}
	for _, section := range record.sections {
		if a.sections == nil {
			a.sections = map[string]*SectionPhases{}
		}
		entry := a.sections[section.Section]
		if entry == nil {
			entry = &SectionPhases{Section: section.Section}
			a.sections[section.Section] = entry
		}
		entry.Hops++
		entry.Ms += section.Ms
		entry.MaxMs = math.Max(entry.MaxMs, section.Ms)
		entry.Rows += section.Rows
		entry.Candidates += section.Candidates
	}
}

// timed records one hop's companion queue and execute time, which every
// timed reply carries whether or not it also carries an observation account.
func (a *observationAccumulator) timed(queueMs, executeMs float64) {
	a.queue = append(a.queue, queueMs)
	a.execute = append(a.execute, executeMs)
}

func (a *observationAccumulator) result() ObservationSample {
	out := a.sample
	out.Capture, out.Format = quantiles(a.capture), quantiles(a.format)
	out.Queue, out.Execute = quantiles(a.queue), quantiles(a.execute)
	for _, section := range a.sections {
		out.Sections = append(out.Sections, *section)
	}
	sort.Slice(out.Sections, func(i, j int) bool {
		if out.Sections[i].Ms != out.Sections[j].Ms {
			return out.Sections[i].Ms > out.Sections[j].Ms
		}
		return out.Sections[i].Section < out.Sections[j].Section
	})
	return out
}

// frameAccumulator differences the cumulative frame counters between the
// recording's first and last sample, the way the native pause account is
// read: a counter that went backwards (a reloaded game reset the session)
// starts a new baseline rather than reporting a negative span.
type frameAccumulator struct {
	first, last frameRecord
	have        bool
	samples     uint64
	maxInterval float64
}

func (a *frameAccumulator) sample(record frameRecord) {
	a.samples++
	a.maxInterval = math.Max(a.maxInterval, record.maxIntervalMs)
	if !a.have || record.updates < a.first.updates {
		a.first, a.have = record, true
	}
	a.last = record
}

func (a *frameAccumulator) result() FrameSample {
	if !a.have {
		return FrameSample{}
	}
	out := FrameSample{Hooked: a.last.hooked, Samples: a.samples, MaxIntervalMs: a.maxInterval, Worst: a.last.worst}
	out.Updates = span(a.last.updates, a.first.updates)
	out.Observations = span(a.last.observations, a.first.observations)
	out.Cancelled = span(a.last.cancelled, a.first.cancelled)
	out.ElapsedMs = spanMs(a.last.elapsedMs, a.first.elapsedMs)
	out.ObservationMs = spanMs(a.last.observationMs, a.first.observationMs)
	out.RecorderMs = spanMs(a.last.recorderMs, a.first.recorderMs)
	for _, threshold := range SlowFrameThresholdsMs {
		if _, ok := a.last.slow[threshold]; !ok {
			continue
		}
		out.Slow = append(out.Slow, SlowFrameBucket{ThresholdMs: threshold, Count: span(a.last.slow[threshold], a.first.slow[threshold])})
	}
	return out
}

func span(last, first uint64) uint64 {
	if last < first {
		return last
	}
	return last - first
}

func spanMs(last, first float64) float64 {
	if last < first {
		if last < 0 {
			return 0
		}
		return last
	}
	return last - first
}
