# Colony review

A nightly review run, not a gate: the governor plays a pinned-seed map for
an in-game season (a quadrum, 15 days) and the run publishes an hour-by-hour
report to GitHub Pages for a person (or a model) to skim for bugs.

- **Case** `review/colony-week` (`go/internal/nativeaccept/cases/review`):
  Crashlanded, three colonists, a 250 map, storyteller on. The seed is
  `review.PinnedSeed` (`review-pinned-1`) every night, one run per seed, so
  nights compare; it picks the biome. To add a seed, add it to that
  constant's documentation and dispatch with the `seed` input.
  `RIMGOVERNOR_REVIEW_SEED` overrides the seed and `RIMGOVERNOR_REVIEW_DAYS`
  the length (default 15). Watch: 12 wall minutes per in-game day (180 min
  per season); budget: that plus 10; the workflow job allows 360. The case
  is off every tier.
- **Recorder** `test/colony_review` (`scripts/fixtures/ColonyReviewFixture.cs`):
  every in-game hour it renders the colony from above to
  `<case output>/review/colony-<tick>.jpg`, and once a day the whole map to
  `map-<tick>.jpg`. It records no colony facts: the run's timeline already
  samples the colony census and every `sustained.ColonyConcerns` concern each
  in-game hour. It needs a graphics device: the case sets `Graphics`, which
  drops `-nographics` from the headless profile (the camera renders on
  demand and no window opens, so it runs on hosted runners, unlike a
  `Rendered` case) and always launches a fresh game. With a device the
  headless mod keeps terrain, wall and floor meshes (`HeadlessPatches`
  skips them only when the device is Null); that is why other cases keep
  `-nographics` (a one-day trial took 3m05s against 2m37s).
- **Report** `go run ./cmd/colonyreview report -in <case output> -out <run dir>`
  reads `result.json`'s timeline and the screenshots and writes `index.html`
  and `run.json`; `site -runs <dir> -out <site>` indexes run dirs. The page is
  a player: one image with a slider over the recorded hours (play/pause,
  speed, step an hour or a day, a Colony / Whole map toggle, day ticks and
  red/amber marks on flagged hours), the chosen hour's census, pawns, flags and
  concerns in deficit underneath, and a sticky column of trend charts with a
  cursor at the chosen hour (click or drag a chart to seek). Space, arrows,
  shift+arrows and [ ] drive it; `#t<tick>` deep-links an hour. The frames are
  embedded as JSON (`player.js` reads them), so a run page needs no server.
  A strip per colonist (a cell per hour, colored by that pawn's mood, downed hours marked)
  sits under the charts and seeks too.
  The game's rich-text tags in pawn labels (`<color=...>`) are stripped. A run whose case
  left no `result.json` (bootstrap or launch failed) still gets a page saying so and a
  row in the site index, and the workflow job then fails.
  Flags mark hours worth a
  look: a colonist lost, food runway under two days, a downed colonist, low
  mood, a concern in deficit twelve hours, starting supplies still forbidden after a day. The storage
  section lists the final zone count per role from the census's
  `stockpiles` block, counted from the standing owned zones each round (see [storage](../architecture/storage.md)). Flags gate
  nothing.
- **Score** `run.json` carries `score`: `components` computed from the
  timeline plus a weighted `scalar` (0-100) for ranking. Signal only. Each
  component has a `value` in its unit, a `score` (value mapped to 0-1, 1
  best, clamped) and a `weight`. A component the timeline cannot supply has
  a null `value` and `score` and is left out of the scalar (the weighted
  mean of the known scores, never a zero for a missing one); no known
  component gives a null scalar. Components (`go/cmd/colonyreview/score.go`):

  | component | value | score 1 at | weight |
  | --- | --- | --- | --- |
  | `wealth_growth` | last wealth / first - 1 | +100% | 3 |
  | `mean_mood` | mean of the hourly colony mood mean | 1.0 | 2 |
  | `min_food_runway` | lowest food runway, days | 10 days | 3 |
  | `deaths` | colonists lost between readings | none lost (0 at all lost) | 4 |
  | `downed_time` | fraction of readings with a colonist downed | never | 1 |
  | `stage_days:<tier>` | days to the first reading of each build tier reached | day 0 (0 at 15 days) | 3 shared evenly by the tiers reached |
  | `raid_damage` | unknown: the timeline records none yet | | 2 |
- **Previous-night comparison** `report -baselines <dir of earlier run dirs>`
  adds `delta` to `run.json` and a section to the run page: this run's
  score minus the baseline's, per component (`delta` in score units, 0-1,
  plus `value_delta` in the component's unit) and for the scalar (points).
  The baseline is the newest earlier run dir with the same `seed` meta that
  has a score: the previous nights' `colony-review-report` artifacts, the
  store the Pages site is built from (the render step downloads the newest 8
  successful runs). One run per seed is noisy: a signal, never a gate. With
  no such run `delta.status` is `no_baseline`; a component either side
  could not read (null score, or absent from one side) is `unknown` with
  null numbers, never zero.
- **Workflow** `.github/workflows/colony-review.yml`: nightly and on demand
  (`seed`, `days` inputs; defaults the pinned seed and 15 days), on the
  remote-acceptance runner setup. The report is rendered from whatever was
  recorded, uploaded as the `colony-review-report` artifact (90 days) and
  deployed with the newest 14 earlier reports to Pages. Pages must use
  "GitHub Actions" as its source (repository Settings → Pages).
