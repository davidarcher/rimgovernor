package remoteaccept

import (
	"fmt"
	"os/exec"
	"slices"
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

// VerifySource compares trees, not commit IDs: squash/rebase/cherry-pick with
// identical inputs remains valid. It also recognizes the clean merge of the
// tested tree with current main, but never arbitrary extra task changes.
func VerifySource(repo string, r Run) error {
	if !oid.MatchString(r.TestedCommit) || !oid.MatchString(r.BaseCommit) {
		return fmt.Errorf("invalid source identity")
	}
	if _, err := git(repo, "merge-base", "--is-ancestor", r.BaseCommit, r.TestedCommit); err != nil {
		return fmt.Errorf("remote base is not an available tested ancestor: %w", err)
	}
	dirty, err := git(repo, "status", "--porcelain", "--untracked-files=no")
	if err != nil {
		return err
	}
	if dirty != "" {
		return fmt.Errorf("tracked changes are not covered by remote evidence")
	}
	head, err := git(repo, "rev-parse", "HEAD^{tree}")
	if err != nil {
		return err
	}
	tested, err := git(repo, "rev-parse", r.TestedCommit+"^{tree}")
	if err != nil {
		return err
	}
	if head == tested {
		return nil
	}
	merged, err := git(repo, "merge-tree", "--write-tree", r.TestedCommit, "main")
	if err == nil && merged == head {
		return nil
	}
	return fmt.Errorf("remote evidence proves %s, not this task's source tree", r.TestedCommit)
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

func VerifySelectionSource(repo string, r Run, s Selection) error {
	files, err := git(repo, "diff", "--name-only", "--no-renames", "-z", r.BaseCommit, r.TestedCommit, "--")
	if err != nil {
		return err
	}
	var changed []string
	if files != "" {
		changed = strings.Split(strings.TrimSuffix(files, "\x00"), "\x00")
	}
	slices.Sort(changed)
	if !slices.Equal(changed, s.Changed) {
		return fmt.Errorf("selection changed_files do not match the tested base/source diff")
	}
	return nil
}
