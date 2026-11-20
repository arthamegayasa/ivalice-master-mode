using Xunit;

// One test changes the minimum level, which is shared state, so the classes must not run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
