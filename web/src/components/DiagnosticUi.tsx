import { useRef, type KeyboardEvent, type ReactNode } from "react";

export type EvidenceTone = "neutral" | "current" | "warning" | "critical" | "unavailable";

export function PageHeading({
  eyebrow,
  title,
  subtitle,
  actions,
}: {
  readonly eyebrow?: string;
  readonly title: string;
  readonly subtitle?: string;
  readonly actions?: ReactNode;
}) {
  return (
    <header className="page-heading">
      <div className="page-heading-copy">
        {eyebrow ? <p className="eyebrow">{eyebrow}</p> : null}
        <h1>{title}</h1>
        {subtitle ? <p>{subtitle}</p> : null}
      </div>
      {actions ? <div className="page-heading-actions">{actions}</div> : null}
    </header>
  );
}

export function EvidenceStatus({
  label,
  detail,
  tone = "neutral",
}: {
  readonly label: string;
  readonly detail?: string;
  readonly tone?: EvidenceTone;
}) {
  return (
    <div className={`evidence-status evidence-${tone}`} role="status">
      <span className="status-mark" aria-hidden="true" />
      <strong>{label}</strong>
      {detail ? <span>{detail}</span> : null}
    </div>
  );
}

export function MetricCard({
  label,
  value,
  detail,
  tone = "neutral",
  icon,
}: {
  readonly label: string;
  readonly value: ReactNode;
  readonly detail?: ReactNode;
  readonly tone?: EvidenceTone;
  readonly icon?: ReactNode;
}) {
  return (
    <section className={`metric-card metric-${tone}`}>
      {icon ? <span className="metric-icon" aria-hidden="true">{icon}</span> : null}
      <div>
        <p>{label}</p>
        <strong>{value}</strong>
        {detail ? <small>{detail}</small> : null}
      </div>
    </section>
  );
}

export function TableCard({
  title,
  description,
  children,
  className = "",
}: {
  readonly title: string;
  readonly description?: ReactNode;
  readonly children: ReactNode;
  readonly className?: string;
}) {
  return (
    <section className={`table-card panel ${className}`.trim()}>
      <div className="table-card-heading">
        <div>
          <h2>{title}</h2>
          {description ? <p>{description}</p> : null}
        </div>
      </div>
      {children}
    </section>
  );
}

export function DetailPane({
  title,
  subtitle,
  actions,
  children,
  className = "",
}: {
  readonly title: string;
  readonly subtitle?: ReactNode;
  readonly actions?: ReactNode;
  readonly children: ReactNode;
  readonly className?: string;
}) {
  return (
    <aside className={`detail-pane ${className}`.trim()} aria-label={title}>
      <div className="detail-pane-heading">
        <div>
          <h2>{title}</h2>
          {subtitle ? <p>{subtitle}</p> : null}
        </div>
        {actions ? <div className="detail-pane-actions">{actions}</div> : null}
      </div>
      {children}
    </aside>
  );
}

export function Tabs({
  tabs,
  value,
  onChange,
  label,
}: {
  readonly tabs: readonly { readonly value: string; readonly label: string }[];
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly label: string;
}) {
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const selectedIndex = tabs.findIndex((tab) => tab.value === value);
  const focusTab = (index: number) => {
    const next = tabs[index];
    if (!next) return;
    onChange(next.value);
    tabRefs.current[index]?.focus();
  };
  const handleKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    let nextIndex: number | undefined;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") nextIndex = (index + 1) % tabs.length;
    if (event.key === "ArrowLeft" || event.key === "ArrowUp") nextIndex = (index - 1 + tabs.length) % tabs.length;
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = tabs.length - 1;
    if (nextIndex === undefined || tabs.length === 0) return;
    event.preventDefault();
    focusTab(nextIndex);
  };
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {tabs.map((tab, index) => (
        <button
          className={tab.value === value ? "tab is-active" : "tab"}
          key={tab.value}
          type="button"
          role="tab"
          aria-selected={tab.value === value}
          ref={(element) => { tabRefs.current[index] = element; }}
          tabIndex={index === (selectedIndex < 0 ? 0 : selectedIndex) ? 0 : -1}
          onKeyDown={(event) => handleKeyDown(event, index)}
          onClick={() => onChange(tab.value)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
