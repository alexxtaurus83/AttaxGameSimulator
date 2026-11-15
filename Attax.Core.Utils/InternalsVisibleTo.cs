using System.Runtime.CompilerServices;

// Friend access for members that no external layer calls but the engine or its tests use directly.
[assembly: InternalsVisibleTo("Attax.Core")]
[assembly: InternalsVisibleTo("Attax.Core.Tests")]
[assembly: InternalsVisibleTo("Attax.Server.UnitTests")]
