using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Finite civil schedules backed by the authoritative maintenance windows.</summary>
public sealed class PostgreSqlMaintenanceSeriesRepositoryPort(NpgsqlDataSource dataSource) : IMaintenanceSeriesRepositoryPort
{
    private readonly NpgsqlDataSource source=dataSource??throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(MaintenanceSeriesWriteRequest request,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request.Audit.TargetId,request.Timeout,async(connection,transaction,token)=>
        {
            await using var command=Command("SELECT * FROM alerting.write_maintenance_series(@target,@series,@revision,@cutoff,@definition,@occurrences,@reason,@operation,@actor,@correlation,@digest);",connection,transaction,request.Timeout);
            command.Parameters.AddWithValue("target",request.Audit.TargetId.Value); command.Parameters.AddWithValue("series",request.SeriesId);
            command.Parameters.Add("revision",NpgsqlDbType.Bigint).Value=(object?)request.ExpectedRevision??DBNull.Value;
            command.Parameters.Add("cutoff",NpgsqlDbType.Date).Value=(object?)request.EffectiveFromDate??DBNull.Value;
            command.Parameters.Add("definition",NpgsqlDbType.Jsonb).Value=request.Definition is null?DBNull.Value:SerializeDefinition(request.Definition);
            command.Parameters.Add("occurrences",NpgsqlDbType.Jsonb).Value=JsonSerializer.Serialize(request.Occurrences.Select(static item=>new
            {
                windowId=item.WindowId,localDate=item.LocalDate,startsAtUtc=item.StartsAtUtc,endsAtUtc=item.EndsAtUtc,
                utcOffsetMinutes=item.UtcOffsetMinutes,ambiguous=item.Ambiguous,expansionVersion=item.ExpansionVersion,
            }));
            command.Parameters.AddWithValue("reason",request.Reason);command.Parameters.AddWithValue("operation",request.IdempotencyKey);
            command.Parameters.AddWithValue("actor",request.Audit.ActorSid.Value);command.Parameters.AddWithValue("correlation",request.Audit.CorrelationId.Value);
            command.Parameters.AddWithValue("digest",request.RequestDigest);
            await using NpgsqlDataReader reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Maintenance series returned no immutable receipt.");
            return new MaintenanceSeriesWriteReceipt(new AdministrativeAuditReceipt(new AdministrativeAuditId(reader.GetGuid(0)),reader.GetFieldValue<DateTimeOffset>(1)),
                reader.GetGuid(2),reader.GetInt64(3),reader.GetFieldValue<DateTimeOffset>(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetInt32(8));
        },cancellationToken);
    }

    public ValueTask<MaintenanceSeriesStatus?> GetAsync(MonitoredInstanceId targetId,Guid seriesId,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
    {
        if(seriesId==Guid.Empty)throw new ArgumentException("Series identity is required.",nameof(seriesId));
        return RunAsync<MaintenanceSeriesStatus?>(targetId,timeout,async(connection,transaction,token)=>
        {
            await using var command=Command("SELECT * FROM alerting.get_maintenance_series(@target,@series);",connection,transaction,timeout);
            command.Parameters.AddWithValue("target",targetId.Value);command.Parameters.AddWithValue("series",seriesId);
            await using NpgsqlDataReader reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)?ReadStatus(targetId,reader):null;
        },cancellationToken);
    }

    public ValueTask<MaintenanceSeriesListPage> ListAsync(MonitoredInstanceId targetId,int limit,Guid? beforeSeriesId,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
    {
        ValidatePage(limit,beforeSeriesId);
        return RunAsync(targetId,timeout,async(connection,transaction,token)=>
        {
            DateTimeOffset now=await ReadClockAsync(connection,transaction,timeout,token).ConfigureAwait(false);
            await using var command=Command("SELECT * FROM alerting.list_maintenance_series(@target,@limit,@before);",connection,transaction,timeout);
            command.Parameters.AddWithValue("target",targetId.Value);command.Parameters.AddWithValue("limit",limit);
            command.Parameters.Add("before",NpgsqlDbType.Uuid).Value=(object?)beforeSeriesId??DBNull.Value;
            var rows=new List<MaintenanceSeriesStatus>(limit+1);
            await using NpgsqlDataReader reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await reader.ReadAsync(token).ConfigureAwait(false))rows.Add(ReadStatus(targetId,reader));
            if(rows.Count>0)now=rows[0].ServerNowUtc;
            bool more=rows.Count>limit;if(more)rows.RemoveAt(limit);
            return new MaintenanceSeriesListPage(targetId,now,rows,more?rows[^1].SeriesId:null);
        },cancellationToken);
    }

