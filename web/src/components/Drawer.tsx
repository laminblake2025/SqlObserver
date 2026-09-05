import { useEffect, useRef, type ReactNode } from "react";
export function Drawer({ children, open, onClose }: { readonly children: ReactNode; readonly open: boolean; readonly onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { if (!open) return; const previous = document.activeElement; const element = dialog.current; element?.showModal(); return () => { element?.close(); if (previous instanceof HTMLElement) previous.focus(); }; }, [open]);
  return <dialog ref={dialog} className="drawer" aria-labelledby="drawer-title" onCancel={onClose}><header className="toolbar"><h2 id="drawer-title">Add server</h2><button type="button" onClick={onClose} aria-label="Close Add server">Close</button></header>{children}</dialog>;
}
