<h1 align="center">TinadecCore</h1>

<p align="center">
  <b>MIT 开源的智能体治理与协作运行时——TinadecOffice 的底层框架，也是你项目的底层框架。</b><br/>
  The open, MIT-licensed agent-governance and collaboration runtime behind TinadecOffice — and yours.
</p>

<p align="center">
  <a href="README.md"><img alt="English" src="https://img.shields.io/badge/English-README.md-2ea44f"></a>
  <a href="README.zh-CN.md"><img alt="中文" src="https://img.shields.io/badge/简体中文-当前版本-d9d9d9"></a>
</p>

<p align="center">
  <img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-green">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet">
  <img alt="Microsoft Agent Framework" src="https://img.shields.io/badge/Microsoft_Agent_Framework-1.18-0B5FFF">
  <img alt="Storage" src="https://img.shields.io/badge/Storage-SQLite%20%2B%20PostgreSQL-336791">
  <img alt="Platforms" src="https://img.shields.io/badge/Platforms-Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-2ea44f">
</p>

<p align="center">
  <a href="https://github.com/Tinadec/TinadecOffice/commits"><img alt="Commits last month" src="https://img.shields.io/github/commit-activity/m/Tinadec/TinadecOffice?labelColor=32b583&color=12b76a"></a>
  <a href="https://discord.gg/EcKYQfbG72"><img alt="Discord" src="https://img.shields.io/badge/Discord-社区-5865F2?logo=discord&logoColor=white"></a>
  <a href="https://x.com/tinadecoffice"><img alt="X" src="https://img.shields.io/badge/X-%40tinadecoffice-000000?logo=x&logoColor=white"></a>
</p>

---

