namespace SqlObserver.ApiContractTests;

// These tests exercise real elapsed-time and principal concurrency boundaries.
// Avoid unrelated TestServer startup contention while keeping every assertion.
[CollectionDefinition("Timing-sensitive HTTP contracts", DisableParallelization = true)]
public sealed class TimingSensitiveHttpTests;
