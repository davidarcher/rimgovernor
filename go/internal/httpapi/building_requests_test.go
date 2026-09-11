package httpapi

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"io"
	"strings"
	"testing"
)

const requestWorld = `{"colonyId":"colony","loadToken":"load","mapId":0}`
const submissionJSON = `{"requestId":"request","expected":` + requestWorld + `,"building":{"defName":"Wall","x":0,"z":0,"rotation":"north","stuff":""}}`
const acquireJSON = `{"requestId":"request","expected":` + requestWorld + `,"planId":"plan","revision":"18446744073709551615","expectedDirection":"0"}`
const manualJSON = `{"requestId":"request","expected":` + requestWorld + `}`

func TestBuildingRequestTypedMapping(t *testing.T) {
	s, e := decodeBuildingSubmission(strings.NewReader(submissionJSON))
	if e != nil || s.RequestID != "request" || s.World != (store.World{Colony: "colony", Load: "load", Map: 0}) || s.Building.Definition() != "Wall" || s.Building.Stuff() != "" || s.Building.Cell() != (domain.Cell{}) || s.Building.Rotation() != domain.North {
		t.Fatal(s, e)
	}
	a, e := decodeBuildingAcquire(strings.NewReader(acquireJSON))
	if e != nil || a.Kind != store.AcquireControl || a.Plan != "plan" || a.Revision != domain.PlanRevision(^uint64(0)) || a.ExpectedDirection != 0 || a.World != s.World || a.RequestID != s.RequestID {
		t.Fatal(a, e)
	}
	m, e := decodeBuildingManual(strings.NewReader(manualJSON))
	if e != nil || m.Kind != store.ManualControl || m.Plan != "" || m.Revision != 0 || m.ExpectedDirection != 0 || m.World != s.World {
		t.Fatal(m, e)
	}
	for _, rotation := range []string{"north", "east", "south", "west"} {
		if _, e := decodeBuildingSubmission(strings.NewReader(strings.Replace(submissionJSON, "north", rotation, 1))); e != nil {
			t.Fatal(rotation, e)
		}
	}
	for _, id := range []string{`colony-\ud83d\ude00`, strings.Repeat("x", 256)} {
		if _, e := decodeBuildingManual(strings.NewReader(strings.Replace(manualJSON, `"request"`, `"`+id+`"`, 1))); e != nil {
			t.Fatal(e)
		}
	}
}
func TestBuildingRequestRejectMalformed(t *testing.T) {
	decode := map[string]func(io.Reader) error{"submission": func(r io.Reader) error { _, e := decodeBuildingSubmission(r); return e }, "acquire": func(r io.Reader) error { _, e := decodeBuildingAcquire(r); return e }, "manual": func(r io.Reader) error { _, e := decodeBuildingManual(r); return e }}
	for name, raw := range map[string]string{"submission": submissionJSON, "acquire": acquireJSON, "manual": manualJSON} {
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
	for _, n := range []string{`0`, `null`, `"0"`, `"01"`, `"+1"`, `"18446744073709551616"`} {
		if _, e := decodeBuildingAcquire(strings.NewReader(strings.Replace(acquireJSON, `"18446744073709551615"`, n, 1))); e == nil {
			t.Fatal(n)
		}
	}
	for _, n := range []string{`0`, `null`, `"-1"`, `"01"`, `"18446744073709551616"`} {
		if _, e := decodeBuildingAcquire(strings.NewReader(strings.Replace(acquireJSON, `"expectedDirection":"0"`, `"expectedDirection":`+n, 1))); e == nil {
			t.Fatal(n)
		}
	}
	for _, bad := range []string{strings.Replace(submissionJSON, `"stuff":""`, `"stuff":null`, 1), strings.Replace(submissionJSON, `,"stuff":""`, "", 1), strings.Replace(submissionJSON, `"x":0`, `"x":0.0`, 1), strings.Replace(submissionJSON, `"z":0`, `"z":-1`, 1), strings.Replace(submissionJSON, `"defName":"Wall"`, `"defName":""`, 1), strings.Replace(submissionJSON, "north", "random", 1), strings.Replace(submissionJSON, `"x":0`, `"x":0,"x":1`, 1)} {
		if _, e := decodeBuildingSubmission(strings.NewReader(bad)); e == nil {
			t.Fatal(bad)
		}
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
	if _, e := decodeBuildingManual(r); e == nil || r.read != 8193 {
		t.Fatal(r.read, e)
	}
	if _, e := decodeBuildingManual(failedRequestReader{}); e == nil {
		t.Fatal("ignored IO error")
	}
	if _, e := decodeBuildingManual(strings.NewReader(manualJSON + strings.Repeat(" ", 8192-len(manualJSON)))); e != nil {
		t.Fatal(e)
	}
}
