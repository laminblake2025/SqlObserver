import { useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane } from "../../components/DiagnosticUi";
import { getAlertRulesForAdmin, type AlertRuleAdminItem } from "./alertRuleAdminApi";
import { disableRule, RuleAttempts } from "./alertRuleAdminActions";
import { RuleCreateForm } from "./RuleCreateForm";
import { RuleEditForm } from "./RuleEditForm";
import { ruleExplanation, ruleSignalLabel } from "./alertRuleSignals.ts";

import { useAlertAdminList } from "./useAlertAdminList";
import { adminSelectionMessage } from "./adminSelectionModel.ts";

const ruleId = (item: AlertRuleAdminItem) => item.ruleId;
const attempts = new RuleAttempts();

export function TargetAlertRulesPanel({ instanceId, displayName, onClose, refresh, canManage, selectedId, onSelect }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number; readonly canManage: boolean; readonly selectedId?: string | null; readonly onSelect: (id: string) => void;
}) {
  const formatTime = useTimeFormatter();
  const list = useAlertAdminList(instanceId, refresh, getAlertRulesForAdmin, ruleId, selectedId);
  const { items, nextCursor, loading, loadingMore, error, loadMore } = list;
  const [busyRuleId, setBusyRuleId] = useState<string>();
  const [actionError, setActionError] = useState<string>();
  const [actionMessage, setActionMessage] = useState<string>();
  const [editRuleId, setEditRuleId] = useState<string>();

  const selected = selectedId === undefined ? items[0] : items.find(item => item.ruleId.toLowerCase() === selectedId?.toLowerCase());
  const selectionMessage = adminSelectionMessage(selectedId, Boolean(selected), list.findingSelection, Boolean(nextCursor));
  async function disable(rule: AlertRuleAdminItem) {
    if (!canManage || !rule.enabled || busyRuleId) return;
    setActionError(undefined); setActionMessage(undefined);
    const operationId = attempts.disableFor(instanceId, rule.ruleId, rule.revision);
    setBusyRuleId(rule.ruleId);
    try {
      await disableRule(instanceId, rule, operationId, new AbortController().signal);
      attempts.completeDisable(instanceId, rule.ruleId, rule.revision);
      setActionMessage("Rule disabled. The list is refreshing.");
      list.retry();
    } catch (failure) {
      setActionError(failure instanceof Error ? failure.message : "The rule could not be disabled.");
    } finally { setBusyRuleId(undefined); }
  }
  return <section className="alerts-screen" aria-labelledby="rules-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="rules-heading">Configured alert rules</h2><p>Review thresholds, confirmation, clear settings, and disabled rules for this server.</p></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {loading ? <p role="status" className="status-message">Loading alert rules…</p> : null}
    {error ? <div role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => list.retry()}>Retry</button></div> : null}
    {selectionMessage ? <p role="status" className="status-message">{selectionMessage}</p> : null}
    {actionError ? <p role="alert" className="status-message">{actionError}</p> : null}
    {actionMessage ? <p role="status" className="status-message">{actionMessage}</p> : null}
    {canManage && editRuleId && selected?.ruleId === editRuleId
      ? <RuleEditForm key={`${instanceId}:${selected.ruleId}:${selected.revision}`} instanceId={instanceId} rule={selected} onSaved={() => {
        setEditRuleId(undefined); setActionMessage("Rule updated. The list is refreshing."); list.retry();
      }} onCancel={() => setEditRuleId(undefined)} />
      : canManage ? <RuleCreateForm key={instanceId} instanceId={instanceId} onCreated={ruleId => { onSelect(ruleId); list.retry(); }} /> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Alert rule list"><div className="table-card-heading"><div><h3>Rules ({items.length} loaded)</h3><p>Includes enabled and disabled rules. Select a rule to inspect its current configuration.</p></div></div><div className="table-scroll"><table><caption>Alert rules for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Rule</th><th scope="col">Condition</th><th scope="col">Updated</th></tr></thead><tbody>{items.map(item => <tr key={item.ruleId} className={item.ruleId === selected?.ruleId ? "selected-row" : ""}><td><span className="state-pill">{item.enabled ? "Enabled" : "Disabled"}</span></td><td><button className="table-link" type="button" aria-pressed={item.ruleId === selected?.ruleId} onClick={() => { onSelect(item.ruleId); setEditRuleId(undefined); }}>{item.name}</button></td><td>{item.kind === "metric_threshold" ? `${item.metricId} ${comparisonLabel(item.comparison)} ${item.threshold.toLocaleString()}` : ruleSignalLabel(item.kind, item.metricId)}</td><td>{formatTime(item.updatedAtUtc)}</td></tr>)}</tbody></table></div>{!loading && items.length === 0 && !error ? <p className="empty-state">No alert rules are configured for this server.</p> : null}<div className="pager"><span>{nextCursor ? "More rules are available." : "End of loaded rules."}</span><button className="secondary-button" type="button" disabled={!nextCursor || loadingMore} onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title={selected.name} subtitle={`Revision ${selected.revision} · ${displayName}`} actions={canManage ? <><button className="secondary-button" type="button" disabled={Boolean(busyRuleId)} onClick={() => setEditRuleId(selected.ruleId)}>Edit rule</button>{selected.enabled ? <button className="secondary-button" type="button" disabled={Boolean(busyRuleId)} onClick={() => void disable(selected)}>{busyRuleId === selected.ruleId ? "Disabling…" : "Disable rule"}</button> : null}</> : undefined}><dl className="rule-details"><div><dt>State</dt><dd>{selected.enabled ? "Enabled" : "Disabled"}</dd></div><div><dt>Kind</dt><dd>{ruleSignalLabel(selected.kind, selected.metricId)}</dd></div><div><dt>Metric</dt><dd>{selected.metricId ?? (selected.kind === "collector_health" ? "Collector availability" : selected.kind === "blocking_duration_v1" ? "Longest current blocked wait (seconds)" : selected.kind === "deadlock_count_v1" ? "Unique deadlocks in five-minute window" : "Affected databases")}</dd></div><div><dt>Condition</dt><dd>{comparisonLabel(selected.comparison)} {selected.threshold.toLocaleString()}{selected.kind === "blocking_duration_v1" ? " seconds" : selected.kind === "deadlock_count_v1" ? " deadlocks" : ""}</dd></div><div><dt>Hysteresis</dt><dd>{selected.hysteresis.toLocaleString()}{selected.kind === "blocking_duration_v1" ? " seconds" : selected.kind === "deadlock_count_v1" ? " deadlocks" : ""}</dd></div><div><dt>Fire confirmation</dt><dd>{selected.confirmationCount} observations within {selected.confirmationSeconds.toLocaleString()} seconds</dd></div><div><dt>Clear confirmation</dt><dd>{selected.kind === "backup_observed_faults_v2" ? "Disable the rule to close an alert" : `${selected.clearConfirmationCount} observations`}</dd></div><div><dt>Evaluation interval</dt><dd>{selected.evaluationSeconds.toLocaleString()} seconds</dd></div><div><dt>Last updated</dt><dd>{formatTime(selected.updatedAtUtc)}</dd></div></dl>{ruleExplanation(selected.kind) ? <p className="table-note">{ruleExplanation(selected.kind)}</p> : null}<p className="table-note">Rule identifier: {selected.ruleId}</p></DetailPane> : <div className="detail-pane empty-state">Select a rule to inspect its configuration.</div>}
    </div>
  </section>;
}

function comparisonLabel(value: AlertRuleAdminItem["comparison"]): string {
  return ({ greater_than: ">", greater_than_or_equal: "≥", less_than: "<", less_than_or_equal: "≤", equal: "=" })[value];
}
