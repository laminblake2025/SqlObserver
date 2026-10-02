import { useEffect, useRef, useState, type FormEvent } from 'react';
import { TargetManagementError, updateObservationTargetGrouping } from '../targets/targetApi';
import type { OverviewTarget } from './overviewTypes';

export type GroupingRecoveryReason = 'unknown' | 'conflict' | 'access' | 'unavailable';

export function FleetServerGroupingEditor({ target, onClose, onSaved, onReload, onAbandoned, onRecoveryRequired }: {
  readonly target: OverviewTarget;
  readonly onClose: () => void;
  readonly onSaved: () => void;
  readonly onReload: () => void;
  readonly onAbandoned: () => void;
  readonly onRecoveryRequired: (reason: GroupingRecoveryReason) => void;
}) {
  const [environment, setEnvironment] = useState(target.environment ?? '');
  const [tags, setTags] = useState(target.tags.join(', '));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [locked, setLocked] = useState(false);
  const [accessRejected, setAccessRejected] = useState(false);
  const request = useRef<AbortController | null>(null);
  const mounted = useRef(true);
  const authority = JSON.stringify([target.targetId.toLowerCase(), target.groupingRevision, target.canManageGrouping]);
  const currentAuthority = useRef(authority); currentAuthority.current = authority;
  const callbacks = useRef({ onClose, onSaved, onReload, onAbandoned, onRecoveryRequired });
  callbacks.current = { onClose, onSaved, onReload, onAbandoned, onRecoveryRequired };
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      const pending = request.current; request.current = null;
      if (pending) { pending.abort(); callbacks.current.onAbandoned(); }
    };
  }, []);
  const cancel = () => {
    const pending = request.current; request.current = null;
    if (pending) { pending.abort(); setSaving(false); callbacks.current.onAbandoned(); }
    callbacks.current.onClose();
  };
  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (request.current || locked || accessRejected || !target.canManageGrouping) return;
    const controller = new AbortController(); request.current = controller;
    const dispatchedAuthority = authority;
    const current = () => mounted.current && !controller.signal.aborted && request.current === controller
      && currentAuthority.current === dispatchedAuthority;
    setSaving(true);
    setError('');
    try {
      await updateObservationTargetGrouping(target.targetId, target.groupingRevision,
        environment.trim() || null, tags.split(',').map(tag => tag.trim()).filter(Boolean), controller.signal);
      if (!current()) return;
      request.current = null; setSaving(false); callbacks.current.onSaved();
    } catch (failure) {
      if (!current()) return;
      const status = failure instanceof TargetManagementError ? failure.status : undefined;
      const unknown = !(failure instanceof TargetManagementError) || failure.outcomeUnknown;
      const requiresRecovery = unknown || status === 409 || status === 401 || status === 403 || status === 404;
      setLocked(requiresRecovery);
      if (requiresRecovery) callbacks.current.onRecoveryRequired(status === 409 ? 'conflict'
        : status === 401 || status === 403 ? 'access' : status === 404 ? 'unavailable' : 'unknown');
      setAccessRejected(status === 401 || status === 403);
      setError(status === 409 ? 'These labels changed elsewhere. Reload server labels before another change.'
        : status === 403 ? 'Target Administrator access on this server is required to change grouping labels. Reload server labels after access is restored.'
        : status === 401 ? 'Administrative access could not be verified. Reload server labels before another change.'
        : status === 404 ? 'This server is unavailable. Reload server labels before another change.'
        : status === 400 ? 'Use up to 16 unique labels of 1–64 letters, digits, dots, underscores, colons, or hyphens.'
        : status === 429 ? 'Too many administrative requests. Wait briefly and retry.'
        : unknown ? 'The server label outcome is unknown. Reload server labels before another change.'
        : 'Check the environment and tags before saving.');
    } finally {
      if (current()) { request.current = null; setSaving(false); }
    }
  };
  const canMutate = target.canManageGrouping && !accessRejected;
  return <form aria-label="Server grouping labels" className="fleet-grouping-editor" onSubmit={event => void save(event)}>
    <div><strong>Edit labels for {target.displayName}</strong><small>Grouping labels do not change collector configuration.</small></div>
    <label>Environment<input aria-label="Server environment" value={environment} maxLength={64} disabled={saving || locked || !canMutate}
      placeholder="production" onChange={event => setEnvironment(event.target.value)} /></label>
    <label>Tags<input aria-label="Server tags" value={tags} placeholder="critical, east" disabled={saving || locked || !canMutate}
      onChange={event => setTags(event.target.value)} /></label>
    <div className="fleet-grouping-actions">{canMutate ? <button type="submit" disabled={saving || locked}>{saving ? 'Saving…' : 'Save labels'}</button> : null}
      <button type="button" className="secondary-button" onClick={cancel}>Cancel</button>
      {locked ? <button type="button" className="secondary-button" disabled={saving} onClick={() => callbacks.current.onReload()}>Reload server labels</button> : null}</div>
    {!canMutate && <p role="status">Target Administrator access on this server unlocks grouping changes.</p>}
    {saving && <p role="status">Cancellation stops waiting; a dispatched label change may still have been applied. Reload server labels before editing that server again.</p>}
    {locked && <p className="table-note">Reload discards this draft and reads the current labels. Open Edit labels again after the refreshed overview arrives.</p>}
    {error && <p role="alert">{error}</p>}
  </form>;
}
