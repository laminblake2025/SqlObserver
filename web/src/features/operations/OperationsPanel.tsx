import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { Tabs } from "../../components/DiagnosticUi";
import { healthSectionHref, readRoute } from "../../dashboardModel";
import "./operationsProjection.css";
import type { OverviewScope } from "../overview/overviewTypes";
import { overviewWindow } from "../overview/overviewModel";
import { AvailabilityEvidencePanel } from "./AvailabilityEvidencePanel";
import type { AvailabilityEvidenceAccessOwner } from "./useAvailabilityEvidence";
import { BackupAssessmentPanel } from "./BackupAssessmentPanel";
import { AgentFailureHistoryPanel } from "./AgentFailureHistoryPanel";
import { healthCheckHref, type HealthCheckTab } from "./agentHistoryModel.ts";
import { useTempDbWindowHistory } from "./useTempDbWindowHistory";
import { TempDbWindowHistoryPanel } from "./TempDbWindowHistoryPanel";
import { tempDbWindow, tempDbContextIdentity, type TempDbSelection } from "./tempDbWindowHistoryModel";
import { useReplicationWindowHistory } from "./useReplicationWindowHistory";
import { ReplicationWindowHistoryPanel } from "./ReplicationWindowHistoryPanel";
import { replicationWindow, replicationContextIdentity, type ReplicationSelection } from "./replicationWindowHistoryModel";
import { retainReplicationSelectionHref } from "./replicationSelectionModel";
import { useBackupAvailabilityWindowHistory } from "./useBackupAvailabilityWindowHistory";
import { BackupAvailabilityWindowHistoryPanel } from "./BackupAvailabilityWindowHistoryPanel";
import { backupAvailabilityWindow, backupAvailabilityContextIdentity, type BackupAvailabilitySelection } from "./backupAvailabilityWindowHistoryModel";
import { retainBackupAvailabilitySelectionHref } from "./backupAvailabilitySelectionModel";
import { useAgentActivityHistory } from "./useAgentActivityHistory";
import { AgentActivityHistoryPanel } from "./AgentActivityHistoryPanel";
import { agentActivityWindow, type AgentActivitySelection } from "./agentActivityHistoryModel";
import { retainAgentActivitySelectionHref } from "./agentActivitySelectionModel";

type OperationsTab = HealthCheckTab;

const tabs: readonly { readonly value: OperationsTab; readonly label: string }[] = [
  { value: "backups", label: "Backups" },
  { value: "agent", label: "SQL Agent" },
  { value: "tempdb", label: "TempDB" },
  { value: "availability", label: "Availability groups" },
  { value: "replication", label: "Replication" },
];

interface OperationsWindow { readonly fromUtc: string; readonly toUtc: string }
export interface OperationsPanelProps {
  readonly instanceId: string;
  readonly targetRevision: number;
  readonly initialTab?: OperationsTab;
  readonly refresh: number;
  readonly scope: OverviewScope;
  readonly canManageBackupPolicies?: boolean;
  readonly selectedTempDb?: TempDbSelection | null;
  readonly onSelectTempDb?: (selection: TempDbSelection | undefined, window?: OperationsWindow) => void;
  readonly onRetainTempDbWindow?: (window: OperationsWindow, selection?: TempDbSelection) => void;
  readonly selectedReplication?: ReplicationSelection | null;
  readonly onSelectReplication?: (selection: ReplicationSelection | undefined, window?: OperationsWindow) => void;
  readonly onRetainReplicationWindow?: (window: OperationsWindow, selection?: ReplicationSelection) => void;
  readonly selectedAgentActivity?: AgentActivitySelection | null;
  readonly onSelectAgentActivity?: (selection: AgentActivitySelection | undefined, window?: OperationsWindow) => void;
  readonly onRetainAgentActivityWindow?: (window: OperationsWindow, selection?: AgentActivitySelection) => void;
  readonly selectedBackupHistory?: BackupAvailabilitySelection | null;
  readonly selectedAvailabilityHistory?: BackupAvailabilitySelection | null;
  readonly onSelectBackupHistory?: (selection: BackupAvailabilitySelection | undefined, window?: OperationsWindow) => void;
  readonly onSelectAvailabilityHistory?: (selection: BackupAvailabilitySelection | undefined, window?: OperationsWindow) => void;
  readonly onRetainBackupHistoryWindow?: (window: OperationsWindow, selection?: BackupAvailabilitySelection) => void;
  readonly onRetainAvailabilityHistoryWindow?: (window: OperationsWindow, selection?: BackupAvailabilitySelection) => void;
  readonly accessContext?: string;
}

