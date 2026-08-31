using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace SqlObserver.Reporting;

/// <summary>Safe, inert printable rendering for materialized report rows.</summary>
public static class ReportRenderer
{
    public static byte[] RenderHtml(ReportRun run, ReportDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<ReportRow>> sections)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(sections);
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>");
        html.Append(WebUtility.HtmlEncode(definition.Title)).Append("</title></head><body><main><h1>").Append(WebUtility.HtmlEncode(definition.Title)).Append("</h1>");
        html.Append("<p>Snapshot: ").Append(WebUtility.HtmlEncode(run.SnapshotUtc.ToString("O", CultureInfo.InvariantCulture))).Append("</p>");
        int total = 0;
        foreach (ReportSectionDefinition section in definition.Sections)
        {
            html.Append("<section><h2>").Append(WebUtility.HtmlEncode(section.Title)).Append("</h2><table><thead><tr>");
            foreach (string column in section.Columns) html.Append("<th scope=\"col\">").Append(WebUtility.HtmlEncode(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            if (sections.TryGetValue(section.Key, out IReadOnlyList<ReportRow>? rows))
            {
                foreach (ReportRow row in rows.Take(ReportContract.HtmlRows - total))
                {
                    html.Append("<tr>"); foreach (string column in section.Columns) { row.Values.TryGetValue(column, out string? value); html.Append("<td>").Append(WebUtility.HtmlEncode(value ?? string.Empty)).Append("</td>"); } html.Append("</tr>"); total++;
                    if (total >= ReportContract.HtmlRows) break;
                }
            }
            html.Append("</tbody></table></section>");
            if (total >= ReportContract.HtmlRows) break;
        }
        html.Append("</main></body></html>");
        byte[] result = Encoding.UTF8.GetBytes(html.ToString());
        if (result.Length > ReportContract.ResponseBytes) throw new ReportLimitException("The HTML report exceeds its response bound.");
        return result;
    }

    public static byte[] RenderCsv(ReportDefinition definition, ReportSectionDefinition section, IReadOnlyList<ReportRow> rows)
    {
        var csv = new StringBuilder();
        AppendCsvRow(csv, section.Columns);
        foreach (ReportRow row in rows.Take(ReportContract.TotalRows)) AppendCsvRow(csv, section.Columns.Select(column => row.Values.TryGetValue(column, out string? value) ? value : null));
        byte[] bytes = Encoding.UTF8.GetBytes(csv.ToString());
        if (bytes.Length > ReportContract.MaterializationBytes) throw new ReportLimitException("The CSV export exceeds its response bound.");
        return bytes;
    }

    private static void AppendCsvRow(StringBuilder output, IEnumerable<string?> values)
    {
        bool first = true;
        foreach (string? original in values)
        {
            if (!first) output.Append(','); first = false;
            string value = Normalize(original ?? string.Empty);
            int firstNonWhitespace = 0;
            while (firstNonWhitespace < value.Length && char.IsWhiteSpace(value[firstNonWhitespace])) firstNonWhitespace++;
            if (firstNonWhitespace < value.Length && "=+-@".Contains(value[firstNonWhitespace])) value = value.Insert(firstNonWhitespace, "'");
            output.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        output.Append("\r\n");
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormC))
            if (c is '\r' or '\n' or '\t' || !char.IsControl(c)) result.Append(c is '\r' or '\n' ? ' ' : c);
        return result.ToString();
    }
}

public sealed class ReportLimitException(string message) : Exception(message);

public sealed class ReportCursorProtector(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider)
{
    private readonly Microsoft.AspNetCore.DataProtection.ITimeLimitedDataProtector protector = (provider ?? throw new ArgumentNullException(nameof(provider))).CreateProtector("SqlObserver.Reporting.Cursor.v1").ToTimeLimitedDataProtector();

