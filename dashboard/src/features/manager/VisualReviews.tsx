import { useState } from "react";

type Region = { left: number; top: number; right: number; bottom: number };
export type VisualReview = {
  id: string;
  question: string;
  current_load: boolean;
  source: { tick: number; width: number; height: number; captured_at: number };
  report: {
    answer: string;
    concerns: { observation: string; region: Region; confidence: string; verify: string }[];
    missing_facts: string[];
  };
};

function ReviewImage({ review }: { review: VisualReview }) {
  const [failed, setFailed] = useState(false);
  if (failed) return <p>Source image unavailable. Concern locations cannot be displayed.</p>;
  return <div style={{ position: "relative", width: "100%", aspectRatio: `${review.source.width} / ${review.source.height}` }}>
    <img src={`/api/visual-reviews/${encodeURIComponent(review.id)}/source`}
      alt={`Original screenshot for: ${review.question}`} onError={() => setFailed(true)}
      style={{ display: "block", width: "100%", height: "100%" }} />
    {review.report.concerns.map((concern, index) => <span key={index}
      title={`${index + 1}. ${concern.observation}`}
      aria-label={`Concern ${index + 1}: ${concern.observation}`}
      style={{ position: "absolute", boxSizing: "border-box", border: "2px solid #ffcb66",
        color: "#fff", background: "#0002", pointerEvents: "none",
        left: `${concern.region.left * 100}%`, top: `${concern.region.top * 100}%`,
        width: `${(concern.region.right - concern.region.left) * 100}%`,
        height: `${(concern.region.bottom - concern.region.top) * 100}%` }}>
      <b style={{ background: "#222", padding: "0 4px" }}>{index + 1}</b>
    </span>)}
  </div>;
}

export default function VisualReviews({ reviews }: { reviews: VisualReview[] }) {
  if (!reviews.length) return null;
  return <section aria-label="Visual reviews">
    <h3>Visual reviews</h3>
    {reviews.map(review => <details key={review.id}>
      <summary>{review.question}</summary>
      <p>{review.report.answer}</p>
      <p>Historical screenshot · tick {review.source.tick}
        {!review.current_load && " · previous load"}. Verify native facts before acting.</p>
      <ReviewImage review={review} />
      <ol>{review.report.concerns.map((concern, index) => <li key={index}>
        {concern.observation} ({concern.confidence} confidence). Check: {concern.verify}
      </li>)}</ol>
      {review.report.missing_facts.length > 0 && <p>Unknown: {review.report.missing_facts.join("; ")}</p>}
    </details>)}
  </section>;
}
