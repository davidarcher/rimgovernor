// The service's stderr under a case (`service*/stderr.log`) carries no time
// or tick stamps, but the clock scheduler writes one `step reads:` line per
// step from the same deferred block that publishes the step's `clock_step`
// flight row, so the log splits into step blocks that align with the rows
// by index. A block is everything after the previous block up to its
// `step reads:` line, plus the `step done:` / `step failed:` lines the
// caller prints right after. The block holds what the row does not: the
// planners' admission results, the window the step sized, the refusals of
// EvaluateClockWindow, a deferred admission and the pause-bound hold.
export type PlannerResult = {planner: string; reason: string; plan: string};
export type StderrStep = {
  index: number;
  lines: string[];
  tick: number | null;
  running: boolean | null;
  stopped: boolean | null;
  stopReason: string | null;
  reason: string | null;
  planners: PlannerResult[];
  window: {ticks: number; targetSeconds: number; ticksPerSecond: number} | null;
  admitted: boolean | null;
  refused: string[];
  mode: string | null;
  deferred: boolean;
  heldMs: number | null;
  gateWaitMs: number | null;
  elapsedMs: number | null;
  error: string | null;
};
export type StderrLog = {steps: StderrStep[]; trailing: string[]; other: string[]};

const closers = [/\] step done:/, /\[clock-worker\] step failed:/];

export function parseStderr(text: string): StderrLog {
  const steps: StderrStep[] = [];
  const other: string[] = [];
  let block: string[] = [];
  let closing = false;
  const flush = () => {steps.push(readStep(steps.length, block)); block = []; closing = false;};
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line === '') continue;
    if (closing) {
      if (closers.some(re => re.test(line))) {block.push(line); continue;}
      flush();
    }
    block.push(line);
    if (!line.startsWith('[')) other.push(line);
    if (/\] step reads:/.test(line)) closing = true;
  }
  if (closing) flush();
  return {steps, trailing: block, other};
}

function readStep(index: number, lines: string[]): StderrStep {
  const step: StderrStep = {index, lines, tick: null, running: null, stopped: null, stopReason: null, reason: null, planners: [], window: null, admitted: null, refused: [], mode: null, deferred: false, heldMs: null, gateWaitMs: null, elapsedMs: null, error: null};
  for (const line of lines) {
    let m: RegExpMatchArray | null;
    if ((m = /\] status: running=(\w+) stopping=\w+ stopped=(\w+) neverStarted=\w+ stopReason=(\S+) tick=(\d+)/.exec(line))) {
      step.running = m[1] === 'true'; step.stopped = m[2] === 'true'; step.stopReason = m[3]; step.tick = Number(m[4]);
    } else if ((m = /\] step reason: (\S+)/.exec(line))) {
      step.reason = m[1];
    } else if ((m = /\] (\w+)\.step result: reason=(\S+) plan=(\S*)/.exec(line))) {
      step.planners.push({planner: m[1], reason: m[2], plan: m[3]});
    } else if ((m = /\] colony window: (\d+) ticks \(target ([\d.]+)s at (\d+) ticks\/s/.exec(line))) {
      step.window = {ticks: Number(m[1]), targetSeconds: Number(m[2]), ticksPerSecond: Number(m[3])};
    } else if ((m = /\] EvaluateClockWindow: work=\w+ combatPlan=\w+ admitted=(\w+) mode=(\S*) hostiles=\[[^\]]*\] refused=\[([^\]]*)\]/.exec(line))) {
      step.admitted = m[1] === 'true'; step.mode = m[2] || null; step.refused = m[3].split(/\s+/).filter(Boolean);
    } else if (/-> deferring admission/.test(line)) {
      step.deferred = true;
    } else if ((m = /\] stop committed: pause-bound admissions held (\S+) before the review/.exec(line))) {
      step.heldMs = goDurationMs(m[1]);
    } else if ((m = /\] step waited (\S+) for the player gate/.exec(line))) {
      step.gateWaitMs = goDurationMs(m[1]);
    } else if ((m = /\] step reads: .* elapsed=(\S+)/.exec(line))) {
      step.elapsedMs = goDurationMs(m[1]);
    } else if ((m = /\] step done: err=(.*?) planner failures=/.exec(line))) {
      if (m[1] !== '<nil>') step.error = m[1];
    }
  }
  return step;
}

const units: Record<string, number> = {h: 3600000, m: 60000, s: 1000, ms: 1, 'µs': 0.001, us: 0.001, ns: 0.000001};

// goDurationMs reads a Go time.Duration string ("1.915s", "942ms",
// "1m2.3s") as milliseconds, or null.
export function goDurationMs(text: string): number | null {
  const re = /(\d+(?:\.\d+)?)(h|ms|m|s|µs|us|ns)/g;
  let total = 0;
  let consumed = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text))) {total += Number(m[1]) * units[m[2]]; consumed += m[0].length;}
  return consumed === text.length && consumed > 0 ? total : null;
}