    public string Protect(ReportCursor cursor)
    {
        Validate(cursor);
        string payload = string.Join('|', cursor.RunId.ToString("N"), cursor.TargetId.ToString("N"), cursor.TargetRevision.ToString(CultureInfo.InvariantCulture), cursor.ReportKind.ToWire(), cursor.DefinitionVersion.ToString(CultureInfo.InvariantCulture), cursor.Section, cursor.AfterOrdinal.ToString(CultureInfo.InvariantCulture), cursor.SnapshotUtc.ToString("O"), cursor.ExpiresAtUtc.ToString("O"));
        TimeSpan lifetime = cursor.ExpiresAtUtc - DateTimeOffset.UtcNow; if (lifetime <= TimeSpan.Zero || lifetime > ReportContract.MaterializationRetention) throw new ArgumentException("Cursor expiry is invalid.");
        return Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(payload), cursor.ExpiresAtUtc));
    }

    public ReportCursor Unprotect(string token)
    {
        if (token is null or { Length: > 4096 }) throw new ArgumentException("Cursor is invalid.", nameof(token));
        DateTimeOffset protectedExpiry;
        byte[] payload;
        try { payload = protector.Unprotect(Convert.FromBase64String(token), out protectedExpiry); }
        catch (CryptographicException exception) when (exception.Message.Contains("expir", StringComparison.OrdinalIgnoreCase)) { throw new ReportCursorExpiredException(); }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { throw new ArgumentException("Cursor is invalid.", nameof(token), exception); }
        string[] p = Encoding.UTF8.GetString(payload).Split('|');
        if (p.Length != 9 || !Guid.TryParse(p[0], out Guid run) || !Guid.TryParse(p[1], out Guid target) || !long.TryParse(p[2], out long revision) || !ReportKindExtensions.TryParse(p[3], out ReportKind kind) || !int.TryParse(p[4], out int version) || !long.TryParse(p[6], out long after) || !DateTimeOffset.TryParse(p[7], out DateTimeOffset snapshot) || !DateTimeOffset.TryParse(p[8], out DateTimeOffset expiry)) throw new ArgumentException("Cursor is invalid.", nameof(token));
        if (expiry <= DateTimeOffset.UtcNow || protectedExpiry <= DateTimeOffset.UtcNow) throw new ReportCursorExpiredException();
        if (Math.Abs((protectedExpiry - expiry).TotalSeconds) > 2) throw new ArgumentException("Cursor expiry is invalid.", nameof(token));
        var cursor = new ReportCursor(run, target, revision, kind, version, p[5], after, snapshot, expiry);
        Validate(cursor);
        return cursor;
    }

    private static void Validate(ReportCursor cursor)
    {
        if (cursor.RunId == Guid.Empty || cursor.TargetId == Guid.Empty || cursor.TargetRevision <= 0 || cursor.DefinitionVersion != ReportContract.Version || cursor.AfterOrdinal < 0 || string.IsNullOrWhiteSpace(cursor.Section) || cursor.Section.Contains('|', StringComparison.Ordinal) || cursor.SnapshotUtc.Offset != TimeSpan.Zero || cursor.ExpiresAtUtc.Offset != TimeSpan.Zero || cursor.ExpiresAtUtc <= DateTimeOffset.UtcNow || cursor.SnapshotUtc > cursor.ExpiresAtUtc || cursor.ExpiresAtUtc - cursor.SnapshotUtc > ReportContract.MaterializationRetention || !ReportCatalog.Get(cursor.ReportKind).Sections.Any(section => section.Key == cursor.Section)) throw new ArgumentException("Cursor is invalid.", nameof(cursor));
    }
}

public sealed record ReportCursor(Guid RunId, Guid TargetId, long TargetRevision, ReportKind ReportKind, int DefinitionVersion, string Section, long AfterOrdinal, DateTimeOffset SnapshotUtc, DateTimeOffset ExpiresAtUtc);
public sealed class ReportCursorExpiredException : Exception;
