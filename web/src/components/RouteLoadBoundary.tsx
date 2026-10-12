import { Component, type ReactNode } from "react";

export class RouteLoadBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  override render() {
    if (this.state.failed) return <section className="panel" role="alert"><h2>This workspace could not load</h2>
      <p>Reload to fetch the current application files and reopen this saved route.</p>
      <button type="button" onClick={() => location.reload()}>Reload workspace</button></section>;
    return this.props.children;
  }
}
