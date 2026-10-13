import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { generatePermissionScript, permissionScriptProvenance, type PermissionScriptValues } from "./permissionScriptModel.ts";

interface Draft {
  readonly version: string; readonly principal: string; readonly operation: "Grant" | "Remove";
  readonly logicalFiles: boolean; readonly queryStore: boolean; readonly database: string;
  readonly replication: boolean; readonly distribution: string;
}
const initial: Draft = { version: "", principal: "", operation: "Grant", logicalFiles: false,
  queryStore: false, database: "", replication: false, distribution: "" };

export function TargetPermissionScriptPanel({ targetId, displayName, host, canGenerate, onClose }: {
  readonly targetId: string; readonly displayName: string; readonly host: string;
  readonly canGenerate: boolean; readonly onClose: () => void;
}) {
  return <section className="permission-screen" aria-labelledby="permission-heading">
    <div className="screen-intro"><div><p className="eyebrow">Server administration · offline SQL</p>
      <h2 id="permission-heading">Permission script for {displayName}</h2><p>Target: {host}. Generate a reviewable SQL file for the existing Collector Windows service identity. The browser does not connect to SQL Server or execute the script.</p>
    </div><button type="button" className="secondary-button" onClick={onClose}>Close</button></div>
    {canGenerate ? <PermissionForm key={JSON.stringify([targetId, displayName, host])} targetId={targetId} />
      : <p role="status" className="permission-role-note">The Target Administrator role on this server unlocks the permission script generator.</p>}
  </section>;
}

