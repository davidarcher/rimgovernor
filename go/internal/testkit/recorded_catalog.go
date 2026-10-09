package testkit

import (
	"compress/gzip"
	"io"
	"os"
	"path/filepath"
	"sync"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// LoadRecordedCatalogWire reads the whole game catalog recorded with every
// expansion (observation/testdata/full_catalog.pb.gz) once per test binary.
// The message is shared: callers must not mutate it.
var LoadRecordedCatalogWire = sync.OnceValues(func() (*o.DefinitionCatalog, error) {
	path, err := recordedCatalogPath()
	if err != nil {
		return nil, err
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	zr, err := gzip.NewReader(file)
	if err != nil {
		return nil, err
	}
	data, err := io.ReadAll(zr)
	if err != nil {
		return nil, err
	}
	wire := &o.DefinitionCatalog{}
	if err = proto.Unmarshal(data, wire); err != nil {
		return nil, err
	}
	return wire, nil
})

// recordedCatalogPath walks up from the test's working directory (its package
// directory) to internal/observation/testdata.
func recordedCatalogPath() (string, error) {
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}
	for {
		path := filepath.Join(dir, "observation", "testdata", "full_catalog.pb.gz")
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", os.ErrNotExist
		}
		dir = parent
	}
}

// RecordedCatalogWire is the recorded catalog message. Packages at or below
// bridge decode it themselves; the rest use testkit/recordedcatalog.
func RecordedCatalogWire(t testing.TB) *o.DefinitionCatalog {
	t.Helper()
	wire, err := LoadRecordedCatalogWire()
	if err != nil {
		t.Fatal(err)
	}
	return wire
}
