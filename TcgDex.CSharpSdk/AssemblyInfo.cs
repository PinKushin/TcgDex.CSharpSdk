// System.Runtime.CompilerServices is a global using — see GlobalUsings.cs.
// Repeating it here is IDE0005, and the build treats that as an error.

// The transport is internal so it stays out of the public API surface, but the
// test suite drives it directly — asserting on the exact URLs and the error
// contract is the point, and doing that through the public resource clients
// would test two things at once.
[assembly: InternalsVisibleTo("TcgDex.CSharpSdk.Tests")]
