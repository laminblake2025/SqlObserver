import templateCatalog from "./permissionTemplates.json" with { type: "json" };

export interface PermissionScriptValues {
  readonly sqlServerMajorVersion: number;
  readonly principal: string;
  readonly operation: "Grant" | "Remove";
  readonly logicalFiles?: boolean;
  readonly queryStoreDatabase?: string;
  readonly replication?: boolean;
  readonly distributionDatabase?: string;
}
export interface PermissionTemplateCatalog {
  readonly schema: 1; readonly sourceSha256: string; readonly templatesSha256: string;
  readonly templates: Readonly<Record<string, string>>;
}
const tokens = ["__SQLOBSERVER_PRINCIPAL_LITERAL__", "__SQLOBSERVER_PRINCIPAL_IDENTIFIER__",
  "__SQLOBSERVER_DATABASE_LITERAL__", "__SQLOBSERVER_DATABASE_IDENTIFIER__", "__SQLOBSERVER_DISTRIBUTION_DATABASE__"] as const;
// These are .NET Char.IsWhiteSpace boundaries; FEFF is not whitespace in the authoritative generator.
const boundaryWhitespace = /^[\p{Zs}\u0009-\u000d\u0085\u2028\u2029]|[\p{Zs}\u0009-\u000d\u0085\u2028\u2029]$/u;
const forbidden = (value: unknown): boolean => typeof value !== "string" || value.length === 0 ||
  value.length > 128 || boundaryWhitespace.test(value) || /\p{Cc}/u.test(value) || /\p{Surrogate}/u.test(value);
const systemDatabases = new Set(["master", "model", "msdb", "tempdb"]);

export function validatePermissionScriptValues(values: PermissionScriptValues): PermissionScriptValues {
  if (!values || ![13, 14, 15, 16, 17].includes(values.sqlServerMajorVersion)) throw new Error("Choose SQL Server 2016, 2017, 2019, 2022, or 2025.");
  if (forbidden(values.principal) || !/^[^\\@]+\\[^\\]+$/u.test(values.principal) &&
      !/^[^@\\\p{Zs}\u0009-\u000d\u0085\u2028\u2029]+@[^@\\\p{Zs}\u0009-\u000d\u0085\u2028\u2029]+$/u.test(values.principal))
    throw new Error("Enter an existing Windows DOMAIN\\name or user@domain principal, trimmed and at most 128 characters without control characters.");
  if (values.operation !== "Grant" && values.operation !== "Remove") throw new Error("Choose Grant or Remove.");
  if (values.logicalFiles !== undefined && typeof values.logicalFiles !== "boolean" ||
      values.replication !== undefined && typeof values.replication !== "boolean") throw new Error("Choose explicit optional permissions.");
  if ([13, 14].includes(values.sqlServerMajorVersion) && (values.queryStoreDatabase !== undefined || values.replication))
    throw new Error("SQL Server 2016/2017 advanced collector contracts are not yet qualified.");
  if (values.operation === "Remove" && (values.logicalFiles || values.replication || values.queryStoreDatabase !== undefined || values.distributionDatabase !== undefined))
    throw new Error("Remove supports only the base capability permission. Database and metadata removal requires DBA provenance review.");
  if (values.queryStoreDatabase !== undefined && (forbidden(values.queryStoreDatabase) || systemDatabases.has(values.queryStoreDatabase.toLowerCase())))
    throw new Error("Enter a trimmed user database name, at most 128 characters without control characters; system databases are not supported.");
  if (values.replication) {
    if (forbidden(values.distributionDatabase) || !/^[A-Za-z0-9_-]+$/u.test(values.distributionDatabase!))
      throw new Error("Enter the explicit distribution database using only letters, digits, underscores, or hyphens (at most 128 characters).");
  } else if (values.distributionDatabase !== undefined) throw new Error("A distribution database requires explicit replication monitoring.");
  return values;
}
export function permissionTemplateKey(values: PermissionScriptValues): string {
  validatePermissionScriptValues(values);
  return values.operation === "Remove" ? `${values.sqlServerMajorVersion}:remove`
    : `${values.sqlServerMajorVersion}:grant:${(values.logicalFiles ? 1 : 0) | (values.queryStoreDatabase !== undefined ? 2 : 0) | (values.replication ? 4 : 0)}`;
}
function readCatalog(value: unknown): PermissionTemplateCatalog {
  const fail = (): never => { throw new Error("The authoritative permission template catalog is unavailable or invalid. Rebuild verified web assets."); };
  if (!value || typeof value !== "object" || Array.isArray(value)) return fail();
  const catalog = value as PermissionTemplateCatalog;
  if (catalog.schema !== 1 || !/^[a-f0-9]{64}$/u.test(catalog.sourceSha256) || !/^[a-f0-9]{64}$/u.test(catalog.templatesSha256) ||
      !catalog.templates || typeof catalog.templates !== "object" || Array.isArray(catalog.templates)) return fail();
  const expected = [...[13, 14].flatMap(major => [`${major}:remove`, `${major}:grant:0`, `${major}:grant:1`]), ...[15, 16, 17].flatMap(major => [`${major}:remove`, ...Array.from({ length: 8 }, (_, mask) => `${major}:grant:${mask}`)])];
  if (Object.keys(catalog.templates).length !== expected.length || expected.some(key => !Object.hasOwn(catalog.templates, key))) return fail();
  for (const key of expected) {
    const script = catalog.templates[key];
    if (typeof script !== "string" || script.length === 0 || script.length > 65536 || script.includes("\r") || !script.endsWith("\n")) return fail();
    const mask = Number(key.split(":")[2] ?? 0);
    const required: readonly string[] = [...tokens.slice(0, 2), ...(mask & 2 ? tokens.slice(2, 4) : []), ...(mask & 4 ? tokens.slice(4) : [])];
    const found: readonly string[] = script.match(/__SQLOBSERVER_[A-Za-z0-9_]+__/gu) ?? [];
    if (required.some(token => !found.includes(token)) || found.some(token => !required.includes(token))) return fail();
  }
  return catalog;
}
/** Runtime substitution never evaluates SQL or recursively interprets operator-provided text. */
export function generatePermissionScript(values: PermissionScriptValues, source: unknown = templateCatalog): string {
  const key = permissionTemplateKey(values), catalog = readCatalog(source);
  const literal = (value: string) => value.replaceAll("'", "''");
  const identifier = (value: string) => value.replaceAll("]", "]]");
  const replacements: Readonly<Record<string, string>> = {
    [tokens[0]]: literal(values.principal), [tokens[1]]: identifier(values.principal),
    [tokens[2]]: literal(values.queryStoreDatabase ?? ""), [tokens[3]]: identifier(values.queryStoreDatabase ?? ""),
    [tokens[4]]: values.distributionDatabase ?? "",
  };
  return catalog.templates[key]!.replace(/__SQLOBSERVER_[A-Za-z0-9_]+__/gu, token => replacements[token]!);
}
export function permissionScriptProvenance(): { sourceSha256: string; templatesSha256: string } {
  const catalog = readCatalog(templateCatalog);
  return { sourceSha256: catalog.sourceSha256, templatesSha256: catalog.templatesSha256 };
}
