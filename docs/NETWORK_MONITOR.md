# 网络监控

从顶部“网络监控”进入，点击“开始监控”并完成 Windows 授权。仅内部采集组件提权；主界面及应用、服务管理的权限规则不变。切换页面保留采集，停止全部监控或关闭主窗口会停止采集并接收最终统计。重新开始清空实时统计，已保存的连接历史继续保留。

## 指标与统计口径

连接表支持 IPv4/IPv6、TCP 连接/监听以及 UDP 绑定端点。每行包含进程/PID、本地和远端 IP:端口、协议、状态、发送/接收速率、累计发送/接收、观测时长。选中后显示完整路径、首次发现时间和精确字节数。IPv6 地址用方括号与端口分隔。

端点来自 Windows IP Helper OWNER_PID 表，传输字节来自内核 TCP/IP ETW 事件的 size 字段。速率是相邻采样间隔的字节差，每秒刷新，保留到显示时会有采样延迟。它不是进程 I/O、网卡线速或运营商计费流量；包括本机与局域网，双方在本机时会分别归属到各自端点。没有捕获内容、DNS 解析或外部探测。

已有连接无法追溯建立时间或启动前流量。观测时长从本次首次发现到监控停止、进程退出或连接结束；TCP 表消失有约两秒容错。UDP 绑定不代表已与某个远端连接，发生传输后按远端区分；绑定存在时，30 秒无事件显示 UDP 空闲。进程名/路径无法读取时保留 PID 并明确缺失。

统计按协议、进程实例（PID + 创建时间）、本地/远端地址和端口归属。TCP 连接标识变化会拆分重连记录。地址表与 ETW 分别采样，短连接、缓冲延迟、未知进程创建时间及 IPv6 scope 信息不足仍可能造成缺失或拆分；底栏的 ETW 丢失数用于提示已知采集损失。

实时列表最多保留 5,000 行。达到上限优先淘汰已结束记录，否则省略新记录；底栏“列表截断”是淘汰/省略更新次数，不是唯一连接数。总字节继续计入，即使对应连接行未能保留。显示连接数指当前保留的活动连接/端点（含 UDP 绑定与远端活动），不是系统 socket 总数。刷新失败会显示原因，并保留上次连接状态。

## 每个进程的连接历史

点击右上方“连接历史”，在左侧选择进程，右侧显示其远端 IP、端口、TCP/UDP、首次和最后观测时间、累计收发及最后状态。选中实时连接后打开历史，会优先定位该进程。左侧按进程名/PID/路径搜索，右侧按远端 IP/端口/协议筛选，输入后按回车或点击搜索；每页 100 条。选中历史连接可查看完整本地与远端地址、进程路径、最后活动和结束观测时间。采集中可点击“刷新历史”读取新记录。

历史保存在程序目录 `data/network-history.db`，使用 SQLite，停止、重新开始和退出后保留，不自动清除旧记录。只浏览历史无需提权或开始采集。按监控会话、PID 和进程创建时间区分进程实例，同名或重用的 PID 不会串入原实例。相同连接多次快照更新已有记录，不重复累计字节；不同连接标识的重连分别保存。

历史写入由普通权限主程序在后台接收快照时完成，停止会等待最终快照保存。写入失败会在网络面板底栏明确提示，采集仍继续，失败期间的历史可能不完整。程序异常中断或实时记录被淘汰时，历史仅能说明最后一次观测，不能据此断定记录当前仍连接。没有真实远端的监听/UDP 绑定不进入连接历史；已有 TCP 连接即使本次没有传输字节，也可记录观测到的远端。

历史来源于每秒收到的连接快照，无法恢复启动监控前记录，也可能遗漏采集丢失或在快照前因 5,000 行上限被淘汰的短连接。旧版仅保存在内存的记录不会自动补录。没有捕获数据包内容或上传历史。

## 开发验证

`build.ps1` 运行格式验证、Release 构建、管理/网络/历史基础自检、行为自检及 WPF 回放。所有历史测试注入独立临时数据库，不访问用户数据库。`Kill.exe --verify-network <report>` 单独请求采集授权，用它自己创建的 IPv4/IPv6 TCP/UDP 本机连接验证实际端口、双向字节与速率、导航、停止、关闭及数据库重开后的端点历史；不访问外部服务器，不修改系统配置。`Kill.exe --render-preview <png> Network` 渲染 1280 × 880 网络页，`NetworkHistory` 渲染 1180 × 720 历史窗口，均明确标记演示数据。

## 接口参考

- [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [GetExtendedUdpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)
- [MIB_TCP6ROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid)
- [MIB_UDP6ROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udp6row_owner_pid)
- [Microsoft TraceEvent](https://github.com/microsoft/perfview/tree/main/src/TraceEvent)
