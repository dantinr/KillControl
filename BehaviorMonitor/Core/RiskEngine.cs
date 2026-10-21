using System.Net;
using System.Text.RegularExpressions;

namespace ProcessSentinel.Core;

public static partial class RiskEngine
{
    public static Activity Evaluate(Activity value)
    {
        var target = value.Target.Replace('/', '\\');
        bool write = value.Operation is "写入" or "删除" or "重命名" or "设置值" or "删除值" or "创建键" or "删除键";
        bool read = value.Operation is "读取" or "打开/创建";
        string attempted = value.Status.HasValue && value.Status.Value < 0 ? "该操作返回失败状态，但仍值得查看尝试原因。" : "该行为也可能来自合法软件，请结合用途复核。";

        if (value.Kind == ActivityKind.File && read && IsSensitiveFile(target))
            return Flag(value, RiskLevel.Attention, "sensitive-file", "访问了密码、Cookie 或私钥相关文件。读取敏感文件不等于已上传。" + attempted);
        if (value.Kind == ActivityKind.File && write && target.Contains("\\Start Menu\\Programs\\Startup\\", StringComparison.OrdinalIgnoreCase))
            return Flag(value, RiskLevel.High, "startup-file", "尝试修改用户登录时自动启动的程序目录。" + attempted);
        if (value.Kind == ActivityKind.File && write && (target.EndsWith("\\drivers\\etc\\hosts", StringComparison.OrdinalIgnoreCase) || target.EndsWith("\\Windows\\System32\\drivers\\etc\\hosts", StringComparison.OrdinalIgnoreCase)))
            return Flag(value, RiskLevel.High, "hosts-write", "尝试修改 hosts，可能改变域名解析或屏蔽服务。" + attempted);
        if (value.Kind == ActivityKind.Registry && write)
        {
            if (RunKey().IsMatch(target)) return Flag(value, RiskLevel.High, "registry-startup", "尝试修改 Run / RunOnce 自启动项。" + attempted);
            if (target.Contains("\\Services\\", StringComparison.OrdinalIgnoreCase)) return Flag(value, RiskLevel.Attention, "registry-service", "尝试修改服务配置，可能涉及后台运行或驱动。" + attempted);
            if (target.Contains("\\Windows Defender", StringComparison.OrdinalIgnoreCase) || target.Contains("\\Image File Execution Options\\", StringComparison.OrdinalIgnoreCase))
                return Flag(value, RiskLevel.High, "security-config", "尝试修改安全软件配置或程序启动重定向配置。" + attempted);
        }
        if (value.Kind == ActivityKind.Process && value.Operation == "启动")
        {
            if (EncodedCommand().IsMatch(value.Detail)) return Flag(value, RiskLevel.High, "encoded-shell", "子进程命令行包含 PowerShell 编码指令或下载后执行模式。管理脚本也可能使用这些模式。");
            string name = Path.GetFileName(value.Target);
            if (new[] { "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                return Flag(value, RiskLevel.Attention, "script-child", "目标创建了命令解释器或脚本宿主子进程，请查看完整命令行。");
        }
        if (value.Kind == ActivityKind.Network && value.Operation is "连接" or "发送" && value.RemotePort is 4444 or 5555 or 1337 or 9001 && !IsLocalAddress(value.RemoteAddress))
            return Flag(value, RiskLevel.Attention, "unusual-port", $"连接到公网地址的 {value.RemotePort} 端口。这只是一条弱线索，端口号不能证明恶意。");
        return value;
    }

    private static Activity Flag(Activity value, RiskLevel risk, string id, string reason) => value with { Risk = risk, RuleId = id, Reason = reason };
    private static bool IsSensitiveFile(string target)
    {
        var name = Path.GetFileName(target);
        return name.Equals("Login Data", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookies", StringComparison.OrdinalIgnoreCase)
            || name.Equals("logins.json", StringComparison.OrdinalIgnoreCase) || name.Equals("key4.db", StringComparison.OrdinalIgnoreCase)
            || (target.Contains("\\.ssh\\", StringComparison.OrdinalIgnoreCase) && new[] { "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa" }.Contains(name, StringComparer.OrdinalIgnoreCase));
    }
    public static bool IsLocalAddress(string text)
    {
        if (!IPAddress.TryParse(text, out var ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xfe) == 0xfc || ip.Equals(IPAddress.IPv6Any);
        return b[0] is 0 or 10 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224;
    }
    [GeneratedRegex(@"\\CurrentVersion\\Run(?:Once)?(?:\\|$)", RegexOptions.IgnoreCase)] private static partial Regex RunKey();
    [GeneratedRegex(@"(?:powershell|pwsh)(?:\.exe)?[\s\S]*\s-(?:e|en|enc|enco|encod|encode|encoded|encodedc|encodedco|encodedcom|encodedcomm|encodedcomma|encodedcomman|encodedcommand)\s|(?:DownloadString|Invoke-WebRequest|\biwr\b)[\s\S]*(?:Invoke-Expression|\biex\b)", RegexOptions.IgnoreCase)] private static partial Regex EncodedCommand();
}
