package speedmatrix

// recordedCeilingTPS is the governor-off wall TPS recorded per matrix stage
// save (profile.save), the ceiling CeilingRatios falls back to when a run
// has no governor-off row of its own (#737). A run's own row wins: it was
// measured on the same box under the same load. Add an entry from a
// result.json's governor-off speed_metrics row and cite the run in the
// commit. Empty until such a run is recorded.
var recordedCeilingTPS = map[string]float64{}
