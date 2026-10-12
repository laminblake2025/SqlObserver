import type { CollectorHealthReason, CollectorHealthSummary } from "./healthTypes";

export interface HealthRecoveryAdvice { readonly message: string; readonly permissions: boolean; }
const recovery: Readonly<Record<Exclude<CollectorHealthReason, "none">, string>> = {
  capability_profile_missing: "Review capability discovery for this server and wait for a current discovery profile.",
  capability_profile_stale: "Review capability discovery and collector service availability so the capability profile can be refreshed.",
  capability_missing: "Review the required capability for this collector against this server's discovered profile.",
  permission_denied: "Ask a DBA to grant the collector Windows identity the required read permissions, then check the next collection result.",
  version_unsupported: "Check the discovered SQL Server version against the collector's supported versions before enabling collection.",
  platform_unsupported: "Check the discovered platform against the collector's supported platforms before enabling collection.",
  edition_unsupported: "Check whether this SQL Server edition supports the capability required by this collector.",
  never_collected: "Check collector service availability and the next due time; wait for the first successful observation.",
  timed_out: "Check server connectivity, response time and collector load, then review the next bounded collection attempt.",
  collection_failed: "Review the last outcome and collector service diagnostics; check server connectivity before the next attempt.",
  output_invalid: "Ask the collector service administrator to review collector output validation diagnostics and the deployed collector version.",
  sample_loss: "Review the loss kind and row or byte limits with the collector service administrator. Treat this evidence as incomplete until a loss-free collection succeeds.",
  circuit_open: "Review the recent failures and next due time. The collector will probe for recovery; this screen cannot reset the circuit.",
  evidence_stale: "Check collector service availability, the last outcome and the next due time; wait for fresh successful evidence.",
};

export function healthRecoveryAdvice(collector: CollectorHealthSummary): HealthRecoveryAdvice | undefined {
  if (collector.reason !== "none") return { message: recovery[collector.reason], permissions: collector.reason === "permission_denied" };
  if (collector.circuitState === "open") return { message: recovery.circuit_open, permissions: false };
  if (collector.state === "disabled") return {
    message: "Ask the collector service administrator to review whether this collector should be enabled for the server.", permissions: false,
  };
  if (collector.state === "current") return undefined;
  return { message: "Review capability discovery, the last collection outcome and the next due time before relying on this evidence.", permissions: false };
}
