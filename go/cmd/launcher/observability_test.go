package main

import (
	"context"
	"errors"
	"os/exec"
	"reflect"
	"strings"
	"testing"
	"time"
)

type fakeRunner struct {
	noDocker bool
	failOn   string // a verb ("info", "up", "down") whose run fails
	calls    [][]string
}

func (f *fakeRunner) LookPath(string) (string, error) {
	if f.noDocker {
		return "", exec.ErrNotFound
	}
	return "docker", nil
}

func (f *fakeRunner) Run(_ context.Context, name string, args ...string) ([]byte, error) {
	f.calls = append(f.calls, append([]string{name}, args...))
	for _, a := range args {
		if a == f.failOn {
			return []byte("boom\n"), errors.New("exit status 1")
		}
	}
	return []byte("ok\n"), nil
}

func newTestObs(f *fakeRunner, healthy *bool) (*obsController, *[]string, *time.Time) {
	var logs []string
	now := time.Unix(1000, 0)
	o := newObsController("repo", f, func(format string, args ...any) { logs = append(logs, format) })
	o.probe = func() bool { return *healthy }
	o.now = func() time.Time { return now }
	return o, &logs, &now
}

func TestComposeCommandConstruction(t *testing.T) {
	f, healthy := &fakeRunner{}, false
	o, _, _ := newTestObs(f, &healthy)
	o.Start()
	o.Stop()
	file := o.compose
	if !strings.HasSuffix(file, "compose.yml") || !strings.Contains(file, "observability") {
		t.Fatalf("compose file %q", file)
	}
	want := [][]string{
		{"docker", "info", "--format", "{{.ServerVersion}}"},
		{"docker", "compose", "-f", file, "-p", "rimgovernor-observability", "up", "-d"},
		{"docker", "info", "--format", "{{.ServerVersion}}"},
		{"docker", "compose", "-f", file, "-p", "rimgovernor-observability", "down"},
	}
	if !reflect.DeepEqual(f.calls, want) {
		t.Fatalf("calls = %v\nwant %v", f.calls, want)
	}
}

func TestObsStatus(t *testing.T) {
	f, healthy := &fakeRunner{}, false
	o, _, now := newTestObs(f, &healthy)
	if v := o.View(); v.State != obsStopped || v.Problem != "" {
		t.Fatalf("initial %+v", v)
	}
	o.Start()
	if v := o.View(); v.State != obsStarting {
		t.Fatalf("after start %+v", v)
	}
	healthy = true
	if v := o.View(); v.State != obsHealthy {
		t.Fatalf("healthy %+v", v)
	}
	healthy = false
	*now = now.Add(obsStartWindow + time.Second)
	if v := o.View(); v.State != obsStopped {
		t.Fatalf("start window over %+v", v)
	}
	o.Start()
	o.Stop()
	if v := o.View(); v.State != obsStopped {
		t.Fatalf("after stop %+v", v)
	}
}

func TestObsDockerMissingAndDaemonDown(t *testing.T) {
	healthy := false
	f := &fakeRunner{noDocker: true}
	o, _, _ := newTestObs(f, &healthy)
	if v := o.View(); v.Problem != dockerMissing {
		t.Fatalf("view %+v", v)
	}
	o.Start()
	if len(f.calls) != 0 {
		t.Fatalf("ran %v without docker", f.calls)
	}
	if v := o.View(); v.State != obsStopped || v.Problem != dockerMissing {
		t.Fatalf("view %+v", v)
	}

	f = &fakeRunner{failOn: "info"}
	o, _, _ = newTestObs(f, &healthy)
	o.Start()
	if len(f.calls) != 1 {
		t.Fatalf("calls %v", f.calls)
	}
	if v := o.View(); v.State != obsStopped || v.Problem != dockerDown {
		t.Fatalf("view %+v", v)
	}
}

func TestObsComposeFailureIsReportedAndLogged(t *testing.T) {
	healthy := false
	f := &fakeRunner{failOn: "up"}
	o, logs, _ := newTestObs(f, &healthy)
	o.Start()
	v := o.View()
	if v.State != obsStopped || !strings.Contains(v.Problem, "docker compose up failed") {
		t.Fatalf("view %+v", v)
	}
	joined := strings.Join(*logs, "|")
	if !strings.Contains(joined, "observability: %s") {
		t.Fatalf("output not logged: %v", *logs)
	}
}

func TestServeArgsTelemetryEndpoint(t *testing.T) {
	p := Paths{Profile: "p", Config: "c", Game: "g", State: "s"}
	off, _ := ServeArgs(Settings{}, p, 8787)
	for _, a := range off {
		if a == "--otlp-endpoint" {
			t.Fatalf("endpoint passed with telemetry off: %v", off)
		}
	}
	on, _ := ServeArgs(Settings{Telemetry: true}, p, 8787)
	found := false
	for i, a := range on {
		if a == "--otlp-endpoint" && on[i+1] == obsOTLPEndpoint {
			found = true
		}
	}
	if !found {
		t.Fatalf("no endpoint with telemetry on: %v", on)
	}
}
