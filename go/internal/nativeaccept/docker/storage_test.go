package docker

import (
	"context"
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestMain doubles as the fake `docker` CLI Stop is driven against: the test
// binary re-invoked with FAKE_DOCKER_DIR set (before any test flag parsing,
// so docker's argv passes through untouched). State lives in that
// directory -- worker/ is the container's /worker, running holds the
// container's running flag, volumes/<name> marks an existing volume -- and
// every invocation's argv is appended to calls.log for the assertions.
func TestMain(m *testing.M) {
	if dir := os.Getenv("FAKE_DOCKER_DIR"); dir != "" {
		os.Exit(fakeDockerMain(dir, os.Args[1:]))
	}
	os.Exit(m.Run())
}

func fakeDockerMain(dir string, args []string) int {
	log, _ := os.OpenFile(filepath.Join(dir, "calls.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	fmt.Fprintln(log, strings.Join(args, " "))
	log.Close()
	if len(args) < 2 {
		return 2
	}
	switch strings.Join(args[:2], " ") {
	case "inspect --format":
		data, _ := os.ReadFile(filepath.Join(dir, "running"))
		fmt.Println(string(data))
		return 0
	case "stop --time":
		if os.Getenv("FAKE_DOCKER_STUCK") == "" {
			_ = os.WriteFile(filepath.Join(dir, "running"), []byte("false"), 0644)
		}
		return 0
	case "cp " + args[1]:
		entry := strings.TrimPrefix(args[1], "worker:/worker/")
		data, err := os.ReadFile(filepath.Join(dir, "worker", entry))
		if err != nil {
			fmt.Print("Error response from daemon: Could not find the file /worker/" + entry + " in container worker")
			return 1
		}
		_ = os.WriteFile(filepath.Join(args[2], entry), data, 0644)
		return 0
	case "rm -f":
		return 0
	case "logs worker":
		fmt.Print("container logs")
		return 0
	case "volume rm":
		if os.Getenv("FAKE_DOCKER_VOLUME_STUCK") == "" {
			_ = os.Remove(filepath.Join(dir, "volumes", args[2]))
		}
		return 0
	case "volume inspect":
		if _, err := os.Stat(filepath.Join(dir, "volumes", args[2])); err != nil {
			fmt.Print("no such volume")
			return 1
		}
		return 0
	}
	fmt.Print("fake docker: unhandled " + strings.Join(args, " "))
	return 2
}

type fakeDocker struct {
	dir, root string
}

func newFakeDocker(t *testing.T, running bool, volume bool) fakeDocker {
	t.Helper()
	dir := t.TempDir()
	for _, sub := range []string{"worker", "volumes", "root"} {
		if err := os.MkdirAll(filepath.Join(dir, sub), 0755); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.WriteFile(filepath.Join(dir, "running"), []byte(fmt.Sprint(running)), 0644); err != nil {
		t.Fatal(err)
	}
	if volume {
		if err := os.WriteFile(filepath.Join(dir, "volumes", "worker-state"), nil, 0644); err != nil {
			t.Fatal(err)
		}
	}
	return fakeDocker{dir: dir, root: filepath.Join(dir, "root")}
}

func (f fakeDocker) worker(t *testing.T, storage Storage, env ...string) *Worker {
	t.Helper()
	t.Setenv("FAKE_DOCKER_DIR", f.dir)
	for _, pair := range env {
		key, value, _ := strings.Cut(pair, "=")
		t.Setenv(key, value)
	}
	w := &Worker{docker: os.Args[0], Name: "worker", Root: f.root, storage: storage}
	if storage == StorageVolume {
		w.volume = "worker-state"
	}
	return w
}

func (f fakeDocker) calls(t *testing.T) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(f.dir, "calls.log"))
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func (f fakeDocker) volumeExists() bool {
	_, err := os.Stat(filepath.Join(f.dir, "volumes", "worker-state"))
	return err == nil
}

func (f fakeDocker) writeWorkerDatabase(t *testing.T, valid bool) {
	t.Helper()
	path := filepath.Join(f.dir, "worker", "state.db")
	if !valid {
		if err := os.WriteFile(path, []byte(strings.Repeat("not a database\n", 200)), 0644); err != nil {
			t.Fatal(err)
		}
		return
	}
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(path))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec("CREATE TABLE evidence(id INTEGER PRIMARY KEY, note TEXT); INSERT INTO evidence(note) VALUES ('ok')"); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
}

func TestStopExportsIntegrityChecksAndReleasesVolume(t *testing.T) {
	fake := newFakeDocker(t, true, true)
	fake.writeWorkerDatabase(t, true)
	if err := os.WriteFile(filepath.Join(fake.dir, "worker", "flight.jsonl"), []byte("{}\n"), 0644); err != nil {
		t.Fatal(err)
	}
	w := fake.worker(t, StorageVolume)
	evidence, err := w.Stop(context.Background())
	if err != nil {
		t.Fatalf("stop: %v", err)
	}
	if !evidence.Stopped || !evidence.Exported || evidence.Retained {
		t.Fatalf("evidence %+v", evidence)
	}
	if len(evidence.Databases) != 1 || evidence.Databases[0] != "state.db" {
		t.Fatalf("databases %v", evidence.Databases)
	}
	for _, name := range []string{"state.db", "flight.jsonl", "container.log"} {
		if _, err := os.Stat(filepath.Join(fake.root, name)); err != nil {
			t.Fatalf("exported %s: %v", name, err)
		}
	}
	if fake.volumeExists() {
		t.Fatal("volume should have been removed")
	}
	calls := fake.calls(t)
	stop, cp, rm, vrm := strings.Index(calls, "stop --time"), strings.Index(calls, "cp worker:/worker/state.db"), strings.Index(calls, "rm -f worker"), strings.Index(calls, "volume rm worker-state")
	if !(stop < cp && cp < rm && rm < vrm) {
		t.Fatalf("expected stop, export, rm, volume rm in order:\n%s", calls)
	}
	if !strings.Contains(calls, "volume inspect worker-state") {
		t.Fatalf("cleanup must be verified with an inspect:\n%s", calls)
	}
}

func TestStopRetainsVolumeWhenExportedDatabaseIsCorrupt(t *testing.T) {
	fake := newFakeDocker(t, true, true)
	fake.writeWorkerDatabase(t, false)
	w := fake.worker(t, StorageVolume)
	evidence, err := w.Stop(context.Background())
	if err == nil || !strings.Contains(err.Error(), "integrity_check") {
		t.Fatalf("expected integrity failure, got %v", err)
	}
	if evidence.Exported || !evidence.Retained || evidence.ExportError == "" {
		t.Fatalf("evidence %+v", evidence)
	}
	if !fake.volumeExists() {
		t.Fatal("volume must be retained as recovery evidence")
	}
	if strings.Contains(fake.calls(t), "volume rm") {
		t.Fatal("volume rm must not run after a failed export")
	}
	if !strings.Contains(fake.calls(t), "rm -f worker") {
		t.Fatal("container is still removed after its evidence was captured")
	}
}

func TestStopRefusesExportWhileContainerStillRuns(t *testing.T) {
	fake := newFakeDocker(t, true, true)
	fake.writeWorkerDatabase(t, true)
	w := fake.worker(t, StorageVolume, "FAKE_DOCKER_STUCK=1")
	evidence, err := w.Stop(context.Background())
	if err == nil || !strings.Contains(err.Error(), "still running") {
		t.Fatalf("expected stop failure, got %v", err)
	}
	if evidence.Stopped || evidence.Exported || !evidence.Retained {
		t.Fatalf("evidence %+v", evidence)
	}
	if strings.Contains(fake.calls(t), "cp worker:") {
		t.Fatal("a live database must never be copied")
	}
	if !fake.volumeExists() {
		t.Fatal("volume must be retained")
	}
}

func TestStopRetainsVolumeWhenRemovalFails(t *testing.T) {
	fake := newFakeDocker(t, false, true)
	fake.writeWorkerDatabase(t, true)
	w := fake.worker(t, StorageVolume, "FAKE_DOCKER_VOLUME_STUCK=1")
	evidence, err := w.Stop(context.Background())
	if err == nil || !strings.Contains(err.Error(), "still exists") {
		t.Fatalf("expected verified-cleanup failure, got %v", err)
	}
	if !evidence.Exported || !evidence.Retained || evidence.CleanupError == "" {
		t.Fatalf("evidence %+v", evidence)
	}
}

func TestStopUnderBindStorageNeverExports(t *testing.T) {
	fake := newFakeDocker(t, true, false)
	w := fake.worker(t, StorageBind)
	evidence, err := w.Stop(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if evidence.Kind != StorageBind || !evidence.Stopped || !evidence.Exported || evidence.Retained || evidence.Volume != "" {
		t.Fatalf("evidence %+v", evidence)
	}
	calls := fake.calls(t)
	if strings.Contains(calls, "cp ") || strings.Contains(calls, "volume") {
		t.Fatalf("bind storage must neither export nor touch volumes:\n%s", calls)
	}
	if !strings.Contains(calls, "stop --time") {
		t.Fatalf("bind storage still stops the container cleanly before removal:\n%s", calls)
	}
}

func TestParseStorage(t *testing.T) {
	for value, want := range map[string]Storage{"": StorageVolume, "volume": StorageVolume, "bind": StorageBind} {
		if got, err := ParseStorage(value); err != nil || got != want {
			t.Fatalf("%q: %v %v", value, got, err)
		}
	}
	if _, err := ParseStorage("tmpfs"); err == nil {
		t.Fatal("unknown storage must be rejected")
	}
}
