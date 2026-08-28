# Windows 软件行为审计器 / 服务统计功能 TODO

> 面向 Codex 的开发规格草案
> 目标：开发一个 Windows 本地软件治理与后台行为审计工具。第一阶段重点实现“服务统计”，长期扩展到软件、服务、启动项、计划任务、驱动和网络行为的统一审计。

---

# 1. 项目定位

这个项目不是传统卸载器，也不是简单的 Windows 服务管理器。

核心目标：

> 让用户知道：电脑里安装了什么软件，以及这些软件在后台到底做了什么。

重点关注：

- 软件安装情况
- Windows Service
- Startup 启动项
- Scheduled Task 计划任务
- Driver 驱动
- 后台进程
- 网络活动
- 软件退出后仍然运行的后台组件
- 后台资源长期占用
- 服务被停止后自动复活

第一阶段不要做“大而全”。

第一版优先实现：

> Windows 服务生命周期统计 + 基础服务管理 + 本地历史记录

---

# 2. 第一阶段 MVP

## P0：必须完成

### 2.1 服务列表

列出本机 Windows Services。

字段至少包括：

- ServiceName
- DisplayName
- Description
- Status
- StartType
- ProcessId
- BinaryPath
- ServiceAccount
- Manufacturer / Publisher（如可获取）
- 是否属于 Microsoft
- 是否为第三方服务
- 是否自动启动

支持：

- 查看全部服务
- 只看 Running
- 只看 Stopped
- 只看 Automatic
- 只看第三方服务
- 搜索服务

---

### 2.2 服务详情

点击/查询某个服务时显示：

- 服务名称
- 显示名称
- 当前状态
- 启动类型
- 可执行文件路径
- PID
- 服务账户
- 描述
- 当前启动时间
- 本次运行时长
- 累计启动次数
- 累计运行时长
- 最近一次停止时间
- 最近一次 Exit Code
- 异常停止次数
- 自动复活次数

---

### 2.3 服务生命周期监控

程序需要持续观察：

```text
Stopped -> Running
Running -> Stopped
```

每次服务启动，创建一个 Session。

数据模型示例：

```text
ServiceSession
├── id
├── service_name
├── process_id
├── started_at
├── stopped_at
├── duration_seconds
├── exit_code
├── stop_reason
├── cpu_time_seconds
├── peak_memory_bytes
└── unexpected_stop
```

服务仍在运行时：

```text
stopped_at = NULL
```

---

### 2.4 启动次数统计

统计维度：

- 本次开机启动次数
- 24 小时启动次数
- 7 天启动次数
- 30 天启动次数
- 历史总启动次数

示例：

```text
AdobeUpdateService

本次开机启动：  1
7天启动：       7
30天启动：      31
历史启动：      128
```

---

### 2.5 运行时长统计

统计：

- 当前运行时长
- 本次开机累计运行时间
- 24 小时累计运行时间
- 7 天累计运行时间
- 30 天累计运行时间
- 历史累计运行时间
- 平均单次运行时间
- 最长单次运行时间

---

### 2.6 驻留率

定义：

```text
Resident Ratio =
服务运行时间 / 系统开机时间
```

例如：

```text
系统开机：10小时
服务运行：9小时45分钟

驻留率：97.5%
```

统计：

- 本次开机驻留率
- 7 天驻留率
- 30 天驻留率

---

### 2.7 服务启动类型统计

需要区分：

- Automatic
- Automatic (Delayed Start)
- Manual
- Disabled
- Trigger Start（若能识别）

并统计服务实际行为是否与启动类型一致。

例如：

```text
StartType: Manual
30天启动次数: 284

提示：
该服务虽然配置为 Manual，但启动频率很高。
```

---

### 2.8 基础服务管理

支持：

```text
start
stop
restart
set-startup automatic
set-startup manual
set-startup disabled
```

默认要求管理员权限。

所有修改必须记录操作日志。

---

# 3. 自动复活检测

这是核心功能之一。

场景：

```text
14:31:02 Service stopped
14:31:07 Service started
```

如果服务停止后在较短时间内重新启动：

