package otel

// Typed readers of the OTLP/JSON file sink (the line format is in
// docs/developers/contracts/otel-core.md). Acceptance oracles, the postmortem
// and the CLI/launcher readers parse the files through these, never through
// the raw JSON. Every reader tolerates a live ring: a torn or unparsable line
// is skipped, and a rotation racing the read can skip or repeat a segment.

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"time"
)

// RingPath is the path of one signal's ring inside a telemetry directory:
// RingPath(dir, LogsFile).
func RingPath(dir, file string) string { return filepath.Join(dir, file) }

// SiblingRing is the ring of another signal beside ring, one of the three
// files: SiblingRing(".../otlp/logs.jsonl", TracesFile) is ".../otlp/traces.jsonl".
func SiblingRing(ring, file string) string { return filepath.Join(filepath.Dir(ring), file) }

// Position locates one line of a ring: the segment file it was read from
// (traces.jsonl, logs.jsonl.2, ...) and its 1-based line number there. A
// reader quotes it as "file#line"; the line number is what `grep -n` prints.
type Position struct {
	File string
	Line int
}

// String is "<base name>#<line>".
func (p Position) String() string { return filepath.Base(p.File) + "#" + strconv.Itoa(p.Line) }

// Scan calls fn with every complete, valid-JSON line of the ring at path, oldest
// first, with its Position. ReadLines is Scan without positions. Torn or invalid
// lines are skipped but still counted in the line numbers.
func Scan(path string, fn func(pos Position, line []byte) error) error {
	for _, seg := range Segments(path) {
		f, err := os.Open(seg)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			return err
		}
		err = scanSegment(f, seg, fn)
		f.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

func scanSegment(r io.Reader, file string, fn func(Position, []byte) error) error {
	br := bufio.NewReaderSize(r, 1<<20)
	n := 0
	for {
		line, err := br.ReadBytes('\n')
		if len(line) > 0 {
			n++
		}
		if len(line) > 0 && line[len(line)-1] == '\n' {
			line = bytes.TrimRight(line, "\r\n")
			if len(line) > 0 && json.Valid(line) {
				if ferr := fn(Position{File: file, Line: n}, line); ferr != nil {
					return ferr
				}
			}
		}
		if err != nil {
			if errors.Is(err, io.EOF) {
				return nil
			}
			return err
		}
	}
}

// Attrs are a record's attributes by key, natively typed: string, bool, int64,
// float64, []any (array values) and map[string]any (kvlist values). Bytes values
// stay base64 text. An attribute with no value is absent.
type Attrs map[string]any

// Str is the string attribute key, "" when absent or not a string.
func (a Attrs) Str(key string) string { s, _ := a[key].(string); return s }

// Int is the integer attribute key; a double with an integral value counts.
func (a Attrs) Int(key string) (int64, bool) {
	switch v := a[key].(type) {
	case int64:
		return v, true
	case float64:
		if v == float64(int64(v)) {
			return int64(v), true
		}
	}
	return 0, false
}

// Float is the numeric attribute key as a float.
func (a Attrs) Float(key string) (float64, bool) {
	switch v := a[key].(type) {
	case int64:
		return float64(v), true
	case float64:
		return v, true
	}
	return 0, false
}

// Bool is the boolean attribute key, false when absent.
func (a Attrs) Bool(key string) bool { b, _ := a[key].(bool); return b }

// Resource is the emitting process: service.name, service.instance.id (one per
// launch) and service.version.
type Resource struct {
	Service, Instance, Version string
}

// Log is one log record (a decision or event row, see otel-signals.md "OTEL mapping").
type Log struct {
	Pos          Position
	Resource     Resource
	Time         time.Time // the record's own time (the row's "at"); Observed when absent
	Observed     time.Time
	EventName    string
	Severity     int // OTLP severity number: INFO 9, WARN 13, ERROR 17
	SeverityText string
	Body         string // a string body; "" for none
	TraceID      string
	SpanID       string
	Attrs        Attrs
}

// Event is one span event.
type Event struct {
	Time  time.Time
	Name  string
	Attrs Attrs
}

// Span status codes (OTLP).
const (
	StatusUnset = 0
	StatusOK    = 1
	StatusError = 2
)

// Span is one finished span.
type Span struct {
	Pos          Position
	Resource     Resource
	TraceID      string
	SpanID       string
	ParentSpanID string // "" for a root
	Name         string
	Kind         int // OTLP span kind: 1 internal, 2 server, 3 client
	Start, End   time.Time
	StatusCode   int
	StatusMsg    string
	Attrs        Attrs
	Events       []Event
}

// Duration is End - Start.
func (s Span) Duration() time.Duration { return s.End.Sub(s.Start) }

// Metric kinds.
const (
	Gauge     = "gauge"
	Sum       = "sum"
	Histogram = "histogram"
)

// Point is one data point of a metric. Gauge and Sum points carry Value;
// Histogram points carry Count, Sum, Bounds and Buckets (len(Bounds)+1 counts).
// Metrics are cumulative, so the latest point of a series is its total.
type Point struct {
	Attrs   Attrs
	Start   time.Time
	Time    time.Time
	Value   float64
	Count   uint64
	Sum     float64
	Bounds  []float64
	Buckets []uint64
}

// Metric is one metric with all its data points at one export.
type Metric struct {
	Pos       Position
	Resource  Resource
	Name      string
	Unit      string
	Kind      string // Gauge, Sum or Histogram
	Monotonic bool   // Sum only
	Points    []Point
}

type wireValue struct {
	StringValue *string
	BoolValue   *bool
	IntValue    *string
	DoubleValue *float64
	ArrayValue  *wireArray
	KvlistValue *wireKvlist
	BytesValue  *string
}

type wireArray struct {
	Values []wireValue `json:"values"`
}

type wireKvlist struct {
	Values []wireAttr `json:"values"`
}

type wireAttr struct {
	Key   string    `json:"key"`
	Value wireValue `json:"value"`
}

func (v wireValue) decode() (any, bool) {
	switch {
	case v.StringValue != nil:
		return *v.StringValue, true
	case v.BoolValue != nil:
		return *v.BoolValue, true
	case v.IntValue != nil:
		n, err := strconv.ParseInt(*v.IntValue, 10, 64)
		return n, err == nil
	case v.DoubleValue != nil:
		return *v.DoubleValue, true
	case v.ArrayValue != nil:
		out := make([]any, 0, len(v.ArrayValue.Values))
		for _, e := range v.ArrayValue.Values {
			x, _ := e.decode()
			out = append(out, x)
		}
		return out, true
	case v.KvlistValue != nil:
		return decodeAttrs(v.KvlistValue.Values), true
	case v.BytesValue != nil:
		return *v.BytesValue, true
	}
	return nil, false
}

func decodeAttrs(in []wireAttr) map[string]any {
	out := make(map[string]any, len(in))
	for _, a := range in {
		if x, ok := a.Value.decode(); ok {
			out[a.Key] = x
		}
	}
	return out
}

// UnmarshalJSON accepts the double both as a JSON number and, for NaN and the
// infinities, as the string form the sink writes.
func (v *wireValue) UnmarshalJSON(data []byte) error {
	var raw struct {
		StringValue *string          `json:"stringValue"`
		BoolValue   *bool            `json:"boolValue"`
		IntValue    *string          `json:"intValue"`
		DoubleValue *json.RawMessage `json:"doubleValue"`
		ArrayValue  *wireArray       `json:"arrayValue"`
		KvlistValue *wireKvlist      `json:"kvlistValue"`
		BytesValue  *string          `json:"bytesValue"`
	}
	if err := json.Unmarshal(data, &raw); err != nil {
		return err
	}
	*v = wireValue{StringValue: raw.StringValue, BoolValue: raw.BoolValue, IntValue: raw.IntValue, ArrayValue: raw.ArrayValue, KvlistValue: raw.KvlistValue, BytesValue: raw.BytesValue}
	if raw.DoubleValue != nil {
		var f float64
		if json.Unmarshal(*raw.DoubleValue, &f) == nil {
			v.DoubleValue = &f
		}
	}
	return nil
}

type wireResource struct {
	Attributes []wireAttr `json:"attributes"`
}

func (r wireResource) decode() Resource {
	a := Attrs(decodeAttrs(r.Attributes))
	return Resource{Service: a.Str("service.name"), Instance: a.Str("service.instance.id"), Version: a.Str("service.version")}
}

func unixNano(s string) time.Time {
	n, err := strconv.ParseInt(s, 10, 64)
	if err != nil || n == 0 {
		return time.Time{}
	}
	return time.Unix(0, n)
}

type wireSpan struct {
	TraceID           string      `json:"traceId"`
	SpanID            string      `json:"spanId"`
	ParentSpanID      string      `json:"parentSpanId"`
	Name              string      `json:"name"`
	Kind              int         `json:"kind"`
	StartTimeUnixNano string      `json:"startTimeUnixNano"`
	EndTimeUnixNano   string      `json:"endTimeUnixNano"`
	Attributes        []wireAttr  `json:"attributes"`
	Events            []wireEvent `json:"events"`
	Status            *struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
	} `json:"status"`
}

