import { useEffect, useState } from "react";
import { analyticsSurfaceHref, type Destination } from "../dashboardModel";
import type { OverviewScope } from "../features/overview/overviewTypes";
import type { AnalyticsSurface } from "../features/analytics/analyticsTypes";

export function DiagnosticIcon({ name, className }: { readonly name: "fleet" | "investigation" | "alert" | "collection" | "report" | "clock" | "sessions"; readonly className?: string }) {
  const paths = {
    fleet: <><rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.01M7 17.5h.01M12 6.5h5M12 17.5h5"/></>,
    investigation: <><circle cx="10" cy="10" r="7"/><path d="m15 15 6 6"/></>,
    alert: <><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9ZM10 21h4"/></>,
    collection: <><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 4 16 4 16 0V5M4 12c0 4 16 4 16 0"/></>,
    report: <><path d="M5 21V13M12 21V3M19 21V8"/></>,
    clock: <><circle cx="12" cy="12" r="9"/><path d="M12 6v6l4 2"/></>,
    sessions: <><circle cx="9" cy="7" r="4"/><path d="M2 21v-3a7 7 0 0 1 14 0v3M17 4a4 4 0 0 1 0 8M19 15a6 6 0 0 1 3 6"/></>,
  };
  return <svg className={className} width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}

export function WorkspaceNavigation({ page, surface, scope, href }: {
  readonly page: Destination; readonly surface: AnalyticsSurface; readonly scope: OverviewScope;
  readonly href: (page: Destination, target?: string) => string;
}) {
  const [open, setOpen] = useState(false);
  useEffect(() => setOpen(false), [page, surface]);
  const selected = page === "analytics" ? surface === "incidents" ? "investigation" : "collection"
    : page === "reports" ? "report" : ["alerts", "rules", "rule-templates", "maintenance", "destinations"].includes(page) ? "alert" : "fleet";
  const entries = [
    { name: "Fleet", icon: "fleet" as const, url: href("overview", "") },
    { name: "Investigations", icon: "investigation" as const, url: analyticsSurfaceHref(scope, scope.target, "incidents") },
    { name: "Alerts", icon: "alert" as const, url: href("alerts", "") },
    { name: "Collection & evidence", icon: "collection" as const, url: analyticsSurfaceHref(scope, scope.target, surface === "incidents" ? "jobs" : surface) },
    { name: "Reports", icon: "report" as const, url: href("reports") },
  ];
  return <>
    <button className="mobile-navigation-toggle secondary-button" type="button" aria-expanded={open} aria-controls="workspace-navigation" onClick={() => setOpen(value => !value)} onKeyDown={event=>{if(event.key==='Escape'){event.preventDefault();setOpen(false);}}}>Menu</button>
    <nav id="workspace-navigation" className={open ? "workspace-navigation is-open" : "workspace-navigation"} aria-label="Main navigation" onKeyDown={event => { if (event.key === "Escape") { setOpen(false); document.querySelector<HTMLButtonElement>(".mobile-navigation-toggle")?.focus(); } }}>
      {entries.map(entry => <a key={entry.icon} href={entry.url} aria-current={selected === entry.icon ? "page" : undefined} onClick={() => setOpen(false)}><DiagnosticIcon name={entry.icon}/><span>{entry.name}</span></a>)}
    </nav>
  </>;
}

const serverTabs = [["health", "Summary"], ["activity", "Activity"], ["waits", "Waits"], ["resources", "Resources"], ["queries", "Queries"], ["deadlocks", "Deadlocks"], ["operations", "Operations"]] as const;
export function ServerNavigation({ page, href }: { readonly page: Destination; readonly href: (page: Destination) => string }) {
  return <nav className="server-navigation" aria-label="Server diagnostics" onFocusCapture={event => {
    const target = event.target;
    requestAnimationFrame(() => { if (target instanceof HTMLElement && document.activeElement === target) target.scrollIntoView({ block: "nearest", inline: "nearest" }); });
  }}>{serverTabs.map(([key, name]) => <a key={key} href={href(key)} aria-current={page === key ? "page" : undefined}>{name}</a>)}</nav>;
}