export function OperationsPanel({
  instanceId, targetRevision, initialTab = "backups", refresh, scope, canManageBackupPolicies = false,
  selectedTempDb, onSelectTempDb, onRetainTempDbWindow,
  selectedReplication, onSelectReplication, onRetainReplicationWindow,
  selectedAgentActivity, onSelectAgentActivity, onRetainAgentActivityWindow,
  selectedBackupHistory, selectedAvailabilityHistory, onSelectBackupHistory, onSelectAvailabilityHistory,
  onRetainBackupHistoryWindow, onRetainAvailabilityHistoryWindow, accessContext = "",
}: OperationsPanelProps) {
  const renderedHash = location.hash;
  const primaryAgAuthority = JSON.stringify([instanceId.toLowerCase(), targetRevision, accessContext]);
  const primaryAgAccess = useRef<{ authority: string; owner: AvailabilityEvidenceAccessOwner }>({ authority: primaryAgAuthority, owner: {} });
  if (primaryAgAccess.current.authority !== primaryAgAuthority) primaryAgAccess.current = { authority: primaryAgAuthority, owner: {} };
  const tabKey = JSON.stringify([instanceId.toLowerCase(), initialTab]);
  const [selectedTab, setSelectedTab] = useState<{ key: string; value: OperationsTab }>();
  const tab = selectedTab?.key === tabKey ? selectedTab.value : initialTab;
  const section = useRef<HTMLElement | null>(null);
  useLayoutEffect(() => {
    const scroller = section.current?.querySelector<HTMLDivElement>(':scope > [role="tablist"]');
    if (!scroller) return;
    function revealSelected() {
      const selected = scroller!.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
      if (!selected || scroller!.scrollWidth <= scroller!.clientWidth) return;
      const viewport = scroller!.getBoundingClientRect(), bounds = selected.getBoundingClientRect();
      const scale = viewport.width / scroller!.offsetWidth;
      if (!Number.isFinite(scale) || scale <= 0) return;
      const left = viewport.left + scroller!.clientLeft * scale, right = left + scroller!.clientWidth * scale;
      // Reveal locally without moving focus or the page's vertical position.
      if (bounds.left < left) scroller!.scrollLeft -= Math.ceil((left - bounds.left) / scale);
      else if (bounds.right > right) scroller!.scrollLeft += Math.ceil((bounds.right - right) / scale);
    }
    revealSelected();
    let width = scroller.clientWidth;
    const observer = new ResizeObserver(() => {
      const nextWidth = scroller.clientWidth;
      if (nextWidth === width) return;
      width = nextWidth;
      revealSelected();
    });
    observer.observe(scroller);
    return () => observer.disconnect();
  }, [tab]);
  const tempDbScopeKey = scope.range === "custom" ? tempDbContextIdentity({targetId:instanceId,targetRevision,fromUtc:scope.from ?? "",toUtc:scope.to ?? ""}) : JSON.stringify([instanceId.toLowerCase(),targetRevision,scope.range]);
  const retainedTempDbWindow = useRef<{key:string;window:{fromUtc:string;toUtc:string}} | undefined>(undefined);
  const previousTempDbScope = useRef(tempDbScopeKey);
  if (previousTempDbScope.current !== tempDbScopeKey) { previousTempDbScope.current = tempDbScopeKey; retainedTempDbWindow.current = undefined; }
  const tempDbContext = useMemo(() => {
    try { const window = retainedTempDbWindow.current?.key === tempDbScopeKey ? retainedTempDbWindow.current.window : overviewWindow(scope, Date.now()); const bounded = tempDbWindow(window.fromUtc, window.toUtc); if (selectedTempDb !== undefined) retainedTempDbWindow.current = {key:tempDbScopeKey,window:bounded}; return { binding: { targetId: instanceId, targetRevision, ...bounded }, error: undefined }; }
    catch (failure) { return { binding: undefined, error: failure instanceof Error ? failure.message : "Choose a valid TempDB history window." }; }
  }, [instanceId, targetRevision, tempDbScopeKey, refresh, selectedTempDb]);
  const tempDbHistory = useTempDbWindowHistory(tab === "tempdb" ? tempDbContext.binding : undefined, selectedTempDb, refresh);
  const receivedTempDbWindow = tempDbHistory.list.pages[0] ?? tempDbHistory.detail.pages[0] ?? tempDbContext.binding;
  const retainTempDbWindow = () => { if (receivedTempDbWindow) { tempDbHistory.retain(); retainedTempDbWindow.current = {key:tempDbScopeKey,window:receivedTempDbWindow}; onRetainTempDbWindow?.(receivedTempDbWindow, tempDbHistory.selected ?? undefined); } };
  const replicationScopeKey = scope.range === "custom" ? replicationContextIdentity({targetId:instanceId,targetRevision,fromUtc:scope.from ?? "",toUtc:scope.to ?? ""}) : JSON.stringify([instanceId.toLowerCase(),targetRevision,scope.range]);
  const retainedReplicationWindow = useRef<{key:string;window:{fromUtc:string;toUtc:string}} | undefined>(undefined);
  const previousReplicationScope = useRef(replicationScopeKey);
  if (previousReplicationScope.current !== replicationScopeKey) { previousReplicationScope.current = replicationScopeKey; retainedReplicationWindow.current = undefined; }
  const replicationContext = useMemo(() => {
    try { const window = retainedReplicationWindow.current?.key === replicationScopeKey ? retainedReplicationWindow.current.window : overviewWindow(scope, Date.now()); const bounded = replicationWindow(window.fromUtc, window.toUtc); if (selectedReplication !== undefined) retainedReplicationWindow.current = {key:replicationScopeKey,window:bounded}; return { binding: { targetId: instanceId, targetRevision, ...bounded }, error: undefined }; }
    catch (failure) { return { binding: undefined, error: failure instanceof Error ? failure.message : "Choose a valid replication history window." }; }
  }, [instanceId, targetRevision, replicationScopeKey, refresh, selectedReplication]);
  const replicationHistory = useReplicationWindowHistory(tab === "replication" ? replicationContext.binding : undefined, selectedReplication, refresh, accessContext);
  const receivedReplicationWindow = replicationHistory.list.pages[0] ?? replicationHistory.detail.pages[0] ?? replicationContext.binding;
  const retainReplicationWindow = () => { if (receivedReplicationWindow) { replicationHistory.retain(); retainedReplicationWindow.current = {key:replicationScopeKey,window:receivedReplicationWindow}; onRetainReplicationWindow?.(receivedReplicationWindow, replicationHistory.selected ?? undefined); } };
  const agentActivityScopeKey = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range,
    scope.range === "custom" ? scope.from ?? "" : "", scope.range === "custom" ? scope.to ?? "" : ""]);
  const retainedAgentActivityWindow = useRef<{ key: string; window: OperationsWindow } | undefined>(undefined);
  const previousAgentActivityScope = useRef(agentActivityScopeKey);
  if (previousAgentActivityScope.current !== agentActivityScopeKey) {
    previousAgentActivityScope.current = agentActivityScopeKey;
    retainedAgentActivityWindow.current = undefined;
  }
  const agentActivityContext = useMemo(() => {
    try {
      const window = retainedAgentActivityWindow.current?.key === agentActivityScopeKey
        ? retainedAgentActivityWindow.current.window : overviewWindow(scope, Date.now());
      const bounded = agentActivityWindow(window.fromUtc, window.toUtc);
      if (selectedAgentActivity !== undefined) retainedAgentActivityWindow.current = { key: agentActivityScopeKey, window: bounded };
      return { binding: { targetId: instanceId, targetRevision, ...bounded }, error: undefined };
    } catch (failure) {
      return { binding: undefined, error: failure instanceof Error ? failure.message : "Choose a valid Agent observation window." };
    }
  }, [instanceId, targetRevision, agentActivityScopeKey, refresh, selectedAgentActivity]);
  const agentActivityHistory = useAgentActivityHistory(tab === "agent" ? agentActivityContext.binding : undefined,
    selectedAgentActivity, refresh, accessContext);
  const receivedAgentActivityWindow = agentActivityHistory.list.pages[0] ?? agentActivityHistory.detail.pages[0] ?? agentActivityContext.binding;
  const retainAgentActivityWindow = () => {
    if (receivedAgentActivityWindow) {
      agentActivityHistory.retain();
      retainedAgentActivityWindow.current = { key: agentActivityScopeKey, window: receivedAgentActivityWindow };
      onRetainAgentActivityWindow?.(receivedAgentActivityWindow, agentActivityHistory.selected ?? undefined);
    }
  };
  const earlierScopeKey = scope.range === "custom" ? backupAvailabilityContextIdentity({ targetId: instanceId, targetRevision, fromUtc: scope.from ?? "", toUtc: scope.to ?? "" }) : JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range]);
  const retainedBackupWindow = useRef<{key:string;window:{fromUtc:string;toUtc:string}} | undefined>(undefined);
  const retainedAvailabilityWindow = useRef<{key:string;window:{fromUtc:string;toUtc:string}} | undefined>(undefined);
  const previousEarlierScope = useRef(earlierScopeKey);
  if (previousEarlierScope.current !== earlierScopeKey) { previousEarlierScope.current = earlierScopeKey; retainedBackupWindow.current = undefined; retainedAvailabilityWindow.current = undefined; }
  function resolveEarlierWindow(selected: BackupAvailabilitySelection | null | undefined, retained: typeof retainedBackupWindow) {
    try {
      const window = retained.current?.key === earlierScopeKey ? retained.current.window : overviewWindow(scope, Date.now()), bounded = backupAvailabilityWindow(window.fromUtc, window.toUtc);
      if (selected !== undefined) retained.current = { key: earlierScopeKey, window: bounded };
      return { binding: { targetId: instanceId, targetRevision, ...bounded }, error: undefined };
    } catch (failure) { return { binding: undefined, error: failure instanceof Error ? failure.message : "Choose a valid earlier collection history window." }; }
  }
  const backupContext = useMemo(() => resolveEarlierWindow(selectedBackupHistory, retainedBackupWindow), [instanceId, targetRevision, earlierScopeKey, refresh, selectedBackupHistory]);
  const availabilityContext = useMemo(() => resolveEarlierWindow(selectedAvailabilityHistory, retainedAvailabilityWindow), [instanceId, targetRevision, earlierScopeKey, refresh, selectedAvailabilityHistory]);
  const backupHistory = useBackupAvailabilityWindowHistory(tab === "backups" ? backupContext.binding : undefined, "backup", selectedBackupHistory, refresh, accessContext);
  const availabilityHistory = useBackupAvailabilityWindowHistory(tab === "availability" ? availabilityContext.binding : undefined, "availability", selectedAvailabilityHistory, refresh, accessContext);
  const receivedBackupWindow = backupHistory.list.pages[0] ?? backupHistory.backup.pages[0] ?? backupContext.binding;
  const receivedAvailabilityWindow = availabilityHistory.list.pages[0] ?? availabilityHistory.replicas.pages[0] ?? availabilityHistory.databases.pages[0] ?? availabilityContext.binding;
  function retainBackupWindow() { if (receivedBackupWindow) { backupHistory.retain(); retainedBackupWindow.current = { key: earlierScopeKey, window: receivedBackupWindow }; onRetainBackupHistoryWindow?.(receivedBackupWindow, backupHistory.selected ?? undefined); } }
  function retainAvailabilityWindow() { if (receivedAvailabilityWindow) { availabilityHistory.retain(); retainedAvailabilityWindow.current = { key: earlierScopeKey, window: receivedAvailabilityWindow }; onRetainAvailabilityHistoryWindow?.(receivedAvailabilityWindow, availabilityHistory.selected ?? undefined); } }
  return <section ref={section} className="operations-screen" aria-label="Health checks">
    <div className="screen-intro"><div><p className="eyebrow">Database operations</p><h2>Health checks</h2><p>Investigate backup freshness, job failures, TempDB usage evidence, availability, and replication from collected evidence.</p></div></div>
    <Tabs label="Health check categories" tabs={tabs} value={tab} revealFocusedTab onChange={(value) => {
      const currentHash = location.hash, currentRoute = readRoute(currentHash);
      if (currentHash !== renderedHash || currentRoute.page !== "operations"
        || currentRoute.target.toLowerCase() !== instanceId.toLowerCase()) return;
      setSelectedTab({ key: tabKey, value: value as OperationsTab });
      location.hash = retainAgentActivitySelectionHref(retainBackupAvailabilitySelectionHref(retainReplicationSelectionHref(
        healthCheckHref(scope, instanceId, value as OperationsTab), currentHash), currentHash), currentHash);
    }} />
    <nav className="toolbar" aria-label="Additional health checks">
      <a className="secondary-button" href={healthSectionHref(scope, instanceId, "summary")}>Configuration drift</a>
      <a className="secondary-button" href={healthSectionHref(scope, instanceId, "databases")}>Database options</a>
    </nav>
    <p className="table-note">Configuration drift and database options open their existing Server summary sections with this workspace time and database context. Change history follows the selected window when supported; current settings and inventory show their latest source snapshots. Server configuration remains server-wide.</p>
    {scope.range === "custom" && <p className="table-note">Rewind selects completion-window TempDB, replication, Agent activity observations and supporting legacy backup/AG receipts. Primary backup assessments and per-replica v2 AG evidence use the declared window end as their assessment/publication cutoff; their start does not filter snapshot rows. SQL Agent failure history uses first-observed time.</p>}
    {tab === "agent" ? <><AgentFailureHistoryPanel instanceId={instanceId} targetRevision={targetRevision} scope={scope} refresh={refresh} />
      {agentActivityContext.error && <p role="alert">{agentActivityContext.error}</p>}
      <AgentActivityHistoryPanel history={agentActivityHistory} selected={selectedAgentActivity}
        onSelectReceipt={selection => onSelectAgentActivity?.(selection, receivedAgentActivityWindow)}
        onRetainWindow={retainAgentActivityWindow} /></>
      : tab === "availability" ? <><AvailabilityEvidencePanel instanceId={instanceId} targetRevision={targetRevision} accessContext={accessContext} accessOwner={primaryAgAccess.current.owner} scope={scope} refresh={refresh} />
      {availabilityContext.error && <p role="alert">{availabilityContext.error}</p>}<BackupAvailabilityWindowHistoryPanel kind="availability" history={availabilityHistory} selected={selectedAvailabilityHistory} onSelectReceipt={selection => onSelectAvailabilityHistory?.(selection, receivedAvailabilityWindow)} onRetainWindow={retainAvailabilityWindow} />
      </> : tab === "backups" ? <><BackupAssessmentPanel instanceId={instanceId} scope={scope} refresh={refresh} canManagePolicies={canManageBackupPolicies} />
        {backupContext.error && <p role="alert">{backupContext.error}</p>}<BackupAvailabilityWindowHistoryPanel kind="backup" history={backupHistory} selected={selectedBackupHistory} onSelectReceipt={selection => onSelectBackupHistory?.(selection, receivedBackupWindow)} onRetainWindow={retainBackupWindow} />
        </> : tab === "replication" ? <>{replicationContext.error && <p role="alert">{replicationContext.error}</p>}<ReplicationWindowHistoryPanel history={replicationHistory} selected={selectedReplication} onSelectReceipt={selection => onSelectReplication?.(selection, receivedReplicationWindow)} onRetainWindow={retainReplicationWindow} /></>
        : <>{tempDbContext.error && <p role="alert">{tempDbContext.error}</p>}<TempDbWindowHistoryPanel history={tempDbHistory} selected={selectedTempDb} onSelectReceipt={selection => onSelectTempDb?.(selection, receivedTempDbWindow)} onRetainWindow={retainTempDbWindow} /></>}
  </section>;
}
