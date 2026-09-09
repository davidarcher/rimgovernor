import { useEffect, useRef, useState } from "react";

type Sample = { tick: number; at: number };
export function tickRate(samples: Sample[]) {
  if (samples.length < 2) return null;
  const first = samples[0],
    last = samples[samples.length - 1];
  return last.at > first.at && last.tick >= first.tick
    ? (last.tick - first.tick) / (last.at - first.at)
    : null;
}
export default function Throughput({
  tick,
  at,
  stale,
  sessionId,
}: {
  tick?: number;
  at?: number;
  stale: boolean;
  sessionId: string;
}) {
  const samples = useRef<Sample[]>([]),
    [rate, setRate] = useState<number | null>(null);
  useEffect(() => {
    samples.current = [];
    setRate(null);
  }, [sessionId]);
  useEffect(() => {
    if (stale || !Number.isFinite(tick) || !Number.isFinite(at)) {
      samples.current = [];
      setRate(null);
      return;
    }
    const sample = { tick: tick!, at: at! },
      last = samples.current.at(-1);
    if (
      last &&
      (sample.tick < last.tick ||
        sample.at <= last.at ||
        sample.at - last.at > 10)
    )
      samples.current = [];
    samples.current = [
      ...samples.current.filter((s) => sample.at - s.at <= 30),
      sample,
    ];
    setRate(tickRate(samples.current));
  }, [tick, at, stale]);
  return (
    <span
      className="throughput"
      title="Observed game ticks per wall-clock second over up to 30 seconds, including controller pauses. This measures throughput, not safety or reaction latency."
    >
      {rate === null ? "—" : Math.round(rate).toLocaleString()}{" "}
      <small>ticks / sec · observed</small>
    </span>
  );
}