function PermissionForm({ targetId }: { readonly targetId: string }) {
  const [draft, setDraft] = useState<Draft>(initial);
  const [output, setOutput] = useState<{ inputKey: string; sql: string }>();
  const [error, setError] = useState<string>();
  const [copyState, setCopyState] = useState<{ key: string; busy: boolean; message?: string }>();
  const inputKey = JSON.stringify(draft), identity = useRef<string | undefined>(inputKey);
  identity.current = inputKey;
  const sql = output?.inputKey === inputKey ? output.sql : undefined;
  const currentCopy = copyState?.key === inputKey ? copyState : undefined;
  const textarea = useRef<HTMLTextAreaElement>(null);
  const explanationId = useId();
  useEffect(() => { identity.current = inputKey; return () => { identity.current = undefined; }; }, []);
  const update = (change: Partial<Draft>) => {
    setDraft(previous => ({ ...previous, ...change })); setOutput(undefined); setError(undefined); setCopyState(undefined);
  };
  let provenance: ReturnType<typeof permissionScriptProvenance> | undefined;
  try { provenance = permissionScriptProvenance(); } catch { /* Generate reports an actionable catalog failure. */ }
  function generate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setOutput(undefined); setError(undefined); setCopyState(undefined);
    const values: PermissionScriptValues = { sqlServerMajorVersion: Number(draft.version), principal: draft.principal,
      operation: draft.operation, logicalFiles: draft.logicalFiles, replication: draft.replication,
      ...(draft.queryStore ? { queryStoreDatabase: draft.database } : {}),
      ...(draft.replication ? { distributionDatabase: draft.distribution } : {}) };
    try { setOutput({ inputKey, sql: generatePermissionScript(values) }); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Check the explicit permission options."); }
  }
  function selectSql() { textarea.current?.focus(); textarea.current?.select(); }
  async function copySql() {
    if (sql === undefined || currentCopy?.busy) return;
    setCopyState({ key: inputKey, busy: true });
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard unavailable");
      await navigator.clipboard.writeText(sql);
      if (identity.current === inputKey) setCopyState({ key: inputKey, busy: false, message: "SQL copied. Review the file before running it independently as an authorized DBA." });
    } catch {
      if (identity.current === inputKey) {
        selectSql(); setCopyState({ key: inputKey, busy: false, message: "Clipboard access is unavailable. SQL is selected; copy it with your keyboard, or download the file." });
      }
    }
  }
  function downloadSql() {
    if (sql === undefined) return;
    const url = URL.createObjectURL(new Blob([sql], { type: "application/sql;charset=utf-8" }));
    const link = document.createElement("a"); link.href = url;
    const safeTarget = /^[0-9a-f-]{36}$/iu.test(targetId) ? targetId.toLowerCase() : "selected-server";
    link.download = `sqlobserver-permissions-${safeTarget}-${draft.operation.toLowerCase()}.sql`;
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
    setCopyState({ key: inputKey, busy: false, message: "SQL file downloaded. Review and run it independently as an authorized DBA." });
  }
  return <>
    <form className="panel permission-form" aria-label="Offline permission script generator" onSubmit={generate}>
      <h3>Choose the intended permission plan</h3>
      <p id={explanationId}>Version and principal are explicit inputs. The registered target does not supply either value; your signed-in account is not assumed to be the collector identity. The script requires a pre-existing Windows login and checks SQL Server version, edition, platform, and absence of sysadmin membership before changing permissions.</p>
      <div className="permission-fields">
        <label>SQL Server version<select required value={draft.version} aria-describedby={explanationId} onChange={event => update({ version: event.target.value, queryStore: false, database: "", replication: false, distribution: "" })}>
          <option value="">Choose a version</option><option value="12">SQL Server 2014 (12)</option><option value="15">SQL Server 2019 (15)</option><option value="16">SQL Server 2022 (16)</option><option value="17">SQL Server 2025 (17)</option>
        </select></label>
        <label>Existing Windows principal<input required autoComplete="off" spellCheck={false} maxLength={128} value={draft.principal} placeholder="DOMAIN\collector or collector@domain" onChange={event => update({ principal: event.target.value })} /></label>
        <label>Operation<select value={draft.operation} onChange={event => update(event.target.value === "Remove"
          ? { operation: "Remove", logicalFiles: false, queryStore: false, database: "", replication: false, distribution: "" }
          : { operation: "Grant" })}><option value="Grant">Grant</option><option value="Remove">Remove</option></select></label>
      </div>
      {draft.version === "12" && <p role="status">SQL Server 2014 supports sessions, blocking, waits, engine memory/workload, database inventory, and file I/O. Query Store, query-performance history, deadlocks, host metrics, and operational collectors are unavailable. Base permissions omit msdb history grants. Native version qualification is pending.</p>}
      {draft.operation === "Grant" ? <fieldset className="permission-options"><legend>Optional access · choose explicitly</legend>
        <label className="permission-check"><input type="checkbox" checked={draft.logicalFiles} onChange={event => update({ logicalFiles: event.target.checked })} />Logical file metadata</label>
        <p>Logical file metadata adds VIEW ANY DEFINITION. Review this broader metadata visibility before applying it.</p>
        <label className="permission-check"><input type="checkbox" disabled={draft.version === "12"} checked={draft.queryStore} onChange={event => update({ queryStore: event.target.checked, database: "" })} />Query Store access for one database</label>
        {draft.queryStore && <label>Query Store database<input required autoComplete="off" spellCheck={false} maxLength={128} value={draft.database} onChange={event => update({ database: event.target.value })} /></label>}
        <p>Query Store access maps the existing login in one named online user database and grants its versioned read permission. It does not enable Query Store or alter its settings.</p>
        <label className="permission-check"><input type="checkbox" disabled={draft.version === "12"} checked={draft.replication} onChange={event => update({ replication: event.target.checked, distribution: "" })} />Replication monitoring</label>
        {draft.replication && <label>Distribution database<input required autoComplete="off" spellCheck={false} maxLength={128} value={draft.distribution} onChange={event => update({ distribution: event.target.value })} /></label>}
        <p>Replication monitoring requires the explicit distribution database and Standard or Enterprise edition. It adds read access and replmonitor membership without changing replication topology.</p>
      </fieldset> : <p className="permission-removal-note">Remove undoes only the base capability permission. It does not revoke existing msdb, database, logical-file metadata, or replication grants; those require DBA review of grant provenance.</p>}
      <p className="table-note">Base Grant includes SQL performance visibility and VIEW ANY DATABASE. SQL Server 2019 and later also receive backup/Agent history object read access in msdb. It may create a database user mapped to the pre-existing login; it never creates a login.</p>
      {error && <p role="alert">{error}</p>}
      <button className="primary" type="submit">Generate SQL</button>
    </form>
    {sql !== undefined && <section className="panel permission-output" aria-label="Generated permission script">
      <h3>Generated SQL · {draft.operation}</h3><p>Review and run independently as an authorized DBA. Output clears when any option changes. SQL Server permissions have not been inspected, applied, or removed by this page.</p>
      <div className="permission-output-actions"><button type="button" className="secondary-button" disabled={currentCopy?.busy} onClick={() => void copySql()}>{currentCopy?.busy ? "Copying SQL…" : "Copy SQL"}</button>
        <button type="button" className="secondary-button" onClick={downloadSql}>Download SQL</button><button type="button" className="secondary-button" onClick={selectSql}>Select SQL</button></div>
      {currentCopy?.message && <p role="status">{currentCopy.message}</p>}
      <textarea ref={textarea} className="permission-sql" aria-label="Generated permission SQL" readOnly spellCheck={false} value={sql} rows={18} />
      {provenance && <details className="permission-provenance"><summary>Authoritative script provenance</summary><p>Catalog generated from tools/generate-permissions.ps1; its source and content digests are checked during web builds. Runtime replacement only escapes the validated inputs.</p><dl><dt>Source SHA-256</dt><dd>{provenance.sourceSha256}</dd><dt>Template catalog SHA-256</dt><dd>{provenance.templatesSha256}</dd></dl></details>}
    </section>}
  </>;
}
