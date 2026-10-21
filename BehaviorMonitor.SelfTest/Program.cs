using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;
using ActivityKind = ProcessSentinel.Core.ActivityKind;

if (args.Length == 2 && args[0] == "--file-fixture")
{
    string fixtureDirectory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(fixtureDirectory);
    var timeout = Stopwatch.StartNew();
    while (timeout.Elapsed < TimeSpan.FromSeconds(90) && !File.Exists(Path.Combine(fixtureDirectory, "stop")))
    {
        await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "fixture.txt"), DateTime.UtcNow.ToString("O"));
        _ = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "fixture.txt"));
        await Task.Delay(100);
    }
    return 0;
}

int reportIndex = Array.IndexOf(args, "--report");
using var reportWriter = reportIndex >= 0 && reportIndex + 1 < args.Length ? new StreamWriter(args[reportIndex + 1], false, new UTF8Encoding(false)) { AutoFlush = true } : null;
if (reportWriter is not null) { Console.SetOut(reportWriter); Console.SetError(reportWriter); }

static async Task RunFixture(string directory, bool includeIPv6)
{
    await Task.Delay(300);
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "probe.txt");
    await File.WriteAllTextAsync(path, "ProcessSentinel benign ETW probe");
    _ = await File.ReadAllTextAsync(path);
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var accept = listener.AcceptTcpClientAsync();
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    using var server = await accept;
    byte[] payload = Encoding.UTF8.GetBytes("loopback-test");
    await client.GetStream().WriteAsync(payload);
    var buffer = new byte[64];
    _ = await server.GetStream().ReadAsync(buffer);
    await server.GetStream().WriteAsync(payload);
    _ = await client.GetStream().ReadAsync(buffer);
    listener.Stop();
    using var udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var udpSender = new UdpClient();
    await udpSender.SendAsync(payload, (IPEndPoint)udpReceiver.Client.LocalEndPoint!);
    await udpReceiver.ReceiveAsync();
    if (Socket.OSSupportsIPv6 && includeIPv6)
    {
        var ipv6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        ipv6.Start();
        var accept6 = ipv6.AcceptTcpClientAsync();
        using var client6 = new TcpClient(AddressFamily.InterNetworkV6);
        await client6.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)ipv6.LocalEndpoint).Port);
        using var server6 = await accept6;
        await client6.GetStream().WriteAsync(payload);
        _ = await server6.GetStream().ReadAsync(buffer);
        ipv6.Stop();
    }
    string registryPath = @"Software\ProcessSentinelSelfTest-" + Path.GetFileName(directory);
    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(registryPath);
    key.SetValue("Probe", "benign");
    _ = key.GetValue("Probe");
    key.DeleteValue("Probe");
    key.Close();
    Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(registryPath);
    using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true });
    if (child is not null) await child.WaitForExitAsync();
    File.Move(path, Path.Combine(directory, "renamed.txt"));
    File.Delete(Path.Combine(directory, "renamed.txt"));
    await Task.Delay(1000);
}

if (args.Length == 2 && args[0] is "--fixture" or "--fixture-ipv4")
{
    await RunFixture(args[1], args[0] == "--fixture");
    return 0;
}

if (args.Length == 3 && args[0] == "--family-fixture")
{
    string directory = args[1], role = args[2];
    var children = new List<Process>();
    try
    {
        Directory.CreateDirectory(directory);
        foreach (string childRole in role == "root" ? new[] { "parent", "sibling" } : role == "parent" ? new[] { "selected" } : Array.Empty<string>())
            children.Add(Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--family-fixture \"" + directory + "\" " + childRole)
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Unable to create family fixture child."));
        string pidMarker = Path.Combine(directory, role + ".pid");
        File.WriteAllText(pidMarker + ".tmp", Environment.ProcessId.ToString());
        File.Move(pidMarker + ".tmp", pidMarker);
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "run")) && !File.Exists(Path.Combine(directory, "exit")))
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Family fixture startup timed out.");
            await Task.Delay(50);
        }
        if (File.Exists(Path.Combine(directory, "run")))
        {
            await RunFixture(Path.Combine(directory, role), false);
            File.WriteAllText(Path.Combine(directory, role + ".done"), "complete");
        }
        while (!File.Exists(Path.Combine(directory, "exit")))
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Family fixture cleanup timed out.");
            await Task.Delay(50);
        }
        foreach (var child in children) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    finally { foreach (var child in children) child.Dispose(); }
}

