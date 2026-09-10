# 第三方软件声明

Kill Control 使用下列第三方组件。Kill Control 自身采用 Apache License 2.0；第三方组件仍分别受其原始许可证或权利声明约束。本文件不替代任何第三方许可证。

本清单覆盖应用的直接与传递运行时依赖，以及自包含发布时一同分发的运行时组件；仅供 SDK 在构建期间使用且不会进入发布产物的工具不在清单范围内。

## 依赖清单

| 组件 | 版本 | 引入方式 | 许可证或权利状态 | 版权所有者/作者 | 上游项目 |
| --- | --- | --- | --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.11 | 直接 NuGet 依赖 | MIT | Microsoft Corporation；.NET Foundation and Contributors | [dotnet/dotnet（对应提交）](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32/src/efcore/src/Microsoft.Data.Sqlite.Core) |
| Microsoft.Data.Sqlite.Core | 10.0.11 | 传递 NuGet 依赖 | MIT | Microsoft Corporation；.NET Foundation and Contributors | [dotnet/dotnet（对应提交）](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32/src/efcore/src/Microsoft.Data.Sqlite.Core) |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | 传递 NuGet 依赖 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC | [SQLitePCL.raw v2.1.12](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12) |
| SQLitePCLRaw.core | 2.1.12 | 传递 NuGet 依赖 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC | [SQLitePCL.raw v2.1.12](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12) |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | 传递 NuGet 依赖，包含原生 SQLite 库 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC | [SQLitePCL.raw v2.1.12](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12) |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | 传递 NuGet 依赖 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC | [SQLitePCL.raw v2.1.12](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12) |
| SQLite | 3.53.3 | 由 SQLitePCLRaw.lib.e_sqlite3 2.1.12 捆绑的原生库 | Public Domain（公有领域） | SQLite authors | [SQLite 版权说明](https://www.sqlite.org/copyright.html) |
| Microsoft .NET Runtime 和 WPF | 10.0.11（当前构建环境） | 目标框架；自包含发布时随程序分发 | MIT，并包含各自的第三方声明 | .NET Foundation and Contributors | [.NET Runtime](https://github.com/dotnet/runtime)、[WPF](https://github.com/dotnet/wpf) |

依赖版本以 `Kill/Kill.csproj` 及还原后的依赖图为准。更换 SDK 或发布运行时后，.NET Runtime/WPF 的实际补丁版本可能变化，发布前应同步复核本文件及上游第三方声明。

## Apache License 2.0 组件

SQLitePCLRaw 系列包按 Apache License 2.0 授权。许可证全文与 Kill Control 的项目许可证相同，见仓库根目录的 `LICENSE`。

上游许可证：[SQLitePCL.raw v2.1.12 LICENSE](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT)

### SQLitePCLRaw 适用归属声明

以下内容保留自 SQLitePCLRaw v2.1.12 的上游 `NOTICE.TXT`；当前项目未引用 SQLCipher 或 OpenSSL 变体，因此不包含该文件中与这些变体专属的声明。

- SQLitePCL.raw 从 2.0 起使用 SourceGear 名义；此前使用的 Zumero 是 SourceGear 的商业别名，开源许可证始终为 Apache License 2.0。
- Copyright © Microsoft Open Technologies, Inc. All Rights Reserved. Licensed under the Apache License, Version 2.0.
- SQLite blessing: “May you do good and not evil. May you find forgiveness for yourself and forgive others. May you share freely, never taking more than you give.”

上游完整声明：[SQLitePCL.raw v2.1.12 NOTICE](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/NOTICE.TXT)

## MIT 组件

Microsoft.Data.Sqlite、Microsoft.Data.Sqlite.Core、.NET Runtime 和 WPF 采用 MIT License。相关上游声明：

Microsoft.Data.Sqlite NuGet 包元数据同时载有：© Microsoft Corporation. All rights reserved.

- [Microsoft.Data.Sqlite 对应源码提交 LICENSE](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT)
- [.NET Runtime 10.0.11 LICENSE](https://github.com/dotnet/runtime/blob/v10.0.11/LICENSE.TXT)
- [.NET Runtime 10.0.11 THIRD-PARTY-NOTICES](third-party/dotnet-runtime-10.0.11-THIRD-PARTY-NOTICES.txt)（[上游版本](https://github.com/dotnet/runtime/blob/v10.0.11/THIRD-PARTY-NOTICES.TXT)）
- [WPF 10.0.11 LICENSE](https://github.com/dotnet/wpf/blob/v10.0.11/LICENSE.TXT)
- [WPF 10.0.11 THIRD-PARTY-NOTICES](third-party/wpf-10.0.11-THIRD-PARTY-NOTICES.txt)（[上游版本](https://github.com/dotnet/wpf/blob/v10.0.11/THIRD-PARTY-NOTICES.TXT)）

MIT License 文本：

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SQLite

`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 随 Windows 发布产物提供 SQLite 3.53.3 原生库。SQLite 作者已将其可交付代码和文档奉献给公有领域，允许任何人出于任何目的复制、修改、发布、使用、编译、销售或分发。

官方权利声明：[SQLite Is Public Domain](https://www.sqlite.org/copyright.html)
