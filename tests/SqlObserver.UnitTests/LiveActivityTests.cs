using System.Security.Cryptography;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlObserver.UnitTests;

public sealed class LiveActivityTests
{
    [Fact]
    public void ProtectedQueryRoundTripsOnlyForSameTargetAndRejectsTampering()
    {
        if(!OperatingSystem.IsWindows()) return;
        string directory=Path.Combine(Path.GetTempPath(),"SqlObserver-live-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"key.dpapi");
        byte[] key=RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllBytes(path,ProtectedData.Protect(key,null,DataProtectionScope.LocalMachine));
            var acl=new FileSecurity(); acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            using var protector=new LiveActivityProtector(path);
            Assert.True(protector.IsAvailable);
            Guid target=Guid.NewGuid(); byte[] text="select 'sensitive-example'"u8.ToArray();
            var payload=protector.Protect(target,text);
            Assert.Equal(text,protector.Unprotect(target,payload));
            Assert.DoesNotContain("sensitive-example",System.Text.Encoding.UTF8.GetString(payload.GetCiphertext()));
            Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(Guid.NewGuid(),payload));
            byte[] ciphertext=payload.GetCiphertext(); ciphertext[0]^=1;
            var changed=new ProtectedSensitivePayload(payload.Kind,payload.Fingerprint,payload.ProtectionAlgorithm,payload.KeyIdentifier,payload.GetNonce(),payload.GetAuthenticationTag(),ciphertext);
            Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(target,changed));
            Assert.Throws<CryptographicException>(()=>protector.Protect(target,new byte[16385]));
        }
        finally { CryptographicOperations.ZeroMemory(key); File.Delete(path); Directory.Delete(directory); }
    }
    [Fact]
    public void IdentitySeparatesRequestsLoginsAndEngineRestarts()
    {
        string original=SqlServerLiveActivityCollector.Identity("engine1","login1","start1",52,0);
        Assert.NotEqual(original,SqlServerLiveActivityCollector.Identity("engine2","login1","start1",52,0));
        Assert.NotEqual(original,SqlServerLiveActivityCollector.Identity("engine1","login2","start1",52,0));
        Assert.NotEqual(original,SqlServerLiveActivityCollector.Identity("engine1","login1","start2",52,0));
        Assert.NotEqual(original,SqlServerLiveActivityCollector.Identity("engine1","login1","start1",52,1));
        Assert.NotEqual(original,SqlServerLiveActivityCollector.Identity("engine1","login1",null,52,null));
    }

    [Fact]
    public void TextBoundPreservesUtf8AndMarksTruncation()
    {
        byte[] bytes=SqlServerLiveActivityCollector.BoundText(new string('\u754c',6000),out bool truncated);
        Assert.True(truncated); Assert.InRange(bytes.Length,1,16384);
        Assert.DoesNotContain('\ufffd',new System.Text.UTF8Encoding(false,true).GetString(bytes));
        Assert.Equal("select 1",System.Text.Encoding.UTF8.GetString(SqlServerLiveActivityCollector.BoundText("select 1",out truncated)));
        Assert.False(truncated);
    }

    [Fact]
    public async Task QueryTextRoleCannotBorrowAnotherTargetsPermissionOrGrantMonitoringAccess()
    {
        Guid target=Guid.NewGuid(),other=Guid.NewGuid();
        var repo=new Repository(); var service=new LiveActivityQueryService(repo,new Protector(),TimeProvider.System);
        var viewer=Auth(new RoleAuthorizationGrant(ApplicationRole.Viewer,Scope(target)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.QueryAsync(viewer,target,Guid.NewGuid(),"identity",CancellationToken.None));
        Assert.Equal(0,repo.QueryReads); Assert.Equal("denied",repo.Outcome);
        var mixed=Auth(new(ApplicationRole.Viewer,Scope(target)),new(ApplicationRole.QueryTextReader,Scope(other)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.QueryAsync(mixed,target,Guid.NewGuid(),"identity",CancellationToken.None));
        var onlyQuery=Auth(new RoleAuthorizationGrant(ApplicationRole.QueryTextReader,Scope(target)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadAsync(onlyQuery,new(target,null,new()),CancellationToken.None));
        var authorized=Auth(new(ApplicationRole.Viewer,Scope(target)),new(ApplicationRole.QueryTextReader,Scope(target)));
        Assert.Equal("unavailable",(await service.QueryAsync(authorized,target,Guid.NewGuid(),"identity",CancellationToken.None)).State);
        Assert.Equal(1,repo.QueryReads);
    }

    private static TargetAuthorizationScope Scope(Guid target)=>TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]);
    private static AuthorizationContext Auth(params RoleAuthorizationGrant[] grants)=>new(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),AuthorizationPrincipalState.Active,grants);
    private sealed class Protector : ILiveActivityProtector
    {
        public bool IsAvailable=>false;
        public ProtectedSensitivePayload Protect(Guid targetId,ReadOnlySpan<byte> text)=>throw new CryptographicException();
        public byte[] Unprotect(Guid targetId,ProtectedSensitivePayload payload)=>throw new CryptographicException();
    }
    private sealed class Repository : ILiveActivityRepository
    {
        public int QueryReads { get; private set; } public string? Outcome { get; private set; }
        public Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId,Guid snapshotId,string identity,CancellationToken cancellationToken) { QueryReads++; return Task.FromResult<ProtectedSensitivePayload?>(null); }
        public Task AuditAsync(Guid targetId,Guid snapshotId,string actor,string outcome,CancellationToken cancellationToken) { Outcome=outcome; return Task.CompletedTask; }
        public Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<LeaseAcquisitionResult> ClaimAsync(LiveActivityTarget target,WorkerExecutionId owner,WorkerLeaseDuration duration,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task CommitAsync(LiveActivityTarget target,WorkerLeaseIdentity lease,LiveActivityCapture capture,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<bool> HasDeadlockSnapshotAsync(Guid targetId,Guid eventId,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task FailedAsync(Guid targetId,WorkerLeaseIdentity lease,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<LiveActivityPage> ReadAsync(LiveActivityRead request,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId,DateTimeOffset fromUtc,DateTimeOffset toUtc,CancellationToken cancellationToken)=>throw new NotSupportedException();
    }
}
