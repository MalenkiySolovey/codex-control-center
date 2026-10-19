# Stage 1 migration handoff

This document records the first architectural extraction in Codex Control Center. It does not authorize or describe a product release. Stage 1 deliberately keeps the existing Windows application and user-data format intact.

## Repository bootstrap

- The public target repository is [MalenkiySolovey/codex-control-center](https://github.com/MalenkiySolovey/codex-control-center).
- It was bootstrapped from the existing `codex-switchboard` Git repository, not generated as a new application. The target remote was added alongside the source remote.
- The source repository remains available as the provenance/reference remote. The source checkout was verified clean after the migration work was made in this target checkout.
- The target repository's starting commit was `b01385a3a9ab326515c7d59c3187cf44df1c68be` (`feat: complete v0.3.0 roadmap milestone`). At bootstrap, both remotes pointed at that same source commit.
- This work creates no other repository and introduces no database, daemon, JSON-RPC server, TypeScript SDK, or web bridge.

## Source repository and provenance

`codex-switchboard` remains the source of truth for the inherited implementation and historical behavior. Control Center changes in this stage are incremental changes to that codebase. The inherited Switchboard release/tag history is provenance; it must not be represented as a Control Center binary release.

## History preservation

The target checkout began with the source's Git history intact: 86 commits and the existing `v0.1.0` through `v0.3.0` tags were present at bootstrap. No history rewrite was used. Stage 1 commits are added on top of the inherited baseline in the target repository only.

## Build/test baseline

- Before extraction, the inherited solution restored in locked mode, its Release build completed with zero warnings/errors, and the full suite passed **978/978**.
- After extracting the account services, `eng/quality/verify.ps1 -SkipPackaging` passed all six applicable gates: pinned SDK, locked restore, format verification, dependency audit (0 critical/high/moderate/low advisories), Release build (0 warnings/errors), and full tests (**984 passed, 0 failed, 0 skipped**). The six additional cases exercise the new service boundaries.
- Packaging was explicitly skipped: this handoff does not create a release artifact or publish a release.

## UI preservation

No layout, styles, resources, assets, navigation, dialogs, tray interactions, or account/provider/launch-profile behavior were redesigned. The only changed XAML files are:

- `src/CodexSwitcher.App/MainWindow.xaml` — application title and tray tooltip branding.
- `src/CodexSwitcher.App/Views/ShellView.xaml` — shell title and one explanatory sentence's product name.
- `src/CodexSwitcher.App/Views/QuickSwitchOverlayWindow.xaml` — window title branding.
- `src/CodexSwitcher.App/Views/SettingsWindow.xaml` — product name in existing setting labels/help copy.
- `src/CodexSwitcher.App/Views/TrayQuickControlPopup.xaml` — popup title branding.

`Strings.cs` and settings text were updated for the Control Center identity and repository link. These are visible copy changes only; they do not change control placement or interaction semantics. All other XAML/resources/assets remain inherited and unchanged.

The Release app was physically launched from an isolated temporary data root. The Accounts, API Providers, and Launch Profiles pages rendered and navigation between them worked. The temporary profile had no saved provider or launch-profile entries. The app detected an existing host Codex session; it was not imported, and no OAuth, account switching, provider editing, or credential-affecting action was performed. The temporary QA process was closed afterward.

## New services

- `AccountRegistry` owns profile metadata loading, lookup, rename, ordering, tracking, and persistence through `IProfileStore`. It exposes a read-only profile view to new consumers and does not handle Codex auth files or process lifecycle.
- `AccountCredentialService` owns auth validation, credential-vault operations, account identity/deduplication, safe credential updates, removal, and orphan-vault audit/recovery. Stale refresh write-back is guarded by the expected credential fingerprint; mutable secret buffers are cleared where applicable.
- `AccountTransferService` owns import/export and the existing versioned and legacy raw-auth compatibility paths. It validates the document before applying imports, so malformed later entries do not leave a partial import.
- `CodexAuthReconciler` owns the Codex-specific relationship to `CODEX_HOME/auth.json`: active/unmanaged-account detection and adoption, reconciliation, same-account credential drift, and OAuth refresh write-back orchestration.
- `SubscriptionMetadataService` contains the focused auth-derived identity/subscription enrichment logic. It is a static Core helper rather than a DI service, and guards against applying claims for a different account identity while preserving manual tracking data.

## Moved responsibilities

`ProfileService` was reduced from the former 660-line service to a compatibility facade of approximately 80 lines. It delegates account metadata to `AccountRegistry`, credential operations to `AccountCredentialService`, transfer operations to `AccountTransferService`, and Codex auth reconciliation to `CodexAuthReconciler`. The subscription metadata helper is called by the relevant account/auth path; it is not duplicated in the facade.

Composition registers the new services. `CodexLaunchService` now depends on `AccountCredentialService` for isolated-launch refresh persistence. `CodexSignedOutService` uses `AccountRegistry` and `CodexAuthReconciler`. `ApiProvidersViewModel` reads the account registry rather than taking a dependency on the compatibility facade.

## Remaining ProfileService facade

The facade remains to avoid rewriting existing account UI flows in this stage. It still exposes the legacy mutable `List<ProfileMetadata>` view for existing consumers and is still used by some composition callbacks that request profile persistence. New code should depend on the narrow service that owns its operation. Replacing the mutable compatibility surface is follow-up work, not a reason to retain duplicated business logic.

## Dependency improvements

Current project references relevant to future extraction:

| Project | Current references |
| --- | --- |
| `CodexSwitcher.Core` | No project references; domain/contracts/services target `net10.0`. |
| `CodexSwitcher.Infra` | `CodexSwitcher.Core`; Windows-specific infrastructure and DPAPI support. |
| `CodexSwitcher.App` | `CodexSwitcher.Core`, `CodexSwitcher.Infra`; WinUI presentation/composition. |
| `CodexSwitcher.Cli` | `CodexSwitcher.Core`, `CodexSwitcher.Infra`. |
| `CodexSwitchboard.KeyBroker` | `CodexSwitcher.Core`, `CodexSwitcher.Infra`. |
| `CodexSwitcher.CatalogSigner` | `CodexSwitcher.Core`, `CodexSwitcher.Infra`. |
| `CodexSwitcher.Core.Tests` | Core, Infra, App, and CatalogSigner for integration/regression coverage. |

The extraction makes the account metadata, credentials, transfer, subscription enrichment, and Codex-auth responsibilities separately addressable in Core. It also removes the broad `ProfileService` dependency from launch refresh persistence, signed-out reconciliation, and API-provider account-list projection. It does not yet establish independent packages or process boundaries.

## Behavior compatibility

Stage 1 leaves the inherited profile store, atomic writes/rolling backups/anti-truncation behavior, DPAPI vault format, OAuth refresh write-back, account identity/deduplication, unmanaged active-account detection, sorting, subscription metadata, import/export, switching, quota/activity monitoring, launch profiles, multi-Codex behavior, tray behavior, and existing GUI flows in place. No schema migration or direct SQLite/session mutation was introduced. The existing regression suite remains the compatibility evidence; the physical QA above is a smoke check, not exhaustive manual qualification of every flow.

For compatibility, the executable/assembly name, existing local data root, and legacy CLI/schema names remain Switchboard-branded internally for now. The new repository's README explains that inherited `v0.3.0` is not a Control Center release. The inherited packaging script still emits Switchboard-named artifacts and needs a separate release-identity change before publishing binaries.

## KeyBroker independence analysis

The current `CodexSwitchboard.KeyBroker` project references both the full Core and Infra projects. Its entry point imports many unrelated account, provider, routing, settings, usage, thread, transfer, lifecycle, and infrastructure namespaces. The actual delivery path is narrower:

1. Parse `--key-id <GUID>` (or `--key-id=<GUID>`).
2. Resolve the protected API-key file with `AppPaths.GetApiKeyPath`, attempting the legacy-path migration when needed.
3. Decrypt using DPAPI `DataProtectionScope.CurrentUser`.
4. Parse the current JSON credential blob's `apiKey` value.
5. On success, write only the raw key and a newline to stdout; write sanitized errors to stderr and clear mutable secret buffers.

The current exit codes are `0` success, `1` invalid arguments, `3` missing credential, `4` DPAPI decryption failure, `5` malformed credential, `6` empty key, and `7` other failure. This command is offline and does not read Codex `config.toml` or `auth.json`.

The coupling that prevents independent reuse is the full Core/Infra project reference, Switchboard-specific `AppPaths`/legacy migration, and an API-key-specific credential blob/model. A future generic keybroker should need only an opaque secret reference and a minimal secret-resolution contract; OS-specific storage belongs behind an adapter/host. Keep secrets out of command-line arguments, keep successful stdout limited to the requested secret bytes, and provide stable sanitized error codes/messages to the caller. Do not transplant the full account/provider domain into the broker or rewrite it as part of Stage 1.

## Cross-language contract notes

Future reusable surfaces should use stable JSON DTOs with `schemaVersion`, string-valued stable enums, opaque IDs, ISO-8601 UTC timestamps, and machine-readable error codes. Contracts must not require WinUI, `DispatcherQueue`, stream subclasses, CLR exceptions as the only error form, `IServiceProvider`, or implementation-specific classes. The planned host framing is UTF-8 NDJSON carrying JSON-RPC 2.0 messages. Stage 1 adds no server, host executable, or wire protocol; the transfer document remains an application/.NET implementation model pending a deliberate protocol design.

## Known technical debt

- Existing UI consumers still reach through `ProfileService`, including its mutable `Profiles` property.
- Some save-needed callbacks still call `ProfileService.Save`; these should move to an explicit persistence/application port.
- `CodexLaunchService` still depends directly on the account credential service; a later runtime adapter may be appropriate once the boundary is stable.
- KeyBroker remains coupled to all of Core and Infra and to Switchboard-specific paths and credential shape.
- The account model and persisted profile store remain monolithic application state; Stage 1 does not split state into independently versioned protocol contracts.
- Product branding is updated in user-facing copy, but executable, data-root, and packaging identities intentionally retain compatibility names. A real Control Center binary release needs a separate packaging/upgrade decision and install smoke test.

## Candidates for agent-state-protocol

Define language-neutral schemas only after the current use cases stabilize: profile metadata snapshots/updates, secret references (never secret values in general state messages), provider/model inventory contracts where genuinely shared, and common result/error envelopes. Version each schema explicitly and keep persistence/transport implementation details out of the DTOs.

## Candidates for agent-state-daemon

Potential future canonical owner for profile/provider/runtime-related local state, with transactional persistence and explicit client contracts. Stage 1 intentionally adds no SQLite, WAL, daemon, named pipe, or Unix socket. First inventory every existing writer and migration/backup guarantee; any future state-owner change must preserve rollback and anti-truncation semantics.

## Candidates for chatgpt-account-kit

Reusable ChatGPT/OpenAI account credential validation, identity extraction, subscription enrichment, and account-specific reusable logic. It should not own Codex process/config/thread operations or introduce a dependency on the desktop UI. Keep secrets behind a storage contract rather than assuming this app's vault implementation.

## Candidates for codex-runtime-kit

Codex-specific paths, runtime discovery, process lifecycle, config/catalog application, and supported app-server/thread operations. ChatGPT auth should enter through a narrow adapter; the runtime kit should not own the account database or generic provider discovery.

## Candidates for llm-provider-kit

Generic provider descriptors, model discovery, and provider/model metadata. It should remain independent of Codex target selection, account-vault formats, and WinUI, and should not assume all providers expose the same discovery behavior.

## Candidates for local-keybroker

A small generic local secret-delivery host and client contract around opaque secret IDs. Retain the current no-network, minimal-stdout, sanitized-failure properties while replacing dependencies on broad Core/Infra and `AppPaths` with narrow interfaces and OS adapters.

## Candidates for codex-web-bridge

Keep this as a separate, optional integration boundary. If pursued later, preserve the stated thin-fork relationship to upstream; do not introduce it as a Control Center runtime dependency during Stage 1.

## Recommended Stage 2

1. Keep the current application usable and ship no architecture rewrite in place of user-facing value. Do not expand Stage 1 into the daemon/SQLite or multi-repository split.
2. Remove the remaining mutable `ProfileService.Profiles` and `Save()` dependencies incrementally by moving consumers to narrow metadata/persistence interfaces; prove compatibility with the existing suite.
3. Add architecture-level checks for intended project references and keep Core free of UI/OS infrastructure.
4. Stabilize cross-language schemas against real consumers before creating `agent-state-protocol` or host processes.
5. Treat extraction into the account, runtime, provider, and keybroker repositories as separate, independently useful milestones with their own tests and migration/compatibility plans.
6. Keep the Windows application as the only supported platform in the near-term release plan; other operating systems remain deferred rather than silently becoming Stage 1 scope.
