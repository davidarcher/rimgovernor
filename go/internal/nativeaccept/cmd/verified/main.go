// Command verified stamps and checks "Verified:" commit trailers, the
// record that an acceptance harness ran against a given set of inputs
// (na.HarnessInputs). It answers "do I need to rerun this harness?" with a
// hash comparison instead of a judgment call:
//
//	go run ./internal/nativeaccept/cmd/verified trailer <harness>...
//	    print one trailer line per harness for the current working tree;
//	    paste them at the end of the commit message of the verified change
//	go run ./internal/nativeaccept/cmd/verified check [-range main..HEAD] [<harness>...]
//	    read the trailers in that commit range and compare each harness's
//	    latest one with the current working tree; exit 1 when any named
//	    (or, with none named, any recorded) harness is stale or unrecorded
//	go run ./internal/nativeaccept/cmd/verified inputs <harness>
//	    list the files the hash covers
package main

import (
	"flag"
	"fmt"
	"os"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	if len(os.Args) < 2 {
		usage()
	}
	cwd, err := os.Getwd()
	if err != nil {
		fail(err)
	}
	repo, ok := na.FindRepo(cwd)
	if !ok {
		fail(fmt.Errorf("not inside a git checkout: %s", cwd))
	}
	switch os.Args[1] {
	case "trailer":
		harnesses := os.Args[2:]
		if len(harnesses) == 0 {
			usage()
		}
		for _, harness := range harnesses {
			trailer, err := na.NewVerifiedTrailer(repo, harness)
			if err != nil {
				fail(err)
			}
			fmt.Println(trailer)
		}
	case "inputs":
		if len(os.Args) != 3 {
			usage()
		}
		files, err := na.HarnessInputs(repo, os.Args[2])
		if err != nil {
			fail(err)
		}
		for _, file := range files {
			fmt.Println(file)
		}
	case "check":
		flags := flag.NewFlagSet("check", flag.ExitOnError)
		rev := flags.String("range", "main..HEAD", "git revision range whose commit messages hold the trailers")
		_ = flags.Parse(os.Args[2:])
		os.Exit(check(repo, *rev, flags.Args()))
	default:
		usage()
	}
}

// check prints one line per harness and returns the exit status.
func check(repo, rev string, wanted []string) int {
	recorded, err := na.RecordedVerifiedTrailers(repo, rev)
	if err != nil {
		fail(err)
	}
	harnesses := wanted
	if len(harnesses) == 0 {
		for harness := range recorded {
			harnesses = append(harnesses, harness)
		}
	}
	if len(harnesses) == 0 {
		fmt.Printf("no %s trailers in %s\n", na.VerifiedTrailerKey, rev)
		return 1
	}
	status := 0
	for _, harness := range harnesses {
		trailer, ok := recorded[harness]
		if !ok {
			fmt.Printf("%s: unrecorded in %s\n", harness, rev)
			status = 1
			continue
		}
		current, err := na.HarnessInputHash(repo, harness)
		if err != nil {
			fail(err)
		}
		if current == trailer.Inputs {
			fmt.Printf("%s: ok inputs=%s\n", harness, current)
			continue
		}
		fmt.Printf("%s: stale recorded=%s current=%s\n", harness, trailer.Inputs, current)
		status = 1
	}
	return status
}

func usage() {
	fmt.Fprintln(os.Stderr, "usage: verified trailer <harness>... | check [-range main..HEAD] [<harness>...] | inputs <harness>")
	os.Exit(2)
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}