```text
restart_interval <= threshold
```

则记录为：

```text
AutoRestartEvent
```

默认阈值建议：

```text
60 秒
```

数据字段：

```text
AutoRestartEvent
├── service_name
├── stopped_at
├── restarted_at
├── interval_seconds
├── suspected_source
└── confidence
```

UI 示例：

```text
⚠ 自动复活

AdobeUpdateService

关闭后 5 秒重新启动。

过去 30 天：
自动复活 19 次
```

---

# 4. 异常停止统计

尽可能判断：

- 用户主动停止
- 系统正常停止
- 服务异常退出
- 进程 Crash
- Service Control Manager 重启
- 系统关机导致停止

若无法精准判断，可以先分类为：

```text
Normal
Unexpected
Unknown
```

---

# 5. CPU 与内存统计

第一版可以按低频采样实现。

例如每：

```text
5 秒 / 10 秒
```

采集运行中服务对应进程：

- CPU Time
- Working Set
- Private Bytes
- Peak Working Set

不建议第一版高频采样。

重点统计：

- 累计 CPU 时间
- 当前内存
- 平均内存
- 峰值内存

示例：

```text
Service: ExampleService

CPU累计时间：1h 42m
平均内存：   82 MB
峰值内存：   410 MB
```

---

# 6. 本次开机统计

首页应提供：

```text
System Uptime
Total Services
Running Services
Stopped Services
Automatic Services
Third-party Services
```

示例：

```text
本次开机：6小时42分钟

服务总数：168
正在运行：93
自动启动：74
第三方服务：41
```

---

# 7. TOP 排行

实现至少以下排行：

## 启动次数 TOP

```text
1. Service A    42
2. Service B    31
3. Service C    18
```

## 累计运行时间 TOP

## CPU 时间 TOP

## 峰值内存 TOP

## 自动复活 TOP

## 驻留率 TOP

---

# 8. GUI 退出后运行时长（P1）

长期目标需要建立：

```text
Software
↓
Foreground Process
↓
Background Process
↓
Windows Service
```

第一阶段暂不强制实现软件归属关联。

未来需要支持：

```text
Foreground Usage Time
Background Runtime
Background / Foreground Ratio
```

定义：

```text
Background Foreground Ratio =
后台运行时间 / 前台使用时间
```

示例：

```text
Adobe Creative Cloud

前台使用：2.7小时
后台运行：612小时

后台/前台比：226x
```

这是长期核心指标。

---

# 9. 软件统一模型（P1/P2）

未来不要只围绕 Service 建模。

建立 Software Entity：

```text
Software
├── id
├── name
├── publisher
├── install_path
├── version
├── uninstall_command
├── services[]
├── processes[]
├── startup_items[]
├── scheduled_tasks[]
├── drivers[]
└── network_activity[]
```

最终希望软件详情页类似：

```text
Adobe Creative Cloud

Installed: Yes

后台组件
---------------------
Services        6
Scheduled Tasks 8
Startup Items   3
Drivers         1

30天行为
---------------------
启动次数        84
后台运行        612小时
CPU时间         14.2小时
自动复活        19次
```

---

# 10. 网络行为（P2）

后续实现：

- 当前外部 TCP 连接
- 每个进程连接次数
- 远程地址
- 远程端口
- DNS / Host
- 上传流量
- 下载流量
- GUI 退出后是否继续联网

注意：

第一版不要做深度包检测（DPI）。

不要抓取用户网络内容。

只做元数据级统计。

---

# 11. 本地数据库

使用 SQLite。

建议：

```text
data/
└── audit.db
```

主要表：

## services

```sql
CREATE TABLE services (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name TEXT NOT NULL UNIQUE,
    display_name TEXT,
    description TEXT,
    binary_path TEXT,
    start_type TEXT,
    service_account TEXT,
    first_seen_at DATETIME,
    last_seen_at DATETIME
);
```

## service_sessions

