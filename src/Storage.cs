using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace AxsManager
{
    internal sealed class InstallationPermissionsException : InvalidOperationException
    {
        public string TargetFile { get; set; }
        public InstallationPermissionsException(string message) : base(message) { }
    }

    internal static class Access
    {
        public static bool IsAdministrator
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity)
                        .IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        public static void RequireAdministrator()
        {
            if (!IsAdministrator)
            {
                throw new InvalidOperationException(
                    "Use 'Run as administrator' to change services or settings.");
            }
        }

        public static void SecureDirectory(string path, bool allowRead)
        {
            Paths.RequireNoReparseAncestors(path);
            Directory.CreateDirectory(path);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            AddDirectoryRule(acl, WellKnownSidType.BuiltinAdministratorsSid,
                FileSystemRights.FullControl);
            AddDirectoryRule(acl, WellKnownSidType.LocalSystemSid,
                FileSystemRights.FullControl);
            if (allowRead)
            {
                AddDirectoryRule(acl, WellKnownSidType.BuiltinUsersSid,
                    FileSystemRights.ReadAndExecute);
            }
            Directory.SetAccessControl(path, acl);
        }

        private static void AddDirectoryRule(DirectorySecurity acl,
            WellKnownSidType sid, FileSystemRights rights)
        {
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        public static void SecureFile(string path)
        {
            Paths.RequireNoReparseAncestors(path);
            File.SetAccessControl(path, PrivateFileSecurity());
        }

        public static FileSecurity PrivateFileSecurity()
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid,
                WellKnownSidType.LocalSystemSid })
            {
                acl.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }
            return acl;
        }

        public static void GrantServiceAccount(string path, string account)
        {
            if (string.IsNullOrWhiteSpace(account) ||
                string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var acl = File.GetAccessControl(path);
            acl.AddAccessRule(new FileSystemAccessRule(account,
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            File.SetAccessControl(path, acl);
            var folder = Path.GetDirectoryName(path);
            var directoryAcl = Directory.GetAccessControl(folder);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(account,
                FileSystemRights.ReadAndExecute | FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(folder, directoryAcl);
        }

        public static void PrepareConfigDirectory(string path)
        {
            Paths.RequireNoReparseAncestors(path);
            if (!Directory.Exists(path))
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                AddDirectoryRule(acl, WellKnownSidType.BuiltinAdministratorsSid,
                    FileSystemRights.FullControl);
                AddDirectoryRule(acl, WellKnownSidType.LocalSystemSid,
                    FileSystemRights.FullControl);
                Directory.CreateDirectory(path, acl);
            }
        }

        public static void RequireInstallationFile(string path,
            ManagerSettings settings)
        {
            Paths.RequireNoReparseAncestors(path);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("Required file is missing: " + path);
            }
            RequireInstallationDestination(path, File.GetAccessControl(path), settings);
        }

        public static void RequireInstallationDestination(string path,
            FileSecurity security, ManagerSettings settings)
        {
            Paths.RequireNoReparseAncestors(path);
            if (!string.IsNullOrEmpty(settings.TrustedPermissionsFolder) &&
                Paths.Equal(settings.TrustedPermissionsFolder, settings.ArtsoftFolder) &&
                Paths.Within(path, settings.ArtsoftFolder)) { return; }
            try
            {
                ValidateProtectedAcl(security, path, false);
                CheckAncestors(path);
            }
            catch (InstallationPermissionsException error)
            {
                error.TargetFile = path;
                throw;
            }
        }

        public static void RequireProtectedFile(string path)
        {
            Paths.RequireNoReparseAncestors(path);
            ValidateProtectedAcl(File.GetAccessControl(path), path, false);
            CheckAncestors(path);
        }

        private static void CheckAncestors(string path)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(parent))
            {
                if (Directory.Exists(parent))
                {
                    ValidateProtectedAcl(Directory.GetAccessControl(parent), parent, true);
                }
                parent = Path.GetDirectoryName(parent);
            }
        }

        public static void ValidateProtectedAcl(FileSystemSecurity acl,
            string path, bool ancestor)
        {
            var dangerous = FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.Delete | FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            if (!ancestor) { dangerous |= FileSystemRights.Write; }
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true,
                typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                bool broad = sid.IsWellKnown(WellKnownSidType.WorldSid) ||
                    sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
                    sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid);
                if (broad && rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & dangerous) != 0 &&
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                {
                    throw new InstallationPermissionsException(
                        "Unsafe write permissions on " + path +
                        ". Restrict writes to administrators and the service account before continuing.");
                }
            }
        }
    }

    internal static class AtomicFile
    {
        public static string Write(string path, byte[] bytes,
            FileSecurity security = null)
        {
            Paths.RequireNoReparseAncestors(path);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".axs-" +
                Guid.NewGuid().ToString("N") + ".tmp");
            string backup = null;
            if (security == null && File.Exists(path))
            {
                security = File.GetAccessControl(path);
            }
            try
            {
                using (var stream = security == null ?
                    new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None) :
                    new FileStream(temporary, FileMode.CreateNew,
                        FileSystemRights.Write, FileShare.None, 4096,
                        FileOptions.WriteThrough, security))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    backup = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") +
                        "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
                    File.Replace(temporary, path, backup);
                }
                else { File.Move(temporary, path); }
                return backup;
            }
            finally
            {
                if (File.Exists(temporary)) { File.Delete(temporary); }
            }
        }

        public static void Restore(string path, string backup)
        {
            if (string.IsNullOrEmpty(backup) || !File.Exists(backup))
            {
                throw new InvalidOperationException("Configuration backup is unavailable.");
            }
            Write(path, File.ReadAllBytes(backup));
        }
    }

    internal sealed class StateStore
    {
        public string DirectoryPath { get; private set; }
        public StateStore()
        {
            DirectoryPath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData), "AxsManager");
        }

        public ManagerSettings Load()
        {
            var path = Path.Combine(DirectoryPath, "settings.xml");
            if (!File.Exists(path)) { return new ManagerSettings(); }
            ManagerSettings result;
            var options = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024
            };
            using (var reader = XmlReader.Create(path, options))
            {
                result = (ManagerSettings)new XmlSerializer(typeof(ManagerSettings))
                    .Deserialize(reader);
            }
            if (result.Version != 1)
            {
                throw new InvalidOperationException("This settings version is not supported.");
            }
            if (!RegexSafeFileName(result.SecretFile))
            {
                throw new InvalidOperationException("Invalid credential file reference.");
            }
            var secret = Path.Combine(DirectoryPath, "secrets", result.SecretFile);
            if (Access.IsAdministrator && File.Exists(secret))
            {
                result.UsersList = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(secret), null, DataProtectionScope.LocalMachine));
            }
            return result;
        }

        private static bool RegexSafeFileName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-zA-Z0-9-]+\.bin\z");
        }

        public void Save(ManagerSettings settings)
        {
            Access.RequireAdministrator();
            Access.SecureDirectory(DirectoryPath, true);
            var secretFolder = Path.Combine(DirectoryPath, "secrets");
            Access.SecureDirectory(secretFolder, false);
            var secretFile = Guid.NewGuid().ToString("N") + ".bin";
            AtomicFile.Write(Path.Combine(secretFolder, secretFile),
                ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.UsersList ?? ""),
                    null, DataProtectionScope.LocalMachine));
            var previousSecret = settings.SecretFile;
            var previousRevision = settings.Revision;
            try
            {
                settings.SecretFile = secretFile;
                settings.Revision++;
                using (var stream = new MemoryStream())
                {
                    new XmlSerializer(typeof(ManagerSettings)).Serialize(stream, settings);
                    AtomicFile.Write(Path.Combine(DirectoryPath, "settings.xml"), stream.ToArray());
                }
            }
            catch
            {
                settings.SecretFile = previousSecret;
                settings.Revision = previousRevision;
                throw;
            }
        }

        public IDisposable AcquireWriteLock()
        {
            Access.RequireAdministrator();
            Access.SecureDirectory(DirectoryPath, true);
            try
            {
                return new FileStream(Path.Combine(DirectoryPath, "operations.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                throw new InvalidOperationException(
                    "Another AXS Manager session is changing services. Try again when it finishes.", error);
            }
        }

        public bool HasPendingMigration
        {
            get { return File.Exists(Path.Combine(DirectoryPath, "migration.xml")); }
        }

        public void WriteMigration(MigrationJournal journal)
        {
            Access.RequireAdministrator();
            using (var stream = new MemoryStream())
            {
                new XmlSerializer(typeof(MigrationJournal)).Serialize(stream, journal);
                AtomicFile.Write(Path.Combine(DirectoryPath, "migration.xml"),
                    stream.ToArray(), Access.PrivateFileSecurity());
            }
        }

        public MigrationJournal ReadMigration()
        {
            Access.RequireAdministrator();
            using (var reader = XmlReader.Create(Path.Combine(DirectoryPath, "migration.xml"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 }))
            {
                return (MigrationJournal)new XmlSerializer(typeof(MigrationJournal)).Deserialize(reader);
            }
        }

        public void ClearMigration()
        {
            Access.RequireAdministrator();
            var path = Path.Combine(DirectoryPath, "migration.xml");
            Paths.RequireNoReparseAncestors(path);
            File.Delete(path);
        }

        public void Log(string operation, string identity, string outcome)
        {
            if (!Access.IsAdministrator) { return; }
            var folder = Path.Combine(DirectoryPath, "logs");
            Access.SecureDirectory(folder, false);
            var text = DateTime.UtcNow.ToString("o") + " | " + operation +
                " | " + identity + " | " + outcome.Replace('\r', ' ').Replace('\n', ' ');
            File.AppendAllText(Path.Combine(folder,
                DateTime.UtcNow.ToString("yyyy-MM") + ".log"), text + Environment.NewLine);
        }
    }
}
