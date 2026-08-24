import { type FormEvent, useCallback, useEffect, useState } from "react";

import { TargetHealthPanel } from "../health/TargetHealthPanel";
import { listObservationTargets, registerObservationTarget } from "./targetApi";
import type {
  CapabilityStatus,
  ObservationTargetSummary,
  RegisterObservationTargetRequest,
} from "./targetTypes";

const statusLabels: Readonly<Record<CapabilityStatus, string>> = {
  pending: "Discovery pending",
  supported: "Supported",
  degraded: "Degraded visibility",
  unsupported: "Unsupported",
  unreachable: "Unreachable",
  authentication_failed: "Authentication failed",
  tls_validation_failed: "TLS validation failed",
  timed_out: "Discovery timed out",
  security_policy_rejected: "Security policy rejected",
  disabled: "Disabled",
  retired: "Retired",
};

const lifecycleLabels = {
  pending_discovery: "Pending discovery",
  active: "Active",
  disabled: "Disabled",
  retired: "Retired",
} as const;

type AddressMode = "tcp" | "named";

export function TargetOnboarding() {
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [addressMode, setAddressMode] = useState<AddressMode>("tcp");
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string>();
  const [selectedTargetId, setSelectedTargetId] = useState<string>();
  const [registrationInstanceId, setRegistrationInstanceId] = useState(createClientInstanceId);
  const selectedTarget = targets.find((target) => target.instanceId === selectedTargetId);

  const refresh = useCallback(async (signal: AbortSignal) => {
    setLoading(true);
    try {
      const page = await listObservationTargets(signal);
      setTargets(page.items);
      setMessage(undefined);
    } catch (error: unknown) {
      if (!signal.aborted) {
        setMessage(getSafeError(error));
      }
    } finally {
      if (!signal.aborted) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    const cancellation = new AbortController();
    void refresh(cancellation.signal);
    return () => cancellation.abort();
  }, [refresh]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    const host = requiredText(form, "host");
    const requestBase = {
      instanceId: registrationInstanceId,
      instanceKey: requiredText(form, "instanceKey"),
      displayName: requiredText(form, "displayName"),
      host,
      ...optionalCertificateHost(form),
    } as const;
    const request: RegisterObservationTargetRequest =
      addressMode === "named"
        ? { ...requestBase, namedInstance: requiredText(form, "namedInstance") }
        : { ...requestBase, tcpPort: requiredPort(form) };

    const cancellation = new AbortController();
    setSubmitting(true);
    setMessage(undefined);
    try {
      const target = await registerObservationTarget(request, cancellation.signal);
      setTargets((current) => [
        target,
        ...current.filter((candidate) => candidate.instanceId !== target.instanceId),
      ]);
      setMessage("Target accepted; capability discovery is pending.");
      formElement.reset();
      setAddressMode("tcp");
      setRegistrationInstanceId(createClientInstanceId());
    } catch (error: unknown) {
      setMessage(getSafeError(error));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="target-workspace" aria-labelledby="targets-heading">
      <div className="section-heading">
        <div>
          <p className="eyebrow">Milestone 3</p>
          <h2 id="targets-heading">Observation targets</h2>
        </div>
        <p>
          Connections use the Collector Windows service identity. SqlObserver does not
          ask for or store a SQL login secret.
        </p>
      </div>

      <div className="target-layout">
        <form className="target-form" onSubmit={(event) => void submit(event)}>
          <h3>Register a SQL Server</h3>

          <label>
            Stable key
            <input name="instanceKey" maxLength={256} pattern="[a-z0-9][a-z0-9._:-]*" required />
          </label>
          <label>
            Display name
            <input name="displayName" maxLength={256} required />
          </label>
          <label>
            Windows host
            <input name="host" maxLength={255} required />
          </label>

          <fieldset>
            <legend>Address</legend>
            <label className="choice">
              <input
                checked={addressMode === "tcp"}
                name="addressMode"
                onChange={() => setAddressMode("tcp")}
                type="radio"
                value="tcp"
              />
              TCP port
            </label>
            <label className="choice">
              <input
                checked={addressMode === "named"}
                name="addressMode"
                onChange={() => setAddressMode("named")}
                type="radio"
                value="named"
              />
              Named instance
            </label>
          </fieldset>

          {addressMode === "tcp" ? (
            <label>
              Port
              <input defaultValue="1433" max="65535" min="1" name="tcpPort" required type="number" />
            </label>
          ) : (
            <label>
              Instance name
              <input name="namedInstance" maxLength={128} required />
            </label>
          )}

          <label>
            Certificate host name <span>(optional)</span>
            <input name="certificateHostName" maxLength={255} />
          </label>

          <button disabled={submitting} type="submit">
            {submitting ? "Registering…" : "Register target"}
          </button>
        </form>

        <div className="target-list" aria-live="polite">
          {message === undefined ? null : <p className="status-message">{message}</p>}
          {loading ? <p>Loading targets…</p> : null}
          {!loading && targets.length === 0 ? (
            <p className="empty-state">No observation targets are registered.</p>
          ) : null}
          {targets.map((target) => (
            <article className="target-card" key={target.instanceId}>
              <div>
                <h3>{target.displayName}</h3>
                <p className="target-address">{formatAddress(target)}</p>
                <p className="target-address">Lifecycle: {lifecycleLabels[target.lifecycle]}</p>
              </div>
              <span className={`capability-status status-${target.capabilityStatus}`}>
                {statusLabels[target.capabilityStatus]}
              </span>
              {target.capabilityReasons.length === 0 ? null : (
                <ul>
                  {target.capabilityReasons.map((reason) => (
                    <li key={reason}>{reason}</li>
                  ))}
                </ul>
              )}
              <button
                className="secondary-button target-health-button"
                onClick={() => setSelectedTargetId(target.instanceId)}
                type="button"
              >
                View health evidence
              </button>
            </article>
          ))}
        </div>
      </div>
      {selectedTarget === undefined ? null : (
        <TargetHealthPanel
          displayName={selectedTarget.displayName}
          instanceId={selectedTarget.instanceId}
          onClose={() => setSelectedTargetId(undefined)}
        />
      )}
    </section>
  );
}

function createClientInstanceId(): string {
  return globalThis.crypto.randomUUID();
}

function requiredText(form: FormData, name: string): string {
  const value = form.get(name);
  if (typeof value !== "string" || value.length === 0) {
    throw new Error("A required target field is missing.");
  }
  return value;
}

function requiredPort(form: FormData): number {
  const value = Number.parseInt(requiredText(form, "tcpPort"), 10);
  if (!Number.isInteger(value) || value < 1 || value > 65_535) {
    throw new Error("The TCP port must be between 1 and 65535.");
  }
  return value;
}

function optionalCertificateHost(form: FormData): { readonly certificateHostName?: string } {
  const value = form.get("certificateHostName");
  return typeof value === "string" && value.length > 0 ? { certificateHostName: value } : {};
}

function formatAddress(target: ObservationTargetSummary): string {
  return target.namedInstance === undefined
    ? `${target.host}:${String(target.tcpPort ?? 1433)}`
    : `${target.host}\\${target.namedInstance}`;
}

function getSafeError(error: unknown): string {
  return error instanceof Error ? error.message : "The SqlObserver request failed.";
}
