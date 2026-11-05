using Xunit;

namespace Aimie.Operations.Tests;

// Settings is process-wide; configuration tests must not overlap other tests.
[CollectionDefinition("Settings", DisableParallelization = true)]
public sealed class SettingsCollection;
