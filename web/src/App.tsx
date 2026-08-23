const boundaries = [
  {
    title: "Observe",
    detail: "Agentless, passive SQL Server diagnostics by default.",
  },
  {
    title: "Retain",
    detail: "UTC telemetry and audit history in PostgreSQL 18.x.",
  },
  {
    title: "Explain",
    detail: "Bounded, read-only diagnostic experiences for people and MCP clients.",
  },
] as const;

export function App() {
  return (
    <main className="shell">
      <header className="hero">
        <p className="eyebrow">Milestone 1 scaffold</p>
        <h1>SqlObserver</h1>
        <p className="lede">
          An original, clean-room foundation for SQL Server monitoring and
          diagnostics on Windows Server.
        </p>
      </header>

      <section aria-labelledby="boundary-heading">
        <h2 id="boundary-heading">Product boundaries</h2>
        <div className="boundary-grid">
          {boundaries.map((boundary) => (
            <article className="boundary-card" key={boundary.title}>
              <h3>{boundary.title}</h3>
              <p>{boundary.detail}</p>
            </article>
          ))}
        </div>
      </section>

      <aside className="notice" aria-label="Implementation status">
        <strong>Architecture only.</strong> Collection, repository access, alerts,
        reports, and MCP tools are intentionally not implemented in this milestone.
      </aside>
    </main>
  );
}
