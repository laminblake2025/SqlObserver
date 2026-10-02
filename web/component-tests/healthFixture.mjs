import { syntheticCollector } from '../tools/health-contract-fixture.mjs';

// Native health DTOs only: bounded synthetic repository evidence, no connected data.
export const alpha = '11111111-1111-4111-8111-111111111111';
export const beta = '22222222-2222-4222-8222-222222222222';
export const at = '2026-01-15T13:00:00.000Z';
export const cursor = 'page+/=two';
export const largeCounter = '9007199254740993';

export function collector(id = 'inventory.databases', overrides = {}) {
  return { ...syntheticCollector(at, id), ...overrides };
}

export function database(databaseId = 7, name = 'Synthetic inventory', source = collector()) {
  return { databaseId, name, state: 'online', recoveryModel: 'full', userAccess: 'multi_user',
    isReadOnly: false, compatibilityLevel: 160, logReuseWaitDescription: 'nothing', observedAtUtc: at, collector: source };
}

export function logicalFile(databaseId = 7, logicalName = 'Synthetic_data', source = collector('inventory.files')) {
  return { databaseId, fileId: 1, logicalName, fileType: 'rows', state: 'online', sizeBytes: largeCounter,
    maximumSizeBytes: '9223372036854775807', growthBytes: '67108864', growthPercent: 0,
    readCount: largeCounter, writeCount: '9007199254740995', bytesRead: '9007199254740997',
    bytesWritten: '9007199254740999', ioStallMilliseconds: '9007199254741001',
    readStallMilliseconds: '9007199254740993', writeStallMilliseconds: '8', observedAtUtc: at, collector: source };
}

export function evidence(instanceId = alpha, overrides = {}) {
  const databasesCollector = collector('inventory.databases', overrides);
  const filesCollector = collector('inventory.files', overrides);
  return {
    target: { instanceId, state: overrides.state ?? 'current', repositoryTimeUtc: at,
      collectors: [databasesCollector, filesCollector], coreMetrics: [] },
    databases: { instanceId, repositoryTimeUtc: at, collector: databasesCollector, items: [], nextCursor: null },
    files: { instanceId, repositoryTimeUtc: at, collector: filesCollector, items: [], nextCursor: null },
  };
}
