package otel

// OTLP/JSON encoding for the local file sink. Each function returns one
// complete, self-contained OTLP/JSON request (ExportTraceServiceRequest,
// ExportMetricsServiceRequest or ExportLogsServiceRequest) carrying exactly one
// span, one metric or one log record, so a file is one request per line. The
// field names, int64-as-string and hex id rules follow the OTLP/JSON mapping:
// https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding

import (
	"encoding/base64"
	"encoding/json"
	"math"
	"strconv"
	"sync"

	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/sdk/instrumentation"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric/metricdata"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

type jsonValue struct {
	StringValue *string     `json:"stringValue,omitempty"`
	BoolValue   *bool       `json:"boolValue,omitempty"`
	IntValue    *string     `json:"intValue,omitempty"`
	DoubleValue *float64    `json:"doubleValue,omitempty"`
	ArrayValue  *jsonArray  `json:"arrayValue,omitempty"`
	KvlistValue *jsonKvlist `json:"kvlistValue,omitempty"`
	BytesValue  *string     `json:"bytesValue,omitempty"`
}

type jsonArray struct {
	Values []jsonValue `json:"values"`
}

type jsonKvlist struct {
	Values []jsonAttr `json:"values"`
}

type jsonAttr struct {
	Key   string    `json:"key"`
	Value jsonValue `json:"value"`
}

func strValue(s string) jsonValue { return jsonValue{StringValue: &s} }

func encodeValue(v attribute.Value) jsonValue {
	switch v.Type() {
	case attribute.EMPTY:
		return jsonValue{}
	case attribute.BOOL:
		b := v.AsBool()
		return jsonValue{BoolValue: &b}
	case attribute.INT64:
		s := strconv.FormatInt(v.AsInt64(), 10)
		return jsonValue{IntValue: &s}
	case attribute.FLOAT64:
		return floatValue(v.AsFloat64())
	case attribute.STRING:
		return strValue(v.AsString())
	case attribute.BOOLSLICE:
		var out []jsonValue
		for _, b := range v.AsBoolSlice() {
			b := b
			out = append(out, jsonValue{BoolValue: &b})
		}
		return jsonValue{ArrayValue: &jsonArray{Values: out}}
	case attribute.INT64SLICE:
		var out []jsonValue
		for _, i := range v.AsInt64Slice() {
			s := strconv.FormatInt(i, 10)
			out = append(out, jsonValue{IntValue: &s})
		}
		return jsonValue{ArrayValue: &jsonArray{Values: out}}
	case attribute.FLOAT64SLICE:
		var out []jsonValue
		for _, f := range v.AsFloat64Slice() {
			out = append(out, floatValue(f))
		}
		return jsonValue{ArrayValue: &jsonArray{Values: out}}
	case attribute.STRINGSLICE:
		var out []jsonValue
		for _, s := range v.AsStringSlice() {
			out = append(out, strValue(s))
		}
		return jsonValue{ArrayValue: &jsonArray{Values: out}}
	case attribute.BYTESLICE:
		s := base64.StdEncoding.EncodeToString(v.AsByteSlice())
		return jsonValue{BytesValue: &s}
	case attribute.SLICE:
		var out []jsonValue
		for _, e := range v.AsSlice() {
			out = append(out, encodeValue(e))
		}
		return jsonValue{ArrayValue: &jsonArray{Values: out}}
	case attribute.MAP:
		return jsonValue{KvlistValue: &jsonKvlist{Values: encodeAttrs(v.AsMap())}}
	}
	return strValue(v.String())
}

// floatValue keeps NaN and the infinities representable: JSON numbers cannot
// carry them, so they travel as their string form.
func floatValue(f float64) jsonValue {
	if math.IsNaN(f) || math.IsInf(f, 0) {
		return strValue(strconv.FormatFloat(f, 'g', -1, 64))
	}
	return jsonValue{DoubleValue: &f}
}

func encodeAttrs(kvs []attribute.KeyValue) []jsonAttr {
	if len(kvs) == 0 {
		return nil
	}
	out := make([]jsonAttr, 0, len(kvs))
	for _, kv := range kvs {
		out = append(out, jsonAttr{Key: string(kv.Key), Value: encodeValue(kv.Value)})
	}
	return out
}

type jsonResource struct {
	Attributes             []jsonAttr `json:"attributes,omitempty"`
	DroppedAttributesCount uint32     `json:"droppedAttributesCount,omitempty"`
}

type jsonScope struct {
	Name       string     `json:"name"`
	Version    string     `json:"version,omitempty"`
	Attributes []jsonAttr `json:"attributes,omitempty"`
}

// envelope caches the encoded `"resource":...,"scope":...` fragment per
// (resource, scope): every record of a batch repeats it, and it never changes.
type envelope struct {
	mu    sync.Mutex
	cache map[envelopeKey]envelopeParts
}

type envelopeKey struct {
	resource attribute.Distinct
	name     string
	version  string
	schema   string
}

type envelopeParts struct {
	resource  []byte // {"attributes":[...]}
	scope     []byte
	schemaURL string
}

const maxEnvelopes = 256

func (e *envelope) parts(res *resource.Resource, scope instrumentation.Scope) envelopeParts {
	var key envelopeKey
	if res != nil {
		key.resource = res.Equivalent()
	}
	key.name, key.version, key.schema = scope.Name, scope.Version, scope.SchemaURL
	e.mu.Lock()
	defer e.mu.Unlock()
	if p, ok := e.cache[key]; ok {
		return p
	}
	var r jsonResource
	if res != nil {
		r.Attributes = encodeAttrs(res.Attributes())
	}
	rb, _ := json.Marshal(r)
	sb, _ := json.Marshal(jsonScope{Name: scope.Name, Version: scope.Version, Attributes: encodeAttrs(scope.Attributes.ToSlice())})
	if e.cache == nil || len(e.cache) >= maxEnvelopes {
		e.cache = make(map[envelopeKey]envelopeParts)
	}
	p := envelopeParts{resource: rb, scope: sb, schemaURL: scope.SchemaURL}
	e.cache[key] = p
	return p
}

// wrap assembles `{"<top>":[{"resource":R,"<scopeKey>":[{"scope":S,"<itemKey>":[ITEM]}]}]}`.
func (e *envelope) wrap(dst []byte, top, scopeKey, itemKey string, res *resource.Resource, scope instrumentation.Scope, item []byte) []byte {
	p := e.parts(res, scope)
	dst = append(dst, `{"`...)
	dst = append(dst, top...)
	dst = append(dst, `":[{"resource":`...)
	dst = append(dst, p.resource...)
	dst = append(dst, `,"`...)
	dst = append(dst, scopeKey...)
	dst = append(dst, `":[{"scope":`...)
	dst = append(dst, p.scope...)
	dst = append(dst, `,"`...)
	dst = append(dst, itemKey...)
	dst = append(dst, `":[`...)
	dst = append(dst, item...)
	dst = append(dst, "]}]}]}"...)
	return dst
}

// --- traces ---

type jsonSpan struct {
	TraceID                string      `json:"traceId"`
	SpanID                 string      `json:"spanId"`
	TraceState             string      `json:"traceState,omitempty"`
	ParentSpanID           string      `json:"parentSpanId,omitempty"`
	Flags                  uint32      `json:"flags,omitempty"`
	Name                   string      `json:"name"`
	Kind                   int         `json:"kind"`
	StartTimeUnixNano      string      `json:"startTimeUnixNano"`
	EndTimeUnixNano        string      `json:"endTimeUnixNano"`
	Attributes             []jsonAttr  `json:"attributes,omitempty"`
	DroppedAttributesCount int         `json:"droppedAttributesCount,omitempty"`
	Events                 []jsonEvent `json:"events,omitempty"`
	DroppedEventsCount     int         `json:"droppedEventsCount,omitempty"`
	Links                  []jsonLink  `json:"links,omitempty"`
	DroppedLinksCount      int         `json:"droppedLinksCount,omitempty"`
	Status                 *jsonStatus `json:"status,omitempty"`
}

type jsonEvent struct {
	TimeUnixNano           string     `json:"timeUnixNano"`
	Name                   string     `json:"name"`
	Attributes             []jsonAttr `json:"attributes,omitempty"`
	DroppedAttributesCount int        `json:"droppedAttributesCount,omitempty"`
}

type jsonLink struct {
	TraceID                string     `json:"traceId"`
	SpanID                 string     `json:"spanId"`
	TraceState             string     `json:"traceState,omitempty"`
	Attributes             []jsonAttr `json:"attributes,omitempty"`
	DroppedAttributesCount int        `json:"droppedAttributesCount,omitempty"`
}

type jsonStatus struct {
	Message string `json:"message,omitempty"`
	Code    int    `json:"code"`
}

func nanos(unixNano int64) string { return strconv.FormatInt(unixNano, 10) }

// encodeSpan appends one ExportTraceServiceRequest line (without newline).
func (e *envelope) encodeSpan(dst []byte, s sdktrace.ReadOnlySpan) ([]byte, error) {
	sc := s.SpanContext()
	js := jsonSpan{
		TraceID:                sc.TraceID().String(),
		SpanID:                 sc.SpanID().String(),
		TraceState:             sc.TraceState().String(),
		Flags:                  uint32(sc.TraceFlags()),
		Name:                   s.Name(),
		Kind:                   int(s.SpanKind()),
		StartTimeUnixNano:      nanos(s.StartTime().UnixNano()),
		EndTimeUnixNano:        nanos(s.EndTime().UnixNano()),
		Attributes:             encodeAttrs(s.Attributes()),
		DroppedAttributesCount: s.DroppedAttributes(),
		DroppedEventsCount:     s.DroppedEvents(),
		DroppedLinksCount:      s.DroppedLinks(),
	}
	if p := s.Parent(); p.SpanID().IsValid() {
		js.ParentSpanID = p.SpanID().String()
	}
	for _, ev := range s.Events() {
		js.Events = append(js.Events, jsonEvent{TimeUnixNano: nanos(ev.Time.UnixNano()), Name: ev.Name, Attributes: encodeAttrs(ev.Attributes), DroppedAttributesCount: ev.DroppedAttributeCount})
	}
	for _, l := range s.Links() {
		js.Links = append(js.Links, jsonLink{TraceID: l.SpanContext.TraceID().String(), SpanID: l.SpanContext.SpanID().String(), TraceState: l.SpanContext.TraceState().String(), Attributes: encodeAttrs(l.Attributes), DroppedAttributesCount: l.DroppedAttributeCount})
	}
	switch st := s.Status(); st.Code {
	case codes.Ok:
		js.Status = &jsonStatus{Code: 1}
	case codes.Error:
		js.Status = &jsonStatus{Code: 2, Message: st.Description}
	}
	item, err := json.Marshal(js)
	if err != nil {
		return dst, err
	}
	return e.wrap(dst, "resourceSpans", "scopeSpans", "spans", s.Resource(), s.InstrumentationScope(), item), nil
}

// --- logs ---

type jsonLog struct {
	TimeUnixNano           string     `json:"timeUnixNano,omitempty"`
	ObservedTimeUnixNano   string     `json:"observedTimeUnixNano"`
	SeverityNumber         int        `json:"severityNumber,omitempty"`
	SeverityText           string     `json:"severityText,omitempty"`
	Body                   *jsonValue `json:"body,omitempty"`
	Attributes             []jsonAttr `json:"attributes,omitempty"`
	DroppedAttributesCount int        `json:"droppedAttributesCount,omitempty"`
	Flags                  uint32     `json:"flags,omitempty"`
	TraceID                string     `json:"traceId,omitempty"`
	SpanID                 string     `json:"spanId,omitempty"`
	EventName              string     `json:"eventName,omitempty"`
}

// encodeLog appends one ExportLogsServiceRequest line (without newline).
func (e *envelope) encodeLog(dst []byte, r *sdklog.Record) ([]byte, error) {
	jl := jsonLog{
		SeverityNumber:         int(r.Severity()),
		SeverityText:           r.SeverityText(),
		DroppedAttributesCount: r.DroppedAttributes(),
		EventName:              r.EventName(),
		Flags:                  uint32(r.TraceFlags()),
	}
	if t := r.Timestamp(); !t.IsZero() {
		jl.TimeUnixNano = nanos(t.UnixNano())
	}
	obs := r.ObservedTimestamp()
	if obs.IsZero() {
		obs = r.Timestamp()
	}
	jl.ObservedTimeUnixNano = nanos(obs.UnixNano())
	if b := r.Body(); b.Type() != attribute.EMPTY {
		v := encodeValue(b)
		jl.Body = &v
	}
	r.WalkAttributes(func(kv attribute.KeyValue) bool {
		jl.Attributes = append(jl.Attributes, jsonAttr{Key: string(kv.Key), Value: encodeValue(kv.Value)})
		return true
	})
	if id := r.TraceID(); id.IsValid() {
		jl.TraceID = id.String()
	}
	if id := r.SpanID(); id.IsValid() {
		jl.SpanID = id.String()
	}
	item, err := json.Marshal(jl)
	if err != nil {
		return dst, err
	}
	return e.wrap(dst, "resourceLogs", "scopeLogs", "logRecords", r.Resource(), r.InstrumentationScope(), item), nil
}

// --- metrics ---

type jsonNumberPoint struct {
	Attributes        []jsonAttr `json:"attributes,omitempty"`
	StartTimeUnixNano string     `json:"startTimeUnixNano,omitempty"`
	TimeUnixNano      string     `json:"timeUnixNano"`
	AsInt             *string    `json:"asInt,omitempty"`
	AsDouble          *float64   `json:"asDouble,omitempty"`
}

type jsonHistogramPoint struct {
	Attributes        []jsonAttr `json:"attributes,omitempty"`
	StartTimeUnixNano string     `json:"startTimeUnixNano,omitempty"`
	TimeUnixNano      string     `json:"timeUnixNano"`
	Count             string     `json:"count"`
	Sum               *float64   `json:"sum,omitempty"`
	BucketCounts      []string   `json:"bucketCounts"`
	ExplicitBounds    []float64  `json:"explicitBounds"`
	Min               *float64   `json:"min,omitempty"`
	Max               *float64   `json:"max,omitempty"`
}

type jsonMetric struct {
	Name        string         `json:"name"`
	Description string         `json:"description,omitempty"`
	Unit        string         `json:"unit,omitempty"`
	Gauge       *jsonGauge     `json:"gauge,omitempty"`
	Sum         *jsonSum       `json:"sum,omitempty"`
	Histogram   *jsonHistogram `json:"histogram,omitempty"`
}

type jsonGauge struct {
	DataPoints []jsonNumberPoint `json:"dataPoints"`
}

type jsonSum struct {
	DataPoints             []jsonNumberPoint `json:"dataPoints"`
	AggregationTemporality int               `json:"aggregationTemporality"`
	IsMonotonic            bool              `json:"isMonotonic"`
}

type jsonHistogram struct {
	DataPoints             []jsonHistogramPoint `json:"dataPoints"`
	AggregationTemporality int                  `json:"aggregationTemporality"`
}

func temporality(t sdkmetric.Temporality) int {
	switch t {
	case sdkmetric.DeltaTemporality:
		return 1
	case sdkmetric.CumulativeTemporality:
		return 2
	}
	return 0
}

type number interface{ int64 | float64 }

func numberPoints[N number](pts []sdkmetric.DataPoint[N]) []jsonNumberPoint {
	out := make([]jsonNumberPoint, 0, len(pts))
	for _, p := range pts {
		jp := jsonNumberPoint{Attributes: encodeAttrs(p.Attributes.ToSlice()), TimeUnixNano: nanos(p.Time.UnixNano())}
		if !p.StartTime.IsZero() {
			jp.StartTimeUnixNano = nanos(p.StartTime.UnixNano())
		}
		switch v := any(p.Value).(type) {
		case int64:
			s := strconv.FormatInt(v, 10)
			jp.AsInt = &s
		case float64:
			jp.AsDouble = &v
		}
		out = append(out, jp)
	}
	return out
}

func histogramPoints[N number](pts []sdkmetric.HistogramDataPoint[N]) []jsonHistogramPoint {
	out := make([]jsonHistogramPoint, 0, len(pts))
	for _, p := range pts {
		sum := float64(p.Sum)
		jp := jsonHistogramPoint{
			Attributes:     encodeAttrs(p.Attributes.ToSlice()),
			TimeUnixNano:   nanos(p.Time.UnixNano()),
			Count:          strconv.FormatUint(p.Count, 10),
			Sum:            &sum,
			ExplicitBounds: p.Bounds,
		}
		if !p.StartTime.IsZero() {
			jp.StartTimeUnixNano = nanos(p.StartTime.UnixNano())
		}
		for _, c := range p.BucketCounts {
			jp.BucketCounts = append(jp.BucketCounts, strconv.FormatUint(c, 10))
		}
		if v, ok := p.Min.Value(); ok {
			f := float64(v)
			jp.Min = &f
		}
		if v, ok := p.Max.Value(); ok {
			f := float64(v)
			jp.Max = &f
		}
		out = append(out, jp)
	}
	return out
}

// encodeMetric converts one metric; ok is false for an aggregation the sink
// does not carry (exponential histograms, which the provider never configures).
func encodeMetric(m sdkmetric.Metrics) (jsonMetric, bool) {
	jm := jsonMetric{Name: m.Name, Description: m.Description, Unit: m.Unit}
	switch d := m.Data.(type) {
	case sdkmetric.Gauge[int64]:
		jm.Gauge = &jsonGauge{DataPoints: numberPoints(d.DataPoints)}
	case sdkmetric.Gauge[float64]:
		jm.Gauge = &jsonGauge{DataPoints: numberPoints(d.DataPoints)}
	case sdkmetric.Sum[int64]:
		jm.Sum = &jsonSum{DataPoints: numberPoints(d.DataPoints), AggregationTemporality: temporality(d.Temporality), IsMonotonic: d.IsMonotonic}
	case sdkmetric.Sum[float64]:
		jm.Sum = &jsonSum{DataPoints: numberPoints(d.DataPoints), AggregationTemporality: temporality(d.Temporality), IsMonotonic: d.IsMonotonic}
	case sdkmetric.Histogram[int64]:
		jm.Histogram = &jsonHistogram{DataPoints: histogramPoints(d.DataPoints), AggregationTemporality: temporality(d.Temporality)}
	case sdkmetric.Histogram[float64]:
		jm.Histogram = &jsonHistogram{DataPoints: histogramPoints(d.DataPoints), AggregationTemporality: temporality(d.Temporality)}
	default:
		return jm, false
	}
	return jm, true
}

// encodeMetricLine appends one ExportMetricsServiceRequest line for a metric.
func (e *envelope) encodeMetricLine(dst []byte, res *resource.Resource, scope instrumentation.Scope, m sdkmetric.Metrics) ([]byte, bool, error) {
	jm, ok := encodeMetric(m)
	if !ok {
		return dst, false, nil
	}
	item, err := json.Marshal(jm)
	if err != nil {
		return dst, false, err
	}
	return e.wrap(dst, "resourceMetrics", "scopeMetrics", "metrics", res, scope, item), true, nil
}
