using System.Reflection;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class ProtectedRepositoryContractTests
{
    private static readonly string[] ForbiddenPublicNameFragments =
    [
        "plaintext",
        "cleartext",
        "connectionstring",
        "password",
        "credential",
    ];

    [Fact]
    public void ProtectedContractsExposeNoPlaintextOrConnectionStringMember()
    {
        Type[] protectedTypes =
        [
            typeof(ProtectedSensitivePayload),
            typeof(SensitivePayloadReference),
            typeof(SensitivePayloadGetOrAddRequest),
            typeof(ISensitivePayloadPort),
            typeof(PostgreSqlSensitivePayloadPort),
        ];

        foreach (Type type in protectedTypes)
        {
            MemberInfo[] members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (MemberInfo member in members)
            {
                AssertSafePublicName($"{type.FullName}.{member.Name}");

                if (member is MethodBase method)
                {
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        AssertSafePublicName(parameter.Name ?? string.Empty);
                        Assert.DoesNotContain(
                            parameter.ParameterType.FullName ?? string.Empty,
                            "ConnectionStringBuilder",
                            StringComparison.Ordinal);
                        Assert.NotEqual("Npgsql.NpgsqlConnection", parameter.ParameterType.FullName);
                    }
                }
            }
        }

        ConstructorInfo adapterConstructor = Assert.Single(
            typeof(PostgreSqlSensitivePayloadPort).GetConstructors());
        ParameterInfo dataSource = Assert.Single(adapterConstructor.GetParameters());

        Assert.Equal("Npgsql.NpgsqlDataSource", dataSource.ParameterType.FullName);
        Assert.NotEqual(typeof(string), dataSource.ParameterType);
    }

    [Fact]
    public void ProtectedPayloadDefensivelyCopiesAllByteArrays()
    {
        byte[] fingerprintBytes = Enumerable.Range(0, SensitivePayloadFingerprint.RequiredLength)
            .Select(static value => (byte)value)
            .ToArray();
        byte[] nonce = Enumerable.Repeat((byte)11, 12).ToArray();
        byte[] authenticationTag = Enumerable.Repeat((byte)22, 16).ToArray();
        byte[] ciphertext = [31, 32, 33, 34];
        var payload = new ProtectedSensitivePayload(
            SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(fingerprintBytes),
            "AES-256-GCM",
            "windows-key-v1",
            nonce,
            authenticationTag,
            ciphertext);

        fingerprintBytes[0] = byte.MaxValue;
        nonce[0] = byte.MaxValue;
        authenticationTag[0] = byte.MaxValue;
        ciphertext[0] = byte.MaxValue;

        byte[] returnedFingerprint = payload.Fingerprint.ToArray();
        byte[] returnedNonce = payload.GetNonce();
        byte[] returnedTag = payload.GetAuthenticationTag();
        byte[] returnedCiphertext = payload.GetCiphertext();
        returnedFingerprint[1] = byte.MaxValue;
        returnedNonce[1] = byte.MaxValue;
        returnedTag[1] = byte.MaxValue;
        returnedCiphertext[1] = byte.MaxValue;

        Assert.Equal(0, payload.Fingerprint.ToArray()[0]);
        Assert.Equal(1, payload.Fingerprint.ToArray()[1]);
        Assert.Equal(11, payload.GetNonce()[0]);
        Assert.Equal(11, payload.GetNonce()[1]);
        Assert.Equal(22, payload.GetAuthenticationTag()[0]);
        Assert.Equal(22, payload.GetAuthenticationTag()[1]);
        Assert.Equal(31, payload.GetCiphertext()[0]);
        Assert.Equal(32, payload.GetCiphertext()[1]);
    }

    [Fact]
    public void VerifiedMigrationSqlIsNotPartOfThePublicRepositorySurface()
    {
        PropertyInfo? publicSql = typeof(PostgreSqlMigrationResource).GetProperty(
            "Sql",
            BindingFlags.Public | BindingFlags.Instance);
        Type[] repositoryTypes =
        [
            .. typeof(ISensitivePayloadPort).Assembly.GetExportedTypes()
                .Where(static type => type.Namespace == "SqlObserver.Application.Ports" && type.Name.Contains("Migration", StringComparison.Ordinal)),
            .. typeof(SqlObserver.Infrastructure.PostgreSql.AssemblyMarker).Assembly.GetExportedTypes()
                .Where(static type => type.Name.Contains("Migration", StringComparison.Ordinal)),
        ];

        Assert.Null(publicSql);

        foreach (Type type in repositoryTypes)
        {
            MemberInfo[] members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (MemberInfo member in members)
            {
                AssertSafePublicName($"{type.FullName}.{member.Name}");

                if (member is not MethodBase method)
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string parameterName = parameter.Name ?? string.Empty;
                    AssertSafePublicName(parameterName);

                    if (parameter.ParameterType == typeof(string))
                    {
                        Assert.DoesNotContain(
                            parameterName,
                            ["sql", "query", "commandText"],
                            StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }

    private static void AssertSafePublicName(string name)
    {
        string normalized = name.Replace("_", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain(
            ForbiddenPublicNameFragments,
            fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
