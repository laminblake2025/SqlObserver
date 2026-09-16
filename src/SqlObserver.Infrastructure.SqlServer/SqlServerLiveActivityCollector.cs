using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Versioned, read-only DMV sampling. One bounded connection and deadline per target cycle.</summary>
public sealed class SqlServerLiveActivityCollector(ILiveActivityProtector protector) : ILiveActivityCollector
{
    private readonly SqlServerIntegratedConnectionFactory connections = new(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
    private static readonly string Sql = LoadSql();
    private static string LoadSql()
    {
        var assembly=typeof(SqlServerLiveActivityCollector).Assembly;
        using var resource=assembly.GetManifestResourceStream("SqlObserver.LiveActivity.sql")!;
        using var reader=new StreamReader(resource);
        string sql=reader.ReadToEnd();
        using var hashes=assembly.GetManifestResourceStream("SqlObserver.LiveActivity.sha256")!;
        using var hashReader=new StreamReader(hashes);
        if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).Equals(hashReader.ReadToEnd().Trim(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live activity collector asset mismatch.");
        return sql;
    }

    public async Task<LiveActivityCapture> CollectAsync(LiveActivityTarget target, CancellationToken cancellationToken)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection=await connections.OpenConnectionAsync(target.Connection,deadline.Token).ConfigureAwait(false);
        await using var command=new SqlCommand(Sql,connection) { CommandTimeout=5 };
        command.Parameters.AddWithValue("captureText",protector.IsAvailable);
        var rows=new List<LiveActivityRow>();
        var payloads=new Dictionary<string,LiveActivityPayload>(StringComparer.Ordinal);
        bool truncated=false;
        var observed=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using var reader=await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        while(await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
        {
            if(rows.Count==512) { truncated=true; continue; }
            string engine=Date(reader,18)!,login=Date(reader,19)!; string? start=Date(reader,20);
            int session=Convert.ToInt32(reader.GetValue(0),CultureInfo.InvariantCulture);
            int? request=reader.IsDBNull(1)?null:Convert.ToInt32(reader.GetValue(1),CultureInfo.InvariantCulture);
            rows.Add(new LiveActivityRow { Identity=Identity(engine,login,start,session,request),EngineStartup=engine,SessionLogin=login,RequestStart=start,
                SessionId=session,RequestId=request,DatabaseId=reader.IsDBNull(2)?null:Convert.ToInt32(reader.GetValue(2),CultureInfo.InvariantCulture),
                DatabaseName=Text(reader,3),Login=Text(reader,4),ClientHost=Text(reader,5),Application=Text(reader,6),Status=Text(reader,7)!,
                Command=Text(reader,8),IsUser=reader.GetBoolean(9),WaitType=Text(reader,10),Blocker=reader.IsDBNull(11)?null:Convert.ToInt32(reader.GetValue(11),CultureInfo.InvariantCulture),
                CpuMs=reader.GetInt64(12),MemoryBytes=reader.GetInt64(13),Reads=reader.GetInt64(14),Writes=reader.GetInt64(15),LogicalReads=reader.GetInt64(16),ElapsedMs=reader.GetInt64(17),
                QueryState=request is null?"not_active":protector.IsAvailable?"omitted":"protection_unavailable" });
        }
        int total=0;
        var databases=new List<LiveActivityDatabase>();
        try
        {
            if(await reader.NextResultAsync(deadline.Token).ConfigureAwait(false) && reader.FieldCount==6)
            while(await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                string identity=Identity(Date(reader,2)!,Date(reader,3)!,Date(reader,4),Convert.ToInt32(reader.GetValue(0),CultureInfo.InvariantCulture),Convert.ToInt32(reader.GetValue(1),CultureInfo.InvariantCulture));
                int index=rows.FindIndex(r=>r.Identity==identity);
                if(index<0 || reader.IsDBNull(5)) continue;
                byte[] bytes=BoundText(reader.GetString(5),out bool textTruncated);
                try
                {
                    if(bytes.Length==0) continue;
                    string hash=Convert.ToHexString(SHA256.HashData(bytes));
                    if(!payloads.TryGetValue(hash,out var payload))
                    {
                        if(total+bytes.Length>1048576) { rows[index]=rows[index] with { QueryState="budget_omitted" }; continue; }
                        var protectedText=protector.Protect(target.Id,bytes);
                        // Stable keyed identity allows cross-snapshot deduplication without plaintext fingerprints.
                        payload=new LiveActivityPayload(new Guid(protectedText.Fingerprint.ToArray().AsSpan(0,16)),protectedText);
                        payloads.Add(hash,payload); total+=bytes.Length;
                    }
                    rows[index]=rows[index] with { QueryId=payload.Id,QueryState=textTruncated?"truncated":"available" };
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if(await reader.NextResultAsync(deadline.Token).ConfigureAwait(false) && reader.FieldCount==2)
                while(await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) databases.Add(new(reader.GetInt32(0),reader.GetString(1)));
        }
        catch(Exception ex) when(ex is SqlException or OperationCanceledException or CryptographicException)
        {
            // Never log the provider exception: diagnostic text may contain a SQL statement.
            cancellationToken.ThrowIfCancellationRequested();
        }
        if(databases.Count==0) databases.AddRange(rows.Where(r=>r.DatabaseId is not null).Select(r=>new LiveActivityDatabase(r.DatabaseId!.Value,r.DatabaseName??$"Database {r.DatabaseId}")).Distinct());
        return new(Guid.NewGuid(),observed,truncated,rows,payloads.Values.ToArray(),databases);
    }

    internal static byte[] BoundText(string text,out bool truncated)
    {
        byte[] buffer=new byte[16384];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(),buffer,true,out _,out int bytes,out bool complete);
        truncated=!complete;
        return buffer.AsSpan(0,bytes).ToArray();
    }
    private static string? Text(SqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetString(ordinal);
    private static string? Date(SqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetDateTime(ordinal).ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture);
    internal static string Identity(string engine,string login,string? start,int session,int? request)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{engine}|{login}|{start}|{session}|{request}"))));
}
