using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Serialization;

namespace AxsManager
{
    public sealed class MigrationJournal
    {
        public string TargetFolder { get; set; }
        public List<MigrationRecord> Items { get; set; }
        public MigrationJournal() { Items = new List<MigrationRecord>(); }
    }

    public sealed class MigrationRecord
    {
        public ServiceEntry Before { get; set; }
        public ServiceEntry After { get; set; }
        public ServiceSnapshot Service { get; set; }
        public string ContentHash { get; set; }
        [XmlIgnore]
        public byte[] Content { get; set; }
    }

    internal static class ServiceMigration
    {
        public static MigrationJournal Prepare(ManagerSettings original,
            ManagerSettings next, IServiceSystem system,
            IEnumerable<ServiceEntry> entries = null)
        {
            var journal = new MigrationJournal { TargetFolder = next.ArtsoftFolder };
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var services = system.Query();
            foreach (var entry in entries ?? original.Entries)
            {
                ServiceManager.ValidateConfigIdentity(entry);
                var updated = entry.Copy();
                updated.ExecutablePath = next.Executable;
                updated.ConfigPath = Path.Combine(next.ArtsoftFolder,
                    "ServicosXMLService", entry.FileName);
                Paths.RequireLocalOrUnc(updated.ConfigPath);
                Paths.RequireNoReparseAncestors(updated.ConfigPath);
                Access.RequireInstallationDestination(updated.ConfigPath,
                    File.GetAccessControl(entry.ConfigPath), next);
                if (!targets.Add(Paths.Canonical(updated.ConfigPath)) ||
                    File.Exists(updated.ConfigPath))
                {
                    throw new InvalidOperationException(
                        "Migration target conflicts with an existing file: " + updated.ConfigPath);
                }
                var matches = services.Where(x => x.Matches(entry.ExecutablePath,
                    entry.ConfigPath)).ToList();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException("Ambiguous service registration.");
                }
                if (matches.Count == 1) { matches[0].RequireStableState(); }
                var cfg = CfgDocument.Read(entry.ConfigPath);
                cfg.Set("CONFIG", "PathDLL", next.ArtsoftFolder);
                cfg.Set("CONFIG", "PathCfg", Path.GetDirectoryName(updated.ConfigPath));
                cfg.Set("CONFIG", "PathLog", Path.ChangeExtension(updated.ConfigPath, ".log"));
                cfg.Set("XMLSERVER", "LogFile", Path.ChangeExtension(updated.ConfigPath, ".log"));
                cfg.Set("CONFIG", "IPList", next.RemoteEnabled ?
                    next.XmlAllowedClients : "localhost");
                var content = cfg.Bytes();
                journal.Items.Add(new MigrationRecord { Before = entry.Copy(), After = updated,
                    Service = matches.SingleOrDefault(), Content = content, ContentHash = Hash(content) });
            }
            return journal;
        }

        public static void Execute(MigrationJournal journal, IServiceSystem system,
            Action commit, Action<ServiceEntry> applyFirewall, Action<ServiceEntry> restoreFirewall)
        {
            try
            {
                foreach (var plan in journal.Items)
                {
                    AtomicFile.Write(plan.After.ConfigPath, plan.Content,
                        File.GetAccessControl(plan.Before.ConfigPath));
                    if (plan.Service == null) { continue; }
                    Access.GrantServiceAccount(plan.After.ConfigPath, plan.Service.Account);
                    if (plan.Service.State == "Running") { system.Stop(plan.Service.Name); }
                    system.SetCommand(plan.Service.Name, plan.Service.Rebind(
                        plan.Before.ExecutablePath, plan.Before.ConfigPath,
                        plan.After.ExecutablePath, plan.After.ConfigPath));
                    if (plan.Service.State == "Running") { system.Start(plan.Service.Name); }
                    applyFirewall(plan.After);
                }
                commit();
            }
            catch (Exception error)
            {
                try { Rollback(journal, system, restoreFirewall); }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException("Migration failed: " + error.Message +
                        " Recovery needs attention: " + recovery.Message +
                        " Use Recover migration before making further changes.", error);
                }
                throw new InvalidOperationException("Migration failed: " + error.Message +
                    " Original registrations and configurations restored. " +
                    "Use Recover migration to clear the recovery record before retrying.", error);
            }
        }

        public static void Rollback(MigrationJournal journal, IServiceSystem system,
            Action<ServiceEntry> restoreFirewall)
        {
            var failures = new List<string>();
            foreach (var plan in journal.Items.AsEnumerable().Reverse())
            {
                try
                {
                    if (plan.Service != null)
                    {
                        var current = system.Query().SingleOrDefault(x => x.Name == plan.Service.Name);
                        if (current == null)
                        {
                            throw new InvalidOperationException("Original Windows service is missing.");
                        }
                        var migrated = plan.Service.Rebind(plan.Before.ExecutablePath,
                            plan.Before.ConfigPath, plan.After.ExecutablePath, plan.After.ConfigPath);
                        if (current.Command == migrated)
                        {
                            ServiceManager.ValidateConfigIdentity(plan.Before);
                            system.Stop(current.Name);
                            system.SetCommand(current.Name, plan.Service.Command);
                        }
                        else if (current.Command != plan.Service.Command)
                        {
                            throw new InvalidOperationException("Service registration was changed outside this migration.");
                        }
                        if (plan.Service.State == "Running") { system.Start(plan.Service.Name); }
                        restoreFirewall(plan.Before);
                    }
                    RemoveCopy(plan, system);
                }
                catch (Exception error)
                {
                    failures.Add(plan.Before.Company + " / " + plan.Before.Year + ": " + error.Message);
                }
            }
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", failures));
            }
        }

        private static void RemoveCopy(MigrationRecord plan, IServiceSystem system)
        {
            var path = plan.After.ConfigPath;
            if (!File.Exists(path)) { return; }
            Paths.RequireNoReparseAncestors(path);
            if (Hash(File.ReadAllBytes(path)) != plan.ContentHash ||
                system.Query().Any(x => x.ReferencesConfig(path)))
            {
                throw new InvalidOperationException(
                    "A copied CFG changed or is still referenced; it was retained: " + path);
            }
            File.Delete(path);
        }

        private static string Hash(byte[] content)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "");
            }
        }
    }
}
