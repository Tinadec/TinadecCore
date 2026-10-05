namespace TinadecCore.Api.Tests;

/// <summary>
/// Marks an Office integration test that needs the App-owned GraphSeedPack.
/// Core-only checkouts do not ship that pack; the assertions still run when
/// the Office desktop sources are present, including checks for missing files.
/// </summary>
public sealed class RequiresGraphSeedPackFactAttribute : FactAttribute
{
    public RequiresGraphSeedPackFactAttribute()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps", "desktop", "src"))) return;
        }

        Skip = "Requires TinadecOffice's App-owned GraphSeedPack, which is not part of a Core-only checkout.";
    }
}
