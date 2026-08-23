export type CapabilityStatus =
  | "pending"
  | "supported"
  | "degraded"
  | "unsupported"
  | "unreachable"
  | "authentication_failed"
  | "tls_validation_failed"
  | "timed_out"
  | "security_policy_rejected"
  | "disabled"
  | "retired";

export type TargetLifecycle = "pending_discovery" | "active" | "disabled" | "retired";

export interface ObservationTargetSummary {
  readonly instanceId: string;
  readonly instanceKey: string;
  readonly displayName: string;
  readonly host: string;
  readonly namedInstance?: string;
  readonly tcpPort?: number;
  readonly certificateHostName?: string;
  readonly authenticationMode: "windows_integrated_service_identity";
  readonly encryptionMode: "mandatory_validated";
  readonly lifecycle: TargetLifecycle;
  readonly capabilityStatus: CapabilityStatus;
  readonly capabilityReasons: readonly string[];
  readonly configurationRevision: number;
  readonly discoveryRequestedAtUtc: string;
  readonly lastDiscoveryAtUtc?: string;
}

export interface ObservationTargetPage {
  readonly items: readonly ObservationTargetSummary[];
  readonly nextCursor?: string;
}

export type TargetAddress =
  | { readonly namedInstance: string; readonly tcpPort?: never }
  | { readonly namedInstance?: never; readonly tcpPort: number };

export type RegisterObservationTargetRequest = TargetAddress & {
  readonly instanceId: string;
  readonly instanceKey: string;
  readonly displayName: string;
  readonly host: string;
  readonly certificateHostName?: string;
};
