// remotebundle packages, verifies and bootstraps encrypted acceptance dependencies.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"

	rb "github.com/davidarcher/RimGovernor/go/internal/remotebundle"
)

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if err := run(ctx, os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "remotebundle:", err)
		os.Exit(1)
	}
}

func run(ctx context.Context, args []string) error {
	if len(args) == 0 {
		return fmt.Errorf("usage: remotebundle pack|publish|verify|extract|bootstrap [flags]")
	}
	f := flag.NewFlagSet(args[0], flag.ContinueOnError)
	var tools rb.Tools
	f.StringVar(&tools.SevenZip, "7z", "7z", "pinned 7-Zip executable")
	f.StringVar(&tools.Age, "age", "age", "pinned age executable")
	manifest := f.String("manifest", "", "bundle manifest path")
	digest := f.String("sha256", "", "expected exact bundle manifest digest")
	if args[0] == "pack" {
		var o rb.PackOptions
		o.Tools = tools
		f.StringVar(&o.Repo, "repo", "", "tested source checkout")
		f.StringVar(&o.StartsDir, "starts", "", "optional generated starts with compatibility.json provenance")
		f.StringVar(&o.GameDir, "game", "", "source game directory (read only)")
		f.StringVar(&o.HarmonyDLL, "harmony", "", "0Harmony.dll")
		f.StringVar(&o.HarmonyMod, "harmony-mod", "", "complete Harmony runtime mod directory")
		f.StringVar(&o.BridgeDir, "bridge", "", "RimBridgeServer directory")
		f.StringVar(&o.GABS, "gabs", "", "gabs.exe")
		f.StringVar(&o.Output, "out", "", "new private output directory")
		f.StringVar(&o.GameVersion, "game-version", "", "exact Version.txt value")
		f.StringVar(&o.HarmonyVersion, "harmony-version", "", "exact Harmony version")
		f.StringVar(&o.BridgeVersion, "bridge-version", "", "exact bridge version")
		f.StringVar(&o.GABSVersion, "gabs-version", "", "exact GABS version")
		f.StringVar(&o.Origin.Repository, "origin", "", "public owner/repository")
		f.Int64Var(&o.Origin.ReleaseID, "release-id", 0, "existing public release ID")
		f.StringVar(&o.Recipient, "recipient", "", "age recipient public key")
		f.StringVar(&o.KeyID, "key-id", "", "nonsecret identity rotation label")
		f.Int64Var(&o.PartBytes, "part-bytes", 1000000000, "maximum encrypted bytes per part")
		if err := f.Parse(args[1:]); err != nil {
			return err
		}
		if f.NArg() != 0 {
			return fmt.Errorf("unexpected positional arguments")
		}
		o.Tools = tools
		out, err := filepath.Abs(o.Output)
		if err != nil {
			return err
		}
		o.Output = out
		m, err := rb.Pack(ctx, o)
		if err != nil {
			return err
		}
		var compressed int64
		for _, p := range m.Parts {
			compressed += p.Bytes
		}
		fmt.Printf("draft: %s\nunpacked: %d bytes; encrypted compressed: %d bytes; parts: %d\n", filepath.Join(o.Output, "bundle.draft.json"), m.UnpackedBytes, compressed, len(m.Parts))
		return nil
	}
	parts := f.String("parts", "", "directory containing encrypted parts")
	dest := f.String("out", "", "new private extraction/job directory")
	identity := f.String("identity", "", "age identity file supplied by trusted runner")
	repo := f.String("repo", "", "tested checkout")
	cache := f.String("cache", "", "encrypted-part cache parent directory")
	trust := f.String("trust", "", "trusted workflow authorization JSON (outside tested checkout)")
	role := f.String("role", "fixture", "fixture or production")
	localDraft := f.Bool("local-draft", false, "offline verify/extract only: permit unpublished asset IDs")
	if err := f.Parse(args[1:]); err != nil {
		return err
	}
	if f.NArg() != 0 {
		return fmt.Errorf("unexpected positional arguments")
	}
	if *localDraft && args[0] != "verify" && args[0] != "extract" {
		return fmt.Errorf("-local-draft is only valid for offline verify/extract")
	}
	if args[0] == "publish" {
		b, err := os.ReadFile(*manifest)
		if err != nil {
			return err
		}
		var m rb.Manifest
		if err := rb.Decode(b, &m); err != nil {
			return err
		}
		name, err := rb.Publish(ctx, filepath.Dir(*manifest), m)
		if err == nil {
			fmt.Println(name)
		}
		return err
	}
	if args[0] == "bootstrap" {
		if err := rb.CheckAuthorizationPath(*repo, *trust); err != nil {
			return err
		}
		b, err := os.ReadFile(*trust)
		if err != nil {
			return err
		}
		var t rb.Trust
		if err := rb.Decode(b, &t); err != nil {
			return err
		}
		r, err := rb.Bootstrap(ctx, rb.GitHub{}, rb.BootstrapOptions{Repo: *repo, Work: *dest, Cache: *cache, Manifest: *manifest, ManifestSHA256: *digest, Identity: *identity, Trust: t, Tools: tools, Role: *role, Log: os.Stdout})
		if err == nil {
			fmt.Printf("ready: %s\ncache hit: %t; bootstrap: %d ms\n", r.Root, r.CacheHit, r.TotalMS)
		}
		return err
	}
	reader := rb.ReadManifest
	if *localDraft {
		reader = rb.ReadLocalDraft
	}
	m, err := reader(*manifest, *digest)
	if err != nil {
		return err
	}
	inv, err := rb.ReadInventory(filepath.Dir(*manifest), m)
	if err != nil {
		return err
	}
	switch args[0] {
	case "verify":
		return rb.VerifyParts(*parts, m)
	case "extract":
		return rb.Extract(ctx, tools, *parts, *dest, *identity, m, inv)
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}
