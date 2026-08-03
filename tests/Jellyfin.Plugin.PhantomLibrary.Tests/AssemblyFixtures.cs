using Xunit;

// PhantomDb.Dispose() calls the PROCESS-WIDE SqliteConnection.ClearAllPools() to release
// its file-based SQLite handle for temp-file deletion in test teardown. xUnit runs test
// classes/collections in parallel by default, so with parallelization enabled one test
// class's teardown races another concurrently-running test class's live pooled SQLite
// connection, manifesting as a spurious "System.ObjectDisposedException: Cannot access a
// disposed object. Object name: 'SQLitePCL.sqlite3'." failure in an unrelated test.
// Disabling test-collection parallelization for this assembly serializes all PhantomDb-
// backed tests so ClearAllPools() can never run concurrently with another test's
// in-flight connection. This assembly's suite runs in ~50s serialized, which is an
// acceptable tradeoff for eliminating a nondeterministic failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
