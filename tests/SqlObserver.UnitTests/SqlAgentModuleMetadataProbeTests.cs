using SqlObserver.Infrastructure.SqlServer;
using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentModuleMetadataProbeTests
{
    private static readonly SqlAgentModuleMetadataPins Enrollment = new(123, 456, new('a', 64), new('b', 64), new('c', 40));
    private static readonly Guid Connection = new("04525053-1234-4567-8abc-123456789abc");

    [Theory]
    [InlineData(-2, typeof(TimeoutException))]
    [InlineData(229, typeof(UnauthorizedAccessException))]
    [InlineData(297, typeof(UnauthorizedAccessException))]
    [InlineData(300, typeof(UnauthorizedAccessException))]
    [InlineData(916, typeof(UnauthorizedAccessException))]
    [InlineData(51005, typeof(UnauthorizedAccessException))]
    [InlineData(11001, typeof(InvalidDataException))]
    public void SqlFailuresRetainOnlyNumericClassificationWithoutRawInnerError(int number, Type exceptionType)
    {
        Exception error = SqlAgentModuleMetadataProbe.ClassifySqlFailure(number);
        Assert.Equal(exceptionType, error.GetType()); Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task SignatureStateRequiresAllIndependentMetadataEvidence(bool signaturePresent)
    {
        using var reader = new StringFixtureReader(Tables(signaturePresent));
        var actual = await SqlAgentModuleMetadataProbe.ParseAsync(reader, Enrollment, CancellationToken.None);
        Assert.Equal(signaturePresent, actual.CaptureEligible);
        Assert.Equal(signaturePresent ? SqlAgentModuleMetadataSignatureState.SignedVerified : SqlAgentModuleMetadataSignatureState.Unsigned, actual.State);
        Assert.Equal(Enrollment.DefinitionSha256, actual.DefinitionSha256);
        Assert.Equal(Enrollment.PublicCertificateSha256, actual.PublicCertificateSha256);
        Assert.Equal(Enrollment.ThumbprintHex, actual.ThumbprintHex);
        Assert.Equal(DateTimeKind.Utc, actual.ObservedAtUtc.Kind);
        Assert.Equal(Guid.Empty, actual.ClientConnectionId); // A parser cannot attest an owned connection.
    }

    public static IEnumerable<object[]> HeaderDrift()
    {
        yield return [0, 1]; yield return [1, 2]; yield return [2, "msdb"]; yield return [3, 0];
        yield return [4, DateTime.MinValue]; yield return [5, 124]; yield return [6, "dbo"];
        yield return [7, "capture_v2"]; yield return [8, "PC"]; yield return [9, -2];
        yield return [10, 0]; yield return [11, 0]; yield return [12, new string('d', 64)];
        yield return [13, 457]; yield return [14, "unexpected_certificate"];
        yield return [15, new string('d', 64)]; yield return [16, new string('d', 40)];
        yield return [17, 32]; yield return [18, "ENCRYPTED_BY_MASTER_KEY"];
        yield return [19, 0]; yield return [20, 0];
    }

    [Theory]
    [MemberData(nameof(HeaderDrift))]
    public async Task ObjectCertificateIdentityPermissionsAndSettingsDriftAreRejected(int index, object value)
    {
        var tables = Tables(); tables[0].Rows[0][index] = value;
        await RejectAsync(tables);
    }

    public static IEnumerable<object[]> RequiredHeaderFields() => Enumerable.Range(0, 22)
        .Where(index => index is not (9 or 21)).Select(index => new object[] { index });

    [Theory]
    [MemberData(nameof(RequiredHeaderFields))]
    public async Task InvisibleOrNullMetadataNeverBecomesTrustedEnrollment(int index)
    {
        var tables = Tables(); tables[0].Rows[0][index] = DBNull.Value;
        await RejectAsync(tables);
    }

    [Theory]
    [InlineData("missing-header")] [InlineData("extra-header")] [InlineData("extra-signature")]
    [InlineData("missing-verification")] [InlineData("extra-verification")]
    [InlineData("missing-set")] [InlineData("extra-set")]
    [InlineData("wrong-name")] [InlineData("wrong-type")] [InlineData("extra-column")]
    public async Task ExactBoundedSchemaAndCardinalityAreRequired(string change)
    {
        var tables = Tables();
        switch (change)
        {
            case "missing-header": tables[0].Rows.Clear(); break;
            case "extra-header": tables[0].Rows.Add(tables[0].Rows[0].ItemArray); break;
            case "extra-signature": tables[1].Rows.Add(tables[1].Rows[0].ItemArray); break;
            case "missing-verification": tables[2].Rows.Clear(); break;
            case "extra-verification": tables[2].Rows.Add(tables[2].Rows[0].ItemArray); break;
            case "missing-set": tables = tables[..2]; break;
            case "extra-set": tables = [.. tables, tables[2].Copy()]; break;
            case "wrong-name": tables[0].Columns[0].ColumnName = "untrusted_alias"; break;
            case "wrong-type": tables[0].Columns.RemoveAt(0); tables[0].Columns.Add("schema_version", typeof(long)).SetOrdinal(0); break;
            case "extra-column": tables[0].Columns.Add("extra", typeof(int)); break;
        }
        await RejectAsync(tables);
    }

    [Theory]
    [InlineData(0, 5)] [InlineData(1, 124)] [InlineData(2, "SPVA")] [InlineData(2, "CPVC")]
    [InlineData(3, "dddddddddddddddddddddddddddddddddddddddd")]
    public async Task SignatureInventoryCannotHideAlternateOrCountersignatures(int field, object value)
    {
        var tables = Tables(); tables[1].Rows[0][field] = value;
        await RejectAsync(tables);
    }

    [Theory]
    [InlineData(true, null, 1, 1)] [InlineData(true, 0, 1, 1)] [InlineData(true, 1, 1, 0)]
    [InlineData(true, 1, 0, 0)] [InlineData(true, 1, 2, 1)] [InlineData(false, 1, 0, 0)]
    [InlineData(false, 0, 0, 0)] [InlineData(false, null, 1, 1)] [InlineData(false, null, 0, 1)]
    public async Task SignatureFunctionDisagreementDoesNotAdmitSignedOrUnsignedState(bool inventory, int? objectSigned, int signaturePresent, int valid)
    {
        var tables = Tables(inventory); tables[0].Rows[0][21] = (object?)objectSigned ?? DBNull.Value;
        tables[2].Rows[0][1] = signaturePresent; tables[2].Rows[0][2] = valid;
        await RejectAsync(tables);
    }

    [Fact]
    public async Task VerificationMustReferToTheEnrolledObject()
    {
        var tables = Tables(); tables[2].Rows[0][0] = 124;
        await RejectAsync(tables);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task NullSignatureVerificationFieldsAreRejected(int index)
    {
        var tables = Tables(); tables[2].Rows[0][index] = DBNull.Value;
        await RejectAsync(tables);
    }

    [Fact]
    public async Task OversizedMetadataIsRejectedWithoutEchoingPrivateInput()
    {
        const string privateValue = "untrusted-private-metadata";
        var tables = Tables(); tables[0].Rows[0][6] = string.Concat(Enumerable.Repeat(privateValue, 100));
        using var reader = new StringFixtureReader(tables);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SqlAgentModuleMetadataProbe.ParseAsync(reader, Enrollment, CancellationToken.None));
        Assert.DoesNotContain(privateValue, error.Message, StringComparison.Ordinal); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("object")] [InlineData("certificate")] [InlineData("definition")] [InlineData("public")]
    [InlineData("thumbprint")] [InlineData("uppercase")] [InlineData("prefix")]
    public async Task TrustedEnrollmentRequiresCanonicalIdsAndHashes(string field)
    {
        var expected = field switch
        {
            "object" => Enrollment with { ObjectId = 0 },
            "certificate" => Enrollment with { CertificateId = -1 },
            "definition" => Enrollment with { DefinitionSha256 = "" },
            "public" => Enrollment with { PublicCertificateSha256 = new string('g', 64) },
            "thumbprint" => Enrollment with { ThumbprintHex = new string('c', 64) },
            "uppercase" => Enrollment with { DefinitionSha256 = new string('A', 64) },
            _ => Enrollment with { ThumbprintHex = "0x" + Enrollment.ThumbprintHex },
        };
        using var reader = new StringFixtureReader(Tables());
        await Assert.ThrowsAsync<InvalidDataException>(() => SqlAgentModuleMetadataProbe.ParseAsync(reader, expected, CancellationToken.None));
    }

    [Theory]
    [InlineData("connection")] [InlineData("session")] [InlineData("object")] [InlineData("certificate")]
    [InlineData("definition")] [InlineData("public")] [InlineData("thumbprint")]
    [InlineData("unsigned")] [InlineData("reversed-time")]
    public async Task BracketComparisonRejectsMetadataStateOrSessionDrift(string field)
    {
        var before = await ObserveAsync();
        var after = field switch
        {
            "connection" => before with { ClientConnectionId = Guid.NewGuid() },
            "session" => before with { SessionId = before.SessionId + 1 },
            "object" => before with { ObjectId = before.ObjectId + 1 },
            "certificate" => before with { CertificateId = before.CertificateId + 1 },
            "definition" => before with { DefinitionSha256 = new string('d', 64) },
            "public" => before with { PublicCertificateSha256 = new string('d', 64) },
            "thumbprint" => before with { ThumbprintHex = new string('d', 40) },
            "unsigned" => before with { State = SqlAgentModuleMetadataSignatureState.Unsigned },
            _ => before with { ObservedAtUtc = before.ObservedAtUtc.AddTicks(-1) }
        };
        Assert.Throws<InvalidDataException>(() => SqlAgentModuleMetadataProbe.RequireStableSigned(before, after));
    }

    [Fact]
    public async Task MatchingPointObservationsAllowOnlySignedOwnedConnectionBracket()
    {
        var before = await ObserveAsync();
        SqlAgentModuleMetadataProbe.RequireStableSigned(before, before with { ObservedAtUtc = before.ObservedAtUtc.AddMilliseconds(10) });
        var noConnection = before with { ClientConnectionId = Guid.Empty };
        Assert.Throws<InvalidDataException>(() => SqlAgentModuleMetadataProbe.RequireStableSigned(noConnection, noConnection));
        var noSignature = before with { State = SqlAgentModuleMetadataSignatureState.Unsigned };
        Assert.Throws<InvalidDataException>(() => SqlAgentModuleMetadataProbe.RequireStableSigned(noSignature, noSignature));
    }

    [Fact]
    public void EmbeddedQueryIsFixedAndRejectsAppendedCaptureExecution()
    {
        string sql = SqlAgentModuleMetadataProbe.LoadSql();
        Assert.Equal(sql, SqlAgentModuleMetadataProbe.ValidateSql(sql.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => SqlAgentModuleMetadataProbe.ValidateSql(sql + "\nEXEC master.sqlobserver_agent.capture_v1;"));
        Assert.Contains("SELECT TOP (2)", sql, StringComparison.Ordinal);
        Assert.Contains("IS_OBJECTSIGNED", sql, StringComparison.Ordinal);
        Assert.Contains("sys.fn_check_object_signatures", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancellationDoesNotReadOrOpenAnyConnection()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var reader = new StringFixtureReader(Tables());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlAgentModuleMetadataProbe.ParseAsync(reader, Enrollment, cancelled.Token));
        await using var connection = new SqlConnection();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlAgentModuleMetadataProbe.ReadAsync(connection, Enrollment, "", TimeSpan.FromSeconds(1), cancelled.Token));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void FixtureCharacterReadsPreserveActualLengthAndOffset()
    {
        using var reader = new StringFixtureReader(Tables());
        Assert.True(reader.Read());
        Assert.Equal(6, reader.GetChars(2, 0, null, 0, 0));
        var buffer = new char[5];
        Assert.Equal(3, reader.GetChars(2, 2, buffer, 1, 3));
        Assert.Equal("ste", new string(buffer, 1, 3));
        Assert.Equal(0, reader.GetChars(2, 6, buffer, 0, 5));
    }

    private static async Task<SqlAgentModuleMetadataReceipt> ObserveAsync()
    {
        using var reader = new StringFixtureReader(Tables());
        return (await SqlAgentModuleMetadataProbe.ParseAsync(reader, Enrollment, CancellationToken.None)) with { ClientConnectionId = Connection };
    }
    private static async Task RejectAsync(DataTable[] tables)
    {
        using var reader = new StringFixtureReader(tables);
        await Assert.ThrowsAsync<InvalidDataException>(() => SqlAgentModuleMetadataProbe.ParseAsync(reader, Enrollment, CancellationToken.None));
    }
    private static DataTable[] Tables(bool signed = true)
    {
        var header = new DataTable("metadata") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        foreach ((string name, Type type) in new (string, Type)[]
        {
            ("schema_version",typeof(int)),("database_id",typeof(int)),("database_name",typeof(string)),("session_id",typeof(int)),
            ("observed_utc",typeof(DateTime)),("object_id",typeof(int)),("schema_name",typeof(string)),("module_name",typeof(string)),
            ("module_type",typeof(string)),("execute_as_principal_id",typeof(int)),("uses_ansi_nulls",typeof(int)),("uses_quoted_identifier",typeof(int)),
            ("definition_sha256",typeof(string)),("certificate_id",typeof(int)),("certificate_name",typeof(string)),("public_certificate_sha256",typeof(string)),
            ("thumbprint_hex",typeof(string)),("thumbprint_bytes",typeof(int)),("private_key_state",typeof(string)),("object_view_definition",typeof(int)),
            ("certificate_view_definition",typeof(int)),("is_object_signed",typeof(int))
        }) header.Columns.Add(name, type);
        header.Rows.Add(2, 1, "master", 51, new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Unspecified), Enrollment.ObjectId,
            "sqlobserver_agent", "capture_v1", "P", DBNull.Value, 1, 1, Enrollment.DefinitionSha256, Enrollment.CertificateId,
            "SqlObserverAgentActivityCertificateV1", Enrollment.PublicCertificateSha256, Enrollment.ThumbprintHex, 20, "NO_PRIVATE_KEY", 1, 1,
            signed ? 1 : DBNull.Value);
        var inventory = new DataTable("inventory") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        inventory.Columns.Add("signature_class", typeof(int)); inventory.Columns.Add("object_id", typeof(int));
        inventory.Columns.Add("crypt_type", typeof(string)); inventory.Columns.Add("thumbprint_hex", typeof(string));
        if (signed) inventory.Rows.Add(1, Enrollment.ObjectId, "SPVC", Enrollment.ThumbprintHex);
        var verification = new DataTable("verification") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        verification.Columns.Add("object_id", typeof(int)); verification.Columns.Add("is_signed", typeof(int)); verification.Columns.Add("is_signature_valid", typeof(int));
        verification.Rows.Add(Enrollment.ObjectId, signed ? 1 : 0, signed ? 1 : 0);
        return [header, inventory, verification];
    }

    // DataTableReader.GetChars expects char[] in its cell; real SqlDataReader streams SQL strings.
    // Preserve production's length-before-materialization path, rather than replacing it with GetString.
    internal sealed class StringFixtureReader(DataTable[] tables) : DbDataReader
    {
        private readonly DataTableReader inner = new(tables);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            string value = inner.GetString(ordinal);
            ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dataOffset, value.Length);
            if (buffer is null) return value.Length;
            ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (bufferOffset > buffer.Length || length > buffer.Length - bufferOffset)
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            int count = Math.Min(length, value.Length - (int)dataOffset);
            value.CopyTo((int)dataOffset, buffer, bufferOffset, count);
            return count;
        }
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override bool Read() => inner.Read();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
