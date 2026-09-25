using System.Security.Cryptography;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.SecurityTests;

public sealed class M7QueryPerformanceSecurityPolicyTests
{
    [Fact]
    public async Task ProductionProtectorIsFailClosed()
    {
        var provider = new UnavailableQuerySensitiveContentProtector();
        Assert.False(provider.IsAvailable);
        Assert.Null(await provider.ProtectAsync(Guid.NewGuid(), SensitivePayloadKind.QueryText,
            new byte[] { 1, 2, 3 }, CancellationToken.None));
        var payload = new ProtectedSensitivePayload(SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(new byte[32]), "AES-256-GCM", "missing",
            new byte[12], new byte[16], new byte[] { 1 });
        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await provider.UnprotectAsync(Guid.NewGuid(), payload, CancellationToken.None));
    }

    [Fact]
    public void QueryContentContractHasNoPlaintextMember()
    {
        Assert.DoesNotContain(typeof(ProtectedSensitivePayload).GetProperties(), property =>
            property.Name.Contains("Plain", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
    }
}
