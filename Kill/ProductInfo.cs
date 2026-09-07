using System.Reflection;

namespace Kill;

public static class ProductInfo
{
    public static string Version { get; } = ReadVersion();
    public static string DisplayVersion => $"v{Version}";

    private static string ReadVersion()
    {
        var informationalVersion = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+', 2)[0];

        var version = typeof(ProductInfo).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
