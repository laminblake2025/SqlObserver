import type { DatabaseHealthPage } from "../features/health/healthTypes";

export interface DatabaseScopeChoice {
  readonly databaseId: number;
  readonly name: string;
}

export interface DatabaseScopeChoices {
  readonly items: readonly DatabaseScopeChoice[];
  readonly complete: boolean;
  readonly repositoryTimeUtc: string | undefined;
}

export type DatabaseScopeReader = (
  targetId: string, signal: AbortSignal, cursor?: string,
) => Promise<DatabaseHealthPage>;

export class DatabaseScopeLoadError extends Error {
  public readonly choices: DatabaseScopeChoices;
  public constructor(message: string, choices: DatabaseScopeChoices) {
    super(message);
    this.name = "DatabaseScopeLoadError";
    this.choices = choices;
  }
}

export async function loadDatabaseScopeChoices(
  targetId: string, signal: AbortSignal, readPage: DatabaseScopeReader,
): Promise<DatabaseScopeChoices> {
  const items: DatabaseScopeChoice[] = [];
  const ids = new Set<number>();
  const cursors = new Set<string>();
  let cursor: string | undefined;
  let repositoryTimeUtc: string | undefined;
  const partial = (): DatabaseScopeChoices => ({ items: [...items], complete: false, repositoryTimeUtc });
  const checkAbort = () => {
    if (signal.aborted) throw signal.reason ?? new DOMException("Database inventory request aborted.", "AbortError");
  };
  try {
    for (let pageNumber = 0; pageNumber < 20; pageNumber++) {
      checkAbort();
      const page = await readPage(targetId, signal, cursor);
      checkAbort();
      if (!page || typeof page.instanceId !== "string" || page.instanceId.toLowerCase() !== targetId.toLowerCase()) {
        throw new Error("The database inventory returned a different server.");
      }
      if (typeof page.repositoryTimeUtc !== "string" || !page.repositoryTimeUtc.endsWith("Z") ||
        !Number.isFinite(Date.parse(page.repositoryTimeUtc)) || !Array.isArray(page.items)) {
        throw new Error("The database inventory response is invalid.");
      }
      const pageIds = new Set<number>();
      const pageItems: DatabaseScopeChoice[] = [];
      for (const item of page.items) {
        if (!item || !Number.isSafeInteger(item.databaseId) || item.databaseId < 1 ||
          item.databaseId > 32767 || typeof item.name !== "string" || item.name.length === 0 || item.name.length > 128) {
          throw new Error("The database inventory contains an invalid database.");
        }
        if (ids.has(item.databaseId) || pageIds.has(item.databaseId)) {
          throw new Error("The database inventory repeated a database ID.");
        }
        pageIds.add(item.databaseId);
        pageItems.push({ databaseId: item.databaseId, name: item.name });
      }
      if (page.nextCursor !== null && (typeof page.nextCursor !== "string" || page.nextCursor.length === 0)) {
        throw new Error("The database inventory returned an invalid cursor.");
      }
      for (const item of pageItems) { ids.add(item.databaseId); items.push(item); }
      repositoryTimeUtc ??= page.repositoryTimeUtc;
      if (page.nextCursor === null) return { items, complete: true, repositoryTimeUtc };
      if (cursors.has(page.nextCursor)) throw new Error("The database inventory repeated a cursor.");
      cursors.add(page.nextCursor);
      cursor = page.nextCursor;
    }
    return partial();
  } catch (failure) {
    checkAbort();
    throw new DatabaseScopeLoadError(failure instanceof Error ? failure.message : "The database inventory could not be loaded.", partial());
  }
}
