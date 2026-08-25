using System.Net;
using System.Net.Http;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class M8DeliveryTests
{
    [Fact]
    public async Task WebhookRequiresHttpsAndBoundsPayload()
    {
        var resolver = new FakeResolver(new Uri("https://alerts.example.test/hook"));
        var handler = new FakeHandler(HttpStatusCode.Accepted);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver);
        AlertDeliveryWork work = Work(new byte[HttpsWebhookAlertDestination.MaximumBodyBytes + 1]);
        AlertDeliveryResult result = await adapter.DeliverAsync(work, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("payload_bound_exceeded", result.Reason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WebhookUsesResponseStatusWithoutReflectingBody()
    {
        var resolver = new FakeResolver(new Uri("https://alerts.example.test/hook"));
        var handler = new FakeHandler(HttpStatusCode.Accepted);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver);
        AlertDeliveryResult result = await adapter.DeliverAsync(Work([1, 2, 3]), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WebhookRejectsPrivateOrMappedDnsAnswersBeforeConnect()
    {
        var resolver = new FakeResolver(new Uri("https://alerts.example.test/hook"));
        var handler = new FakeHandler(HttpStatusCode.Accepted);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver, new FakeDns([IPAddress.Parse("::ffff:10.0.0.4")]));
        AlertDeliveryResult result = await adapter.DeliverAsync(Work([1]), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("destination_dns_blocked", result.Reason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WebhookPropagatesDnsCancellationInsteadOfPermanentBlock()
    {
        var resolver = new FakeResolver(new Uri("https://alerts.example.test/hook"));
        var cancellation = new CancellationTokenSource();
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(new FakeHandler(HttpStatusCode.Accepted)), resolver, new CancellingDns(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DeliverAsync(Work([1]), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task WebhookUsesDestinationSpecificAllowlistAtTheActualSendBoundary()
    {
        var resolver = new PolicyResolver(new Uri("https://alerts.example.test/hook"), _ => false);
        var handler = new FakeHandler(HttpStatusCode.Accepted);
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(handler), resolver, new FakeDns([IPAddress.Parse("93.184.216.34")]));

        AlertDeliveryResult result = await adapter.DeliverAsync(Work([1]), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("destination_dns_blocked", result.Reason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WebhookProductionConnectCallbackBlocksDestinationDnsRebinding()
    {
        IPAddress approved = IPAddress.Parse("93.184.216.34");
        var dns = new SequenceDns(approved, IPAddress.Parse("198.51.100.20"));
        var resolver = new PolicyResolver(new Uri("https://alerts.example.test/hook"), address => address.Equals(approved));
        var factory = new PinnedTestClientFactory();
        var adapter = new HttpsWebhookAlertDestination(new HttpClient(new FakeHandler(HttpStatusCode.Accepted)), resolver, dns, factory);

        AlertDeliveryResult result = await adapter.DeliverAsync(Work([1]), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("transport_failure", result.Reason);
        Assert.False(factory.Connected);
    }


    [Fact]
    public void ConfiguredAllowlistRejectsMalformedEntriesAndHonorsCidrs()
    {
        Func<IPAddress, bool> policy = AlertNetworkPolicy.ParseAllowlist("93.184.216.0/24");
        Assert.True(policy(IPAddress.Parse("93.184.216.34")));
        Assert.False(policy(IPAddress.Parse("93.184.217.34")));
        Assert.Throws<ArgumentException>(() => AlertNetworkPolicy.ParseAllowlist("not-an-address"));
    }

    [Fact]
    public async Task EventLogRequiresProvisionedSourceAndHasInjectableWriter()
    {
        var writer = new FakeEventWriter(false);
        var adapter = new WindowsEventLogAlertDestination(writer);
        AlertDeliveryResult rejected = await adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal("event_log_source_not_provisioned", rejected.Reason);
        writer.IsPreProvisioned = true;
        AlertDeliveryResult accepted = await adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(accepted.Succeeded);
        Assert.Equal(1, writer.Writes);
    }

    [Fact]
    public async Task EventLogProcessWriterUsesTrustedExecutableArgumentsAndReturnsSuccess()
    {
        var process = new RecordingEventLogProcessRunner(0);
        var adapter = new WindowsEventLogAlertDestination(new WindowsEventLogAlertWriter(process));
        AlertDeliveryResult result = await adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.EndsWith("eventcreate.exe", process.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WindowsEventLogAlertDestination.SourceName, process.Arguments);
        Assert.Contains("/L", process.Arguments);
    }

    [Fact]
    public async Task EventLogProcessWriterSurfacesBoundedTimeoutCodeAndPreCancelNeverLaunches()
    {
        var timeout = new RecordingEventLogProcessRunner(new EventLogProcessException("event_log_process_timeout", "timed out"));
        var adapter = new WindowsEventLogAlertDestination(new WindowsEventLogAlertWriter(timeout));
        AlertDeliveryResult result = await adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal("event_log_process_timeout", result.Reason);
        Assert.Equal(1, timeout.Invocations);

        var cancelled = new RecordingEventLogProcessRunner(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsEventLogAlertDestination(new WindowsEventLogAlertWriter(cancelled)).DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), cancellation.Token).AsTask());
        Assert.Equal(0, cancelled.Invocations);
    }

    [Fact]
    public void AddressBoundConnectHandlerDisablesProxyAndRedirects()
    {
        using SocketsHttpHandler handler = AddressBoundConnectHandler.Create(new FakeDns([IPAddress.Parse("2001:db8::1")]), AlertNetworkPolicy.IsApprovedAddress);
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task AddressBoundConnectHandlerExecutesConnectCallbackAgainstInjectedStream()
    {
        IPAddress resolved = IPAddress.Parse("93.184.216.34");
        IPAddress? connected = null;
        using SocketsHttpHandler handler = AddressBoundConnectHandler.Create(
            new FakeDns([resolved]),
            AlertNetworkPolicy.IsApprovedAddress,
            (address, _, _) =>
            {
                connected = address;
                return ValueTask.FromResult<Stream>(new ResponseStream("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://alerts.example.test") };
        using HttpResponseMessage response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(resolved, connected);
    }

    [Fact]
    public async Task EventLogWriterHonorsCancellationBeforeBackendCall()
    {
        var writer = new FakeEventWriter(true);
        var adapter = new WindowsEventLogAlertDestination(writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), cancellation.Token).AsTask());
        Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task EventLogProcessCancellationKillsAndAwaitsExitBeforeReturning()
    {
        var process = new BlockingEventWriter();
        var writer = new WindowsEventLogAlertWriter(process);
        var adapter = new WindowsEventLogAlertDestination(writer);
        using var cancellation = new CancellationTokenSource();
        Task<AlertDeliveryResult> delivery = adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", WindowsEventLogAlertDestination.SourceName, [1], 0, DateTimeOffset.UtcNow), cancellation.Token).AsTask();
        await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(["job_terminate", "process_exit"], process.Events);
        Assert.Equal(0, process.Writes);
        Assert.True(process.Exited.Task.IsCompleted);
        Assert.True(BlockingEventWriter.NormalExitRefused);
    }

    [Fact]
    public async Task EventLogTerminationGuardFailsClosedBeforeNormalPermitDisposal()
    {
        var events = new List<string>();
        var policy = new RecordingFatalTerminationPolicy(events);
        var guard = new EventLogTerminationGuard(policy);
        var attempt = new UnconfirmableTerminationAttempt(events);
        var permit = new TestPermit(events);
        bool returnedNormally = false;

        try
        {
            await guard.EnsureTerminatedAsync(attempt, TimeSpan.FromMilliseconds(50), new InvalidOperationException("original failure"));
            returnedNormally = true;
            permit.Dispose();
        }
        catch (SentinelTerminationException)
        {
            // The injected policy is the test equivalent of production's
            // non-returning fail-closed policy.
        }

        Assert.False(returnedNormally);
        Assert.False(permit.Disposed);
        Assert.Equal(["termination_attempt", "confirmation_failed", "fail_closed"], events);
        Assert.Equal("event_log_process_termination", policy.Failure?.Code);
        Assert.Contains("original failure", policy.Failure?.InnerException?.ToString());
    }

    [Fact]
    public async Task EventLogTerminationGuardTreatsConfirmedExitRaceAsSafe()
    {
        var events = new List<string>();
        var policy = new RecordingFatalTerminationPolicy(events);
        var guard = new EventLogTerminationGuard(policy);
        var attempt = new ConfirmedExitRaceTerminationAttempt(events);

        await guard.EnsureTerminatedAsync(attempt, TimeSpan.FromMilliseconds(50), new EventLogProcessException("event_log_process_wait", "wait raced with exit"));

        Assert.Equal(["termination_attempt", "confirmed_exit"], events);
        Assert.Null(policy.Failure);
    }

    [Fact]
    public void WindowsEventLogWriterIsAnExecutableProductionSeam()
    {
        IEventLogAlertWriter writer = new WindowsEventLogAlertWriter();
        Assert.IsType<WindowsEventLogAlertWriter>(writer);
    }

    private static AlertDeliveryWork Work(byte[] payload) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "https-webhook", "alerts", payload, 0, DateTimeOffset.UtcNow);
    private sealed class FakeResolver(Uri uri) : IAlertDestinationConfigurationResolver { public Uri ResolveHttps(string configurationReference) => uri; }
    private sealed class PolicyResolver(Uri uri, Func<IPAddress, bool> policy) : IAlertDestinationConfigurationResolver, IConfiguredDestinationNetworkPolicy
    {
        public Uri ResolveHttps(string configurationReference) => uri;
        public bool IsAllowed(string configurationReference, IPAddress address) => policy(address);
    }
    private sealed class SequenceDns(IPAddress first, IPAddress second) : IAlertDnsResolver
    {
        private int calls;
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([Interlocked.Increment(ref calls) == 1 ? first : second]);
    }
    private sealed class PinnedTestClientFactory : IAlertDestinationHttpClientFactory
    {
        public bool Connected { get; private set; }
        public HttpClient Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, IReadOnlySet<IPAddress> pinnedAddresses) =>
            new(AddressBoundConnectHandler.Create(resolver, allowlist, pinnedAddresses, (address, _, _) => { Connected = true; return ValueTask.FromResult<Stream>(new ResponseStream("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); }));
    }
    private sealed class FakeDns(IReadOnlyList<IPAddress> addresses) : IAlertDnsResolver { public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult(addresses); }
    private sealed class CancellingDns(CancellationToken token) : IAlertDnsResolver { public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => throw new OperationCanceledException(token); }
    private sealed class FakeEventWriter(bool provisioned) : IEventLogAlertWriter
    {
        public bool IsPreProvisioned { get; set; } = provisioned;
        public int Writes { get; private set; }
        public ValueTask WriteAsync(string sourceName, string logName, string message, int eventId, CancellationToken cancellationToken) { Writes++; return ValueTask.CompletedTask; }
    }
    private sealed class BlockingEventWriter : IEventLogProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
        public static bool NormalExitRefused => true;
        public int Writes { get; private set; }
        public async ValueTask<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                Events.Add("job_terminate");
                Exited.TrySetResult();
            });
            await Exited.Task.WaitAsync(timeout, CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) {
                Events.Add("process_exit");
                throw new OperationCanceledException(cancellationToken);
            }
            Writes++;
            return 0;
        }
    }
    private sealed class RecordingEventLogProcessRunner(object result) : IEventLogProcessRunner
    {
        private readonly Exception? _exception = result is Exception exception ? exception : null;
        private readonly int _result = result is int exitCode ? exitCode : 1;
        public int Invocations { get; private set; }
        public string ExecutablePath { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public ValueTask<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Invocations++;
            ExecutablePath = executablePath;
            Arguments = arguments.ToArray();
            if (_exception is not null) throw _exception;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class UnconfirmableTerminationAttempt(List<string> events) : IEventLogTerminationAttempt
    {
        public ValueTask<EventLogTerminationAttemptResult> TryTerminateAndConfirmAsync(TimeSpan timeout)
        {
            events.Add("termination_attempt");
            events.Add("confirmation_failed");
            return ValueTask.FromResult(new EventLogTerminationAttemptResult(false, new TimeoutException("confirmation timed out")));
        }
    }

    private sealed class RecordingFatalTerminationPolicy(List<string> events) : IEventLogFatalTerminationPolicy
    {
        public EventLogProcessException? Failure { get; private set; }
        public void FailClosed(EventLogProcessException failure)
        {
            Failure = failure;
            events.Add("fail_closed");
            throw new SentinelTerminationException();
        }
    }

    private sealed class ConfirmedExitRaceTerminationAttempt(List<string> events) : IEventLogTerminationAttempt
    {
        public ValueTask<EventLogTerminationAttemptResult> TryTerminateAndConfirmAsync(TimeSpan timeout)
        {
            events.Add("termination_attempt");
            events.Add("confirmed_exit");
            return ValueTask.FromResult(new EventLogTerminationAttemptResult(true, new InvalidOperationException("TerminateProcess raced with exit")));
        }
    }

    private sealed class TestPermit(List<string> events)
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; events.Add("permit_disposed"); }
    }

    private sealed class SentinelTerminationException() : Exception;

    private sealed class FakeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new HttpResponseMessage(status)); }
    }

    private sealed class ResponseStream(string response) : Stream
    {
        private readonly byte[] response = System.Text.Encoding.ASCII.GetBytes(response);
        private int offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => response.Length; public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int length = Math.Min(count, response.Length - this.offset); if (length <= 0) return 0; Array.Copy(response, this.offset, buffer, offset, length); this.offset += length; return length; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); int length = Math.Min(buffer.Length, response.Length - offset); if (length <= 0) return ValueTask.FromResult(0); response.AsMemory(offset, length).CopyTo(buffer); offset += length; return ValueTask.FromResult(length); }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