type wireEvent struct {
	TimeUnixNano string     `json:"timeUnixNano"`
	Name         string     `json:"name"`
	Attributes   []wireAttr `json:"attributes"`
}

// ReadSpans calls fn with every span of the traces ring at path, oldest first.
func ReadSpans(path string, fn func(Span) error) error {
	return Scan(path, func(pos Position, line []byte) error {
		var req struct {
			ResourceSpans []struct {
				Resource   wireResource `json:"resource"`
				ScopeSpans []struct {
					Spans []wireSpan `json:"spans"`
				} `json:"scopeSpans"`
			} `json:"resourceSpans"`
		}
		if json.Unmarshal(line, &req) != nil {
			return nil
		}
		for _, rs := range req.ResourceSpans {
			res := rs.Resource.decode()
			for _, ss := range rs.ScopeSpans {
				for _, w := range ss.Spans {
					s := Span{Pos: pos, Resource: res, TraceID: w.TraceID, SpanID: w.SpanID, ParentSpanID: w.ParentSpanID, Name: w.Name, Kind: w.Kind,
						Start: unixNano(w.StartTimeUnixNano), End: unixNano(w.EndTimeUnixNano), Attrs: decodeAttrs(w.Attributes)}
					if w.Status != nil {
						s.StatusCode, s.StatusMsg = w.Status.Code, w.Status.Message
					}
					for _, e := range w.Events {
						s.Events = append(s.Events, Event{Time: unixNano(e.TimeUnixNano), Name: e.Name, Attrs: decodeAttrs(e.Attributes)})
					}
					if err := fn(s); err != nil {
						return err
					}
				}
			}
		}
		return nil
	})
}

