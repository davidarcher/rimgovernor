// Package dockeraccept ports the disposable-worker parts of the deleted
// scripts/container_checks.py and scripts/container_native_acceptance.py to
// Go, for acceptance binaries that must prove the Go controller behaves
// correctly when run as the containers/Dockerfile "worker" image under
// --network host (see that Dockerfile stage's comment for why host
// networking is required). It never reaches into a container's native GABS
// attachment directly -- go/internal/bridge.Client.ConnectGameWithTakeover
// would force a takeover of the in-container rimgovernor process's own
// attachment, which is exactly the stability this package is meant to prove.
// Everything here drives the container the same way an external caller
// would: its published HTTP API.
package dockeraccept

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// DockerBinary resolves the docker CLI, preferring PATH and falling back to
// the standard Windows Docker Desktop resource-bin location, mirroring the
// deleted scripts/container_checks.py's docker_environment() discovery.
func DockerBinary() (string, error) {
	if path, err := exec.LookPath("docker"); err == nil {
		return path, nil
	}
	if programData := os.Getenv("ProgramFiles"); programData != "" {
		candidate := filepath.Join(programData, "Docker", "Docker", "resources", "bin", "docker.exe")
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			return candidate, nil
		}
	}
	return "", fmt.Errorf("docker CLI not found on PATH or in the standard Docker Desktop install location")
}

// RequireLinuxContainers confirms the Docker daemon serves Linux containers;
// the worker image cannot run under Windows containers.
func RequireLinuxContainers(ctx context.Context, docker string) error {
	out, err := exec.CommandContext(ctx, docker, "info", "--format", "{{.OSType}}").Output()
	if err != nil {
		return fmt.Errorf("docker info: %w", err)
	}
	osType := strings.TrimSpace(string(out))
	if osType != "linux" {
		return fmt.Errorf("docker daemon reports OSType %q, need linux containers", osType)
	}
	return nil
}

// BuildImage runs `docker build --target target -t tag .` against source (the
// repository root containing containers/Dockerfile), writing combined
// build output to buildLog, and returns the built image's immutable ID.
func BuildImage(ctx context.Context, docker, source, target, tag, buildLog string) (string, error) {
	log, err := os.Create(buildLog)
	if err != nil {
		return "", fmt.Errorf("create build log: %w", err)
	}
	defer log.Close()
	cmd := exec.CommandContext(ctx, docker, "build", "-f", filepath.Join("containers", "Dockerfile"), "--target", target, "-t", tag, ".")
	cmd.Dir = source
	cmd.Stdout, cmd.Stderr = log, log
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("docker build --target %s: %w (see %s)", target, err, buildLog)
	}
	return InspectImage(ctx, docker, tag)
}

// InspectImage returns tag's immutable image ID.
func InspectImage(ctx context.Context, docker, tag string) (string, error) {
	var out bytes.Buffer
	cmd := exec.CommandContext(ctx, docker, "image", "inspect", "--format", "{{.Id}}", tag)
	cmd.Stdout = &out
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("docker image inspect %s: %w", tag, err)
	}
	return strings.TrimSpace(out.String()), nil
}

// RemoveContainer force-removes a container by name, ignoring "not found"
// (it may never have started, or may already be gone). Errors are returned
// so a caller's cleanup evidence can still record an unexpected failure.
func RemoveContainer(ctx context.Context, docker, name string) error {
	cmd := exec.CommandContext(ctx, docker, "rm", "-f", name)
	var out bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &out
	if err := cmd.Run(); err != nil && !strings.Contains(out.String(), "No such container") {
		return fmt.Errorf("docker rm -f %s: %w: %s", name, err, out.String())
	}
	return nil
}

// ContainerLogs returns the named container's combined stdout/stderr.
func ContainerLogs(ctx context.Context, docker, name string) (string, error) {
	var out bytes.Buffer
	cmd := exec.CommandContext(ctx, docker, "logs", name)
	cmd.Stdout, cmd.Stderr = &out, &out
	_ = cmd.Run() // A dead/removed container still returns partial logs; only I/O errors matter here.
	return out.String(), nil
}

// ContainerState reports whether name is currently running, for readiness
// polling and post-mortem evidence (mirrors the deleted
// container_native_acceptance.py's stopped_state()).
func ContainerState(ctx context.Context, docker, name string) (running bool, err error) {
	var out bytes.Buffer
	cmd := exec.CommandContext(ctx, docker, "inspect", "--format", "{{.State.Running}}", name)
	cmd.Stdout = &out
	if err := cmd.Run(); err != nil {
		return false, fmt.Errorf("docker inspect %s: %w", name, err)
	}
	return strings.TrimSpace(out.String()) == "true", nil
}
