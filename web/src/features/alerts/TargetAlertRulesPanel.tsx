import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane } from "../../components/DiagnosticUi";
import { getAlertRulesForAdmin, type AlertRuleAdminItem } from "./alertRuleAdminApi";
import { disableRule, RuleAttempts } from "./alertRuleAdminActions";
import { RuleCreateForm } from "./RuleCreateForm";
import { RuleEditForm } from "./RuleEditForm";

const attempts = new RuleAttempts();

export function TargetAlertRulesPanel({ instanceId, displayName, onClose, refresh, canManage }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number; readonly canManage: boolean;
}) {
  const formatTime = useTimeFormatter();
  const [items, setItems] = useState<readonly AlertRuleAdminItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string>();
  const [selectedId, setSelectedId] = useState<string>();
  const [reload, setReload] = useState(0);
  const [listingStartedUtc, setListingStartedUtc] = useState<string>();
  const moreRequest = useRef<AbortController | null>(null);
  const [busyRuleId, setBusyRuleId] = useState<string>();
  const [actionError, setActionError] = useState<string>();
  const [actionMessage, setActionMessage] = useState<string>();
  const [editRuleId, setEditRuleId] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    moreRequest.current?.abort();
    setLoading(true);
    setLoadingMore(false);
    setItems([]);
    setNextCursor(undefined);
    setListingStartedUtc(undefined);
    setError(undefined);
    void getAlertRulesForAdmin(instanceId, controller.signal)
      .then(page => { if (!controller.signal.aborted) { setItems(page.items); setNextCursor(page.nextCursor); setListingStartedUtc(page.listingStartedUtc); setLoading(false); } })
      .catch(failure => { if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : "Alert rules are unavailable."); setLoading(false); } });
    return () => { controller.abort(); moreRequest.current?.abort(); };
  }, [instanceId, refresh, reload]);

  async function loadMore() {
    if (!nextCursor || loadingMore) return;
    const controller = new AbortController();
    moreRequest.current = controller;
    setLoadingMore(true);
    try {
      const page = await getAlertRulesForAdmin(instanceId, controller.signal, nextCursor);
      if (controller.signal.aborted) return;
      if (page.listingStartedUtc !== listingStartedUtc) throw new Error("The rule listing changed. Refresh to load it again.");
      setItems(current => [...current, ...page.items.filter(item => !current.some(existing => existing.ruleId === item.ruleId))]);
      setNextCursor(page.nextCursor);
      setError(undefined);
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Alert rules are unavailable.");
    } finally { if (!controller.signal.aborted) setLoadingMore(false); }
  }

  const selected = items.find(item => item.ruleId === selectedId) ?? items[0];
  async function disable(rule: AlertRuleAdminItem) {
    if (!canManage || !rule.enabled || busyRuleId) return;
    setActionError(undefined); setActionMessage(undefined);
    const operationId = attempts.disableFor(instanceId, rule.ruleId, rule.revision);
    setBusyRuleId(rule.ruleId);
    try {
      await disableRule(instanceId, rule, operationId, new AbortController().signal);
      attempts.completeDisable(instanceId, rule.ruleId, rule.revision);
      setActionMessage("Rule disabled. The list is refreshing.");
      setReload(value => value + 1);
    } catch (failure) {
      setActionError(failure instanceof Error ? failure.message : "The rule could not be disabled.");
    } finally { setBusyRuleId(undefined); }
  }
  return <section className="alerts-screen" aria-labelledby="rules-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="rules-heading">Configured alert rules</h2><p>Review thresholds, confirmation, clear settings, and disabled rules for this server.</p></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {loading ? <p role="status" className="status-message">Loading alert rules…</p> : null}
    {error ? <div role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => setReload(value => value + 1)}>Retry</button></div> : null}
    {actionError ? <p role="alert" className="status-message">{actionError}</p> : null}
    {actionMessage ? <p role="status" className="status-message">{actionMessage}</p> : null}
    {canManage && editRuleId && selected?.ruleId === editRuleId
      ? <RuleEditForm key={`${instanceId}:${selected.ruleId}:${selected.revision}`} instanceId={instanceId} rule={selected} onSaved={() => {
        setEditRuleId(undefined); setActionMessage("Rule updated. The list is refreshing."); setReload(value => value + 1);
      }} onCancel={() => setEditRuleId(undefined)} />
      : canManage ? <RuleCreateForm key={instanceId} instanceId={instanceId} onCreated={ruleId => { setSelectedId(ruleId); setReload(value => value + 1); }} /> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Configured alert rules"><div className="table-card-heading"><div><h3>Rules ({items.length} loaded)</h3><p>Includes enabled and disabled rules. Select a rule to inspect its current configuration.</p></div></div><div className="table-scroll"><table><caption>Alert rules for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Rule</th><th scope="col">Condition</th><th scope="col">Updated</th></tr></thead><tbody>{items.map(item => <tr key={item.ruleId} className={item.ruleId === selected?.ruleId ? "selected-row" : ""}><td><span className="state-pill">{item.enabled ? "Enabled" : "Disabled"}</span></td><td><button className="table-link" type="button" onClick={() => { setSelectedId(item.ruleId); setEditRuleId(undefined); }}>{item.name}</button></td><td>{item.kind === "collector_health" ? "Collector health" : `${item.metricId} ${comparisonLabel(item.comparison)} ${item.threshold.toLocaleString()}`}</td><td>{formatTime(item.updatedAtUtc)}</td></tr>)}</tbody></table></div>{!loading && items.length === 0 && !error ? <p className="empty-state">No alert rules are configured for this server.</p> : null}<div className="pager"><span>{nextCursor ? "More rules are available." : "End of loaded rules."}</span><button className="secondary-button" type="button" disabled={!nextCursor || loadingMore} onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title={selected.name} subtitle={`Revision ${selected.revision} · ${displayName}`} actions={canManage ? <><button className="secondary-button" type="button" disabled={Boolean(busyRuleId)} onClick={() => setEditRuleId(selected.ruleId)}>Edit rule</button>{selected.enabled ? <button className="secondary-button" type="button" disabled={Boolean(busyRuleId)} onClick={() => void disable(selected)}>{busyRuleId === selected.ruleId ? "Disabling…" : "Disable rule"}</button> : null}</> : undefined}><dl className="rule-details"><div><dt>State</dt><dd>{selected.enabled ? "Enabled" : "Disabled"}</dd></div><div><dt>Kind</dt><dd>{selected.kind === "metric_threshold" ? "Metric threshold" : "Collector health"}</dd></div><div><dt>Metric</dt><dd>{selected.metricId ?? "Collector health"}</dd></div><div><dt>Condition</dt><dd>{comparisonLabel(selected.comparison)} {selected.threshold.toLocaleString()}</dd></div><div><dt>Hysteresis</dt><dd>{selected.hysteresis.toLocaleString()}</dd></div><div><dt>Fire confirmation</dt><dd>{selected.confirmationCount} observations within {selected.confirmationSeconds.toLocaleString()} seconds</dd></div><div><dt>Clear confirmation</dt><dd>{selected.clearConfirmationCount} observations</dd></div><div><dt>Evaluation interval</dt><dd>{selected.evaluationSeconds.toLocaleString()} seconds</dd></div><div><dt>Last updated</dt><dd>{formatTime(selected.updatedAtUtc)}</dd></div></dl><p className="table-note">Rule identifier: {selected.ruleId}</p></DetailPane> : <div className="detail-pane empty-state">Select a rule to inspect its configuration.</div>}
    </div>
  </section>;
}

function comparisonLabel(value: AlertRuleAdminItem["comparison"]): string {
  return ({ greater_than: ">", greater_than_or_equal: "≥", less_than: "<", less_than_or_equal: "≤", equal: "=" })[value];
}