// ReadLogs calls fn with every log record of the logs ring at path, oldest first.
func ReadLogs(path string, fn func(Log) error) error {
	return Scan(path, func(pos Position, line []byte) error {
		l, ok := decodeLog(pos, line)
		if !ok {
			return nil
		}
		return fn(l)
	})
}

// DecodeLog decodes one logs.jsonl line. A reader following a growing file
// (a tail) decodes lines itself; ReadLogs covers a whole ring.
func DecodeLog(line []byte) (Log, bool) { return decodeLog(Position{}, line) }

func decodeLog(pos Position, line []byte) (Log, bool) {
	var req struct {
		ResourceLogs []struct {
			Resource  wireResource `json:"resource"`
			ScopeLogs []struct {
				LogRecords []struct {
					TimeUnixNano         string     `json:"timeUnixNano"`
					ObservedTimeUnixNano string     `json:"observedTimeUnixNano"`
					SeverityNumber       int        `json:"severityNumber"`
					SeverityText         string     `json:"severityText"`
					Body                 *wireValue `json:"body"`
					Attributes           []wireAttr `json:"attributes"`
					TraceID              string     `json:"traceId"`
					SpanID               string     `json:"spanId"`
					EventName            string     `json:"eventName"`
				} `json:"logRecords"`
			} `json:"scopeLogs"`
		} `json:"resourceLogs"`
	}
	if json.Unmarshal(line, &req) != nil {
		return Log{}, false
	}
	// The sink writes one record per request, so a line holds at most one.
	for _, rl := range req.ResourceLogs {
		for _, sl := range rl.ScopeLogs {
			if len(sl.LogRecords) == 0 {
				continue
			}
			w := sl.LogRecords[0]
			l := Log{Pos: pos, Resource: rl.Resource.decode(), Time: unixNano(w.TimeUnixNano), Observed: unixNano(w.ObservedTimeUnixNano),
				EventName: w.EventName, Severity: w.SeverityNumber, SeverityText: w.SeverityText, TraceID: w.TraceID, SpanID: w.SpanID, Attrs: decodeAttrs(w.Attributes)}
			if l.Time.IsZero() {
				l.Time = l.Observed
			}
			if w.Body != nil && w.Body.StringValue != nil {
				l.Body = *w.Body.StringValue
			}
			return l, true
		}
	}
	return Log{}, false
}

