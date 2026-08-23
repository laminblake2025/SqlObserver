using System.Reflection;
using System.Text.Json;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ObservationTargetApiContractTests
{
    private static readonly string[] ExpectedRegistrationProperties =
    [
        "CertificateHostName",
        "DisplayName",
        "Host",
        "InstanceId",
        "InstanceKey",
        "NamedInstance",
        "TcpPort",
    ];

    [Fact]
    public void RegistrationContractHasOnlyStructuredCredentialFreeFields()
    {
        string[] propertyNames = typeof(RegisterObservationTargetBody)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedRegistrationProperties,
            propertyNames);
        Assert.DoesNotContain(
            propertyNames,
            static name => name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegistrationRejectsUnknownCredentialShapedJson()
    {
        const string json = """
            {
              "instanceId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "instanceKey": "lab.primary",
              "displayName": "Lab primary",
              "host": "sql01.contoso.example",
              "namedInstance": null,
              "tcpPort": 1433,
              "certificateHostName": "sql01.contoso.example",
              "password": "must-not-be-accepted"
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RegisterObservationTargetBody>(json, options));
    }

    [Fact]
    public void RegistrationRequiresInstanceIdInJson()
    {
        const string json = """
            {
              "instanceKey": "lab.primary",
              "displayName": "Lab primary",
              "host": "sql01.contoso.example",
              "namedInstance": null,
              "tcpPort": 1433,
              "certificateHostName": "sql01.contoso.example"
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RegisterObservationTargetBody>(json, options));
    }

    [Fact]
    public void TargetResponseExposesFixedIdentityAndTransportModes()
    {
        var response = new ObservationTargetResponse(
            Guid.NewGuid(),
            "lab.primary",
            "Lab primary",
            "sql01.contoso.example",
            null,
            1433,
            "sql01.contoso.example",
            "windows_integrated_service_identity",
            "mandatory_validated",
            "pending_discovery",
            "pending",
            ["discovery_pending"],
            1,
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            null);

        Assert.Equal("windows_integrated_service_identity", response.AuthenticationMode);
        Assert.Equal("mandatory_validated", response.EncryptionMode);
        Assert.Equal("pending_discovery", response.Lifecycle);
        Assert.Equal("pending", response.CapabilityStatus);
        Assert.DoesNotContain(
            typeof(ObservationTargetResponse).GetProperties(),
            static property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
