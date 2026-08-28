namespace Kill.Services;

public static class KillPaths
{
    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kill");

    public static string CommonRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kill");

    public static string TempRoot => Path.Combine(LocalRoot, "Temp");
    public static string HistoryRoot => Path.Combine(CommonRoot, "History");
    public static string QuarantineRoot => Path.Combine(CommonRoot, "Quarantine");
    public static string ServiceBackupRoot => Path.Combine(CommonRoot, "ServiceBackups");
}
