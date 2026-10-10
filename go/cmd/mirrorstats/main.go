// Command mirrorstats prints the def-mirror measurement baseline of
// docs/developers/architecture/mirror-measurements.md:
//
//	go run ./cmd/mirrorstats [-recording <path>] [-contracts <dir>] [-runs <n>]
//
// It needs no game. It reports the sizes of the recorded catalog (raw and
// gzip, with and without GameConstants), of contracts/proto/defs.proto, of the
// generated defs.pb.go and Defs.cs (the latter is gitignored: "absent" until
// generatecsharp has run), the message, field and enum counts of the defs
// package and the median Go decode time of the recording. Native fill time and
// native build time need the game and are recorded by hand in the same
// document.
package main

import (
	"bytes"
	"compress/gzip"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func main() {
	recording := flag.String("recording", filepath.FromSlash("internal/observation/testdata/full_catalog.pb.gz"), "recorded definition catalog")
	contracts := flag.String("contracts", filepath.FromSlash("../contracts"), "contracts directory")
	runs := flag.Int("runs", 9, "decode repetitions; the median is reported")
	flag.Parse()
	if err := run(os.Stdout, *recording, *contracts, *runs); err != nil {
		fmt.Fprintln(os.Stderr, "mirrorstats:", err)
		os.Exit(1)
	}
}

func run(out io.Writer, recording, contracts string, runs int) error {
	gz, err := os.ReadFile(recording)
	if err != nil {
		return err
	}
	zr, err := gzip.NewReader(bytes.NewReader(gz))
	if err != nil {
		return err
	}
	raw, err := io.ReadAll(zr)
	if err != nil {
		return err
	}
	catalog := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(raw, catalog); err != nil {
		return err
	}
	constants, err := proto.Marshal(catalog.GetGameConstants())
	if err != nil {
		return err
	}
	// The recording as it was before the constants were added to it.
	catalog.GameConstants = nil
	without, err := proto.Marshal(catalog)
	if err != nil {
		return err
	}
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write(without); err != nil {
		return err
	}
	if err := zw.Close(); err != nil {
		return err
	}
	fmt.Fprintf(out, "recording raw bytes\t%d\n", len(raw))
	fmt.Fprintf(out, "recording gzip bytes\t%d\n", len(gz))
	fmt.Fprintf(out, "game_constants wire bytes\t%d\n", len(constants))
	fmt.Fprintf(out, "recording without game_constants raw bytes\t%d\n", len(without))
	fmt.Fprintf(out, "recording without game_constants gzip bytes\t%d\n", buf.Len())

	for _, f := range []struct{ label, path string }{
		{"defs.proto bytes", filepath.Join(contracts, "proto", "defs.proto")},
		{"defs.pb.go bytes", filepath.Join(contracts, "generated", "protobuf", "go", "defspb", "defs.pb.go")},
		{"Defs.cs bytes", filepath.Join(contracts, "generated", "protobuf", "csharp", "Defs.cs")},
	} {
		if info, err := os.Stat(f.path); err == nil {
			fmt.Fprintf(out, "%s\t%d\n", f.label, info.Size())
		} else {
			fmt.Fprintf(out, "%s\tabsent\n", f.label)
		}
	}

	file := catalog.GetDefs().ProtoReflect().Descriptor().ParentFile()
	var messages, fields, constantMessages, constantFields int
	walk(file.Messages(), func(m protoreflect.MessageDescriptor) {
		messages++
		fields += m.Fields().Len()
		if strings.HasSuffix(string(m.Name()), "Constants") {
			constantMessages++
			constantFields += m.Fields().Len()
		}
	})
	enums := file.Enums().Len()
	walk(file.Messages(), func(m protoreflect.MessageDescriptor) { enums += m.Enums().Len() })
	fmt.Fprintf(out, "defs messages\t%d\n", messages)
	fmt.Fprintf(out, "defs fields\t%d\n", fields)
	fmt.Fprintf(out, "defs enums\t%d\n", enums)
	fmt.Fprintf(out, "of which Constants messages (incl. root GameConstants)\t%d\n", constantMessages)
	fmt.Fprintf(out, "of which Constants fields\t%d\n", constantFields)

	for _, d := range []struct {
		label string
		data  []byte
	}{{"whole catalog", raw}, {"without game_constants", without}} {
		median, err := decodeMedian(d.data, runs)
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "go decode, %s (median of %d)\t%s\n", d.label, runs, median)
	}
	return nil
}

func walk(ms protoreflect.MessageDescriptors, visit func(protoreflect.MessageDescriptor)) {
	for i := 0; i < ms.Len(); i++ {
		visit(ms.Get(i))
		walk(ms.Get(i).Messages(), visit)
	}
}

func decodeMedian(data []byte, runs int) (time.Duration, error) {
	times := make([]time.Duration, 0, runs)
	for i := 0; i < runs; i++ {
		start := time.Now()
		if err := proto.Unmarshal(data, &o.DefinitionCatalog{}); err != nil {
			return 0, err
		}
		times = append(times, time.Since(start))
	}
	sort.Slice(times, func(a, b int) bool { return times[a] < times[b] })
	return times[len(times)/2].Round(time.Millisecond), nil
}
