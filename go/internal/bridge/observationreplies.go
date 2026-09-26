package bridge

// NativeAccount aggregates the observation and frame accounts (#642) carried
// by raw native reply envelopes, for a caller that has no flight recording to
// summarize: the speed matrix's governor-off row plays the save with no
// controller attached, so its only samples are the harness's own replies.
//
// The reductions are the ones SummarizePhases uses, so a row built here is
// comparable to a governed row's field for field.
type NativeAccount struct {
	observation observationAccumulator
	frames      frameAccumulator
}

// Reply reads one native reply envelope's timing block and reports whether it
// carried anything. An envelope from a companion that predates the account
// contributes nothing, which leaves the result unknown rather than zero.
func (a *NativeAccount) Reply(envelope map[string]any) bool {
	timing, ok := envelope["timing"].(map[string]any)
	if !ok {
		return false
	}
	// The companion's own key names; the service renames them when it copies
	// the blocks into a flight row, which is what the readers expect.
	row := map[string]any{"native_observation": timing["observation"], "native_frames": timing["frames"]}
	found := false
	if queue, ok := number(timing["queueMs"]); ok {
		execute, _ := number(timing["executeMs"])
		a.observation.timed(queue, execute)
		found = true
	}
	if account, ok := readObservation(row); ok {
		a.observation.hop(account)
		found = true
	}
	if account, ok := readFrames(row); ok {
		a.frames.sample(account)
		found = true
	}
	return found
}

// Observation is the aggregate over the replies read so far.
func (a *NativeAccount) Observation() ObservationSample { return a.observation.result() }

// Frames is the frame recorder's account differenced between the first and
// last reply that carried it.
func (a *NativeAccount) Frames() FrameSample { return a.frames.result() }
