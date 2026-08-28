# Kill Control

Kill Control 是一款面向 Windows 的应用卸载、残留复核和后台服务管理工具，使用 Visual Studio 2026、.NET 10 和 WPF 开发。程序文件仍使用 `Kill.exe`，产品名称为 **Kill Control**。

## 核心功能

### 应用管理

- 从 32/64 位、当前用户和所有用户卸载注册表项读取桌面应用。
- 读取当前用户可卸载的 MSIX/AppX 商店应用。
- 优先运行应用登记的官方卸载程序，不模拟或替代厂商卸载流程。
- 扫描可与应用名称明确对应的安装目录、AppData、ProgramData 和注册表残留。
- 清理前逐项复核；高风险项目默认不选中，并要求输入应用名称确认。
- 默认将文件移入隔离区并导出注册表备份，支持查看隔离记录和恢复。
- 支持不可恢复的“直接清理”，执行前会再次给出高风险提示。

### 服务管理

- 默认列出正在运行且宿主可明确归为第三方软件的服务。
- 支持搜索服务名称、发布者、服务命令和宿主路径。
- 支持查看服务状态、启动方式、运行账户、进程 ID、宿主程序和服务模块。
- 支持定位宿主、禁止运行、列入/移出黑名单，以及备份配置后删除服务。
- Windows、Microsoft、驱动、关键服务和宿主来源不明确的服务会进入“系统保护”，只能查看和定位。
- 所有服务修改均通过 Windows UAC 提权，并在提权工作进程中重新读取和复核服务信息。

可从主窗口进入“服务管理”，也可以直接运行：

```powershell
.\Kill.exe --services
```

## 安全边界

不存在能够对任意 Windows 软件保证“完美卸载、零残留、零影响”的通用算法。便携软件、未登记的软件、其他 Windows 用户的商店包，以及多个程序共用的组件，都无法可靠地自动归属。

Kill Control 因此采用保守策略：

- 不做模糊名称匹配，不递归搜索整块磁盘。
- 不清理 Windows、Common Files、WindowsApps、磁盘根目录或常见共享目录。
- 无法明确归属的文件、目录、注册表项和服务宁可保留。
- 服务删除前必须导出注册表配置；建议优先选择“禁止运行”。
- 如果服务拒绝立即停止，Kill Control 会明确提示禁用状态将在重启后生效。
- 黑名单会记录原启动方式并禁用服务，但不是常驻拦截器；拥有管理员权限的软件仍可重新创建或修改自己的服务。

## 数据目录

| 内容 | 默认位置 |
| --- | --- |
| 临时提权请求 | `%LOCALAPPDATA%\Kill\Temp` |
| 隔离记录 | `%ProgramData%\Kill\History` |
| 系统盘隔离文件 | `%ProgramData%\Kill\Quarantine` |
| 服务注册表备份 | `%ProgramData%\Kill\ServiceBackups` |
| 服务黑名单 | `%ProgramData%\Kill\ServiceBlacklist.json` |
| 其他磁盘隔离文件 | `<磁盘根目录>\Kill Quarantine` |

恢复操作不会覆盖原位置已经存在的内容。其他磁盘使用同卷隔离目录，避免跨卷移动造成额外复制或中断风险。

## 项目结构

```text
KillControl/
├─ Kill/                 WPF 主程序
│  ├─ Models/            应用、残留和服务数据模型
│  ├─ Services/          枚举、扫描、安全策略和提权工作进程
│  └─ Themes/            WPF 公共样式
├─ Kill.SelfTest/        不修改系统状态的安全自检
├─ Kill.slnx             Visual Studio 解决方案
└─ AGENTS.md             后续开发约束
```

## 开发与构建

在 Visual Studio 2026 中打开 `Kill.slnx`，将 `Kill` 设置为启动项目，选择 `Debug | Any CPU` 后按 `F5`。

Release 构建：

```powershell
dotnet build .\Kill.slnx -c Release
```

检查代码格式：

```powershell
dotnet format .\Kill.slnx --verify-no-changes --verbosity minimal
```

运行只读安全自检：

```powershell
dotnet run --project .\Kill.SelfTest\Kill.SelfTest.csproj -c Release
```

生成自包含的 Windows x64 单文件版本：

```powershell
dotnet publish .\Kill\Kill.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\artifacts\Kill-portable-win-x64
```

`artifacts/`、`bin/`、`obj/` 和 Visual Studio 用户文件已由 `.gitignore` 排除。
