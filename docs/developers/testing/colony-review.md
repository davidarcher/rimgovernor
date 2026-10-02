# Colony review

A nightly review run, not a gate: the governor plays a fresh random map for
an in-game week and the run publishes an hour-by-hour report to GitHub
Pages for a person (or a model) to skim for bugs.

- **Case** `review/colony-week` (`go/internal/nativeaccept/cases/review`):
  Crashlanded, three colonists, a 250 map, storyteller on. The seed is
  `review-<UTC date>` (`RIMGOVERNOR_REVIEW_SEED` pins it) and picks the
  biome; `RIMGOVERNOR_REVIEW_DAYS` changes the length (default 7). The case
  is off every tier.
- **Recorder** `test/colony_review` (`scripts/fixtures/ColonyReviewFixture.cs`):
  every in-game hour it renders the colony from above to
  `colony-<tick>.jpg`, once a day the whole map to `map-<tick>.jpg`, and
  appends colony facts (colonists' mood, health, job; food, wealth,
  buildings, blueprints, hostiles, fires, archived letters and messages) to
  `stats.jsonl` in `<case output>/review`. It needs a graphics device:
  `RIMGOVERNOR_ACCEPT_GRAPHICS=1` drops `-nographics` from the headless
  profile, so the camera renders on demand without a window.
- **Report** `go run ./cmd/colonyreview report -in <review dir> -out <run dir>`
  writes `index.html` (summary, trends, flagged hours, daily map shots,
  hourly cards) and `run.json`; `site -runs <dir> -out <site>` indexes run
  dirs. Flags point at hours worth a look (deaths, food under two days,
  mental breaks, low mood or health, a colonist idle six hours, blueprints
  with no new building for twelve); they gate nothing.
- **Workflow** `.github/workflows/colony-review.yml`: nightly and on
  demand (`seed`, `days` inputs), on the remote-acceptance runner setup.
  The report is rendered from whatever was recorded, uploaded as the
  `colony-review-report` artifact (90 days) and deployed with the newest
  14 earlier reports to Pages. Pages must use "GitHub Actions" as its
  source (repository Settings → Pages).
