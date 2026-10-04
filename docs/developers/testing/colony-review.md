# Colony review

A nightly review run, not a gate: the governor plays a pinned-seed map for
an in-game season (a quadrum, 15 days) and the run publishes an hour-by-hour
report to GitHub Pages for a person (or a model) to skim for bugs.

- **Case** `review/colony-week` (`go/internal/nativeaccept/cases/review`;
  the name predates the season length): Crashlanded, three colonists, a 250
  map, storyteller on. The seed is `review.PinnedSeed` (`review-pinned-1`)
  every night, one run per seed, so nights compare; it picks the biome. To
  add a seed, add it to that constant's documentation and dispatch with the
  `seed` input; `RIMGOVERNOR_REVIEW_SEED` overrides it and
  `RIMGOVERNOR_REVIEW_DAYS` changes the length (default 15). The case's
  watch is 12 wall minutes per in-game day (180 min for a season) and its
  budget that plus 10; the workflow job allows 360. The case is off every
  tier.
- **Recorder** `test/colony_review` (`scripts/fixtures/ColonyReviewFixture.cs`):
  every in-game hour it renders the colony from above to
  `<case output>/review/colony-<tick>.jpg`, and once a day the whole map to
  `map-<tick>.jpg`. It records no colony facts: the run's timeline already
  samples the colony census and every `sustained.ColonyGoals` goal each
  in-game hour. It needs a graphics device: the case sets
  `Graphics`, which drops `-nographics` from the headless profile (the
  camera renders on demand, no window opens, so it runs on hosted runners
  unlike a `Rendered` case) and always launches a fresh game. With a
  device the headless mod keeps the map's terrain, wall and floor meshes
  (`HeadlessPatches` skips them only when the device is Null); that mesh
  work is why other cases keep `-nographics`: a one-day trial took 3m05s
  against 2m37s without it.
- **Report** `go run ./cmd/colonyreview report -in <case output> -out <run dir>`
  reads `result.json`'s timeline and the screenshots and writes
  `index.html` (summary, trends, flagged hours, daily map shots, hourly
  cards with each colonist's mood and food and every goal's state) and
  `run.json`; `site -runs <dir> -out <site>` indexes run dirs. Flags point
  at hours worth a look (a colonist lost, food runway under two days, a
  downed colonist, low mood, a goal in deficit twelve hours, a role's stockpile zone count falling, starting supplies still forbidden after a day); the
  storage section lists the final zone count per role from the census's
  `stockpiles` block (see [storage](../architecture/storage.md)). They gate
  nothing.
- **Workflow** `.github/workflows/colony-review.yml`: nightly and on
  demand (`seed`, `days` inputs; defaults the pinned seed and 15 days), on the remote-acceptance runner setup.
  The report is rendered from whatever was recorded, uploaded as the
  `colony-review-report` artifact (90 days) and deployed with the newest
  14 earlier reports to Pages. Pages must use "GitHub Actions" as its
  source (repository Settings → Pages).
