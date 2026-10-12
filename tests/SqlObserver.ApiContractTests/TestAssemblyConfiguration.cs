// Hundreds of in-process HTTP hosts should not start at the machine's full CPU count.
// Preserve parallel contract execution while bounding TestServer contention.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
