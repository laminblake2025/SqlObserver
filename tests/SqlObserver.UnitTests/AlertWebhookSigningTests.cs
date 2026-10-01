using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class AlertWebhookSigningTests
{
    private static readonly byte[] TestKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string[] RetryTimestamps = ["1790769600", "1790769660"];
    private static Dictionary<string, string?> Configuration() => new()
    {
        ["AlertDestinations:primary"] = "https://93.184.216.34/hook",
        ["AlertDestinations:primary:Allowlist"] = "93.184.216.0/24",
        ["AlertDestinations:primary:Signing:KeyId"] = "primary-2026",
        ["AlertDestinations:primary:Signing:ProtectedKeyPath"] = "protected-key-reference",
    };
    private static ConfigurationAlertDestinationResolver Resolver(Dictionary<string, string?> values, Keys keys) =>
        new(name => values.GetValueOrDefault(name), keys);
    private static string Digest(ConfigurationAlertDestinationResolver resolver)
    {
        using AlertWebhookConfiguration configuration = resolver.ResolveWebhook("primary");
        return configuration.ConfigurationDigest(7);
    }
    private static AlertDeliveryWork Work(string digest, byte[]? payload = null) => new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.NewGuid(), Guid.NewGuid(), "https-webhook",
        "primary", payload ?? Encoding.UTF8.GetBytes("{\"message\":\"A Unicode alert: ☃\"}"), 0, DateTimeOffset.UtcNow,
        ConfigurationRevision: 7, ConfigurationDigest: digest);

    [Fact]
    public async Task ReceiverVerifiesExactWireBytesAndFreshRetryTimestampWithStableDeliveryIdentity()
    {
        var keys = new Keys(TestKey); var resolver = Resolver(Configuration(), keys);
        var handler = new Receiver(TestKey); var clock = new Clock();
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver, timeProvider: clock);
        AlertDeliveryWork work = Work(Digest(resolver));
        Assert.True((await adapter.DeliverAsync(work, CancellationToken.None)).Succeeded);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True((await adapter.DeliverAsync(work with { Attempt = 1 }, CancellationToken.None)).Succeeded);
        Assert.Equal(2, handler.Signatures.Count);
        Assert.NotEqual(handler.Signatures[0], handler.Signatures[1]);
        Assert.Equal(RetryTimestamps, handler.Timestamps);
        Assert.All(handler.Deliveries, id => Assert.Equal(work.DeliveryId.ToString("D"), id));
        Assert.All(keys.Buffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task SignedBodyIsASnapshotWhenCallerMutatesItsArrayDuringSend()
    {
        var keys = new Keys(TestKey); var resolver = Resolver(Configuration(), keys);
        byte[] payload = Encoding.UTF8.GetBytes("{\"value\":42}");
        byte[] expected = payload.ToArray();
        var receiver = new Receiver(TestKey, () => Array.Fill(payload, (byte)'x'));
        AlertDeliveryResult result = await new HttpsWebhookAlertDestination(new HttpClient(receiver), resolver)
            .DeliverAsync(Work(Digest(resolver), payload), CancellationToken.None);
        Assert.True(result.Succeeded); Assert.Equal(expected, receiver.LastBody);
    }

    [Fact]
    public async Task KeyRotationAndSigningRemovalRejectThePreviouslyApprovedOutboxBeforeSending()
    {
        Dictionary<string, string?> values = Configuration(); var keys = new Keys(TestKey);
        var resolver = Resolver(values, keys); string approved = Digest(resolver);
        keys.Value = Enumerable.Repeat((byte)83, 32).ToArray();
        Assert.NotEqual(approved, Digest(resolver));
        var handler = new Receiver(keys.Value);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver);
        AlertDeliveryResult rotated = await adapter.DeliverAsync(Work(approved), CancellationToken.None);
        Assert.Equal("destination_configuration_invalid", rotated.Reason); Assert.True(rotated.PermanentFailure);
        values.Remove("AlertDestinations:primary:Signing:KeyId"); values.Remove("AlertDestinations:primary:Signing:ProtectedKeyPath");
        AlertDeliveryResult downgraded = await adapter.DeliverAsync(Work(approved), CancellationToken.None);
        Assert.Equal("destination_configuration_invalid", downgraded.Reason); Assert.Empty(handler.Signatures);
        Assert.All(keys.Buffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData(null, "path")]
    [InlineData("id", null)]
    [InlineData("id\r\nInjected: header", "path")]
    [InlineData("", "path")]
    [InlineData("id", "")]
    public async Task PartialOrUnsafeSigningConfigurationCannotFallBackToUnsigned(string? id, string? path)
    {
        Dictionary<string, string?> values = Configuration(); var keys = new Keys(TestKey);
        var resolver = Resolver(values, keys); string approved = Digest(resolver);
        values["AlertDestinations:primary:Signing:KeyId"] = id; values["AlertDestinations:primary:Signing:ProtectedKeyPath"] = path;
        var handler = new Receiver(TestKey);
        AlertDeliveryResult result = await new HttpsWebhookAlertDestination(new HttpClient(handler), resolver)
            .DeliverAsync(Work(approved), CancellationToken.None);
        Assert.Equal("destination_configuration_invalid", result.Reason); Assert.Empty(handler.Signatures);
    }

    [Fact]
    public void InvalidKeyBufferIsErasedAndDisposedSnapshotsCannotSign()
    {
        var keys = new Keys(Enumerable.Repeat((byte)91, 31).ToArray()); var resolver = Resolver(Configuration(), keys);
        Assert.Throws<InvalidOperationException>(() => resolver.ResolveWebhook("primary"));
        Assert.All(keys.Buffers.Single(), value => Assert.Equal(0, value));
        keys.Value = TestKey;
        AlertWebhookConfiguration snapshot = resolver.ResolveWebhook("primary"); snapshot.Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Post, snapshot.Uri);
        Assert.Throws<ObjectDisposedException>(() => snapshot.Sign(request, [1], Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.All(keys.Buffers.Last(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void UnsignedConfigurationRetainsItsOriginalDigest()
    {
        Dictionary<string, string?> values = Configuration(); values.Remove("AlertDestinations:primary:Signing:KeyId");
        values.Remove("AlertDestinations:primary:Signing:ProtectedKeyPath");
        var resolver = Resolver(values, new Keys(TestKey));
        string oldCanonical = "kind=https-webhook|uri=https://93.184.216.34/hook|host=93.184.216.34|port=443|path=/hook|allowlist=93.184.216.0/24|proxy=false|redirect=false|tls=https|revision=7";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldCanonical))).ToLowerInvariant(), Digest(resolver));
    }

    [Fact]
    public async Task PreCancelledSendDoesNotLoadASigningKeyOrContactTheReceiver()
    {
        var keys = new Keys(TestKey); var receiver = new Receiver(TestKey);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(receiver), Resolver(Configuration(), keys));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DeliverAsync(Work(new string('a', 64)), cancelled.Token).AsTask());
        Assert.Empty(keys.Buffers); Assert.Empty(receiver.Signatures);
    }

    [Fact]
    public async Task InFlightCancellationErasesSigningKeysAndUnboundWorkCannotSend()
    {
        var keys = new Keys(TestKey); var resolver = Resolver(Configuration(), keys);
        string approved = Digest(resolver);
        using var cancelled = new CancellationTokenSource();
        var receiver = new Receiver(TestKey, cancelled.Cancel);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(receiver), resolver);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DeliverAsync(Work(approved), cancelled.Token).AsTask());
        Assert.All(keys.Buffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
        int sentBeforeUnboundWork = receiver.Signatures.Count;
        AlertDeliveryResult unbound = await adapter.DeliverAsync(Work(approved) with { ConfigurationRevision = null, ConfigurationDigest = null }, CancellationToken.None);
        Assert.Equal("destination_configuration_invalid", unbound.Reason);
        Assert.Equal(sentBeforeUnboundWork, receiver.Signatures.Count);
        Assert.All(keys.Buffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    private sealed class Keys(byte[] value) : IWebhookSigningKeyProvider
    {
        public byte[] Value { get; set; } = value;
        public List<byte[]> Buffers { get; } = [];
        public byte[] ReadKey(string protectedKeyPath) { byte[] copy = Value.ToArray(); Buffers.Add(copy); return copy; }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-30T12:00:00Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Receiver(byte[] key, Action? mutate = null) : HttpMessageHandler
    {
        public List<string> Signatures { get; } = []; public List<string> Deliveries { get; } = [];
        public List<string> Timestamps { get; } = [];
        public byte[]? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            mutate?.Invoke();
            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken); LastBody = body.ToArray();
            string id = request.Headers.GetValues("X-SqlObserver-Key-Id").Single();
            string timestamp = request.Headers.GetValues("X-SqlObserver-Timestamp").Single();
            string delivery = request.Headers.GetValues("X-SqlObserver-Delivery-Id").Single();
            string signature = request.Headers.GetValues("X-SqlObserver-Signature").Single();
            // Independent receiver implementation: exact prefix + untouched wire body.
            byte[] prefix = Encoding.UTF8.GetBytes($"sqlobserver.webhook.v1\nPOST\n{request.RequestUri!.AbsoluteUri}\n{id}\n{timestamp}\n{delivery}\n");
            byte[] signed = new byte[prefix.Length + body.Length]; prefix.CopyTo(signed, 0); body.CopyTo(signed, prefix.Length);
            string expected = "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, signed)).ToLowerInvariant();
            Assert.Equal(expected, signature); Signatures.Add(signature); Deliveries.Add(delivery); Timestamps.Add(timestamp);
            // Any wire-body change has a different digest.
            signed[^1] ^= 1;
            Assert.NotEqual(expected, "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, signed)).ToLowerInvariant());
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
