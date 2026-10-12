import { useCallback, useEffect, useRef, useState } from "react";

export function useResponsiveChart() {
  const [width, setWidth] = useState(650);
  const observer = useRef<ResizeObserver | undefined>(undefined);
  const ref = useCallback((element: HTMLElement | null) => {
    observer.current?.disconnect();
    if (!element) return;
    const measure = () => setWidth(Math.max(200, element.clientWidth));
    measure();
    observer.current = new ResizeObserver(measure);
    observer.current.observe(element);
  }, []);
  useEffect(() => () => observer.current?.disconnect(), []);
  return { ref, width, fontSize: Math.min(48, Math.max(13, 14 * 650 / width)) };
}
