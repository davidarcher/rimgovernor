import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import PlayerGuide from "./PlayerGuide";

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
  await userEvent.click(screen.getByRole("link", { name: "Player guide" }));
  await screen.findByRole("heading", { name: "Player guide" });
  expect(screen.getByRole("link", { name: "backlog issues" })).toHaveAttribute("href",
    "https://github.com/davidarcher/rimgovernor/issues");
  expect(screen.getByRole("link", { name: "backlog issues" })).toHaveAttribute("rel", "noopener noreferrer");
});
