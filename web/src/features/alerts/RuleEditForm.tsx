import { useEffect, useRef, useState, type FormEvent } from "react";
import type { AlertRuleAdminItem } from "./alertRuleAdminApi";
import { getRuleCatalog, prepareRule, RuleAttempts, updateRule,
  type RuleTemplate, type RuleValues } from "./alertRuleAdminActions";
import { ruleExplanation, ruleSignalLabel } from "./alertRuleSignals.ts";

const attempts = new RuleAttempts();

function valuesFromRule(rule: AlertRuleAdminItem): RuleValues {
  return {
    name: rule.name, kind: rule.kind, metricId: rule.metricId, comparison: rule.comparison,
    threshold: rule.threshold, hysteresis: rule.hysteresis,
    confirmationCount: rule.confirmationCount, confirmationSeconds: rule.confirmationSeconds,
    evaluationSeconds: rule.evaluationSeconds, clearConfirmationCount: rule.clearConfirmationCount,
    enabled: rule.enabled,
  };
}

export function RuleEditForm({ instanceId, rule, onSaved, onCancel }: {
  readonly instanceId: string; readonly rule: AlertRuleAdminItem;
  readonly onSaved: () => void; readonly onCancel: () => void;
}) {
  const [template, setTemplate] = useState<RuleTemplate>();
  const [catalogError, setCatalogError] = useState<string>();
  const [retry, setRetry] = useState(0);
  const [values, setValues] = useState<RuleValues>(() => valuesFromRule(rule));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const contextKey = JSON.stringify([instanceId, rule.ruleId, rule.revision]);
  const currentContext = useRef(contextKey);
  currentContext.current = contextKey;
  const request = useRef<AbortController | undefined>(undefined);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      request.current?.abort();
      request.current = undefined;
    };
  }, [contextKey]);

  useEffect(() => {
    const controller = new AbortController();
    setCatalogError(undefined);
    void getRuleCatalog(instanceId, controller.signal).then(templates => {
      if (controller.signal.aborted) return;
      const approved = templates.find(item => item.kind === rule.kind && item.metricId === rule.metricId);
      if (approved) setTemplate(approved);
      else setCatalogError("This rule's signal is no longer in the approved catalog.");
    }).catch(failure => {
      if (!controller.signal.aborted) setCatalogError(failure instanceof Error ? failure.message : "Rule templates are unavailable.");
    });
    return () => controller.abort();
  }, [instanceId, rule.kind, rule.metricId, retry]);

  const blocking = values.kind === "blocking_duration_v1";
  const deadlock = values?.kind === "deadlock_count_v1";
  const thresholdSignal = values.kind === "metric_threshold" || blocking || deadlock;
  const setNumber = (name: keyof RuleValues, value: string) =>
    setValues(current => ({ ...current, [name]: Number(value) }));
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!template || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    const acceptsResult = () => mounted.current && currentContext.current === contextKey &&
      request.current === controller && !controller.signal.aborted;
    setError(undefined);
    try {
      const updated = prepareRule(values, template);
      const operationId = attempts.updateFor(instanceId, rule.ruleId, rule.revision, updated);
      setBusy(true);
      await updateRule(instanceId, rule.ruleId, rule.revision, updated, operationId, controller.signal);
      if (!acceptsResult()) return;
      attempts.completeUpdate(instanceId, rule.ruleId, rule.revision, updated);
      onSaved();
    } catch (failure) {
      if (acceptsResult()) setError(failure instanceof Error ? failure.message : "The rule could not be updated.");
    } finally {
      if (request.current === controller) {
        request.current = undefined;
        if (mounted.current && currentContext.current === contextKey) setBusy(false);
      }
    }
  }
  return <form className="panel maintenance-form" aria-label="Edit alert rule" onSubmit={(event) => void submit(event)}>
    <div><h3>Edit {rule.name}</h3><p>Saving checks revision {rule.revision}. Collector health settings are fixed by the approved catalog.</p></div>
    {catalogError ? <p role="alert">{catalogError} <button className="secondary-button" type="button" onClick={() => setRetry(value => value + 1)}>Retry</button></p> : null}
    {error ? <p role="alert">{error}</p> : null}
    <div className="maintenance-fields">
      <label>Signal<input value={ruleSignalLabel(rule.kind, rule.metricId)} disabled /></label>
      {ruleExplanation(rule.kind) ? <p className="table-note">{ruleExplanation(rule.kind)}</p> : null}
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
      <label>Enabled<input type="checkbox" checked={values.enabled} disabled={busy} onChange={event => setValues({ ...values, enabled: event.target.checked })} /></label>
    </div>
    <div className="form-actions"><button className="primary" type="submit" disabled={!template || busy}>{busy ? "Saving…" : "Save rule"}</button><button className="secondary-button" type="button" onClick={onCancel}>Cancel</button></div>
  </form>;
}
