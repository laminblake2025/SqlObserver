import type { ReactNode } from "react";
export function EvidenceTable({ rows, label }: { readonly rows: readonly Readonly<Record<string, unknown>>[]; readonly label: string }) {
  const columns = [...new Set(rows.flatMap(row => Object.keys(row)))];
  const cell = (value: unknown): ReactNode => value == null ? "Unavailable" : typeof value === "boolean" ? value ? "Yes" : "No" : typeof value === "number" || typeof value === "string" ? String(value) : <details><summary>Evidence details</summary><pre>{JSON.stringify(value, null, 2)}</pre></details>;
  const columnLabel = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('_', ' ');
  const isIdentifierColumn = (name: string) => /\b(?:id|identifier|fingerprint|digest|hash|token)\b/i.test(columnLabel(name));
  const cellClass = (name: string, value: unknown) => {
    if (value !== null && typeof value === "object") return undefined;
    const wraps = typeof value === "string" && (value.length > 32 || isIdentifierColumn(name));
    return `evidence-scalar${wraps ? " evidence-wrap" : ""}`;
  };
  return <div className="table-scroll evidence-table-scroll"><table className="evidence-table"><caption>{label} · {rows.length} loaded records</caption><thead><tr>{columns.map(name => <th scope="col" key={name}>{columnLabel(name)}</th>)}</tr></thead><tbody>{rows.map((row, index) => <tr key={index}>{columns.map(name => <td key={name} className={cellClass(name, row[name])}>{cell(row[name])}</td>)}</tr>)}</tbody></table></div>;
}
