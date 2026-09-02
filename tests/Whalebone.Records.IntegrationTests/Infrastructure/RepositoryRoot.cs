namespace Whalebone.Records.IntegrationTests.Infrastructure;

internal static class RepositoryRoot
{
    /// <summary>
    /// Locates the directory holding the Dockerfile by walking up from the test assembly.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>CommonDirectoryPath.GetSolutionDirectory()</c>. That resolves
    /// through <c>[CallerFilePath]</c>, and <c>ContinuousIntegrationBuild=true</c> turns on
    /// deterministic source paths, rewriting the compile-time path to something like
    /// <c>/_/tests/...</c> which does not exist on any disk. Walking the real filesystem
    /// works under both normal and deterministic builds.
    /// </remarks>
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dockerfile")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No Dockerfile found walking up from '{AppContext.BaseDirectory}'.");
    }
}
