import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { RuleTemplatesPage } from "../src/features/alerts/RuleTemplatesPage";
import { readRuleTemplateSelection, ruleTemplateSelectionHref } from "../src/features/alerts/ruleTemplateSelection";
import "../src/styles.css";
import "./harness.css";
function Harness() {
  const [hash, setHash] = useState(location.hash), [allowed, setAllowed] = useState(true), [mounted, setMounted] = useState(true), [tick, setTick] = useState(0);
  useEffect(() => { const change = () => setHash(location.hash); addEventListener("hashchange", change); return () => removeEventListener("hashchange", change); }, []);
  const selection = readRuleTemplateSelection(hash);
  return <main className="component-harness"><h1>Rule templates harness</h1><fieldset className="harness-controls"><legend>Context</legend><label className="harness-checkbox"><input type="checkbox" checked={allowed} onChange={event => setAllowed(event.target.checked)} />Target Administrator permission</label><button type="button" onClick={() => setTick(value => value + 1)}>Workspace tick</button><button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount templates" : "Mount templates"}</button><output aria-label="Workspace updates">{tick}</output></fieldset>
    {mounted && <RuleTemplatesPage allowed={allowed} selectedTemplateId={selection.templateId} selectedTemplateVersion={selection.templateVersion} selectedDeploymentId={selection.deploymentId} onSelectTemplate={(templateId, templateVersion) => { location.hash = ruleTemplateSelectionHref(hash, { templateId, templateVersion }); }} onSelectDeployment={deploymentId => { location.hash = ruleTemplateSelectionHref(hash, { deploymentId }); }} onBack={() => setMounted(false)} />}</main>;
}
createRoot(document.getElementById("root")!).render(<Harness />);
