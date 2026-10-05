# SYNC PROVENANCE
source_repo: TinadecOffice (github.com/Tinadec/TinadecOffice)
source_commit: 55fafae
source_commit_full: 55fafae6980719d2eaf6987ab2cd0d1bd8e5c26f
source_tree: 45ef51702f6f1f14080d1f3abe245c9b6d015ef4
synced_at: 2026-10-05T03:40:27.396Z
product: core
license: MIT (unchanged from source)

## Sync scope

Copied 561 Core source, configuration, documentation, and test files. Removed 23 obsolete mirrored source files that no longer exist in the source tree. Preserved this repository's `.gitattributes` and `.gitignore`. Runtime data and build outputs are excluded, including historical `Api/data` files tracked in the Office repository.

## Standalone adaptations

- `AGENTS.md` records this checkout's root-level commands and validation boundary. The remaining dated entries describe source-repository history.
- Added `tests/TinadecCore.Api.Tests/RequiresGraphSeedPackFactAttribute.cs` and applied it to the 17 Office integration tests in `GraphSeedPackClosureTests.cs` and `ToolChainEndpointTests{,.ApprovalGates,.Environments,.Interrupts,.Worktrees}.cs`. These tests skip when the Office desktop source tree is absent. When that tree is present, their assertions run unchanged, including failures for unexpectedly missing pack files. The pack is not included in Core delivery artifacts.
- `TinadecToolsProcessTests.cs` uses the existing `RequiresTinadecToolsFactAttribute` for all six real-process tests, matching the other nine tests that already use it. A Core-only build skips all 15 when the Tools apphost is absent.
- `Api/Dockerfile` copies this standalone repository root into the container's existing Core layout; `.dockerignore` excludes Git metadata, local data, and build outputs. Use `docker build -f Api/Dockerfile -t tinadec-core-api:dev .`. Docker is unavailable on this machine, so the container image was not built.
- Production C#/F# source files match the source snapshot. The Office copy-only sync script overwrites the test adaptations and does not remove obsolete files; preserve these adaptations and reconcile source deletions on the next sync.

## Validation

All commands ran from this repository's Core root with .NET SDK 10.0.303. Final test results use fresh outputs under `artifacts/sync-validation/isolated-dotnet`; earlier August TinadecTools binaries in the default output directory were excluded from the final validation.

| Check | Result |
| --- | --- |
| `dotnet build TinadecCore.slnx` | Passed; 0 errors (existing source warnings remain) |
| AgentFramework tests, isolated outputs | 626 passed |
| Architecture tests, isolated outputs | 18 passed |
| Governance tests, isolated outputs | 88 passed |
| API tests, isolated outputs | 657 passed; 32 skipped (17 Office Pack + 15 Tools integration tests); 0 failed |
| `dotnet pack TinadecCore.slnx -c Release --no-restore` | Passed; 23 packages |
| `dotnet publish Api/TinadecCore.Api.csproj -c Release --no-restore` | Passed |
| Published API smoke with a fresh SQLite data directory | Health `ok`; 15 modules; database created |
| Vulnerable package scan, including transitive dependencies | 28 projects; no known vulnerabilities reported |
| Delivery artifact audit | No App-owned Pack, TinadecTools binary, or runtime database included |
| Staged-file and whitespace checks | No runtime/build artifacts; whitespace check passed |

The published API smoke pre-created the explicitly configured SQLite database parent directory and used an isolated port and data root. All four suites total 1,389 passed tests and 32 explicit integration skips. No live development data was migrated. PostgreSQL and real external model providers were not exercised.

Tests can be repeated with `dotnet test TinadecCore.slnx --artifacts-path artifacts/sync-validation/isolated-dotnet`. Pack and publish outputs remain ignored local validation artifacts.
