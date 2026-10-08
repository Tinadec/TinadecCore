<h1 align="center">TinadecCore</h1>

<p align="center">
  <b>The open, MIT-licensed agent-governance and collaboration runtime behind TinadecOffice — and behind your project, if you build with us.</b><br/>
  TinadecOffice（以及你的项目）背后那套 MIT 开源的智能体治理与协作运行时。
</p>

<p align="center">
  <a href="README.md"><img alt="English" src="https://img.shields.io/badge/English-current-2ea44f"></a>
  <a href="README.zh-CN.md"><img alt="中文" src="https://img.shields.io/badge/简体中文-README.zh--CN.md-d9d9d9"></a>
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
  <a href="https://discord.gg/EcKYQfbG72"><img alt="Discord" src="https://img.shields.io/badge/Discord-Community-5865F2?logo=discord&logoColor=white"></a>
  <a href="https://x.com/tinadecoffice"><img alt="X" src="https://img.shields.io/badge/X-%40tinadecoffice-000000?logo=x&logoColor=white"></a>
</p>

---

TinadecCore is the **underlying framework** of the Tinadec product family: a modular .NET 10 runtime for building **production-grade multi-agent systems with real governance**. Every public capability of [TinadecOffice](https://github.com/Tinadec/TinadecOffice) — the dual-layer agent orchestration (DmaEA), the durable run engine, the permission and approval machinery, the tool-provider contract — lives here, and only here. Office is UI and wiring; Core is the brain.

We publish it under the **MIT license** for one reason: so that you can embed it, fork it, and build your own agent system on top of it without asking anyone's permission.

## Is this the right framework for you?

TinadecCore is a strong fit if you:

- are building an agent system that must survive contact with production — not a chat demo,
- want a **two-layer architecture where planning/reviewing agents and working agents are different things**, with an approval gate between them and the filesystem,
- need runs that pause, resume, cancel and recover across process restarts,
- care about governance: permissions as data, per-run frozen tool manifests, auditable approvals,
- want to keep provider choice (OpenAI-compatible, Anthropic, CLI harnesses) swappable without rewriting your app,
- prefer a mountable ASP.NET Core HTTP layer over adopting someone else's monolith.

## Key features

- **DmaEA dual-layer orchestration** — an *operation* layer (meeting / planner / supervisor) orchestrates a permission-scoped *execution* layer of spawned workers. Governance agents declare tools but never execute them; Core owns dispatch
- **Durable full-duplex runs** — idempotent admission, context revisions, task graphs, spawn budgets, pause / resume / cancel, restart recovery via leased checkpoints
- **Governance as permission data** — capability grants, resource-envelope boundaries, per-call approval gates (human, delegated reviewer gates, or pre-authorized leases), all audited
- **Governed tool plane** — a versioned tool-provider contract; [TinadecTools](https://github.com/Tinadec/TinadecOffice/tree/main/TinadecTools) (files / shell / git) and MCP servers behind one manifest, hash-frozen per run
- **Model & agent center** — provider instances, routes, per-agent model policies, pinned at run admission
- **Immutable versioning everywhere** — Agent/Mode/Prompt versions, ETag'd revisions, immutable published configuration
- **Storage abstraction** — EF Core LINQ surface; SQLite out of the box, PostgreSQL when you need it; vectors via sqlite-vec or pgvector
- **Embeddable HTTP layer** — mount Core routes into your own host: `AddTinadecCoreHttp()` + `MapTinadecCore()`. MAF types stay behind an internal adapter; the public surface is provider-neutral

<p align="center">
  <img src="docs/assets/dual-layer.svg" alt="Dual-layer DmaEA: the operation layer orchestrates the execution layer, with an approval gate on every write" width="100%" />
</p>

## Packages

Four projects are the public, packable surface (`IsPackable=true`):

| Package | Role |
|---------|------|
| `TinadecCore.Contracts` | HTTP DTOs, event envelopes, provider-neutral data types; no MAF, no ASP.NET |
| `TinadecCore.Abstractions` | Core ports and module-registration abstractions; exposes no MAF types |
| `TinadecCore.Runtime` | The full composition root; MAF 1.18 enters only through the internal DmaEA adapter |
| `TinadecCore.AspNetCore` | Mountable ASP.NET Core HTTP layer (`AddTinadecCoreHttp()` / `MapTinadecCore()`) for any host |

Everything else in `TinadecCore/` is MIT too, but implementation packages: restore them from the same feed as Runtime, don't treat them as a stable API. Nothing is published to nuget.org yet — build from source today:

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

Standalone API host and container slicing are documented in the [packaging & standalone deployment guide](https://github.com/Tinadec/TinadecOffice/blob/main/docs/tinadec-core-packaging.zh-CN.md).

## Why this repository exists

This is the important part, and it is the point of the whole thing:

- **We MIT-licensed the foundation deliberately.** You can embed TinadecCore in a commercial product, an internal platform, or another open-source project — no strings, no copyleft on the Core. (The products built *on* it have their own licenses; see the [TinadecOffice LICENSE table](https://github.com/Tinadec/TinadecOffice#license).)
- **TinadecOffice does not keep a private Core fork.** The Office team treats this repository as upstream: every change Office makes to Core comes back here **as a pull request**, reviewed in the open. There is exactly one Core, and it's this one.
- **Your project can do the same thing.** If you build on TinadecCore, you'll eventually need to change it — a new tool authorizer, a storage backend, an orchestration rule. Fork it, make it fit, and when your change would help others, **send it back as a PR to this repository**. Core stays product-neutral and portable; the stuff that's specific to your product stays in your repo.
- **The loop is the product.** Office feeds Core; external users feed Core; Core ships back improved frameworks for everyone. That loop is why the repo exists.

> [!TIP]
> The bar for a Core PR is: *would this help some agent system that isn't TinadecOffice?* If yes, it belongs here. If no, keep it in your product layer — that's what the package boundary is for.

## Thanks

TinadecCore stands on other people's work, and we say so plainly:

- **[Microsoft Agent Framework](https://github.com/microsoft/agent-framework)** — our normative orchestration baseline (MAF 1.18), used behind a strict internal adapter so that Core's contracts stay provider-neutral. The engineering quality of MAF made this possible; the discipline of its README is visibly borrowed here.
- **The open-source agent community** — our reference decisions (what we adopted, what we rejected, and why, with source-level evidence) are recorded in [tinadec-core-reference-decisions](https://github.com/Tinadec/TinadecOffice/blob/main/docs/tinadec-core-reference-decisions.zh-CN.md). Among others: the session/workspace decoupling ideas traced from Codex-style CLI runtimes, and tool/VFS thinking traced from JetBrains platforms.
- **Everyone who sends a PR** — including the ones we end up not merging. A rejected PR is still time you spent making this better.

## Community

- **Discord** — [discord.gg/EcKYQfbG72](https://discord.gg/EcKYQfbG72)
- **QQ 群** — `370780878`
- **飞书群** — [点击加入](https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=7a3k9813-07f8-4e13-b00c-d9d1c07d539b)
- **X (Twitter)** — [@tinadecoffice](https://x.com/tinadecoffice)
- Issues and PRs: [Tinadec/TinadecCore](https://github.com/Tinadec/TinadecCore) — this tree is published as a standalone repository; open Core work there.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 Lincube.

*Embedding note for vendor checkers: TinadecCore carries MIT for every module under this directory (`PackageLicenseExpression=MIT` is set centrally in `Directory.Build.props`).*
