using SqlObserver.Domain.Retention;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheRetentionPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalPolicyPreservesTheReplayFloorAndBoundedMaximum(bool enabled)
    {
        RetentionPolicyValidator.Validate(new("collection_run_canonical", enabled, TimeSpan.FromDays(90), 1));
        RetentionPolicyValidator.Validate(new("collection_run_canonical", enabled, TimeSpan.FromDays(3650), 1));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidator.Validate(new("collection_run_canonical", enabled, TimeSpan.FromDays(90) - TimeSpan.FromTicks(1), 1)));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidator.Validate(new("collection_run_canonical", enabled, TimeSpan.FromDays(3650) + TimeSpan.FromTicks(1), 1)));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidator.Validate(new("collection_run_canonical", enabled, TimeSpan.FromDays(90), 2)));
        if (!enabled) RetentionPolicyValidator.Validate(new("collection_run_canonical", false, null, 1));
    }

    [Theory]
    [InlineData("m7_cache_watermarks", 8)]
    [InlineData("m7_cache_deltas", 30)]
    [InlineData("m7_cache_receipts", 30)]
    [InlineData("m7_cache_content_receipts", 30)]
    [InlineData("m7_cache_content_links", 30)]
    public void EnabledPolicyPreservesItsRequiredComparisonOrHistoryWindow(string dataClass, int days)
    {
        TimeSpan minimum = TimeSpan.FromDays(days);
        RetentionPolicyValidator.Validate(new(dataClass, true, minimum));
        RetentionPolicyValidator.Validate(new(dataClass, true, minimum + TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidator.Validate(new(dataClass, true, minimum - TimeSpan.FromTicks(1))));
    }

    [Theory]
    [InlineData("m7_cache_watermarks")]
    [InlineData("m7_cache_deltas")]
    [InlineData("m7_cache_receipts")]
    [InlineData("m7_cache_content_receipts")]
    [InlineData("m7_cache_content_links")]
    public void DisabledPolicyRemainsAdministrativelyConfigurableWithoutClaimingActiveRetention(string dataClass)
    {
        RetentionPolicyValidator.Validate(new(dataClass, false));
        RetentionPolicyValidator.Validate(new(dataClass, false, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidator.Validate(new(dataClass, true)));
    }
}
