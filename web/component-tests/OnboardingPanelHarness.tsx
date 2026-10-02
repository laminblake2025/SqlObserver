import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TargetOnboarding } from "../src/features/targets/TargetOnboarding";
import { ServersPage } from "../src/features/targets/ServersPage";
import type { CapabilityStatus, ObservationTargetSummary, TargetLifecycle } from "../src/features/targets/targetTypes";
import type { FleetEvidence } from "../src/features/targets/useFleetEvidence";
import { routeHref } from "../src/dashboardModel";
import "../src/styles.css";
import "./harness.css";

function target(index: number, displayName: string, capabilityStatus: CapabilityStatus, lifecycle: TargetLifecycle): ObservationTargetSummary {
  return {
    instanceId: `${String(index).repeat(8)}-${String(index).repeat(4)}-4${String(index).repeat(3)}-8${String(index).repeat(3)}-${String(index).repeat(12)}`,
    instanceKey: `synthetic-${index}`, displayName, host: `sql-${index}.example.test`,
    ...(index === 2 ? { namedInstance: "APP" } : { tcpPort: 1433 }),
    authenticationMode: "windows_integrated_service_identity", encryptionMode: "mandatory_validated",
    lifecycle, capabilityStatus, capabilityReasons: [`Synthetic ${capabilityStatus} discovery evidence.`],
    configurationRevision: 1, discoveryRequestedAtUtc: "2026-09-30T13:00:00.000Z",
  };
}

// Explicitly typed prop fixtures; no fleet evidence fetch hook or backend runs.
const initialTargets: readonly ObservationTargetSummary[] = [
  target(1, "Alpha pending", "pending", "pending_discovery"),
  target(2, "Beta supported", "supported", "active"),
  target(3, "Delta degraded", "degraded", "active"),
  target(4, "Epsilon unsupported", "unsupported", "active"),
  target(5, "Gamma unreachable", "unreachable", "active"),
  target(6, "Zeta disabled", "disabled", "disabled"),
  target(7, "Zulu retired", "retired", "retired"),
];
const syntheticEvidence: Readonly<Record<string, FleetEvidence>> = {};
const inertName = '<img src="https://invalid.example/onboarding" onerror="window.onboardingInjected=true">';
const inertReason = '<script>window.onboardingInjected=true</script>';
const loadedTargets: readonly ObservationTargetSummary[] = new URLSearchParams(location.search).get("scenario") === "inert"
  ? initialTargets.map((item, index) => index === 0 ? { ...item, displayName: inertName, capabilityReasons: [inertReason] } : item)
  : initialTargets;
const nextPage: readonly ObservationTargetSummary[] = [target(8, "Next page server", "supported", "active")];

function Harness() {
  const [mounted, setMounted] = useState(true);
  const [registered, setRegistered] = useState<readonly ObservationTargetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const targets = cursor === undefined ? [...registered, ...loadedTargets] : nextPage;
  return <main className="component-harness">
    <h1>Onboarding and servers harness</h1>
    <div className="harness-controls">
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Hide registration" : "Show registration"}</button>
    </div>
    <output aria-label="Registration callbacks">{registered.length}</output>{" "}
    <output aria-label="Last registered identity">{registered.at(-1)?.instanceId ?? "None"}</output>{" "}
    <output aria-label="Server page cursor">{cursor ?? "First"}</output>
    <section aria-label="Target registration"><h2>Add a server</h2>
      {mounted ? <TargetOnboarding onRegistered={item => setRegistered(previous => [...previous, item])} /> : <p>Registration unmounted</p>}
    </section>
    <section aria-label="Loaded servers">
      <ServersPage targets={targets} evidence={syntheticEvidence} cursor={cursor}
        nextCursor={cursor === undefined ? "targets+/=next" : undefined} setCursor={setCursor} routeHref={routeHref} />
    </section>
  </main>;
}

createRoot(document.getElementById("root")!).render(<Harness />);
