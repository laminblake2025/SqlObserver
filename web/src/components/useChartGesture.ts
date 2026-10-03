import { useLayoutEffect, useRef, useState } from 'react';
import { chartSelection } from '../features/overview/chartTimeModel.ts';

/** A captured drag belongs to the window and series that were visible at pointer down. */
export function useChartGesture(key: string, fromUtc: string, toUtc: string,
  onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void) {
  const gesture = useRef<{ key: string; start: number; pointerId: number } | null>(null);
  const [preview, setPreview] = useState<{ key: string; start: number; end: number } | null>(null);
  useLayoutEffect(() => {
    gesture.current = null;
    setPreview(null);
    return () => { gesture.current = null; };
  }, [key]);
  const visible = preview?.key === key ? preview : null;
  const clear = () => { gesture.current = null; setPreview(null); };
  return {
    start: visible?.start ?? null, end: visible?.end ?? null,
    acceptsPointer(pointerId: number) {
      return gesture.current === null || (gesture.current.key === key && gesture.current.pointerId === pointerId);
    },
    begin(pointerId: number, position: number) {
      if (!onSelectWindow || gesture.current !== null) return false;
      gesture.current = { key, start: position, pointerId };
      setPreview({ key, start: position, end: position });
      return true;
    },
    move(pointerId: number, position: number) {
      if (gesture.current?.key === key && gesture.current.pointerId === pointerId) {
        setPreview({ key, start: gesture.current.start, end: position });
      }
    },
    finish(pointerId: number, position: number | null) {
      const current = gesture.current;
      if (!current || current.key !== key || current.pointerId !== pointerId) return;
      clear();
      if (position === null) return;
      const selected = chartSelection(current.start, position, fromUtc, toUtc);
      if (selected) onSelectWindow?.(selected);
    },
    cancel(pointerId: number) {
      if (gesture.current?.pointerId === pointerId) clear();
    },
  };
}
