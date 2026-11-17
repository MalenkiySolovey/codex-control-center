using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.AgentState.Shadow;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;
using Xunit.Abstractions;

namespace CodexSwitcher.AgentStateShadow.CrossRepoTests;

public sealed class RealDaemonNicknameAuthorityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PinnedDaemonSupportsSyntheticCutoverMutationRestartOfflineImportExportAndRollback()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real daemon integration test requires Windows named pipes.");

        var executablePath = Environment.GetEnvironmentVariable(AccountStateShadowSettings.ExecutableEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        Assert.True(File.Exists(executablePath));

        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(
            root: Path.Combine(temp.Path, "switchboard"),
            codexHome: Path.Combine(temp.Path, "codex-home"),
            legacyRoot: Path.Combine(temp.Path, "legacy-switcher"));
        appPaths.EnsureDirectories();

        var clock = new TestClock();
        var audit = new NoopAuditLog();
        var profileStore = new ProfileStore(new PhysicalFileSystem(), appPaths.ProfilesPath);
        var registry = new AccountRegistry(profileStore, clock, audit);
        registry.Load();
        var legacyProfile = CreateProfile("Work", "Alice", 0);
        legacyProfile.AccountEmail = "synthetic-private-marker@example.invalid";
        legacyProfile.AccountSub = "synthetic-legacy-work";
        var secondProfile = CreateProfile("Studio", "Bob", 1);
        secondProfile.DetectedDisplayName = null;
        secondProfile.AccountEmail = "synthetic-email-only-marker@example.invalid";
        secondProfile.AccountSub = "synthetic-legacy-studio";
        var daemonOwnedProfile = CreateProfile(string.Empty, "Carla", 2);
        daemonOwnedProfile.AccountSub = "synthetic-legacy-daemon-owned";
        var unnamedProfile = CreateProfile(string.Empty, "Dana", 3);
        unnamedProfile.AccountSub = "synthetic-legacy-null";
        registry.Add(legacyProfile);
        registry.Add(secondProfile);
        registry.Add(daemonOwnedProfile);
        registry.Add(unnamedProfile);

        var journal = new NicknameAuthorityMigrationJournal(new PhysicalFileSystem(), appPaths);
        var shadowSettings = AccountStateShadowSettings.Parse("1", executablePath);
        var authoritySettings = NicknameAuthorityCanarySettings.Parse("1", executablePath);
        var legacyCoordinatorSettings = NicknameAuthorityCanarySettings.Parse("0", executablePath);
        Assert.True(shadowSettings.Enabled);
        Assert.False(legacyCoordinatorSettings.Enabled);
        Assert.True(authoritySettings.Enabled);

        var pipeName = $"agent-state-daemon-test-{Guid.NewGuid():N}";
        using var processManager = new AccountStateShadowDaemonProcessManager(
            shadowSettings,
            appPaths,
            pipeName,
            authoritySettings,
            journal);
        using var coordinator = new AccountStateShadowCoordinator(
            registry,
            shadowSettings,
            processManager,
            nicknameAuthoritySettings: legacyCoordinatorSettings,
            migrationJournal: journal);
        var vault = new SyntheticVaultService();
        vault.SaveBlob(legacyProfile.Id, CreateSyntheticAuthJson("synthetic-legacy-work"));
        vault.SaveBlob(secondProfile.Id, CreateSyntheticAuthJson("synthetic-legacy-studio"));
        vault.SaveBlob(daemonOwnedProfile.Id, CreateSyntheticAuthJson("synthetic-legacy-daemon-owned"));
        vault.SaveBlob(unnamedProfile.Id, CreateSyntheticAuthJson("synthetic-legacy-null"));
        var credentials = new AccountCredentialService(vault, registry, clock, audit);
        var transfer = new AccountTransferService(registry, credentials, audit);
        var authority = new AccountNicknameAuthorityService(
            registry,
            transfer,
            authoritySettings,
            journal,
            coordinator,
            processManager,
            appPaths);

        var optOutSettings = NicknameAuthorityCanarySettings.Parse(null, executablePath);
        var optOutManager = new AccountStateShadowDaemonProcessManager(
            shadowSettings,
            appPaths,
            $"agent-state-daemon-test-{Guid.NewGuid():N}",
            optOutSettings,
            journal);
        var optOutCoordinator = new AccountStateShadowCoordinator(
            registry,
            shadowSettings,
            optOutManager,
            nicknameAuthoritySettings: optOutSettings,
            migrationJournal: journal);
        using (optOutManager)
        using (optOutCoordinator)
        using (var optOutAuthority = new AccountNicknameAuthorityService(
            registry, transfer, optOutSettings, journal, optOutCoordinator, optOutManager, appPaths))
        {
            await optOutAuthority.InitializeAsync(CancellationToken.None);
            Assert.True(optOutAuthority.IsLocalAuthorityVerified);
            Assert.False(optOutAuthority.IsAuthorityPotentiallyActive);
            Assert.Null(optOutManager.OwnedProcessId);
        }

        int? legacyPid = null;
        var daemonPids = new List<int>();
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            await WaitForLegacyProjectionAsync(pipeName, coordinator, 4);
            legacyPid = processManager.OwnedProcessId;
            Assert.NotNull(legacyPid);
            var shadowRows = await ReadLegacyAccountsAsync(pipeName);
            Assert.Equal(4, shadowRows.Count);
            Assert.Contains(shadowRows, account => account.AccountId == legacyProfile.Id.ToString("D"));

            // The legacy shadow owner is stopped before the authority-mode process is started.
            await coordinator.PauseLegacyShadowAsync(CancellationToken.None);
            await WaitForProcessExitAsync(legacyPid.Value);
            Assert.Null(processManager.OwnedProcessId);

            // Seed an explicit remote/local conflict through the real experimental RPC surface.
            await using (var preparationClient = await processManager.ConnectNicknameAuthorityCompatibleAsync(CancellationToken.None))
            {
                var info = await preparationClient.GetAuthorityDaemonInfoAsync(CancellationToken.None);
                Assert.Equal(1, info.DatabaseSchemaVersion);
                Assert.True(preparationClient.SupportsNicknameAuthority);
                Assert.False(info.CapabilityEnabled["accounts.updateNickname"]);

                var initialStatus = await preparationClient.GetNicknameAuthorityAsync(CancellationToken.None);
                Assert.Equal(NicknameAuthorityStates.LegacyLocal, initialStatus.State);
                Assert.Equal(AccountStateShadowRpcClient.ProtocolManifestSha256, initialStatus.ProtocolManifestSha256);
                var preparing = await preparationClient.BeginNicknameAuthorityPreparationAsync(initialStatus.Epoch, CancellationToken.None);
                Assert.Equal(NicknameAuthorityStates.Preparing, preparing.State);
                var sourceProfiles = new[] { legacyProfile, secondProfile, daemonOwnedProfile, unnamedProfile };
                var source = new AccountSourceCollection
                {
                    Accounts = sourceProfiles.Select(profile => new AccountSourceDescriptor
                    {
                        AccountId = profile.Id.ToString("D"),
                        SafeFallbackDisplayName = string.IsNullOrWhiteSpace(profile.Nickname)
                            ? profile.DetectedDisplayName ?? "Account"
                            : "Account",
                        Health = "valid",
                        IsActive = false,
                        SortOrder = profile.SortOrder,
                        CreatedAt = profile.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        PlanType = profile.PlanType
                    }).ToArray()
                };
                await preparationClient.ReconcileSourceAsync(source, preparing.Epoch, preparing.MigrationId, CancellationToken.None);
                var remoteBeforeConflict = await preparationClient.GetNicknameAsync(legacyProfile.Id.ToString("D"), CancellationToken.None);
                Assert.Null(remoteBeforeConflict.Nickname);
                await preparationClient.UpdateNicknameAsync(new AccountNicknameMutation
                {
                    AccountId = legacyProfile.Id.ToString("D"),
                    Nickname = "Personal",
                    SafeFallbackDisplayName = "Account",
                    ExpectedRevision = remoteBeforeConflict.Revision
                }, preparing.Epoch, preparing.MigrationId, CancellationToken.None);

                var remoteOwnedNickname = await preparationClient.GetNicknameAsync(daemonOwnedProfile.Id.ToString("D"), CancellationToken.None);
                await preparationClient.UpdateNicknameAsync(new AccountNicknameMutation
                {
                    AccountId = daemonOwnedProfile.Id.ToString("D"),
                    Nickname = "Remote",
                    SafeFallbackDisplayName = "Account",
                    ExpectedRevision = remoteOwnedNickname.Revision
                }, preparing.Epoch, preparing.MigrationId, CancellationToken.None);
            }

            var authorityPid = processManager.OwnedProcessId;
            Assert.NotNull(authorityPid);
            Assert.NotEqual(legacyPid, authorityPid);
            daemonPids.Add(authorityPid.Value);
            Assert.True(ProcessHasExited(legacyPid.Value), "The legacy daemon must exit before authority mode owns the shared data directory.");

            await authority.InitializeAsync(CancellationToken.None);
            Assert.True(registry.NicknameWritesFenced);
            Assert.False(authority.IsAuthorityActive);
            var conflict = Assert.Single(authority.GetPendingConflicts());
            Assert.Equal(legacyProfile.Id, conflict.AccountId);
            Assert.Equal("Work", conflict.LocalNickname);
            Assert.Equal("Personal", conflict.DaemonNickname);
            Assert.Equal("Work", registry.Find(legacyProfile.Id)!.Nickname);
            Assert.Equal(string.Empty, registry.Find(daemonOwnedProfile.Id)!.Nickname);
            Assert.Contains("Preparing", journal.Read()!.AuthorityState, StringComparison.Ordinal);

            var selectedLocal = await authority.ResolveMigrationConflictAsync(legacyProfile.Id, "Work", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, selectedLocal.Outcome);
            Assert.True(authority.IsAuthorityActive);
            Assert.True(registry.NicknameWritesFenced);
            Assert.Empty(authority.GetPendingConflicts());

            var activeSnapshot = await ReadAuthoritySnapshotAsync();
            Assert.Equal(NicknameAuthorityStates.DaemonNicknameAuthority, activeSnapshot.Status.State);
            Assert.Equal(2, activeSnapshot.Info.DatabaseSchemaVersion);
            Assert.Equal("Work", activeSnapshot.Nicknames.Single(state => state.AccountId == legacyProfile.Id.ToString("D")).Nickname);
            Assert.Equal("Studio", activeSnapshot.Nicknames.Single(state => state.AccountId == secondProfile.Id.ToString("D")).Nickname);
            Assert.Equal("Remote", activeSnapshot.Nicknames.Single(state => state.AccountId == daemonOwnedProfile.Id.ToString("D")).Nickname);
            Assert.Null(activeSnapshot.Nicknames.Single(state => state.AccountId == unnamedProfile.Id.ToString("D")).Nickname);
            Assert.Equal("Work", activeSnapshot.Accounts.Single(account => account.AccountId == legacyProfile.Id.ToString("D")).DisplayName);
            Assert.Equal("Remote", registry.Find(daemonOwnedProfile.Id)!.Nickname);

            var rename = await authority.RenameAsync(legacyProfile.Id, "Personal", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, rename.Outcome);
            Assert.Equal("Personal", await ReadNicknameAsync(legacyProfile.Id));
            Assert.Equal("Personal", profileStore.LoadAll().Single(profile => profile.Id == legacyProfile.Id).Nickname);

            var clear = await authority.RenameAsync(legacyProfile.Id, "", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, clear.Outcome);
            Assert.Null(await ReadNicknameAsync(legacyProfile.Id));
            Assert.Equal("Alice", (await ReadAuthoritySnapshotAsync()).Accounts.Single(account => account.AccountId == legacyProfile.Id.ToString("D")).DisplayName);

            var explicitAtNickname = await authority.RenameAsync(secondProfile.Id, "@synthetic-label", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, explicitAtNickname.Outcome);
            Assert.Equal("@synthetic-label", await ReadNicknameAsync(secondProfile.Id));
            await authority.RenameAsync(secondProfile.Id, "", CancellationToken.None);
            var emailOnlyFallback = (await ReadAuthoritySnapshotAsync()).Accounts.Single(account => account.AccountId == secondProfile.Id.ToString("D"));
            Assert.Equal("Account", emailOnlyFallback.DisplayName);
            Assert.DoesNotContain("synthetic-email-only-marker@example.invalid", JsonSerializer.Serialize(emailOnlyFallback), StringComparison.Ordinal);
            await authority.RenameAsync(secondProfile.Id, "Studio", CancellationToken.None);

            var restoreNickname = await authority.RenameAsync(legacyProfile.Id, "Work", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, restoreNickname.Outcome);
            registry.UpdateUnderLock(legacyProfile.Id, profile => profile.DetectedDisplayName = "Alice Smith");
            await authority.SynchronizeActiveSourceAsync(CancellationToken.None);
            var hiddenBaseUpdate = await ReadAuthoritySnapshotAsync();
            Assert.Equal("Work", hiddenBaseUpdate.Nicknames.Single(state => state.AccountId == legacyProfile.Id.ToString("D")).Nickname);
            Assert.Equal("Work", hiddenBaseUpdate.Accounts.Single(account => account.AccountId == legacyProfile.Id.ToString("D")).DisplayName);

            var clearAfterBaseUpdate = await authority.RenameAsync(legacyProfile.Id, "", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, clearAfterBaseUpdate.Outcome);
            Assert.Equal("Alice Smith", (await ReadAuthoritySnapshotAsync()).Accounts.Single(account => account.AccountId == legacyProfile.Id.ToString("D")).DisplayName);
            await authority.RenameAsync(legacyProfile.Id, "Work", CancellationToken.None);

            var addedProfile = credentials.AddFromAuthJson(CreateSyntheticAuthJson("synthetic-direct-add"), "Requested add name");
            Assert.True(string.IsNullOrEmpty(addedProfile.Nickname));
            await authority.SynchronizeActiveSourceAsync(CancellationToken.None);
            var addNickname = await authority.RenameAsync(addedProfile.Id, "Requested add name", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, addNickname.Outcome);
            Assert.Equal("Requested add name", await ReadNicknameAsync(addedProfile.Id));

            var importDocument = CreateTransferDocument("synthetic-import-sub", "Imported", 2);
            var imported = await authority.ImportAsync(importDocument, CancellationToken.None);
            Assert.Equal(new AccountNicknameImportResult(1, 1, 0), imported);
            var importedProfile = Assert.Single(registry.Profiles, profile => profile.AccountSub == "synthetic-import-sub");
            Assert.Equal("Imported", importedProfile.Nickname);
            Assert.Equal("Imported", await ReadNicknameAsync(importedProfile.Id));

            var updateDocument = CreateTransferDocument("synthetic-import-sub", "Updated import", 2);
            var updatedImport = await authority.ImportAsync(updateDocument, CancellationToken.None);
            Assert.Equal(new AccountNicknameImportResult(1, 1, 0), updatedImport);
            Assert.Equal(6, registry.Profiles.Count);
            Assert.Equal("Updated import", await ReadNicknameAsync(importedProfile.Id));

            var exported = JsonSerializer.Deserialize<AccountTransferDocument>(await authority.ExportAllAsync(CancellationToken.None))!;
            Assert.Equal(6, exported.Accounts.Count);
            Assert.Contains(exported.Accounts, account => account.Nickname == "Updated import");
            Assert.DoesNotContain("synthetic-private-marker@example.invalid", await File.ReadAllTextAsync(Path.Combine(appPaths.Root, "agent-state-shadow-v1", "nickname-authority-migration-v1.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("Work", await File.ReadAllTextAsync(Path.Combine(appPaths.Root, "agent-state-shadow-v1", "nickname-authority-migration-v1.json")), StringComparison.Ordinal);

            var unexpectedExitPid = processManager.OwnedProcessId;
            Assert.NotNull(unexpectedExitPid);
            using (var unexpectedlyStoppedDaemon = Process.GetProcessById(unexpectedExitPid.Value))
            {
                Assert.Equal(Path.GetFullPath(executablePath), Path.GetFullPath(unexpectedlyStoppedDaemon.MainModule!.FileName!));
                unexpectedlyStoppedDaemon.Kill();
                await unexpectedlyStoppedDaemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            daemonPids.Add(unexpectedExitPid.Value);

            var unexpectedExitRename = await authority.RenameAsync(legacyProfile.Id, "Must not persist offline", CancellationToken.None);
            Assert.Equal(AccountNicknameMutationOutcome.FailedDiverged, unexpectedExitRename.Outcome);
            Assert.Equal("Work", registry.Find(legacyProfile.Id)!.Nickname);
            Assert.Null(processManager.OwnedProcessId);
            Assert.False(processManager.IsLastConnectionHealthy);
            var unexpectedExitExport = await Record.ExceptionAsync(() => authority.ExportAllAsync(CancellationToken.None));
            Assert.IsType<AccountStateShadowUnavailableException>(unexpectedExitExport);

            var firstAuthorityPid = processManager.OwnedProcessId;
            await processManager.StopOwnedProcessAsync(CancellationToken.None);
            if (firstAuthorityPid.HasValue)
            {
                await WaitForProcessExitAsync(firstAuthorityPid.Value);
                daemonPids.Add(firstAuthorityPid.Value);
            }
            Assert.False(processManager.IsLastConnectionHealthy);

            var readProvider = new AccountReadSnapshotProvider(
                registry,
                coordinator,
                AccountReadAuthorityCanarySettings.Parse("0", true),
                nicknameAuthority: authority);
            var offlineRead = readProvider.Read();
            Assert.Equal(AccountReadSource.Local, offlineRead.Source);
            Assert.Equal(AccountReadCanaryState.LocalFallbackDaemonUnavailable, offlineRead.CanaryState);
            Assert.Equal("Work", offlineRead.Accounts.Single(account => account.AccountId == legacyProfile.Id.ToString("D")).DisplayName);
            authority.Dispose();

            // A disabled daemon executable simulates offline operation without changing real state.
            var offlineSettings = NicknameAuthorityCanarySettings.Parse("1", null);
            var offlineManager = new AccountStateShadowDaemonProcessManager(shadowSettings, appPaths,
                $"agent-state-daemon-test-{Guid.NewGuid():N}", offlineSettings, journal);
            var offlineCoordinator = new AccountStateShadowCoordinator(
                registry,
                shadowSettings,
                offlineManager,
                nicknameAuthoritySettings: offlineSettings,
                migrationJournal: journal);
            using (offlineManager)
            using (offlineCoordinator)
            using (var offlineAuthority = new AccountNicknameAuthorityService(
                registry, transfer, offlineSettings, journal, offlineCoordinator, offlineManager, appPaths))
            {
                await offlineAuthority.InitializeAsync(CancellationToken.None);
                Assert.True(registry.NicknameWritesFenced);
                Assert.False(offlineAuthority.IsLocalAuthorityVerified);
                var offlineRename = await offlineAuthority.RenameAsync(legacyProfile.Id, "Must not persist offline", CancellationToken.None);
                Assert.Equal(AccountNicknameMutationOutcome.FailedDiverged, offlineRename.Outcome);
                Assert.Equal("Work", registry.Find(legacyProfile.Id)!.Nickname);
                var offlineExport = await Record.ExceptionAsync(() => offlineAuthority.ExportAllAsync(CancellationToken.None));
                Assert.IsType<AccountStateShadowUnavailableException>(offlineExport);

                var rejectedNicknameImport = await offlineAuthority.ImportAsync(
                    CreateTransferDocument("synthetic-offline-import", "Offline nickname", 4), CancellationToken.None);
                Assert.Equal(1, rejectedNicknameImport.ImportedAccounts);
                Assert.Equal(0, rejectedNicknameImport.AppliedNicknames);
                Assert.Equal(1, rejectedNicknameImport.FailedNicknames);
                var offlineProfile = Assert.Single(registry.Profiles, profile => profile.AccountSub == "synthetic-offline-import");
                Assert.True(string.IsNullOrEmpty(offlineProfile.Nickname));
                Assert.True(vault.Exists(offlineProfile.Id));
            }

            authority = new AccountNicknameAuthorityService(
                registry, transfer, authoritySettings, journal, coordinator, processManager, appPaths);
            await authority.InitializeAsync(CancellationToken.None);
            Assert.True(authority.IsAuthorityActive);
            Assert.Equal("Work", await ReadNicknameAsync(legacyProfile.Id));
            var activeProcessAfterRestart = processManager.OwnedProcessId;
            Assert.NotNull(activeProcessAfterRestart);
            daemonPids.Add(activeProcessAfterRestart.Value);

            var rollbackCompleted = await authority.RollbackAsync(CancellationToken.None);
            Assert.True(rollbackCompleted);
            Assert.True(authority.IsLocalAuthorityVerified);
            Assert.False(registry.NicknameWritesFenced);

            await using (var afterRollbackClient = await processManager.ConnectNicknameAuthorityCompatibleAsync(CancellationToken.None))
            {
                var afterRollback = await afterRollbackClient.GetAuthorityDaemonInfoAsync(CancellationToken.None);
                var status = await afterRollbackClient.GetNicknameAuthorityAsync(CancellationToken.None);
                Assert.Equal(2, afterRollback.DatabaseSchemaVersion);
                Assert.Equal(NicknameAuthorityStates.LegacyLocal, status.State);
                Assert.Equal("Work", (await afterRollbackClient.GetNicknameAsync(legacyProfile.Id.ToString("D"), CancellationToken.None)).Nickname);
            }

            await Assert.ThrowsAsync<AccountStateShadowUnavailableException>(() =>
                processManager.ConnectCompatibleAsync(CancellationToken.None));
            var journalText = await File.ReadAllTextAsync(Path.Combine(appPaths.Root, "agent-state-shadow-v1", "nickname-authority-migration-v1.json"));
            Assert.DoesNotContain("synthetic-private-marker@example.invalid", journalText, StringComparison.Ordinal);
            Assert.DoesNotContain("Updated import", journalText, StringComparison.Ordinal);
            Assert.Contains("\"rollbackCompleted\":true", journalText, StringComparison.Ordinal);

            output.WriteLine("nickname_authority=real_pinned_daemon_cutover_conflict_rename_clear_restart_offline_import_export_rollback_pass");
            output.WriteLine("synthetic_profiles=4 credentials=synthetic_nonfunctional_only");
            output.WriteLine("legacy_to_authority=shared_database_exact_pid_sequential_no_overlap");
            output.WriteLine("rollback_database_schema=2 legacy_shadow_fenced=true");
        }
        finally
        {
            var ownedPid = processManager.OwnedProcessId;
            authority.Dispose();
            await coordinator.StopAsync(CancellationToken.None);
            if (ownedPid.HasValue)
                daemonPids.Add(ownedPid.Value);
            foreach (var processId in daemonPids.Distinct())
                await WaitForProcessExitAsync(processId);
            Assert.Null(processManager.OwnedProcessId);
        }
    }

    private static async Task WaitForLegacyProjectionAsync(string pipeName, AccountStateShadowCoordinator coordinator, int expectedCount)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                await using var client = new AccountStateShadowRpcClient(pipeName);
                await client.ConnectAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                await client.VerifyCompatibleAsync(CancellationToken.None);
                var rows = await client.ListAccountsAsync(CancellationToken.None);
                if (rows.Count == expectedCount && coordinator.LastStatus.Category == "synchronized")
                    return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
            }
            await Task.Delay(50);
        }
        Assert.Fail("The synthetic legacy projection did not converge.");
    }

    private static async Task<IReadOnlyList<AccountDescriptor>> ReadLegacyAccountsAsync(string pipeName)
    {
        await using var client = new AccountStateShadowRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await client.VerifyCompatibleAsync(CancellationToken.None);
        return await client.ListAccountsAsync(CancellationToken.None);
    }

    private static async Task<(AuthorityDaemonInfo Info, NicknameAuthorityStatus Status,
        IReadOnlyList<AccountNicknameState> Nicknames, IReadOnlyList<AccountDescriptor> Accounts)> ReadAuthoritySnapshotAsync()
    {
        await using var client = new AccountStateShadowRpcClient(AccountStateShadowDaemonProcessManager.NicknameAuthorityPipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await client.VerifyNicknameAuthorityCompatibleAsync(CancellationToken.None);
        var nicknames = await client.ListNicknamesAsync(CancellationToken.None);
        return (
            await client.GetAuthorityDaemonInfoAsync(CancellationToken.None),
            await client.GetNicknameAuthorityAsync(CancellationToken.None),
            nicknames.Accounts,
            await client.ListAccountsAsync(CancellationToken.None));
    }

    private static async Task<string?> ReadNicknameAsync(Guid accountId)
    {
        await using var client = new AccountStateShadowRpcClient(AccountStateShadowDaemonProcessManager.NicknameAuthorityPipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await client.VerifyNicknameAuthorityCompatibleAsync(CancellationToken.None);
        return (await client.GetNicknameAsync(accountId.ToString("D"), CancellationToken.None)).Nickname;
    }

    private static string CreateTransferDocument(string sub, string nickname, int sortOrder)
    {
        var transfer = new AccountTransferDocument
        {
            Accounts =
            [
                new TransferredAccount
                {
                    Nickname = nickname,
                    SortOrder = sortOrder,
                    AuthJsonBase64 = Convert.ToBase64String(CreateSyntheticAuthJson(sub))
                }
            ]
        };
        return JsonSerializer.Serialize(transfer);
    }

    private static byte[] CreateSyntheticAuthJson(string sub)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sub,
            email = $"{sub}@example.invalid"
        }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var idToken = $"eyJhbGciOiJub25lIn0.{payload}.synthetic-signature";
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            OPENAI_API_KEY = (string?)null,
            tokens = new
            {
                id_token = idToken,
                access_token = "synthetic-nonfunctional-access-token",
                refresh_token = "synthetic-nonfunctional-refresh-token",
                account_id = $"synthetic-{sub}"
            },
            last_refresh = "2026-10-20T12:00:00Z"
        }));
    }

    private static ProfileMetadata CreateProfile(string nickname, string detectedName, int order) => new()
    {
        Id = Guid.NewGuid(),
        Nickname = nickname,
        DetectedDisplayName = detectedName,
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(order),
        PlanType = "synthetic",
        HealthStatus = HealthStatus.Valid,
        SortOrder = order
    };

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (ProcessHasExited(processId))
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Owned synthetic daemon process {processId} did not exit.");
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public void Record(string action, string outcome, string? detail = null)
        {
            _ = action;
            _ = outcome;
            _ = detail;
        }
    }

    private sealed class SyntheticVaultService : IVaultService
    {
        private readonly Dictionary<Guid, byte[]> _blobs = [];
        public IProfileOperationCoordinator Coordinator => null!;
        public string BlobPath(Guid profileId) => $"synthetic-vault:{profileId:D}";
        public bool Exists(Guid profileId) => _blobs.ContainsKey(profileId);
        public string SaveBlob(Guid profileId, byte[] authJson)
        {
            _blobs[profileId] = authJson.ToArray();
            return Convert.ToHexString(SHA256.HashData(authJson)).ToLowerInvariant();
        }
        public (bool Success, string? CurrentFingerprint) SaveBlobIfUnchanged(Guid profileId, string expectedOriginalFingerprint, byte[] newAuthJson)
        {
            _ = expectedOriginalFingerprint;
            _ = newAuthJson;
            if (!_blobs.TryGetValue(profileId, out var current))
                return (false, null);
            var fingerprint = Convert.ToHexString(SHA256.HashData(current)).ToLowerInvariant();
            return (false, fingerprint);
        }
        public Task<(bool Success, string? CurrentFingerprint)> SaveBlobIfUnchangedAsync(Guid profileId, string expectedOriginalFingerprint, byte[] newAuthJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(SaveBlobIfUnchanged(profileId, expectedOriginalFingerprint, newAuthJson));
        public byte[] LoadBlob(Guid profileId) => _blobs.TryGetValue(profileId, out var value)
            ? value.ToArray()
            : throw new FileNotFoundException("Synthetic credential not found.");
        public void DeleteBlob(Guid profileId) => _blobs.Remove(profileId);
        public IReadOnlyList<Guid> EnumerateBlobs() => _blobs.Keys.ToArray();
        public void SaveEncryptedFile(string path, byte[] plaintext) => throw new NotSupportedException();
        public byte[] LoadEncryptedFile(string path) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-switchboard-nickname-authority-crossrepo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
