export type AlertState = "normal" | "pending" | "firing" | "acknowledged" | "resolved";
export interface ActiveAlert { readonly alertId: string; readonly ruleId: string; readonly ruleName: string; readonly state: AlertState; readonly firstObservedUtc: string; readonly firedUtc?: string; readonly acknowledgedUtc?: string; readonly value?: number | null; readonly reason?: string; readonly deliverySuppressed: boolean; }
export interface ActiveAlertPage { readonly targetId: string; readonly items: readonly ActiveAlert[]; readonly nextCursor?: string; readonly snapshotUtc?: string; }
export interface FleetAlert extends ActiveAlert { readonly targetId: string; readonly targetName: string; }
export interface FleetAlertPage { readonly items: readonly FleetAlert[]; readonly nextCursor?: string; readonly snapshotUtc?: string; }
