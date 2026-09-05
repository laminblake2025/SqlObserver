import { type FormEvent, useRef, useState, useEffect } from "react";
import { registerObservationTarget } from "./targetApi";
import type { ObservationTargetSummary, RegisterObservationTargetRequest } from "./targetTypes";
export function TargetOnboarding({onRegistered}: {readonly onRegistered: (target: ObservationTargetSummary) => void}) {
  const [addressMode, setAddressMode] = useState<"tcp" | "named">("tcp");
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string>();
  const [registrationInstanceId, setRegistrationInstanceId] = useState(createClientInstanceId);
  const activeRequest = useRef<AbortController | null>(null);
  useEffect(() => () => activeRequest.current?.abort(), []);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (activeRequest.current !== null) return;
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
    activeRequest.current = cancellation;
    setSubmitting(true);
    setMessage(undefined);
    try {
      const target = await registerObservationTarget(request, cancellation.signal);
      if (cancellation.signal.aborted) return;
      onRegistered(target);
      setMessage("Target accepted; capability discovery is pending.");
      formElement.reset();
      setAddressMode("tcp");
      setRegistrationInstanceId(createClientInstanceId());
    } catch (error: unknown) {
      if (!cancellation.signal.aborted) setMessage(getSafeError(error));
    } finally {
      activeRequest.current = null;
      setSubmitting(false);
    }
  }

  return <>
    <p>Connections use the Collector Windows service identity. SqlObserver does not ask for or store a SQL login secret.</p>
    {message === undefined ? null : <p role="status" className="status-message">{message}</p>}
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

</>;
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

function getSafeError(error: unknown): string {
  return error instanceof Error ? error.message : "The SqlObserver request failed.";
}
