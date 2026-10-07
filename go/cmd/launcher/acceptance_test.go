package main

import (
	"errors"
	"io"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestParseCaseList(t *testing.T) {
	got := ParseCaseList("recovery/ruin\tCovered ruin is cleared\r\nsmoke/boot\t\n\nnoscope\n")
	want := []AcceptCase{
		{Name: "recovery/ruin", Scope: "Covered ruin is cleared"},
		{Name: "smoke/boot"},
		{Name: "noscope"},
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("ParseCaseList = %+v, want %+v", got, want)
	}
}

func TestAcceptRunArgs(t *testing.T) {
	got := strings.Join(AcceptRunArgs("a/b", `C:\r`, `C:\o`, `C:\rg.exe`, true), " ")
	want := `run ./internal/nativeaccept/cmd/acceptance run a/b -root C:\r -output C:\o -headless=false -no-series -rimgovernor C:\rg.exe -fresh`
	if got != want {
		t.Fatalf("args = %q, want %q", got, want)
	}
	if got := strings.Join(AcceptRunArgs("a/b", "r", "o", "", false), " "); strings.Contains(got, "-rimgovernor") || strings.Contains(got, "-fresh") {
		t.Fatalf("optional flags leaked into %q", got)
	}
}

type fakeProc struct {
	done    chan error
	stopped chan struct{}
}

func newFakeProc() *fakeProc {
	return &fakeProc{done: make(chan error, 1), stopped: make(chan struct{})}
}
func (p *fakeProc) Wait() error { return <-p.done }
func (p *fakeProc) Stop() {
	close(p.stopped)
	p.done <- errors.New("killed")
}

type fakeHost struct {
	list     string
	listErr  error
	guardErr error
	proc     *fakeProc
	spawned  []string
	fresh    bool
	out      io.Writer
}

func (h *fakeHost) List() (string, error) { return h.list, h.listErr }
func (h *fakeHost) Guard() error          { return h.guardErr }
func (h *fakeHost) OutputDir(name string) string {
	return "out/" + name
}
func (h *fakeHost) Spawn(name, output string, fresh bool, out io.Writer) (acceptProc, error) {
	h.spawned, h.fresh, h.out = append(h.spawned, name+"@"+output), fresh, out
	h.proc = newFakeProc()
	return h.proc, nil
}

// loaded returns a runner whose case list has loaded.
func loaded(t *testing.T, h *fakeHost) *acceptRunner {
	t.Helper()
	r := newAcceptRunner(h)
	r.Cases()
	waitFor(t, func() bool { return !r.Cases().Loading })
	return r
}

func waitFor(t *testing.T, ok func() bool) {
	t.Helper()
	for deadline := time.Now().Add(10 * time.Second); !ok(); time.Sleep(time.Millisecond) {
		if time.Now().After(deadline) {
			t.Fatal("condition not reached")
		}
	}
}

func TestAcceptCasesLoadOnceAndFailureIsSticky(t *testing.T) {
	r := loaded(t, &fakeHost{list: "a/b\tscope\n"})
	if v := r.Cases(); len(v.Cases) != 1 || v.Cases[0].Name != "a/b" || v.Error != "" {
		t.Fatalf("cases = %+v", v)
	}
	bad := loaded(t, &fakeHost{listErr: errors.New("no go")})
	if v := bad.Cases(); v.Error != "no go" || v.Loading || len(v.Cases) != 0 {
		t.Fatalf("failed load = %+v", v)
	}
}

func TestAcceptRunPassesAndFails(t *testing.T) {
	h := &fakeHost{list: "a/b\tscope\n"}
	r := loaded(t, h)
	if err := r.Run("a/b", true); err != nil {
		t.Fatal(err)
	}
	if !h.fresh || len(h.spawned) != 1 || h.spawned[0] != "a/b@out/a/b" {
		t.Fatalf("spawned %v fresh=%v", h.spawned, h.fresh)
	}
	h.out.Write([]byte("PASS\ta/"))
	h.out.Write([]byte("b\t1s\r\nnext"))
	if v := r.View(); v.State != acceptRunning || !reflect.DeepEqual(v.Output, []string{"PASS\ta/b\t1s", "next"}) {
		t.Fatalf("running view = %+v", v)
	}
	if err := r.Run("a/b", false); err == nil {
		t.Fatal("a second run started while one is in flight")
	}
	if !r.Running() {
		t.Fatal("Running() false during a run")
	}
	h.proc.done <- nil
	waitFor(t, func() bool { return !r.Running() })
	if v := r.View(); v.State != acceptPassed || v.Output[len(v.Output)-1] != "next" {
		t.Fatalf("passed view = %+v", v)
	}

	if err := r.Run("a/b", false); err != nil {
		t.Fatal(err)
	}
	h.proc.done <- errors.New("exit status 1")
	waitFor(t, func() bool { return !r.Running() })
	if v := r.View(); v.State != acceptFailed || v.Error != "exit status 1" || len(v.Output) != 0 {
		t.Fatalf("failed view = %+v (output must restart per run)", v)
	}
}

func TestAcceptRunRefusals(t *testing.T) {
	h := &fakeHost{list: "a/b\tscope\n", guardErr: errors.New("close the game first")}
	r := loaded(t, h)
	if err := r.Run("a/b", false); err == nil || err.Error() != "close the game first" {
		t.Fatalf("guard refusal = %v", err)
	}
	if err := r.Run("nope/case", false); err == nil || !strings.Contains(err.Error(), "unknown case") {
		t.Fatalf("unknown case = %v", err)
	}
	if len(h.spawned) != 0 || r.Running() {
		t.Fatalf("a refused run spawned %v", h.spawned)
	}
	if err := r.Stop(); err == nil {
		t.Fatal("Stop with nothing running succeeded")
	}
}

func TestAcceptStop(t *testing.T) {
	h := &fakeHost{list: "a/b\tscope\n"}
	r := loaded(t, h)
	if err := r.Run("a/b", false); err != nil {
		t.Fatal(err)
	}
	if err := r.Stop(); err != nil {
		t.Fatal(err)
	}
	<-h.proc.stopped
	waitFor(t, func() bool { return !r.Running() })
	if v := r.View(); v.State != acceptStopped || v.Error != "" {
		t.Fatalf("stopped view = %+v", v)
	}
}

func TestAcceptOutputIsBounded(t *testing.T) {
	h := &fakeHost{list: "a/b\tscope\n"}
	r := loaded(t, h)
	if err := r.Run("a/b", false); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < acceptKeepLines+50; i++ {
		h.out.Write([]byte("line\n"))
	}
	if n := len(r.View().Output); n != acceptKeepLines {
		t.Fatalf("kept %d lines, want %d", n, acceptKeepLines)
	}
	h.proc.done <- nil
}
