import type { ReactNode } from "react";
export function EvidenceTable({ rows, label }: { readonly rows: readonly Readonly<Record<string, unknown>>[]; readonly label: string }) {
  const columns = [...new Set(rows.flatMap(row => Object.keys(row)))];
  const cell = (value: unknown): ReactNode => value == null ? "Unavailable" : typeof value === "boolean" ? value ? "Yes" : "No" : typeof value === "number" || typeof value === "string" ? String(value) : <details><summary>Evidence details</summary><pre>{JSON.stringify(value, null, 2)}</pre></details>;
  return <div className="table-scroll"><table><caption>{label} · {rows.length} loaded records</caption><thead><tr>{columns.map(name => <th scope="col" key={name}>{name.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('_',' ')}</th>)}</tr></thead><tbody>{rows.map((row, index) => <tr key={index}>{columns.map(name => <td key={name} className={row[name] === null || typeof row[name] !== "object" ? "evidence-scalar" : undefined}>{cell(row[name])}</td>)}</tr>)}</tbody></table></div>;
}
