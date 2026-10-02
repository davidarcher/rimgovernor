// Command colonyreview turns a review/colony-week recording (the
// test/colony_review recorder's stats.jsonl and JPEGs) into a static HTML
// report a player can skim, and indexes reports into a site.
//
//	colonyreview report -in <review dir> -out <run dir> [-meta key=value ...]
//	colonyreview site -runs <dir of run dirs> -out <site dir>
//
// report copies the images, writes index.html and run.json (meta plus the
// summary); site writes index.html at -out listing every run dir under
// -runs that holds a run.json, newest first, and copies them beside it.
package main

import (
	"flag"
	"fmt"
	"os"
	"strings"
)

func main() {
	if len(os.Args) < 2 {
		usage()
	}
	var err error
	switch os.Args[1] {
	case "report":
		fs := flag.NewFlagSet("report", flag.ExitOnError)
		in := fs.String("in", "", "recorder directory (stats.jsonl and images)")
		out := fs.String("out", "", "run report directory to write")
		meta := metaFlag{}
		fs.Var(&meta, "meta", "key=value shown in the report header and saved in run.json (repeatable)")
		fs.Parse(os.Args[2:])
		if *in == "" || *out == "" {
			usage()
		}
		err = Report(*in, *out, meta)
	case "site":
		fs := flag.NewFlagSet("site", flag.ExitOnError)
		runs := fs.String("runs", "", "directory holding run report directories")
		out := fs.String("out", "", "site directory to write")
		fs.Parse(os.Args[2:])
		if *runs == "" || *out == "" {
			usage()
		}
		err = Site(*runs, *out)
	default:
		usage()
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "colonyreview:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, "usage: colonyreview report -in <dir> -out <dir> [-meta k=v ...] | site -runs <dir> -out <dir>")
	os.Exit(2)
}

type metaFlag map[string]string

func (m metaFlag) String() string { return fmt.Sprint(map[string]string(m)) }
func (m metaFlag) Set(v string) error {
	k, val, ok := strings.Cut(v, "=")
	if !ok || k == "" {
		return fmt.Errorf("want key=value, got %q", v)
	}
	m[k] = val
	return nil
}
