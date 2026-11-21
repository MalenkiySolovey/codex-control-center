using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexSwitcher.Core.Tests;

public sealed class DirectoryHardeningTests
{
    [Fact]
    public void RestrictToCurrentUserSecuresOwnedDirectoryWithModifyOnlyAcl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodexSwitcher.DirectoryHardening.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user SID is unavailable.");
            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var rootInfo = new DirectoryInfo(root);
            var rootSecurity = rootInfo.GetAccessControl();
            rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in rootSecurity.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                rootSecurity.RemoveAccessRuleAll(rule);
            }

            rootSecurity.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            rootSecurity.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            rootInfo.SetAccessControl(rootSecurity);

            var profileHome = Path.Combine(root, "profile");
            Directory.CreateDirectory(profileHome);
            var profileInfo = new DirectoryInfo(profileHome);
            var profileSecurity = profileInfo.GetAccessControl();
            if (profileSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(user))
            {
                profileSecurity.SetOwner(user);
            }

            profileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule existingRule in profileSecurity.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                profileSecurity.RemoveAccessRuleAll(existingRule);
            }

            profileSecurity.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.Modify,
                AccessControlType.Allow));
            profileInfo.SetAccessControl(profileSecurity);

            var before = profileInfo.GetAccessControl();
            Assert.Equal(user, before.GetOwner(typeof(SecurityIdentifier)));
            Assert.Contains(
                before.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                existingRule => existingRule.IdentityReference.Equals(authenticatedUsers) &&
                                (existingRule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);

            DirectoryHardening.RestrictToCurrentUser(profileHome);

            var secured = profileInfo.GetAccessControl();
            Assert.True(secured.AreAccessRulesProtected);
            Assert.Equal(user, secured.GetOwner(typeof(SecurityIdentifier)));
            var rules = secured.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            var onlyRule = Assert.Single(rules);
            Assert.Equal(user, onlyRule.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, onlyRule.FileSystemRights);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