TinadecCore 是 Tinadec 产品族的**底层框架**：一个模块化的 .NET 10 运行时，用来构建**有真实治理能力的生产级多智能体系统**。[TinadecOffice](https://github.com/Tinadec/TinadecOffice) 的每一项公共能力——双层智能体编排（DmaEA）、持久化 run 引擎、权限与审批机制、工具提供者契约——都活在这里，且只活在这里。Office 是界面与布线；Core 是大脑。

我们以 **MIT 协议**发布它只有一个原因：让你可以嵌入它、改它、在它之上搭自己的智能体系统，而不用向任何人申请许可。

## 它是为你准备的吗？

如果你需要下面这些，TinadecCore 是对的框架：

- 做的是要真上生产的智能体系统，而不是聊天 demo
- 想要**治理层与执行层是不同东西**的双层架构——负责规划和评审的智能体、干活拿证据的智能体，中间隔着一道审批门
- run 能暂停、恢复、取消，进程重启后接着跑
- 关心治理：权限即数据、按 run 冻结工具清单、审批可审计
- 模型 provider（OpenAI 兼容 / Anthropic / CLI harness）可插拔，不换架构
- 想要一层可挂载的 ASP.NET Core HTTP 面，而不是被迫接受别人的整个单体

## 核心能力

- **DmaEA 双层编排** —— *治理层*（会议 / 规划 / 监督）编排按权限收窄的 *执行层* worker。治理智能体只声明工具、绝不亲手执行；派发权在 Core
- **持久化全双工 run** —— 幂等准入、上下文 revision、任务图、生成预算、暂停 / 恢复 / 取消、经租约检查点的重启恢复
- **权限即数据** —— 能力授权、资源包络边界、逐次审批门（人工、委托审查门、预授权租约），全程审计
- **受治理的工具面** —— 版本化工具提供者契约；[TinadecTools](https://github.com/Tinadec/TinadecOffice/tree/main/TinadecTools)（文件 / shell / git）和 MCP server 同走一清单，按 run 哈希冻结
- **模型与智能体中心** —— provider 实例、路由、逐智能体模型策略，准入时冻结
- **一切皆不可变版本** —— Agent/Mode/Prompt 版本、ETag 修订、发布后不可变的配置
- **存储抽象** —— EF Core LINQ 面；默认 SQLite，需要时 PostgreSQL；向量走 sqlite-vec 或 pgvector
- **可嵌入的 HTTP 层** —— 把 Core 路由挂进你自己的宿主：`AddTinadecCoreHttp()` + `MapTinadecCore()`。MAF 类型只存在内部适配器里，公共面对 provider 中立

<p align="center">
  <img src="docs/assets/dual-layer.svg" alt="双层 DmaEA：治理层编排执行层，每一次写调用都过审批门" width="100%" />
</p>

## 包

四个工程构成公共可打包面（`IsPackable=true`）：

| 包 | 职责 |
|----|------|
| `TinadecCore.Contracts` | HTTP DTO、事件 envelope、provider 中立数据类型；无 MAF、无 ASP.NET |
| `TinadecCore.Abstractions` | Core 端口与模块注册抽象；不暴露 MAF 类型 |
| `TinadecCore.Runtime` | 完整组合根；MAF 1.18 仅经 DmaEA 内部适配层接入 |
| `TinadecCore.AspNetCore` | 可挂载的 ASP.NET Core HTTP 层（`AddTinadecCoreHttp()` / `MapTinadecCore()`），任意宿主可嵌 |

`TinadecCore/` 下其它模块同样 MIT，但属实现包：与 Runtime 从同一 feed 还原，别当稳定 API 用。目前尚未发布到 nuget.org——今天从源码构建：

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
dotnet restore TinadecCore.slnx
dotnet build TinadecCore.slnx --no-restore
dotnet test TinadecCore.slnx --no-build

dotnet pack Contracts/TinadecCore.Contracts.csproj -c Release --no-restore
dotnet pack Abstractions/TinadecCore.Abstractions.csproj -c Release --no-restore
dotnet pack Runtime/TinadecCore.Runtime.csproj -c Release --no-restore
dotnet pack AspNetCore/TinadecCore.AspNetCore.csproj -c Release --no-restore
```

独立 API 宿主与容器切片见[打包与独立部署指南](https://github.com/Tinadec/TinadecOffice/blob/main/docs/tinadec-core-packaging.zh-CN.md)。

## 这个仓库为什么存在

这是最重要的一部分，也是整个仓库的意义：

- **我们把底层框架按 MIT 开源是经过考虑的。** 你可以把 TinadecCore 嵌进商业产品、内部平台、或另一个开源项目——无附加条件，Core 上无 copyleft。建在它之上的产品各有自己的许可证（见 [TinadecOffice 的许可证表](https://github.com/Tinadec/TinadecOffice#license)）。
- **TinadecOffice 不养私有 Core 分支。** Office 团队把这个仓库当作上游：Office 对 Core 的每一处改动，都会**以 PR 的形式**回流到这里，公开评审。世界上只有一份 Core，就在这个仓库。
- **你的项目也可以做同样的事。** 你把 TinadecCore 用起来之后，迟早要改它——新的工具授权器、新的存储后端、某条编排规则。Fork 它、改到合手，当你的修改对别人也有用时，**以 PR 的形式送回这个仓库**。Core 保持产品中立、可移植；属于你自己产品的那部分，留在你的仓库里。
- **这个回路本身才是产品。** Office 反哺 Core，外部用户反哺 Core，Core 再把长好的框架还给所有人。仓库存在，就是为了这个回路。

> [!TIP]
> 一次 Core PR 的及格线就一句话：*这对"不是 TinadecOffice 的某个智能体系统"有用吗？* 有用，就该进这里。没用，就留在你的产品层——包边界就是为这个造的。

## 感谢

TinadecCore 站在别人的工作上，我们明说：

- **[Microsoft Agent Framework](https://github.com/microsoft/agent-framework)** —— 我们的规范编排基线（MAF 1.18），被收在严格的内部适配器后面，让 Core 的契约对 provider 保持中立。MAF 的工程质量让这一切成为可能；这份 README 的骨架与克制，也明显借自 MAF。
- **每一位发 PR 的人** —— 包括那些我们最后没合的。一个被拒的 PR，也是你花在了让它更好的时间。

## 社区

- **QQ 群** —— `370780878`
- **飞书群** —— [点击加入](https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=7a3k9813-07f8-4e13-b00c-d9d1c07d539b)
- **Discord** —— [discord.gg/EcKYQfbG72](https://discord.gg/EcKYQfbG72)
- **X (Twitter)** —— [@tinadecoffice](https://x.com/tinadecoffice)
- Issue 与 PR：[Tinadec/TinadecCore](https://github.com/Tinadec/TinadecCore) —— 本目录已发布为独立仓库，Core 的工作请开在那里。

## 许可证

MIT —— 见 [LICENSE](LICENSE)。Copyright (c) 2026 Lincube。

*给合规审阅者的备注：本目录下所有模块均按 MIT 授权（`PackageLicenseExpression=MIT` 集中于 `Directory.Build.props`）。*
