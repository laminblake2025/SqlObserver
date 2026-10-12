export interface FileCursorIdentity { readonly run: string; readonly revision: string; readonly databaseId: number; readonly fileId: number; }
const fail = (): never => { throw new Error("File evidence changed or returned an invalid continuation. Return to the latest file snapshot to retry."); };
export function resourceFileCursor(cursor: string, target: string): FileCursorIdentity {
  if (!/^[A-Za-z0-9_-]{1,256}$/u.test(cursor)) return fail();
  let components: string[];
  try { components = atob(cursor.replaceAll("-", "+").replaceAll("_", "/")).split("\n"); }
  catch { return fail(); }
  const [kind, id, run, snapshotRevision, database, file] = components;
  if (components.length !== 6 || kind !== "f" || id !== target.toLowerCase().replaceAll("-", "")
    || !run || !/^[0-9a-f]{32}$/u.test(run) || /^0{32}$/u.test(run)
    || !snapshotRevision || !/^[1-9]\d{0,18}$/u.test(snapshotRevision) || BigInt(snapshotRevision) > 9223372036854775807n
    || !database || !file || !/^[1-9]\d{0,4}$/u.test(database) || !/^[1-9]\d{0,4}$/u.test(file)
    || Number(database) > 32767 || Number(file) > 65535) return fail();
  return { run, revision: snapshotRevision, databaseId: Number(database), fileId: Number(file) };
}
