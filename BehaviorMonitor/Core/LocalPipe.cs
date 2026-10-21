using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ProcessSentinel.Core;

public static class LocalPipe
{
    public static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法读取 Windows 用户标识。");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly compares token owner SIDs, which differ across UAC elevation.
        // Use an explicit user SID ACL and validate the launched peer's PID instead.
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
    }
    public static void VerifyClient(NamedPipeServerStream pipe, int expectedPid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != expectedPid)
            throw new UnauthorizedAccessException("连接的采集器身份不匹配。");
    }
    public static void VerifyServer(NamedPipeClientStream pipe, int expectedPid)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != expectedPid)
            throw new UnauthorizedAccessException("连接的监控界面身份不匹配。");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
}
