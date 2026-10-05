using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class PdhTargetProcessMappingTests
{
    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    public void UnrelatedDuplicatesDoNotHideUniqueTargetAndIndependentArrayOrderIsRespected(uint status)
    {
        var result = PdhTargetProcessMapping.Select(
            ["worker", "sqlservr", "worker"], [21, 42, 22], [0, status, 0],
            ["worker", "worker", "sqlservr"], 42, out int idIndex, out int cpuIndex);

        Assert.Equal(PdhTargetProcessMappingResult.Selected, result);
        Assert.Equal(1, idIndex);
        Assert.Equal(2, cpuIndex);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(0xC0000BBAU)]
    public void DuplicateTargetIdNameRefusesEvenWhenOtherRowHasDifferentPidOrInvalidStatus(uint otherStatus)
    {
        AssertRefused(PdhTargetProcessMappingResult.Ambiguous,
            ["sqlservr", "sqlservr"], [42, 43], [0, otherStatus], ["sqlservr"]);
    }

    [Fact]
    public void DuplicateTargetCpuNameRefuses()
    {
        AssertRefused(PdhTargetProcessMappingResult.Ambiguous,
            ["sqlservr"], [42], [0], ["sqlservr", "sqlservr"]);
    }

    [Theory]
    [InlineData("other", 0U)]
    [InlineData("other", 1U)]
    [InlineData("sqlservr", 0U)]
    public void MultipleValidTargetPidRowsRefuseEvenWhenOnlyOneNameHasACpuRow(string otherName, uint status)
    {
        AssertRefused(PdhTargetProcessMappingResult.Ambiguous,
            ["sqlservr", otherName], [42, 42], [0, status], ["sqlservr"]);
    }

    [Fact]
    public void InvalidUnrelatedPidRowDoesNotOverrideValidTarget()
    {
        var result = PdhTargetProcessMapping.Select(
            ["other", "sqlservr"], [42, 42], [0xC0000BBAU, 0], ["sqlservr"],
            42, out int idIndex, out int cpuIndex);

        Assert.Equal(PdhTargetProcessMappingResult.Selected, result);
        Assert.Equal(1, idIndex);
        Assert.Equal(0, cpuIndex);
    }

    [Theory]
    [InlineData(2U)]
    [InlineData(0xC0000BBAU)]
    public void InvalidTargetIdStatusDoesNotSelect(uint status)
    {
        AssertRefused(PdhTargetProcessMappingResult.Missing,
            ["sqlservr"], [42], [status], ["sqlservr"]);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("SQLSERVR")]
    [InlineData("sqlservr#1")]
    public void MissingExactCpuNameDoesNotSelectByOrdinalOrNormalizeTheInstanceName(string cpuName)
    {
        AssertRefused(PdhTargetProcessMappingResult.Missing,
            ["sqlservr"], [42], [0], [cpuName]);
    }

    [Fact]
    public void AbsentRowsAndMissingTargetReturnNoSelection()
    {
        AssertRefused(PdhTargetProcessMappingResult.Missing, [], [], [], ["sqlservr"]);
        AssertRefused(PdhTargetProcessMappingResult.Missing, ["sqlservr"], [42], [0], []);
        AssertRefused(PdhTargetProcessMappingResult.Missing, ["sqlservr"], [43], [0], ["sqlservr"]);
        AssertRefused(PdhTargetProcessMappingResult.Missing, [""], [42], [0], [""]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnavailableTargetPidDoesNotSelect(int pid)
    {
        var result = PdhTargetProcessMapping.Select(["process"], [pid], [0], ["process"], pid,
            out int idIndex, out int cpuIndex);
        Assert.Equal(PdhTargetProcessMappingResult.Missing, result);
        Assert.Equal(-1, idIndex);
        Assert.Equal(-1, cpuIndex);
    }

    [Fact]
    public void FullBoundedArraysCanSelectTheirLastRows()
    {
        string[] names = Enumerable.Repeat("worker", 1024).ToArray();
        names[1023] = "sqlservr";
        long[] pids = new long[1024];
        pids[1023] = 42;
        var result = PdhTargetProcessMapping.Select(names, pids, new uint[1024], names, 42,
            out int idIndex, out int cpuIndex);
        Assert.Equal(PdhTargetProcessMappingResult.Selected, result);
        Assert.Equal(1023, idIndex);
        Assert.Equal(1023, cpuIndex);
    }

    [Fact]
    public void OversizedOrMisalignedArraysRefuseBeforeSelection()
    {
        Assert.Throws<ArgumentException>(() => PdhTargetProcessMapping.Select(
            new string[1025], new long[1025], new uint[1025], [], 42, out _, out _));
        Assert.Throws<ArgumentException>(() => PdhTargetProcessMapping.Select(
            [], [], [], new string[1025], 42, out _, out _));
        Assert.Throws<ArgumentException>(() => PdhTargetProcessMapping.Select(
            ["sqlservr"], [], [0], ["sqlservr"], 42, out _, out _));
        Assert.Throws<ArgumentException>(() => PdhTargetProcessMapping.Select(
            ["sqlservr"], [42], [], ["sqlservr"], 42, out _, out _));
    }

    private static void AssertRefused(PdhTargetProcessMappingResult expected, string[] idNames,
        long[] pids, uint[] statuses, string[] cpuNames)
    {
        var result = PdhTargetProcessMapping.Select(idNames, pids, statuses, cpuNames, 42,
            out int idIndex, out int cpuIndex);
        Assert.Equal(expected, result);
        Assert.Equal(-1, idIndex);
        Assert.Equal(-1, cpuIndex);
    }
}