int passed = 0;
void Check(bool result, string title)
{
    if (!result) throw new InvalidOperationException("FAIL: " + title);
    Console.WriteLine("PASS: " + title);
    passed++;
}
Activity FileEvent(string operation, string target) => new() { Kind = ActivityKind.File, Operation = operation, Target = target };
Activity RegistryEvent(string operation, string target) => new() { Kind = ActivityKind.Registry, Operation = operation, Target = target };
RiskLevel Risk(Activity value) => RiskEngine.Evaluate(value).Risk;

try
{
    Check(Risk(FileEvent("读取", @"C:\Users\test\AppData\Local\Chrome\Default\Login Data")) == RiskLevel.Attention, "credential database read is a review clue");
    Check(Risk(FileEvent("读取", @"C:\Users\test\.ssh\id_ed25519")) == RiskLevel.Attention, "SSH private key read");
    Check(Risk(FileEvent("读取", @"C:\Users\test\.ssh\id_ed25519.pub")) == RiskLevel.None, "public key is not treated as private key");
    Check(Risk(FileEvent("写入", @"C:\Users\test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\tool.lnk")) == RiskLevel.High, "startup directory write");
    Check(Risk(FileEvent("读取", @"C:\Windows\System32\drivers\etc\hosts")) == RiskLevel.None, "hosts read is ordinary");
    Check(Risk(FileEvent("写入", @"C:\Windows\System32\drivers\etc\hosts")) == RiskLevel.High, "hosts write");
    Check(Risk(RegistryEvent("设置值", @"\REGISTRY\USER\test\Software\Microsoft\Windows\CurrentVersion\Run\tool")) == RiskLevel.High, "registry startup write");
    Check(Risk(RegistryEvent("设置值", @"\REGISTRY\USER\test\Software\Microsoft\Windows\CurrentVersion\Runtime\tool")) == RiskLevel.None, "Run path requires component boundary");
    Check(Risk(RegistryEvent("查询值", @"\REGISTRY\USER\test\Software\Microsoft\Windows\CurrentVersion\Run\tool")) == RiskLevel.None, "registry startup read is ordinary");
    Check(RiskEngine.Evaluate(RegistryEvent("设置值", @"\REGISTRY\USER\test\Software\Microsoft\Windows\CurrentVersion\Run\tool") with { Status = unchecked((int)0xc0000022) }).Reason.Contains("失败"), "failed attempt never presented as successful persistence");
    Check(Risk(new() { Kind = ActivityKind.Process, Operation = "启动", Target = @"C:\Windows\System32\cmd.exe", Detail = "cmd.exe /c echo hello" }) == RiskLevel.Attention, "script child carries explanation");
    Check(Risk(new() { Kind = ActivityKind.Process, Operation = "启动", Target = "powershell.exe", Detail = "powershell.exe -NoProfile -enc ZQB4AGkAdAA=" }) == RiskLevel.High, "encoded PowerShell command");
    Check(Risk(new() { Kind = ActivityKind.Process, Operation = "启动", Target = "tool.exe", Detail = "tool.exe --encrypt" }) == RiskLevel.None, "ordinary encrypt argument is not PowerShell encoding");
    Check(Risk(new() { Kind = ActivityKind.Network, Operation = "连接", RemoteAddress = "8.8.8.8", RemotePort = 443 }) == RiskLevel.None, "HTTPS alone is not suspicious");
    Check(Risk(new() { Kind = ActivityKind.Network, Operation = "连接", RemoteAddress = "8.8.8.8", RemotePort = 4444 }) == RiskLevel.Attention, "unusual external port is only a weak clue");
    Check(Risk(new() { Kind = ActivityKind.Network, Operation = "连接", RemoteAddress = "127.0.0.1", RemotePort = 4444 }) == RiskLevel.None, "localhost port avoids false warning");
    Check(RiskEngine.IsLocalAddress("::ffff:192.168.0.2") && RiskEngine.IsLocalAddress("fd00::1") && !RiskEngine.IsLocalAddress("2606:4700:4700::1111"), "IPv4 mapped and IPv6 private address classification");

    var payload64 = new byte[24 + Encoding.Unicode.GetByteCount("Probe") + 2];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(payload64.AsSpan(16), 0x1122334455667788);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload64.AsSpan(8), unchecked((int)0xc0000022));
    Encoding.Unicode.GetBytes("Probe").CopyTo(payload64, 24);
    var decoded = RegistryResolver.Decode(payload64, 2, 8);
    Check(decoded.Handle == 0x1122334455667788 && decoded.Status == unchecked((int)0xc0000022) && decoded.Name == "Probe", "registry raw 64-bit handle, status and value decode");
    var payload32 = new byte[22];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload32.AsSpan(16), 0xdeadbeef);
    Check(RegistryResolver.Decode(payload32, 2, 4).Handle == 0xdeadbeef, "registry raw 32-bit handle decode");
    Check(RegistryResolver.Decode(new byte[5], 2, 8).Status is null, "short registry payload remains unknown");

    var root = new ProcessInfo(10, 1, "root.exe", "", 100);
    var tracker = new ProcessTracker(root, true);
    tracker.Seed([new(12, 11, "grandchild.exe", "", 130), new(11, 10, "child.exe", "", 120), new(13, 10, "old.exe", "", 90), root]);
    Check(tracker.Count == 3 && tracker.Find(12) is not null && tracker.Find(13) is null, "unordered descendants with creation time checks");
    var seededProcesses = tracker.GetSnapshot()!;
    Check(seededProcesses.ActiveCount == 3 && seededProcesses.Processes.Single(x => x.IsRoot).Process == root
        && seededProcesses.Processes.Any(x => x.Process.Id == 12), "process roster includes the root and idle seeded descendants");
    Check(tracker.GetSnapshot(seededProcesses.Revision) is null, "unchanged process roster does not require another snapshot");
    foreach (string suiteHost in new[] { "Kill.exe", "Kill-v1.2.10.exe" })
    {
        var host = new ProcessInfo(910, 1, suiteHost, @"C:\Tools\" + suiteHost, 10);
        var target = new ProcessInfo(911, 910, "test-target.exe", @"C:\Apps\test-target.exe", 20);
        Check(ProcessFamilyResolver.Resolve(target, [host, target]).Root.Id == target.Id,
            "suite entry is a program-tree boundary: " + suiteHost);
    }
    Check(tracker.Stop(10) is not null && tracker.Find(12) is not null, "descendants survive root exit");
    var exitedRoot = tracker.GetSnapshot(seededProcesses.Revision)!;
    Check(exitedRoot.ActiveCount == 2 && !exitedRoot.Processes.Single(x => x.IsRoot).IsRunning
        && seededProcesses.Processes.All(x => x.IsRunning), "root exit updates roster without mutating prior snapshots or losing descendants");
    Check(!tracker.Start(new(10, 1, "unrelated.exe", "", 200)) && tracker.Find(10) is null, "root PID reuse is not followed");
    Check(!tracker.Start(new(11, 1, "unrelated.exe", "", 300)) && tracker.Find(11) is null, "tracked child PID reuse removes old identity");
    Check(tracker.GetSnapshot()!.Processes.Single(x => x.Process.Id == 11).IsRunning == false,
        "unrelated PID reuse marks the previous tracked identity as exited");
    Check(tracker.Start(new(11, 12, "new-child.exe", @"C:\Apps\new-child.exe", 400))
        && tracker.GetSnapshot()!.Processes.Count(x => x.Process.Id == 11) == 2,
        "related PID reuse retains separate old and new process identities");
    var single = new ProcessTracker(root, false);
    Check(!single.Start(new(11, 10, "child.exe", "", 120)) && single.Count == 1, "child toggle excludes descendants");
    Check(single.GetSnapshot()!.Processes.Count == 1 && single.GetSnapshot()!.Processes[0].IsRoot,
        "root-only monitoring roster excludes descendants");

    var trafficTracker = new ProcessTracker(root, true);
    trafficTracker.Start(new(11, 10, "sender.exe", "", 120));
    var beforeTraffic = trafficTracker.GetSnapshot()!;
    trafficTracker.RecordNetworkSend(11, 1024, "8.8.8.8");
    trafficTracker.RecordNetworkSend(11, 512, "2606:4700:4700::1111");
    var trafficSnapshot = trafficTracker.GetSnapshot(beforeTraffic.Revision)!;
    Check(trafficSnapshot.Processes.Single(x => x.Process.Id == 11).OutboundBytes == 1536
        && trafficSnapshot.Processes.Single(x => x.IsRoot).OutboundBytes == 0
        && beforeTraffic.Processes.All(x => x.OutboundBytes == 0),
        "IPv4 and IPv6 outbound bytes update only the sending process and preserve prior snapshots");
    foreach (string address in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "192.168.1.10", "10.0.0.1", "172.16.0.1", "fd00::1", "fe80::1", "224.0.0.1", "ff02::1", "100.64.0.1", "invalid", "" })
        trafficTracker.RecordNetworkSend(11, 4096, address);
    trafficTracker.RecordNetworkSend(11, 0, "8.8.8.8");
    trafficTracker.RecordNetworkSend(11, -1, "8.8.8.8");
    trafficTracker.RecordNetworkSend(999, 4096, "8.8.8.8");
    Check(trafficTracker.GetSnapshot(trafficSnapshot.Revision) is null,
        "local, private, multicast, invalid and nonpositive sends or untracked PIDs do not count as Internet egress");
    trafficTracker.Stop(11);
    trafficTracker.RecordNetworkSend(11, 1024, "8.8.8.8");
    Check(trafficTracker.GetSnapshot()!.Processes.Single(x => x.Process.Id == 11) is { IsRunning: false, OutboundBytes: 1536 },
        "exited processes retain their final outbound total and reject later sends");
    trafficTracker.Start(new(11, 10, "reused-sender.exe", "", 200));
    trafficTracker.RecordNetworkSend(11, 256, "::ffff:8.8.8.8");
    var trafficRows = trafficTracker.GetSnapshot()!.Processes;
    Check(trafficRows.Single(x => x.Process.StartTimeUtcTicks == 120).OutboundBytes == 1536
        && trafficRows.Single(x => x.Process.StartTimeUtcTicks == 200).OutboundBytes == 256,
        "PID reuse starts independent outbound accounting including mapped public IPv4 addresses");
    var trafficRoundTrip = Protocol.Deserialize<WireMessage>(Protocol.Serialize(new WireMessage("statistics", Processes: trafficRows)));
    Check(trafficRoundTrip!.Processes!.Single(x => x.Process.StartTimeUtcTicks == 120).OutboundBytes == 1536,
        "outbound byte totals survive process snapshot transport and journal serialization");
    var legacyRow = Protocol.Deserialize<TrackedProcess>("{\"Process\":{\"Id\":10,\"ParentId\":1,\"Name\":\"old.exe\",\"Path\":\"\",\"StartTimeUtcTicks\":100},\"IsRoot\":true,\"IsRunning\":true}");
    Check(legacyRow!.OutboundBytes == 0 && legacyRow.OutboundTrafficText == "0 B",
        "old process snapshots without traffic remain readable");
    Check(trafficRows.Single(x => x.Process.StartTimeUtcTicks == 120).OutboundTrafficText == "1.5 KB"
        && ByteSize.Format(1024 * 1024) == "1 MB" && ByteSize.Format(1024L * 1024 * 1024 * 1024) == "1 TB",
        "outbound traffic uses readable byte units");
    trafficTracker.RecordNetworkSend(11, long.MaxValue, "8.8.8.8");
    trafficTracker.RecordNetworkSend(11, 1, "8.8.8.8");
    Check(trafficTracker.GetSnapshot()!.Processes.Single(x => x.Process.StartTimeUtcTicks == 200).OutboundBytes == long.MaxValue,
        "very large cumulative outbound totals saturate instead of wrapping negative");

    var launcher = new ProcessInfo(20, 1, "explorer.exe", @"C:\Windows\explorer.exe", 10);
    var application = new ProcessInfo(21, 20, "app.exe", @"C:\Apps\app.exe", 20);
    var helper = new ProcessInfo(22, 21, "helper.exe", @"C:\OtherRuntime\helper.exe", 30);
    var chosen = new ProcessInfo(23, 22, "worker.exe", @"C:\OtherRuntime\worker.exe", 40);
    var sibling = new ProcessInfo(24, 21, "sibling.exe", @"C:\Apps\sibling.exe", 50);
    var unrelated = new ProcessInfo(25, 20, "unrelated.exe", @"C:\OtherApp\unrelated.exe", 60);
    ProcessInfo[] familySnapshot = [chosen, sibling, launcher, helper, application, unrelated];
    var family = ProcessFamilyResolver.Resolve(chosen, familySnapshot);
    Check(family.Root == application && family.Lineage.SequenceEqual(new[] { chosen, helper, application }),
        "child selection resolves its application root across different helper executable paths");
    var familyTracker = new ProcessTracker(family.Root, true, family.Selected, family.Lineage);
    familyTracker.Seed(familySnapshot);
    var familyRoster = familyTracker.GetSnapshot()!.Processes;
    Check(familyRoster.Count == 4 && familyTracker.Find(sibling.Id) is not null && familyTracker.Find(launcher.Id) is null
        && familyTracker.Find(unrelated.Id) is null, "program scope includes parents, siblings and descendants while excluding Explorer and unrelated apps");
    Check(familyRoster.Single(x => x.IsRoot).RoleText == "主进程" && familyRoster.Single(x => x.Process.Id == helper.Id).RoleText == "父进程"
        && familyRoster.Single(x => x.IsSelected).Process == chosen, "process roster identifies application root, intermediate parents and exact selection");
    Check(familyTracker.Start(new(26, 24, "later-child.exe", "", 70)), "new descendants of an existing sibling are followed");
    Check(ProcessFamilyResolver.Resolve(sibling, familySnapshot).Root == application, "different children resolve to the same program root");
    foreach (string boundaryName in new[] { "cmd.exe", "pwsh.exe", "WindowsTerminal.exe", "svchost.exe", "services.exe", "ProcessSentinel.exe" })
        Check(ProcessFamilyResolver.Resolve(chosen, familySnapshot.Select(x => x.Id == launcher.Id ? x with { Name = boundaryName } : x)).Root == application,
            "program scope stops before shared launcher " + boundaryName);
    Check(ProcessFamilyResolver.Resolve(chosen, familySnapshot.Where(x => x.Id != helper.Id)).Root == chosen,
        "missing parent leaves the selected subtree explicit rather than guessing by process name");
    Check(ProcessFamilyResolver.Resolve(chosen, familySnapshot.Select(x => x.Id == helper.Id ? x with { StartTimeUtcTicks = 1000 } : x)).Root == chosen,
        "reused parent PID cannot expand monitoring into an unrelated newer process");
    Check(ProcessFamilyResolver.Resolve(chosen, familySnapshot.Select(x => x.Id == helper.Id ? x with { StartTimeUtcTicks = 0 } : x)).Root == chosen,
        "unreadable parent identity stops automatic expansion");
    var cyclic = ProcessFamilyResolver.Resolve(chosen, familySnapshot.Select(x => x.Id == application.Id ? x with { ParentId = helper.Id, StartTimeUtcTicks = 30 } : x));
    Check(cyclic.Lineage.Count == 3 && cyclic.Boundary.Contains("循环"), "cyclic parent chain terminates safely");
    bool rejectedSelection = false;
    try { ProcessFamilyResolver.Resolve(chosen with { StartTimeUtcTicks = 999 }, familySnapshot); }
    catch (InvalidOperationException) { rejectedSelection = true; }
    Check(rejectedSelection, "stale selected PID identity is rejected");
    familyTracker.Stop(chosen.Id);
    Check(familyTracker.Start(chosen with { StartTimeUtcTicks = 100, Name = "reused-worker.exe" })
        && !familyTracker.GetSnapshot()!.Processes.Single(x => x.Process.Id == chosen.Id && x.IsRunning).IsSelected,
        "reused selected PID does not inherit the original selection role");
    var familyRequestCopy = Protocol.Deserialize<MonitorRequest>(Protocol.Serialize(family.Request));
    Check(familyRequestCopy?.Root == application && familyRequestCopy.SelectedProcess == chosen,
        "family protocol preserves both effective root and original selected process");
    var legacyRequest = Protocol.Deserialize<MonitorRequest>("{\"Root\":" + Protocol.Serialize(root) + ",\"IncludeChildren\":true}");
    Check(legacyRequest?.Root == root && legacyRequest.SelectedProcess is null, "older single-root requests remain readable");
    var catalog = ProcessCatalog.Snapshot();
    Check(catalog.Any(x => x.Id == Environment.ProcessId && x.StartTimeUtcTicks > 0 && File.Exists(x.Path)), "real Windows process snapshot");

    string temp = Path.Combine(Path.GetTempPath(), "ProcessSentinel-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        string journalPath = Path.Combine(temp, "evidence.jsonl");
        using (var journal = new EvidenceJournal(journalPath, new(root, true)))
        {
            var item = RiskEngine.Evaluate(FileEvent("读取", @"C:\Users\test\.ssh\id_rsa") with { Sequence = 1, Detail = "quoted \"value\"\nsecond line" });
            journal.Append(new("event", Event: item));
            journal.Append(new("statistics", EtwLost: 2, QueueLost: 3, ActiveProcesses: 1));
            journal.CopyTo(Path.Combine(temp, "snapshot.jsonl"));
            Check(EvidenceJournal.ReadEvents(journalPath).Single().Detail == item.Detail, "live journal supports reading and retains multiline evidence");
            EvidenceJournal.ExportCsv(Path.Combine(temp, "snapshot.jsonl"), Path.Combine(temp, "evidence.csv"));
            Check(File.ReadAllText(Path.Combine(temp, "evidence.csv")).Contains("sensitive-file"), "CSV export includes rule evidence");
        }
        File.AppendAllText(journalPath, "{\"Type\":\"event\",\"Event\":");
        Check(EvidenceJournal.ReadEvents(journalPath).Count() == 1, "interrupted journal tail is recoverable");
        Check(EvidenceJournal.CsvCell(" =HYPERLINK(\"http://localhost\")").StartsWith("\"'"), "CSV formula injection is neutralized");
        string reviewPath = Path.Combine(temp, "evidence.review.jsonl");
        using (var reviewJournal = new EvidenceJournal(reviewPath, new(root, true), reviewOnly: true))
        {
            var attention = FileEvent("读取", @"C:\Users\test\.ssh\id_rsa") with { Sequence = 71, Risk = RiskLevel.Attention, RuleId = "review-test", Reason = "review fixture" };
            var high = attention with { Sequence = 72, Risk = RiskLevel.High };
            reviewJournal.Append(new("event", Event: attention));
            for (int i = 0; i < 12000; i++) reviewJournal.Append(new("event", Event: FileEvent("读取", @"C:\ordinary.txt") with { Sequence = 100 + i }));
            reviewJournal.Append(new("event", Event: high));
            reviewJournal.Append(new("statistics", EtwLost: 2, QueueLost: 3));
            reviewJournal.CopyTo(Path.Combine(temp, "review-snapshot.jsonl"));
            var reviewEvents = EvidenceJournal.ReadEvents(reviewPath).ToArray();
            Check(reviewEvents.Length == 2 && reviewEvents[0].Sequence == 71 && reviewEvents[1].Risk == RiskLevel.High,
                "review journal retains both risk levels while excluding 12,000 ordinary events");
            using var reviewStream = new FileStream(reviewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reviewReader = new StreamReader(reviewStream);
            string reviewText = reviewReader.ReadToEnd();
            Check(reviewText.Contains("\"JournalType\":\"review\"") && reviewText.Contains("\"EtwLost\":2") && reviewText.Contains("\"QueueLost\":3"),
                "review journal identifies its scope and preserves session and loss metadata");
            EvidenceJournal.ExportCsv(Path.Combine(temp, "review-snapshot.jsonl"), Path.Combine(temp, "review.csv"));
            Check(File.ReadAllLines(Path.Combine(temp, "review.csv")).Length == 3 && File.ReadAllText(Path.Combine(temp, "review.csv")).Contains("review-test"),
                "review-only live snapshot exports complete evidence without ordinary records");
        }
        var eventCopy = Protocol.Deserialize<WireMessage>(Protocol.Serialize(new WireMessage("event", Event: FileEvent("读取", @"C:\测试\文件.txt"))));
        Check(eventCopy?.Event?.Target == @"C:\测试\文件.txt", "Chinese event protocol roundtrip");
        var rosterCopy = Protocol.Deserialize<WireMessage>(Protocol.Serialize(new WireMessage("ready",
            ActiveProcesses: exitedRoot.ActiveCount, Processes: exitedRoot.Processes)));
        Check(rosterCopy?.Processes?.Count == 3 && rosterCopy.Processes.Single(x => x.IsRoot).IsRunning == false
            && rosterCopy.Processes.Any(x => x.Process.Id == 12 && x.IsRunning), "process roster protocol retains ancestry and exit state");

        string marker = Path.Combine(temp, "resumed.txt");
        using (var suspended = SuspendedProgram.Create(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c echo benign>\"{marker}\""))
        {
            using var process = Process.GetProcessById(suspended.Id);
            await Task.Delay(200);
            Check(!File.Exists(marker) && !process.HasExited, "launched target remains suspended before monitoring is ready");
            suspended.Resume();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(File.Exists(marker), "target resumes and executes only after explicit resume");
        }
        marker = Path.Combine(temp, "never-run.txt");
        int canceledId;
        using (var suspended = SuspendedProgram.Create(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c echo benign>\"{marker}\"")) canceledId = suspended.Id;
        await Task.Delay(100);
        Check(!File.Exists(marker) && !ProcessCatalog.Snapshot().Any(x => x.Id == canceledId), "canceling launch removes only the unstarted suspended target");
    }
    finally { Directory.Delete(temp, true); }

    if (args.Contains("--integration"))
    {
        if (!EtwMonitor.IsAdministrator()) { Console.Error.WriteLine("ETW INTEGRATION REQUIRES ADMINISTRATOR. Run this command in an elevated terminal."); return 2; }
        string directory = Path.Combine(Path.GetTempPath(), "ProcessSentinel-probe-" + Guid.NewGuid().ToString("N"));
        var captured = new ConcurrentQueue<Activity>();
        using var program = SuspendedProgram.Create(Environment.ProcessPath!, "--fixture \"" + directory + "\"");
        using var process = Process.GetProcessById(program.Id);
        using var monitor = new EtwMonitor();
        monitor.ActivityReceived += captured.Enqueue;
        monitor.Start(new(ProcessCatalog.Get(program.Id), true));
        program.Resume();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(1500);
        monitor.Stop();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var events = captured.ToArray();
        var report = Path.Combine(AppContext.BaseDirectory, "integration-events.jsonl");
        await File.WriteAllLinesAsync(report, events.Select(item => Protocol.Serialize(new WireMessage("event", Event: item))));
        Console.WriteLine("Integration evidence: " + report);
        Check(events.Any(x => x.Kind == ActivityKind.File && x.Operation == "写入" && x.Target.Contains("ProcessSentinel-probe-")), "ETW real file write attribution");
        Check(events.Any(x => x.Kind == ActivityKind.File && x.Operation == "读取" && x.Target.Contains("ProcessSentinel-probe-")), "ETW real file read attribution");
        Check(events.Any(x => x.Kind == ActivityKind.Network && x.Operation == "发送" && x.Bytes > 0 && x.Detail.StartsWith("TCP/IPv4")), "ETW IPv4 TCP send attribution");
        Check(events.Any(x => x.Kind == ActivityKind.Network && x.Operation == "接收" && x.Bytes > 0 && x.Detail.StartsWith("TCP/IPv4")), "ETW IPv4 TCP receive attribution");
        Check(events.Any(x => x.Kind == ActivityKind.Network && x.Operation == "发送" && x.Detail.StartsWith("UDP/IPv4")), "ETW UDP send attribution");
        if (Socket.OSSupportsIPv6) Check(events.Any(x => x.Kind == ActivityKind.Network && x.Operation == "发送" && x.Detail.StartsWith("TCP/IPv6")), "ETW IPv6 TCP send attribution");
        Check(events.Any(x => x.Kind == ActivityKind.Registry && x.Operation == "设置值" && x.Target.Contains("ProcessSentinelSelfTest", StringComparison.OrdinalIgnoreCase)), "ETW registry path attribution");
        Check(events.Any(x => x.Kind == ActivityKind.Process && x.Operation == "启动" && x.Target.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)), "ETW child process attribution");
        Check(monitor.EventsLost == 0, "ETW probe had no reported event loss");
        Directory.Delete(directory, true);
    }
    else Console.WriteLine("ETW integration not run (use --integration as administrator).");
    Console.WriteLine($"ALL {passed} CHECKS PASSED");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