```sql
CREATE TABLE service_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name TEXT NOT NULL,
    process_id INTEGER,
    started_at DATETIME NOT NULL,
    stopped_at DATETIME,
    duration_seconds INTEGER,
    exit_code INTEGER,
    stop_reason TEXT,
    cpu_time_seconds REAL,
    peak_memory_bytes INTEGER,
    unexpected_stop INTEGER DEFAULT 0
);
```

## service_samples

```sql
CREATE TABLE service_samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name TEXT NOT NULL,
    process_id INTEGER,
    sampled_at DATETIME NOT NULL,
    cpu_time_seconds REAL,
    working_set_bytes INTEGER,
    private_bytes INTEGER
);
```

## service_restart_events

```sql
CREATE TABLE service_restart_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name TEXT NOT NULL,
    stopped_at DATETIME NOT NULL,
    restarted_at DATETIME NOT NULL,
    interval_seconds INTEGER,
    suspected_source TEXT
);
```

## operations

```sql
CREATE TABLE operations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    operation TEXT NOT NULL,
    target TEXT NOT NULL,
    created_at DATETIME NOT NULL,
    result TEXT,
    detail TEXT
);
```

---

# 12. 数据采集架构

推荐：

```text
Windows
│
├── Service Control Manager
├── Windows Event Log
├── WMI / CIM
├── Process API
├── Performance Counters
│
▼
Collector
│
▼
Normalizer
│
▼
SQLite
│
▼
Statistics Engine
│
├── Session Statistics
├── Restart Detection
├── Resident Ratio
└── Rankings
│
▼
CLI / GUI
```

---

# 13. Windows API / 技术来源

可以优先研究：

- Windows Service Control Manager API
- WMI / CIM
- Win32_Service
- Windows Event Log
- Get-Service
- Get-CimInstance
- Performance Counters
- Process APIs
- Windows Registry

Codex 开发时：

> 优先使用正式 Windows API / .NET API，不要依赖解析 PowerShell 文本输出作为核心实现。

PowerShell 可以用于开发期验证。

---

# 14. 技术栈

优先建议：

## Option A：C# / .NET

推荐。

原因：

- Windows 原生能力访问方便
- Service API 支持好
- WMI / Event Log / Registry 生态完整
- 后续做 GUI 容易
- 可以使用 WinUI / WPF

建议：

```text
.NET 10+
C#
SQLite
```

第一版可以先 CLI。

---

# 15. 项目目录建议

```text
src/
├── App/
├── Core/
│   ├── Models/
│   ├── Interfaces/
│   └── Enums/
│
├── Windows/
│   ├── Services/
│   ├── Processes/
│   ├── Events/
│   ├── Performance/
│   └── Registry/
│
├── Collector/
│   ├── ServiceCollector.cs
│   ├── ProcessSampler.cs
│   └── RestartDetector.cs
│
├── Statistics/
│   ├── ServiceStatistics.cs
│   ├── RankingService.cs
│   └── ResidentCalculator.cs
│
├── Storage/
│   ├── Database.cs
│   └── Repositories/
│
├── Cli/
└── Tests/
```

---

# 16. CLI 草案

```bash
audit services
```

列出全部服务。

```bash
audit services --running
```

```bash
audit services --third-party
```

```bash
audit service info AdobeUpdateService
```

```bash
audit service stats AdobeUpdateService
```

```bash
audit service start NAME
audit service stop NAME
audit service restart NAME
```

```bash
audit stats boot
```

本次开机统计。

```bash
audit stats 7d
```

```bash
audit top runtime
audit top starts
audit top cpu
audit top memory
audit top restart
audit top resident
```

---

# 17. 安全要求

这是系统级工具，必须非常谨慎。

## 所有危险操作默认需要确认

例如：

```text
Stop service?
Disable service?
```

## Microsoft 核心服务保护

第一版建立 Critical Service List。

禁止默认：

```text
disable
stop
remove
```

关键系统服务。

必须：

```text
--force
```

才能执行高风险操作。

---

# 18. 隐私原则

项目本身必须坚持：

> 所有统计默认只保存在本机。

第一版：

- 不上传服务器
- 不需要账号
- 不需要遥测
- 不采集文件内容
- 不采集网络内容
- 不读取用户文档
- 不抓 HTTPS 内容

