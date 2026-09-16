using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class LiveActivityEndpoints
{
    public static IEndpointRouteBuilder MapLiveActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group=endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/activity/live").RequireAuthorization();
        group.MapGet("",async (HttpContext context,Guid instanceId,ILiveActivityQueryService service,WindowsGroupRoleResolver resolver,
            Guid? snapshot,int? databaseId,string? login,string? application,string? status,bool? blockedOnly,bool? includeIdle,bool? includeSystem,
            string? sort,bool? descending,string? cursor,CancellationToken token)=>
            await Execute(context,resolver,auth=>service.ReadAsync(auth,new LiveActivityRead(instanceId,snapshot,
                new LiveActivityFilter(databaseId,login,application,status,blockedOnly??false,includeIdle??false,includeSystem??false,sort??"cpu",descending??true),cursor),token)));
        group.MapGet("/history",async (HttpContext context,Guid instanceId,DateTimeOffset from,DateTimeOffset to,ILiveActivityQueryService service,
            WindowsGroupRoleResolver resolver,CancellationToken token)=>await Execute(context,resolver,auth=>service.HistoryAsync(auth,instanceId,from,to,token)));
        // Only opaque observation identifiers appear in this route; SQL is never accepted in a URL or request.
        group.MapGet("/query",async (HttpContext context,Guid instanceId,Guid snapshot,string identity,ILiveActivityQueryService service,
            WindowsGroupRoleResolver resolver,CancellationToken token)=>await Execute(context,resolver,auth=>service.QueryAsync(auth,instanceId,snapshot,identity,token)));
        return endpoints;
    }

    private static async Task<IResult> Execute<T>(HttpContext context,WindowsGroupRoleResolver resolver,Func<AuthorizationContext,Task<T>> action)
    {
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.Pragma="no-cache";
        try { return Results.Ok(await action(resolver.Resolve(context.User)).ConfigureAwait(false)); }
        catch(UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch(ArgumentException) { return Results.BadRequest(new { error="Invalid activity request." }); }
        catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch(Exception) { return Results.Json(new { error="Activity evidence is unavailable." },statusCode:503); }
    }
}