// ReadMetrics calls fn with every metric export of the metrics ring at path,
// oldest first. Exports are cumulative every 10 s and once at shutdown; the
// last export of a metric is its total.
func ReadMetrics(path string, fn func(Metric) error) error {
	return Scan(path, func(pos Position, line []byte) error {
		var req struct {
			ResourceMetrics []struct {
				Resource     wireResource `json:"resource"`
				ScopeMetrics []struct {
					Metrics []struct {
						Name  string `json:"name"`
						Unit  string `json:"unit"`
						Gauge *struct {
							DataPoints []wirePoint `json:"dataPoints"`
						} `json:"gauge"`
						Sum *struct {
							DataPoints  []wirePoint `json:"dataPoints"`
							IsMonotonic bool        `json:"isMonotonic"`
						} `json:"sum"`
						Histogram *struct {
							DataPoints []wirePoint `json:"dataPoints"`
						} `json:"histogram"`
					} `json:"metrics"`
				} `json:"scopeMetrics"`
			} `json:"resourceMetrics"`
		}
		if json.Unmarshal(line, &req) != nil {
			return nil
		}
		for _, rm := range req.ResourceMetrics {
			res := rm.Resource.decode()
			for _, sm := range rm.ScopeMetrics {
				for _, w := range sm.Metrics {
					m := Metric{Pos: pos, Resource: res, Name: w.Name, Unit: w.Unit}
					var points []wirePoint
					switch {
					case w.Gauge != nil:
						m.Kind, points = Gauge, w.Gauge.DataPoints
					case w.Sum != nil:
						m.Kind, m.Monotonic, points = Sum, w.Sum.IsMonotonic, w.Sum.DataPoints
					case w.Histogram != nil:
						m.Kind, points = Histogram, w.Histogram.DataPoints
					default:
						continue
					}
					for _, p := range points {
						m.Points = append(m.Points, p.decode())
					}
					if err := fn(m); err != nil {
						return err
					}
				}
			}
		}
		return nil
	})
}

type wirePoint struct {
	Attributes        []wireAttr `json:"attributes"`
	StartTimeUnixNano string     `json:"startTimeUnixNano"`
	TimeUnixNano      string     `json:"timeUnixNano"`
	AsInt             *string    `json:"asInt"`
	AsDouble          *float64   `json:"asDouble"`
	Count             string     `json:"count"`
	Sum               *float64   `json:"sum"`
	BucketCounts      []string   `json:"bucketCounts"`
	ExplicitBounds    []float64  `json:"explicitBounds"`
}

func (w wirePoint) decode() Point {
	p := Point{Attrs: decodeAttrs(w.Attributes), Start: unixNano(w.StartTimeUnixNano), Time: unixNano(w.TimeUnixNano), Bounds: w.ExplicitBounds}
	switch {
	case w.AsInt != nil:
		n, _ := strconv.ParseInt(*w.AsInt, 10, 64)
		p.Value = float64(n)
	case w.AsDouble != nil:
		p.Value = *w.AsDouble
	}
	if w.Count != "" {
		p.Count, _ = strconv.ParseUint(w.Count, 10, 64)
	}
	if w.Sum != nil {
		p.Sum = *w.Sum
	}
	for _, c := range w.BucketCounts {
		n, _ := strconv.ParseUint(c, 10, 64)
		p.Buckets = append(p.Buckets, n)
	}
	return p
}
