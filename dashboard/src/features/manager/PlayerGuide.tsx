import { useEffect, useRef, useState } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import overview from "../../../../docs/players/README.md?raw";
import controls from "../../../../docs/players/controls.md?raw";
import setup from "../../../../docs/players/setup.md?raw";
import launch from "../../../../docs/players/launch.md?raw";
import saves from "../../../../docs/players/save-and-resume.md?raw";
import "./PlayerGuide.css";

const pages = [
  { id: "overview", file: "README.md", label: "Getting started", text: overview },
  { id: "controls", file: "controls.md", label: "Dashboard & controls", text: controls },
  { id: "setup", file: "setup.md", label: "Windows setup", text: setup },
  { id: "launch", file: "launch.md", label: "Launch options", text: launch },
  { id: "saves", file: "save-and-resume.md", label: "Save & resume", text: saves },
] as const;

const source = "https://github.com/davidarcher/RimBot/blob/main/docs/players/";

function currentPage() {
  return pages.find(page => location.hash === `#help/${page.id}`) ?? pages[0];
}

function guideLink(href: string, file: string): string {
  if (!href) return "";
  // The default address in the launch instructions refers to this dashboard.
  if (/^http:\/\/(127\.0\.0\.1|localhost):8787\/?$/.test(href)) return "#";
  const url = new URL(href, source + file);
  const page = pages.find(candidate => url.href === source + candidate.file);
  return page ? `#help/${page.id}` : url.href;
}

export default function PlayerGuide() {
  const [page, setPage] = useState(currentPage);
  const article = useRef<HTMLElement>(null);

  useEffect(() => {
    const changed = () => setPage(currentPage());
    window.addEventListener("hashchange", changed);
    return () => window.removeEventListener("hashchange", changed);
  }, []);

  useEffect(() => {
    article.current?.focus({ preventScroll: true });
  }, [page.id]);

  return (
    <section className="player-guide" aria-label="Player help">
      <aside className="player-guide-sidebar">
        <p className="mgr-eyebrow">PLAYER GUIDE</p>
        <p>Help for your next step.</p>
        <nav aria-label="Help topics">
          {pages.map(topic => (
            <a key={topic.id} href={`#help/${topic.id}`}
              aria-current={page.id === topic.id ? "page" : undefined}>
              {topic.label}
            </a>
          ))}
        </nav>
        <a className="player-guide-source" href={source + page.file}
          target="_blank" rel="noopener noreferrer">Read on GitHub ↗</a>
      </aside>
      <article ref={article} tabIndex={-1} className="player-guide-content mgr-card"
        aria-label={page.label}>
        <Markdown remarkPlugins={[remarkGfm]} components={{
          a: ({ href = "", children }) => {
            const target = guideLink(href, page.file);
            return <a href={target || undefined}
              target={target.startsWith("#") ? undefined : "_blank"}
              rel={target.startsWith("#") ? undefined : "noopener noreferrer"}>{children}</a>;
          },
          table: ({ children }) => <div className="player-guide-table"><table>{children}</table></div>,
        }}>{page.text}</Markdown>
      </article>
    </section>
  );
}
