import "@testing-library/jest-dom/vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import PlayerGuide from "./PlayerGuide";
import BridgeColony from "./BridgeColony";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  location.hash = "";
});

it("navigates the bundled player docs and renders controls and code examples", async () => {
  location.hash = "#help";
  render(<PlayerGuide />);
  expect(screen.getByRole("heading", { name: "Player guide" })).toBeVisible();
  await userEvent.click(screen.getByRole("link", { name: "Use the dashboard and controls" }));
  await screen.findByRole("heading", { name: "Dashboard and controls" });
  expect(screen.getByRole("table")).toHaveTextContent("save instructions");
  expect(screen.getByRole("link", { name: "Player guide" })).toHaveAttribute("href", "#help/overview");
  await userEvent.click(within(screen.getByRole("navigation", { name: "Help topics" }))
    .getByRole("link", { name: "Windows setup" }));
  await screen.findByRole("heading", { name: "Windows setup" });
  expect(screen.getByRole("article")).toHaveFocus();
  expect(screen.getByText(/powershell -ExecutionPolicy Bypass/).closest("pre")).toBeTruthy();
  expect(screen.getByRole("link", { name: "Docker checks" })).toHaveAttribute("href",
    "https://github.com/davidarcher/RimBot/blob/main/docs/developers/testing/docker-checks.md");
  expect(screen.getByRole("link", { name: "Docker checks" })).toHaveAttribute("rel", "noopener noreferrer");
});

it("supports direct links and browser history without requiring a game connection", async () => {
  location.hash = "#help/saves";
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("Game disconnected")));
  render(<BridgeColony />);
  expect(await screen.findByRole("heading", { name: "Save and resume" })).toBeVisible();
  expect(screen.getByRole("link", { name: "05 Help" })).toHaveAttribute("aria-current", "page");
  await screen.findByRole("alert");
  location.hash = "#help/launch";
  fireEvent(window, new Event("hashchange"));
  await screen.findByRole("heading", { name: "Launch a prepared colony" });
  expect(screen.getByRole("heading", { name: "Launch a prepared colony" })).toBeVisible();
  location.hash = "#help/unknown";
  fireEvent(window, new Event("hashchange"));
  await screen.findByRole("heading", { name: "Player guide" });
});

it("keeps unsent player direction when opening Help and returning to Watch", async () => {
  const request = vi.fn(async (url: RequestInfo | URL) => new Response(JSON.stringify(
    url === "/api/state" ? {
      sessionId: "a", connected: true, mode: "manual", mood: "happy", cameraVersion: 0,
      goals: { long: "", short: "" }, feed: [], status: { label: "Manual" },
      game: { paused: true, stale: false }, counters: { tools: 0, actions: 0, model_calls: 0 },
    } : { events: [] },
  ), { status: 200 }));
  vi.stubGlobal("fetch", request);
  render(<BridgeColony />);
  await screen.findByText("Colony connected");
  const draft = screen.getByLabelText("Message the colony manager");
  fireEvent.change(draft, { target: { value: "Build a warm shelter" } });
  await userEvent.click(screen.getByRole("link", { name: "05 Help" }));
  await screen.findByRole("heading", { name: "Player guide" });
  expect(draft).not.toBeVisible();
  await userEvent.click(screen.getByRole("link", { name: "01 Watch" }));
  await waitFor(() => expect(draft).toBeVisible());
  expect(draft).toHaveValue("Build a warm shelter");
  expect(request.mock.calls.some(([url]) => url === "/api/chat" || url === "/api/control")).toBe(false);
});
