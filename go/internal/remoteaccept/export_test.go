package remoteaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func exportFixture(t *testing.T, f *fixture, index int) ExportJob {
	t.Helper()
	output := t.TempDir()
	rows := []map[string]any{}
	for _, a := range f.attempts[index].Attempts {
		var native map[string]json.RawMessage
		readTest(t, f.root, a.Evidence.Path, &native)
		a.Evidence = writeTest(t, output, a.Case+"/result.json", native)
		log := a.Case + ".log"
		if err := os.WriteFile(filepath.Join(output, log), []byte("test-token diagnostic at C:\\runner\\private\\game.exe\n"), 0600); err != nil {
			t.Fatal(err)
		}
		ref, err := FileRef(output, log)
		if err != nil {
			t.Fatal(err)
		}
		a.Log, _ = json.Marshal(ref)
		rows = append(rows, map[string]any{"name": a.Case, "attempts": []Attempt{a}, "output": filepath.Join(output, a.Case), "log": filepath.Join(output, log)})
	}
	writeTest(t, output, "result.json", map[string]any{"cases": rows})
	boot := filepath.Join(t.TempDir(), "bootstrap.json")
	b := []byte(`{"passed":true,"os":"windows","image_version":"fixture","cpu_count":4,"memory_bytes":16000000000,"free_disk_bytes":8000000000}`)
	if err := os.WriteFile(boot, b, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(output, "workers", "1"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(output, "workers", "1", "private.json"), []byte(`{"secret":"AGE-SECRET-KEY-private"}`), 0600); err != nil {
		t.Fatal(err)
	}
	return ExportJob{Role: "fixture", Output: output, Bootstrap: boot}
}

func TestExportPortableEvidenceAndRedVerdict(t *testing.T) {
	for _, fail := range []bool{false, true} {
		t.Run(map[bool]string{false: "green", true: "red"}[fail], func(t *testing.T) {
			f := fixtureRun(t)
			if fail {
				f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["passed"] = raw("false"); m["exit"] = raw("1") })
				f.attempts[0].Attempts[0].Status = "failed"
				f.attempts[0].Attempts[0].Classification = "assertion"
				f.attempts[0].Attempts[0].Exit = ptr(1)
				f.attempts[0].Attempts[0].Error = ptr("injected assertion failure")
			}
			r, s := f.save(t)
			for i, sh := range f.shards {
				job := exportFixture(t, f, i)
				if err := ExportShard(f.root, sh.ID, []ExportJob{job}, []string{"test-token"}); err != nil {
					t.Fatal(err)
				}
				ref, err := FileRef(f.root, sh.ID+"/attempts.json")
				if err != nil {
					t.Fatal(err)
				}
				f.shards[i].Attempts = &ref
				b, err := os.ReadFile(filepath.Join(f.root, sh.ID, "fixture", f.attempts[i].Attempts[0].Case+".log"))
				if err != nil {
					t.Fatal(err)
				}
				if strings.Contains(string(b), "test-token") || strings.Contains(string(b), "private") {
					t.Fatalf("unsanitized log %s", b)
				}
				if _, err = os.Stat(filepath.Join(f.root, sh.ID, "fixture", "workers")); !os.IsNotExist(err) {
					t.Fatal("worker tree exported")
				}
			}
			e, err := Evaluate(f.root, r, s, f.shards)
			if err != nil {
				t.Fatal(err)
			}
			if e.Report.Passed == fail {
				t.Fatalf("incorrect verdict %+v", e.Aggregate)
			}
		})
	}
}

func TestExportRejectsUnsafeOrIncompleteEvidence(t *testing.T) {
	for _, kind := range []string{"digest", "missing", "restricted", "budget", "extra"} {
		t.Run(kind, func(t *testing.T) {
			f := fixtureRun(t)
			job := exportFixture(t, f, 0)
			switch kind {
			case "digest":
				if err := os.WriteFile(filepath.Join(job.Output, f.attempts[0].Attempts[0].Case+"/result.json"), []byte(`{"passed":true}`), 0600); err != nil {
					t.Fatal(err)
				}
			case "missing":
				if err := os.Remove(filepath.Join(job.Output, f.attempts[0].Attempts[0].Case+".log")); err != nil {
					t.Fatal(err)
				}
			case "restricted":
				if err := os.WriteFile(filepath.Join(job.Output, "private.txt"), []byte("AGE-SECRET-KEY-abc"), 0600); err != nil {
					t.Fatal(err)
				}
			case "budget":
				f.run.Limits.Bytes = 64 << 20
				f.save(t)
			case "extra":
				var suite map[string]json.RawMessage
				readTest(t, job.Output, "result.json", &suite)
				suite["cases"] = raw(`[]`)
				writeTest(t, job.Output, "result.json", suite)
			}
			if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err == nil {
				t.Fatal("unsafe export succeeded")
			}
		})
	}
}

func TestScheduledFullEvidence(t *testing.T) {
	f := fixtureRun(t)
	f.run.Tier = "full"
	f.run.Trigger.Event = "schedule"
	f.run.Trigger.PublishedRef = "refs/heads/main"
	for i := range f.selection.Cases {
		f.selection.Cases[i].Reasons = []string{"full"}
	}
	e, err := f.evaluate(t)
	if err != nil || !e.Report.Passed {
		t.Fatalf("%+v %v", e.Aggregate, err)
	}
}
