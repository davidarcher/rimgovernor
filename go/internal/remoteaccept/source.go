package remoteaccept

import (
	"fmt"
	"os/exec"
	"strings"
)

func git(repo string, args ...string) (string, error) {
	cmd := exec.Command("git", args...)
	cmd.Dir = repo
	b, err := cmd.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("git %s: %w: %s", strings.Join(args, " "), err, b)
	}
	return strings.TrimSpace(string(b)), nil
}

func LocalTrust(repo string) (Trust, error) {
	t := Trust{}
	for key, dst := range map[string]*string{"repository": &t.Repository, "workflow": &t.Workflow, "workflowCommit": &t.WorkflowCommit, "bundleSHA256": &t.BundleSHA256} {
		v, err := git(repo, "config", "--local", "--get", "rimgovernor.acceptance"+key)
		if err != nil {
			return t, fmt.Errorf("configure local rimgovernor.acceptance%s trust pin: %w", key, err)
		}
		*dst = v
	}
	return t, nil
}
