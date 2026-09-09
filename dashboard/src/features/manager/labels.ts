import type { Committed } from "./CommittedPlan";
export const goalName = (id: string) =>
  (
    ({
      AllowStartingSupplies: "Allow starting supplies",
      EnsureWorkAssignments: "Assign colony work",
      EnsureFoodSupply: "Maintain food supply",
      EnsureInitialShelter: "Establish shelter",
      EnsureTemperatureSafety: "Keep sleeping rooms comfortable",
      EnsureCooking: "Maintain cooking",
      EnsureFoodStorage: "Store food indoors",
      EnsureBasicDefense: "Equip defenders",
      MaintainWood: "Maintain wood supply",
      ActiveCombat: "Defend the colony",
      CriticalMedical: "Treat urgent injuries",
      RestoreWorkers: "Return defenders to work",
      ConfirmColonyNames: "Confirm colony names",
    }) as Record<string, string>
  )[id] ||
  (id.startsWith("intent-") ? "Player construction" : id)
    .replace(/-[a-f0-9]{8,}$/i, "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^intent-/, "")
    .replaceAll("-", " ");
export const priorities = [
  "Emergency",
  "Urgent care",
  "Essential",
  "Development",
  "Optional",
];
export const humanize = (value: string) =>
  value.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ");
export function readable(
  value: string,
  plan?: Committed,
  pawns?: { thing_id: string; name: string }[],
) {
  if (value.startsWith("Bridge tool failed:"))
    return "The game connection was interrupted. Native details are available in diagnostics.";
  let text = value;
  for (const pawn of pawns || [])
    text = text.replaceAll(pawn.thing_id, pawn.name);
  const methods: Record<string, string> = {
    allow: "make starting supplies available",
    assign: "update work assignments",
    acquire: "gather supplies",
    hunt: "hunt selected prey",
    repel: "repel the threat",
    tend: "treat the injured colonist",
  };
  for (const step of [...(plan?.steps || [])].sort(
    (a, b) => b.id.length - a.id.length,
  ))
    text = text.replaceAll(step.id, step.title);
  for (const id of Object.keys(plan?.colonyGoals || {}).sort(
    (a, b) => b.length - a.length,
  ))
    text = text.replaceAll(id, goalName(id));
  text = text.replace(
    /\b(allow|assign|acquire|hunt|repel|tend)-[\w-]+/g,
    (_, method: string) => methods[method],
  );
  return text
    .replace(/\bThing_[A-Za-z]+\d+\b/g, "tracked target")
    .replace(
      /\b(?:[a-f0-9]{24,64}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})\b/gi,
      "tracked item",
    );
}
