import { useState, type FormEvent } from 'react';
import { updateObservationTargetGrouping } from '../targets/targetApi';
import type { OverviewTarget } from './overviewTypes';

export function FleetServerGroupingEditor({ target, onClose, onSaved }: {
  readonly target: OverviewTarget;
  readonly onClose: () => void;
  readonly onSaved: () => void;
}) {
  const [environment, setEnvironment] = useState(target.environment ?? '');
  const [tags, setTags] = useState(target.tags.join(', '));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (saving) return;
    setSaving(true);
    setError('');
    try {
      await updateObservationTargetGrouping(target.targetId, target.groupingRevision,
        environment.trim() || null, tags.split(',').map(tag => tag.trim()).filter(Boolean), new AbortController().signal);
      onSaved();
    } catch (failure) {
      const message = failure instanceof Error ? failure.message : '';
      setError(message.includes('409') ? 'These labels changed elsewhere. Refresh and retry.'
        : message.includes('400') ? 'Use up to 16 unique labels of 1–64 letters, digits, dots, underscores, colons, or hyphens.'
          : 'Could not save server labels. Try again.');
    } finally {
      setSaving(false);
    }
  };
  return <form className="fleet-grouping-editor" onSubmit={event => void save(event)}>
    <div><strong>Edit labels for {target.displayName}</strong><small>Grouping labels do not change collector configuration.</small></div>
    <label>Environment<input aria-label="Server environment" value={environment} maxLength={64}
      placeholder="production" onChange={event => setEnvironment(event.target.value)} /></label>
    <label>Tags<input aria-label="Server tags" value={tags} placeholder="critical, east"
      onChange={event => setTags(event.target.value)} /></label>
    <div className="fleet-grouping-actions"><button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save labels'}</button>
      <button type="button" className="secondary-button" onClick={onClose} disabled={saving}>Cancel</button></div>
    {error && <p role="alert">{error}</p>}
  </form>;
}
