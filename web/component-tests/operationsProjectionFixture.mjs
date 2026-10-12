// Migrated supporting-history fixture. Primary v2 assessments retain their own fixtures.
export {backupAvailabilityAlpha as alpha,backupAvailabilityBeta as beta,backupAvailabilityBackend,backupAvailabilityReadBinding,backupAvailabilityJson,backupAvailabilityHeld,installBackupAvailabilityFixture,backupAvailabilityReceipt} from './backupAvailabilityWindowHistoryFixture.mjs';
export const fromUtc='2026-01-15T12:00:00.123456Z',toUtc='2026-01-15T13:00:00.123456Z';
export const providerText='SECRET_PROJECTION_PROVIDER <img src="https://projection.invalid" onerror="window.projectionInjected=true">';
export function routeHash(target='11111111-1111-4111-8111-000000000001',check='availability'){return '#/operations?'+new URLSearchParams({target,range:'custom',from:fromUtc,to:toUtc,databaseId:'7',check});}