只统计：

```text
Process
Service
Runtime
CPU
Memory
Network Metadata
```

未来即使增加云功能，也必须显式 opt-in。

---

# 19. 性能要求

Collector 本身不能成为新的“后台垃圾”。

目标：

```text
Idle CPU < 0.5%
Memory < 100MB
```

尽量事件驱动。

避免：

```text
每秒遍历全部 Windows Service
```

采样型指标可以：

```text
5~10 秒
```

事件类数据使用事件通知。

---

# 20. 第一版明确不做

为了控制范围，MVP 不做：

- 软件卸载器
- Driver 管理
- 网络抓包
- DPI
- 防病毒
- 防火墙
- 文件行为监控
- Registry 全量监控
- 云同步
- 用户账号
- AI 分析
- 自动判断“恶意软件”
- 自动禁用服务

第一版只解决：

> Windows Service 在什么时候启动、运行多久、消耗多少资源、停止后是否自动复活。

---

# 21. MVP 验收标准

完成后应该可以回答：

```text
1. 当前有哪些服务正在运行？
2. 哪些是第三方服务？
3. 某服务什么时候启动的？
4. 已经运行多久？
5. 今天启动了多少次？
6. 过去 7 天启动多少次？
7. 过去 30 天累计运行多久？
8. 驻留率是多少？
9. CPU 时间是多少？
10. 峰值内存是多少？
11. 服务是否发生异常停止？
12. 服务被停止后是否自动复活？
13. 哪些服务启动最频繁？
14. 哪些服务运行时间最长？
15. 哪些服务长期常驻？
```

如果以上问题可以稳定回答，则 MVP 成立。

---

# 22. 后续方向

## P1

- Startup 启动项
- Scheduled Tasks
- 软件与 Service 归属
- 软件前台使用时间
- GUI 退出后后台运行时间
- 后台 / 前台比

## P2

- 网络连接统计
- 上传/下载统计
- Driver
- 软件行为时间线
- 常驻评分 Resident Score

## P3

形成完整：

> Windows Software Governance / Software Behavior Audit

产品最终回答的问题：

> “这个软件安装以后，到底在我的电脑里做了什么？”

---

# 23. TODO

## P0

- [ ] 初始化 .NET/C# 项目
- [ ] 建立 Core Models
- [ ] 建立 SQLite 数据库
- [ ] 实现 Win32 Service 列表
- [ ] 实现 Service Detail
- [ ] 实现运行状态监听
- [ ] 实现 Service Session
- [ ] 实现启动次数
- [ ] 实现运行时长
- [ ] 实现系统 Uptime
- [ ] 实现驻留率
- [ ] 实现 CPU 时间
- [ ] 实现内存采样
- [ ] 实现异常停止记录
- [ ] 实现自动复活检测
- [ ] 实现 TOP Rankings
- [ ] 实现 start/stop/restart
- [ ] 实现操作日志
- [ ] 实现核心服务保护
- [ ] 实现 CLI

## P1

- [ ] GUI
- [ ] Startup Items
- [ ] Scheduled Tasks
- [ ] 软件归属识别
- [ ] 前台使用时间
- [ ] GUI 退出后运行统计
- [ ] 后台 / 前台比

## P2

- [ ] Network Metadata
- [ ] 上传下载统计
- [ ] Drivers
- [ ] Resident Score
- [ ] Software Behavior Timeline

---

# 24. 开发原则

1. 不重新实现 Windows Service Manager。
2. 尽可能使用 Windows 原生 API。
3. 数据默认全部本地保存。
4. Collector 必须极轻量。
5. 不把软件“常驻”直接等同于恶意。
6. 只展示客观数据，让用户自己判断。
7. 所有高风险系统操作默认禁止或二次确认。
8. 第一阶段优先把统计做准确，再考虑 GUI。
9. 所有统计指标必须可追溯到原始 Session / Sample。
10. 为后续软件级统一行为审计预留扩展接口。

---

# 一句话产品定义

> 一个告诉用户“Windows 软件在后台到底干了什么”的本地软件行为审计工具。
