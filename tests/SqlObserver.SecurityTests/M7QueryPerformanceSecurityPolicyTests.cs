using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.SecurityTests;

public sealed class M7QueryPerformanceSecurityPolicyTests
{
    [Fact] public async Task ProductionProtectorIsFailClosed() { var provider = new UnavailableQuerySensitiveContentProtector(); Assert.False(provider.IsAvailable); Assert.Null(await provider.ProtectAsync(SensitivePayloadKind.QueryText, new byte[] { 1,2,3 }, CancellationToken.None)); }
    [Fact] public void QueryContentContractHasNoPlaintextMember() { Assert.DoesNotContain(typeof(ProtectedSensitivePayload).GetProperties(), p => p.Name.Contains("Plain", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase)); }
}
