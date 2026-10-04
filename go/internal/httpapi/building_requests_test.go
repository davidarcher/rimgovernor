package httpapi

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"io"
	"strings"
	"testing"
)

const requestWorld = `{"colonyId":"colony","loadToken":"load","mapId":0}`
const manualJSON = `{"requestId":"request","expected":` + requestWorld + `}`

func TestBuildingRequestTypedMapping(t *testing.T) {
	world := store.World{Colony: "colony", Load: "load", Map: 0}
	a, e := decodeControl(strings.NewReader(manualJSON), store.ResumeControl)
	if e != nil || a.Kind != store.ResumeControl || a.World != world || a.RequestID != "request" {
		t.Fatal(a, e)
	}
	m, e := decodeControl(strings.NewReader(manualJSON), store.PauseControl)
	if e != nil || m.Kind != store.PauseControl || m.World != world {
		t.Fatal(m, e)
	}
	for _, id := range []string{`colony-\ud83d\ude00`, strings.Repeat("x", 256)} {
		if _, e := decodeControl(strings.NewReader(strings.Replace(manualJSON, `"request"`, `"`+id+`"`, 1)), store.PauseControl); e != nil {
			t.Fatal(e)
		}
	}
}
func TestBuildingRequestRejectMalformed(t *testing.T) {
	decode := map[string]func(io.Reader) error{"resume": func(r io.Reader) error { _, e := decodeControl(r, store.ResumeControl); return e }, "pause": func(r io.Reader) error { _, e := decodeControl(r, store.PauseControl); return e }}
	for name, raw := range map[string]string{"resume": manualJSON, "pause": manualJSON} {
		t.Run(name, func(t *testing.T) {
			for _, bad := range []string{raw + `{}`, raw[:len(raw)-1], `null`, `[]`, strings.Replace(raw, `"requestId":"request"`, `"RequestId":"request"`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":null`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"request","requestId":"again"`, 1), strings.Replace(raw, `"mapId":0`, `"mapId":0,"map\u0049d":1`, 1), strings.Replace(raw, `"mapId":0`, `"mapId":null`, 1), strings.Replace(raw, `"mapId":0`, `"mapId":0,"unknown":1`, 1), strings.Replace(raw, `"expected":`+requestWorld, `"expected":null`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"\ud800"`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"`+string([]byte{255})+`"`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"\u0000"`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"   "`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"`+strings.Repeat("x", 257)+`"`, 1), strings.Replace(raw, `"requestId":"request"`, `"requestId":"request","kind":"manual"`, 1)} {
				if e := decode[name](strings.NewReader(bad)); e == nil {
					t.Fatalf("accepted %s", bad)
				}
			}
			for _, n := range []string{"-1", "2147483648", "0.0", "1e0", `"0"`} {
				if e := decode[name](strings.NewReader(strings.Replace(raw, `"mapId":0`, `"mapId":`+n, 1))); e == nil {
					t.Fatal(n)
				}
			}
		})
	}
}

type boundedRequestReader struct{ read int }

func (r *boundedRequestReader) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = ' '
	}
	r.read += len(p)
	return len(p), nil
}

type failedRequestReader struct{}

func (failedRequestReader) Read([]byte) (int, error) { return 0, errors.New("read failed") }
func TestBuildingRequestBoundedRead(t *testing.T) {
	r := &boundedRequestReader{}
	if _, e := decodeControl(r, store.PauseControl); e == nil || r.read != 8193 {
		t.Fatal(r.read, e)
	}
	if _, e := decodeControl(failedRequestReader{}, store.PauseControl); e == nil {
		t.Fatal("ignored IO error")
	}
	if _, e := decodeControl(strings.NewReader(manualJSON+strings.Repeat(" ", 8192-len(manualJSON))), store.PauseControl); e != nil {
		t.Fatal(e)
	}
}
