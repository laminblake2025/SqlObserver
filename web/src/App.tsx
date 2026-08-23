import { TargetOnboarding } from "./features/targets/TargetOnboarding";

export function App() {
  return (
    <main className="shell">
      <header className="hero">
        <p className="eyebrow">Passive SQL Server diagnostics</p>
        <h1>SqlObserver</h1>
        <p className="lede">
          Register a bounded, Windows-integrated observation target and inspect its
          explicitly discovered capability and lifecycle state. Registration retries
          reuse a client-owned target identity.
        </p>
      </header>
      <TargetOnboarding />
    </main>
  );
}
