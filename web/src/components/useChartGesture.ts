import { useEffect, useRef, useState } from 'react';
import { chartSelection } from '../features/overview/chartTimeModel.ts';

/** A captured drag belongs to the window and series that were visible at pointer down. */
export function useChartGesture(key: string, fromUtc: string, toUtc: string,
  onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void) {
  const gesture = useRef<{ key: string; start: number; pointerId: number } | null>(null);
  const [preview, setPreview] = useState<{ key: string; start: number; end: number } | null>(null);
  useEffect(() => {
    if (gesture.current?.key !== key) gesture.current = null;
    return () => { gesture.current = null; };
  }, [key]);
  const visible = preview?.key === key ? preview : null;
  const cancel = () => { gesture.current = null; setPreview(null); };
  return {
    start: visible?.start ?? null, end: visible?.end ?? null,
    begin(pointerId: number, position: number) {
      if (!onSelectWindow) return;
      gesture.current = { key, start: position, pointerId };
      setPreview({ key, start: position, end: position });
    },
    move(position: number) {
      if (gesture.current?.key === key) setPreview({ key, start: gesture.current.start, end: position });
    },
    finish(pointerId: number, position: number | null) {
      const current = gesture.current;
      cancel();
      if (!current || current.key !== key || current.pointerId !== pointerId || position === null) return;
      const selected = chartSelection(current.start, position, fromUtc, toUtc);
      if (selected) onSelectWindow?.(selected);
    },
    cancel,
  };
}
