import { useEffect, useState, type FormEvent } from "react";
import { createRule, getRuleCatalog, prepareRule, RuleAttempts,
  type RuleTemplate, type RuleValues } from "./alertRuleAdminActions";
import { ruleExplanation, ruleSignalLabel } from "./alertRuleSignals.ts";

const attempts = new RuleAttempts();

function valuesFromTemplate(template: RuleTemplate): RuleValues {
  const { unit: _unit, ...values } = template;
  return { ...values, name: template.kind === "metric_threshold" ? "" : template.name, enabled: true };
}

export function RuleCreateForm({ instanceId, onCreated }: {
  readonly instanceId: string; readonly onCreated: (ruleId: string) => void;
}) {
  const [templates, setTemplates] = useState<readonly RuleTemplate[]>([]);
  const [catalogError, setCatalogError] = useState<string>();
  const [catalogRetry, setCatalogRetry] = useState(0);
  const [selectedTemplate, setSelectedTemplate] = useState(0);
  const [values, setValues] = useState<RuleValues>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    setCatalogError(undefined);
    void getRuleCatalog(instanceId, controller.signal).then(entries => {
      if (controller.signal.aborted) return;
      setTemplates(entries);
      setSelectedTemplate(0);
      setValues(entries[0] ? valuesFromTemplate(entries[0]) : undefined);
    }).catch(failure => {
      if (!controller.signal.aborted) setCatalogError(failure instanceof Error ? failure.message : "Rule templates are unavailable.");
    });
    return () => controller.abort();
  }, [instanceId, catalogRetry]);

  const template = templates[selectedTemplate];
  const blocking = values?.kind === "blocking_duration_v1";
  const deadlock = values?.kind === "deadlock_count_v1";
  const thresholdSignal = values?.kind === "metric_threshold" || blocking || deadlock;
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy || !template || !values) return;
    setError(undefined); setMessage(undefined);
    try {
      const rule = prepareRule(values, template);
      const attempt = attempts.createFor(instanceId, rule);
      setBusy(true);
      await createRule(instanceId, rule, attempt, new AbortController().signal);
      attempts.completeCreate(instanceId, rule);
      setMessage("Rule created. The list is refreshing.");
      onCreated(attempt.ruleId);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "The rule could not be created.");
    } finally { setBusy(false); }
  }
  const setNumber = (name: keyof RuleValues, value: string) =>
    setValues(current => current ? { ...current, [name]: Number(value) } : current);

  return <form className="panel maintenance-form" onSubmit={(event) => void submit(event)}>
    <div><h3>Create an alert rule</h3><p>Choose a signal and its confirmation timing. Backup policy limits are configured per database in Health checks.</p></div>
    {catalogError ? <p role="alert">{catalogError} <button className="secondary-button" type="button" onClick={() => setCatalogRetry(value => value + 1)}>Retry</button></p> : null}
    {error ? <p role="alert">{error}</p> : null}
    {message ? <p role="status">{message}</p> : null}
    {template && values ? <div className="maintenance-fields">
      <label>Signal<select value={selectedTemplate} disabled={busy} onChange={event => {
        const index = Number(event.target.value);
        const next = templates[index];
        if (!next) return;
        setSelectedTemplate(index); setValues(valuesFromTemplate(next)); setError(undefined);
      }}>{templates.map((entry, index) => <option key={`${entry.kind}:${entry.metricId}`} value={index}>{ruleSignalLabel(entry.kind, entry.metricId)} ({entry.unit})</option>)}</select></label>
      {ruleExplanation(values.kind) ? <p className="table-note">{ruleExplanation(values.kind)}</p> : null}
      <label>Rule name<input required maxLength={128} pattern="[A-Za-z0-9][A-Za-z0-9._-]*" value={values.name} disabled={busy || values.kind === "collector_health"} onChange={event => setValues({ ...values, name: event.target.value })} /></label>
      {thresholdSignal ? <>
        <label>Condition<select value={values.comparison} disabled={busy} onChange={event => setValues({ ...values, comparison: event.target.value as RuleValues["comparison"] })}><option value="greater_than_or_equal">At least</option><option value="greater_than">Greater than</option></select></label>
        <label>{blocking ? "Duration threshold (seconds)" : deadlock ? "Deadlock threshold (count)" : "Threshold"}<input type="number" required min="0" max={blocking ? 86400 : deadlock ? 256 : 1000000} step={deadlock ? 1 : "any"} value={values.threshold} disabled={busy} onChange={event => setNumber("threshold", event.target.value)} /></label>
        <label>{blocking ? "Clear below threshold by (seconds)" : deadlock ? "Clear below threshold by (count)" : "Clear below threshold by"}<input type="number" required min="0" max={blocking ? 86400 : deadlock ? 256 : 1000000} step={deadlock ? 1 : "any"} value={values.hysteresis} disabled={busy} onChange={event => setNumber("hysteresis", event.target.value)} /></label>
      </> : null}
      {values.kind !== "collector_health" ? <>
        <label>Fire after observations<input type="number" required min="1" max="100" value={values.confirmationCount} disabled={busy} onChange={event => setNumber("confirmationCount", event.target.value)} /></label>
        <label>Within seconds<input type="number" required min="0" max="604800" value={values.confirmationSeconds} disabled={busy} onChange={event => setNumber("confirmationSeconds", event.target.value)} /></label>
        <label>Evaluate every seconds<input type="number" required min={blocking ? 5 : 15} max={blocking || deadlock ? 60 : 3600} value={values.evaluationSeconds} disabled={busy} onChange={event => setNumber("evaluationSeconds", event.target.value)} /></label>
      </> : null}
      {values.kind !== "backup_observed_faults_v2" ? <label>Clear after observations<input type="number" required min="1" max="100" value={values.clearConfirmationCount} disabled={busy} onChange={event => setNumber("clearConfirmationCount", event.target.value)} /></label> : null}
    </div> : null}
    <button className="primary" type="submit" disabled={!template || !values || busy}>{busy ? "Creating…" : "Create rule"}</button>
  </form>;
}
