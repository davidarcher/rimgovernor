// Package remoteaccept validates the remote acceptance v1 evidence graph.
// Native report fields remain raw JSON at this boundary so diagnostics and
// checkpoint provenance survive import without a lossy projection.
package remoteaccept

import "encoding/json"

type Ref struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
}

type Run struct {
	Version    int    `json:"schema_version"`
	ID         string `json:"run_id"`
	Repository string `json:"repository"`
	Trigger    struct {
		Event        string `json:"event"`
		Actor        string `json:"actor"`
		PublishedRef string `json:"published_ref"`
		RunID        int64  `json:"actions_run_id"`
		Attempt      int    `json:"actions_run_attempt"`
	} `json:"trigger"`
	WorkflowCommit string `json:"workflow_commit"`
	TestedCommit   string `json:"tested_commit"`
	BaseCommit     string `json:"base_commit"`
	Tier           string `json:"tier"`
	Bundle         Ref    `json:"bundle"`
	Limits         struct {
		Runner       string `json:"runner_label"`
		Shards       int    `json:"shards"`
		Parallel     int    `json:"max_parallel"`
		Workers      int    `json:"workers_per_shard"`
		JobMinutes   int    `json:"job_timeout_minutes"`
		SuiteMinutes int    `json:"suite_timeout_minutes"`
		Attempts     int    `json:"max_attempts"`
		Retention    int    `json:"artifact_retention_days"`
		Bytes        int64  `json:"artifact_max_bytes"`
		Paid         bool   `json:"paid_usage_authorized"`
	} `json:"limits"`
}

type SkippedCase struct {
	Name   string `json:"name"`
	Reason string `json:"reason"`
}

type Selection struct {
	Skipped   []SkippedCase  `json:"skipped,omitempty"`
	Version   int            `json:"schema_version"`
	Run       Ref            `json:"run"`
	Planner   string         `json:"planner_commit"`
	DiffMode  string         `json:"diff_mode"`
	Changed   []string       `json:"changed_files"`
	Cases     []SelectedCase `json:"cases"`
	Sampled   []string       `json:"sampled_areas"`
	Algorithm string         `json:"algorithm"`
	Shards    []PlannedShard `json:"shards"`
}
type SelectedCase struct {
	Name    string   `json:"name"`
	Reasons []string `json:"reasons"`
}
type PlannedShard struct {
	ID    string   `json:"id"`
	Cases []string `json:"cases"`
}
type Attempts struct {
	Version   int    `json:"schema_version"`
	Run       Ref    `json:"run"`
	Selection Ref    `json:"selection"`
	ShardID   string `json:"shard_id"`
	Runner    struct {
		OS        string `json:"os"`
		Image     string `json:"image_version"`
		Arch      string `json:"arch"`
		CPUs      int    `json:"cpu_count"`
		Memory    int64  `json:"memory_bytes"`
		Disk      int64  `json:"free_disk_bytes"`
		Bootstrap Ref    `json:"bootstrap"`
	} `json:"runner"`
	Attempts []Attempt `json:"attempts"`
}
type Attempt struct {
	Case           string          `json:"case"`
	Number         int             `json:"number"`
	Status         string          `json:"status"`
	Classification string          `json:"classification"`
	RetryOf        *int            `json:"retry_of"`
	Started        string          `json:"started_at"`
	Finished       string          `json:"finished_at"`
	Exit           *int            `json:"exit"`
	Error          *string         `json:"error"`
	Evidence       Ref             `json:"evidence"`
	Log            json.RawMessage `json:"log,omitempty"`
}
type Shard struct {
	ID       string `json:"id"`
	Status   string `json:"status"`
	Attempts *Ref   `json:"attempts"`
}
type Case struct {
	Name         string `json:"name"`
	ShardID      string `json:"shard_id"`
	AttemptCount int    `json:"attempt_count"`
	FinalAttempt *int   `json:"final_attempt"`
	Status       string `json:"status"`
}
type Aggregate struct {
	Skipped   []SkippedCase `json:"skipped,omitempty"`
	Version   int           `json:"schema_version"`
	Run       Ref           `json:"run"`
	Selection Ref           `json:"selection"`
	Shards    []Shard       `json:"shards"`
	Status    string        `json:"status"`
	Passed    bool          `json:"passed"`
	Cases     []Case        `json:"cases"`
	Error     *string       `json:"error"`
}
type Remote struct {
	Version      int    `json:"schema_version"`
	Aggregate    Ref    `json:"aggregate"`
	TestedCommit string `json:"tested_commit"`
	BaseCommit   string `json:"base_commit"`
	BundleSHA256 string `json:"bundle_sha256"`
	// Import records the authenticated Actions artifact, not a user-set pass bit.
	Import *Provenance `json:"import,omitempty"`
}
type Report struct {
	Tier   string            `json:"tier"`
	Passed bool              `json:"passed"`
	Error  *string           `json:"error"`
	Cases  []json.RawMessage `json:"cases"`
	Remote Remote            `json:"remote"`
}