    public ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(MonitoredInstanceId targetId,Guid seriesId,int limit,Guid? beforeWindowId,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
    {
        ValidatePage(limit,beforeWindowId);if(seriesId==Guid.Empty)throw new ArgumentException("Series identity is required.",nameof(seriesId));
        return RunAsync(targetId,timeout,async(connection,transaction,token)=>
        {
            DateTimeOffset now=await ReadClockAsync(connection,transaction,timeout,token).ConfigureAwait(false);
            await using var command=Command("SELECT * FROM alerting.list_maintenance_series_occurrences(@target,@series,@limit,@before);",connection,transaction,timeout);
            command.Parameters.AddWithValue("target",targetId.Value);command.Parameters.AddWithValue("series",seriesId);command.Parameters.AddWithValue("limit",limit);
            command.Parameters.Add("before",NpgsqlDbType.Uuid).Value=(object?)beforeWindowId??DBNull.Value;
            var rows=new List<MaintenanceSeriesOccurrenceItem>(limit+1);
            await using NpgsqlDataReader reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await reader.ReadAsync(token).ConfigureAwait(false))
            {
                now=reader.GetFieldValue<DateTimeOffset>(12);
                rows.Add(new MaintenanceSeriesOccurrenceItem(reader.GetGuid(0),reader.GetFieldValue<DateOnly>(1),reader.GetFieldValue<DateTimeOffset>(2),reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4),reader.GetInt64(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),reader.GetInt64(7),reader.GetBoolean(8),reader.GetInt32(9),reader.GetBoolean(10),reader.GetString(11)));
            }
            bool more=rows.Count>limit;if(more)rows.RemoveAt(limit);
            return new MaintenanceSeriesOccurrencePage(targetId,seriesId,now,rows,more?rows[^1].WindowId:null);
        },cancellationToken);
    }

    private async ValueTask<T> RunAsync<T>(MonitoredInstanceId target,RepositoryCallTimeout timeout,
        Func<NpgsqlConnection,NpgsqlTransaction,CancellationToken,Task<T>> action,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);ArgumentNullException.ThrowIfNull(timeout);
        using CancellationTokenSource deadline=PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout,cancellationToken);
        try
        {
            await using NpgsqlConnection connection=await source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection,transaction,timeout,deadline.Token).ConfigureAwait(false);
            await using(var scope=Command("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction,timeout))
            {scope.Parameters.AddWithValue("target",target.Value.ToString("D"));await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);}
            T result=await action(connection,transaction,deadline.Token).ConfigureAwait(false);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);return result;
        }
        catch(PostgresException error)
        {
            throw error.SqlState switch
            {
                "40001" or "23505"=>new AlertRepositoryOperationException("conflict","The maintenance series or time-zone rules changed.",409,error),
                "P0002"=>new AlertRepositoryOperationException("target_not_found","The maintenance series or page anchor was not found.",404,error),
                "42501"=>new AlertRepositoryOperationException("forbidden","The maintenance series request is forbidden.",403,error),
                "22023" or "22P02" or "22007" or "22008" or "23502"=>new AlertRepositoryOperationException("invalid_request","The bounded maintenance series request is invalid.",400,error),
                "57014"=>new AlertRepositoryOperationException("request_timed_out","The maintenance repository deadline expired.",503,error),
                _=>new AlertRepositoryOperationException("repository_unavailable","The maintenance repository is unavailable.",503,error),
            };
        }
        catch(OperationCanceledException error)when(!cancellationToken.IsCancellationRequested)
        {throw PostgreSqlRuntimeSupport.CreateTimeoutException("maintenance series",error);}
    }

    private static NpgsqlCommand Command(string sql,NpgsqlConnection connection,NpgsqlTransaction transaction,RepositoryCallTimeout timeout)=>
        new(sql,connection,transaction){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout)};
    private static void ValidatePage(int limit,Guid? before)
    {if(limit is <1 or >50 || before==Guid.Empty)throw new ArgumentException("Maintenance page bounds and anchor must be valid.");}
    private static async Task<DateTimeOffset> ReadClockAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,RepositoryCallTimeout timeout,CancellationToken token)
    {
        await using var clock=Command("SELECT clock_timestamp();",connection,transaction,timeout);
        return PostgreSqlRuntimeSupport.ConvertUtcTimestamp(await clock.ExecuteScalarAsync(token).ConfigureAwait(false)??throw new InvalidDataException("Repository clock unavailable."));
    }
    private static MaintenanceSeriesStatus ReadStatus(MonitoredInstanceId target,NpgsqlDataReader reader)=>
        new(target,reader.GetGuid(0),reader.GetInt64(1),ParseDefinition(reader.GetString(2)),reader.IsDBNull(3)?null:reader.GetFieldValue<DateOnly>(3),
            reader.GetFieldValue<DateTimeOffset>(4),reader.GetFieldValue<DateTimeOffset>(5),reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7),reader.GetInt32(8));
    private static string SerializeDefinition(MaintenanceSeriesDefinition definition)=>JsonSerializer.Serialize(new
    {
        frequency=definition.Frequency.ToString().ToLowerInvariant(),interval=definition.Interval,startDate=definition.StartDate,endDate=definition.EndDate,
        localStartTime=definition.LocalStartTime.ToString("HH:mm",CultureInfo.InvariantCulture),durationMinutes=definition.DurationMinutes,timeZoneId=definition.TimeZoneId,
        weekdays=definition.Weekdays,dayOfMonth=definition.DayOfMonth,gapPolicy=definition.GapPolicy,foldPolicy=definition.FoldPolicy.ToString().ToLowerInvariant(),reason=definition.Reason,
    });
    private static MaintenanceSeriesDefinition ParseDefinition(string json)
    {
        using JsonDocument document=JsonDocument.Parse(json);JsonElement d=document.RootElement;
        return new MaintenanceSeriesDefinition(d.GetProperty("timeZoneId").GetString()!,DateOnly.ParseExact(d.GetProperty("startDate").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture),
            DateOnly.ParseExact(d.GetProperty("endDate").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture),TimeOnly.ParseExact(d.GetProperty("localStartTime").GetString()!,"HH:mm",CultureInfo.InvariantCulture),
            d.GetProperty("durationMinutes").GetInt32(),Enum.Parse<MaintenanceSeriesFrequency>(d.GetProperty("frequency").GetString()!,true),d.GetProperty("interval").GetInt32(),
            d.GetProperty("weekdays").EnumerateArray().Select(static day=>day.GetInt32()).ToArray(),d.GetProperty("dayOfMonth").ValueKind==JsonValueKind.Null?null:d.GetProperty("dayOfMonth").GetInt32(),
            d.GetProperty("gapPolicy").GetString()!,Enum.Parse<MaintenanceFoldPolicy>(d.GetProperty("foldPolicy").GetString()!,true),d.GetProperty("reason").GetString()!);
    }
}
