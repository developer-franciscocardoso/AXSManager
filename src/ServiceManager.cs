using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AxsManager
{
    internal static class ConfigurationUpdate
    {
        public static bool Apply(string path, byte[] content,
            ServiceSnapshot service, IServiceSystem system)
        {
            if (service != null) { service.RequireStableState(); }
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(content))
            {
                return false;
            }
            bool running = service != null && service.State == "Running";
            string backup = null;
            bool written = false;
            if (running) { system.Stop(service.Name); }
            try
            {
                backup = AtomicFile.Write(path, content);
                written = true;
                if (running) { system.Start(service.Name); }
                return true;
            }
            catch (Exception error)
            {
                try
                {
                    if (written && backup != null)
                    {
                        if (running) { system.Stop(service.Name); }
                        AtomicFile.Restore(path, backup);
                    }
                    if (running) { system.Start(service.Name); }
                }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException(
                        "Update failed: " + error.Message +
                        " Recovery also failed: " + recovery.Message +
                        " Backup: " + (backup ?? "no file was replaced"), error);
                }
                throw new InvalidOperationException(
                    "Update failed; original configuration and running state restored. " +
                    error.Message, error);
            }
        }
    }

    internal sealed class Inventory
    {
        public List<ServiceEntry> Items = new List<ServiceEntry>();
        public List<string> Warnings = new List<string>();
        public HashSet<int> ConfiguredPorts = new HashSet<int>();
        public bool Complete = true;
    }

    internal sealed class ServiceManager
    {
        private readonly IServiceSystem system;
        private readonly StateStore store;
        private HashSet<int> occupiedPorts;
        public ManagerSettings Settings { get; private set; }
        public Inventory Current { get; private set; }

        public ServiceManager(ManagerSettings settings, StateStore stateStore,
            IServiceSystem serviceSystem)
        {
            Settings = settings;
            store = stateStore;
            system = serviceSystem;
            Current = new Inventory();
        }

        public Inventory Refresh()
        {
            occupiedPorts = null;
            var discovery = FolderDiscovery.Scan(Settings.Roots);
            var result = new Inventory { Items = discovery.Items,
                Warnings = discovery.Warnings };
            if (store.HasPendingMigration)
            {
                result.Warnings.Add("A migration recovery record exists. Use Recover migration before making changes.");
            }
            foreach (var saved in Settings.Entries)
            {
                if (!Settings.Roots.Any(root => Paths.Equal(root, saved.Root)))
                {
                    continue;
                }
                var found = result.Items.SingleOrDefault(x => x.Id == saved.Id);
                var entry = saved.Copy();
                entry.Available = found != null;
                entry.Problem = "";
                entry.Status = "Not installed";
                if (found != null) { result.Items.Remove(found); }
                result.Items.Add(entry);
            }
            var services = system.Query();
            var configs = FindConfigs(result);
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in configs)
            {
                try { ReadConfig(path, services, result, matched); }
                catch (Exception error)
                {
                    if (!(error is IOException) &&
                        !(error is UnauthorizedAccessException) &&
                        !(error is InvalidOperationException) &&
                        !(error is ArgumentException)) { throw; }
                    result.Complete = false;
                    result.Warnings.Add(Path.GetFileName(path) + ": " + error.Message);
                }
            }
            foreach (var entry in result.Items)
            {
                BindService(entry, services);
                if (!entry.Available && string.IsNullOrEmpty(entry.Problem))
                {
                    entry.Status += " / data folder missing";
                }
                if (entry.Managed && !string.IsNullOrEmpty(entry.ConfigPath) &&
                    !File.Exists(entry.ConfigPath))
                {
                    entry.Problem = "Managed CFG is missing. Restore its backup before continuing.";
                }
            }
            foreach (var group in result.Items.Where(x => x.Port > 0)
                .GroupBy(x => x.Port).Where(x => x.Count() > 1))
            {
                foreach (var entry in group)
                {
                    if (entry.Problem == "")
                    {
                        entry.Problem = "Port " + group.Key + " is assigned more than once. Edit port to resolve.";
                    }
                }
            }
            result.Items = result.Items.OrderBy(x => x.Company)
                .ThenByDescending(x => x.Year).ThenBy(x => x.Root).ToList();
            Current = result;
            return result;
        }

        private HashSet<string> FindConfigs(Inventory result)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Settings.ArtsoftFolder,
                Path.Combine(Settings.ArtsoftFolder, "ServicosXMLService") })
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { continue; }
                try
                {
                    foreach (var path in Directory.GetFiles(folder, "*.cfg"))
                    {
                        paths.Add(Paths.Canonical(path));
                    }
                }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException)) { throw; }
                    result.Complete = false;
                    result.Warnings.Add(folder + ": " + error.Message);
                }
            }
            foreach (var entry in Settings.Entries)
            {
                if (File.Exists(entry.ConfigPath)) { paths.Add(Paths.Canonical(entry.ConfigPath)); }
            }
            return paths;
        }

        private void ReadConfig(string path, List<ServiceSnapshot> services,
            Inventory result, HashSet<string> matched)
        {
            var cfg = CfgDocument.Read(path);
            if (!cfg.HasSection("XMLSERVER")) { return; }
            int port;
            bool validPort = int.TryParse(cfg.Get("XMLSERVER", "XMLPort"), out port) &&
                port > 0 && port <= 65535;
            if (validPort) { result.ConfiguredPorts.Add(port); }
            var company = cfg.Get("CONFIG", "Company").Trim('\'', '"');
            var root = cfg.Get("CONFIG", "PathDB");
            int year;
            if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(root) ||
                !int.TryParse(cfg.Get("CONFIG", "Year"), out year)) { return; }
            var identity = Paths.Identity(root, company, year);
            var entry = result.Items.SingleOrDefault(x => x.Id == identity);
            if (entry == null) { return; }
            if (!matched.Add(identity))
            {
                entry.Problem = "Multiple CFG files reference this company/year. Resolve them first.";
                return;
            }
            if (entry.Managed && !Paths.Equal(entry.ConfigPath, path))
            {
                entry.Problem = "A different CFG already references this managed company/year.";
                return;
            }
            entry.ConfigPath = path;
            if (string.IsNullOrEmpty(entry.ExecutablePath))
            {
                entry.ExecutablePath = Settings.Executable;
            }
            entry.Port = validPort ? port : 0;
            if (!validPort)
            {
                result.Complete = false;
                entry.Problem = "Invalid XMLPort in the existing CFG. Correct it before managing this service.";
            }
            entry.Status = "Configuration ready";
            BindService(entry, services);
        }

        private static void BindService(ServiceEntry entry,
            IEnumerable<ServiceSnapshot> services)
        {
            if (string.IsNullOrEmpty(entry.ConfigPath) ||
                string.IsNullOrEmpty(entry.ExecutablePath)) { return; }
            var matches = services.Where(x => x.Matches(entry.ExecutablePath,
                entry.ConfigPath)).ToList();
            if (matches.Count > 1)
            {
                entry.Problem = "Multiple Windows services use this configuration.";
                return;
            }
            if (matches.Count == 1)
            {
                entry.ServiceName = matches[0].Name;
                entry.Status = matches[0].State;
            }
            else
            {
                entry.ServiceName = "";
                entry.Status = File.Exists(entry.ConfigPath) ? "Not installed" : "Not created";
            }
        }

        private ServiceSnapshot Registered(ServiceEntry entry,
            IEnumerable<ServiceSnapshot> services = null)
        {
            var matches = (services ?? system.Query()).Where(x => x.Matches(
                entry.ExecutablePath, entry.ConfigPath)).ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException("Multiple matching Windows services.");
            }
            return matches.SingleOrDefault();
        }

        private void Guard(ServiceEntry entry, bool needsData, bool requiresManaged)
        {
            Access.RequireAdministrator();
            RequireNoMigration();
            if (!string.IsNullOrEmpty(entry.Problem))
            {
                throw new InvalidOperationException(entry.Problem);
            }
            if (needsData && (!entry.Available ||
                !Directory.Exists(Path.Combine(entry.Root, entry.Company + ".art",
                    entry.Year.ToString()))))
            {
                throw new InvalidOperationException("The company/year data folder is unavailable.");
            }
            if (!string.IsNullOrEmpty(entry.ConfigPath))
            {
                Paths.RequireNoReparseAncestors(entry.ConfigPath);
                if (File.Exists(entry.ConfigPath) && needsData)
                {
                    Access.RequireInstallationFile(entry.ConfigPath, Settings);
                    Access.RequireInstallationFile(entry.ExecutablePath, Settings);
                }
            }
            if (requiresManaged && !entry.Managed)
            {
                entry.Managed = true;
                Remember(entry);
            }
        }

        internal List<ServiceEntry> TrackedEntries()
        {
            var entries = Settings.Entries.ToDictionary(x => x.Id, x => x.Copy());
            foreach (var entry in Current.Items.Where(x =>
                !string.IsNullOrEmpty(x.ConfigPath) && string.IsNullOrEmpty(x.Problem)))
            {
                var tracked = entry.Copy();
                tracked.Managed = true;
                entries[entry.Id] = tracked;
            }
            return entries.Values.ToList();
        }

        private void Remember(ServiceEntry entry)
        {
            Settings.Entries = TrackedEntries();
            var old = Settings.Entries.SingleOrDefault(x => x.Id == entry.Id);
            if (old != null) { Settings.Entries.Remove(old); }
            Settings.Entries.Add(entry.Copy());
            store.Save(Settings);
        }

        public void EnsureStarted(ServiceEntry entry, Action<string> report = null)
        {
            report = report ?? delegate { };
            Guard(entry, true, false);
            if (string.IsNullOrEmpty(entry.ConfigPath))
            {
                report("Creating CFG...");
                Create(entry);
            }
            else
            {
                var service = Registered(entry);
                if (service == null || service.State == "Stopped")
                {
                    report("Applying settings...");
                    var configured = ConfigBuilder.Build(Settings, entry,
                        CfgDocument.Read(entry.ConfigPath)).Bytes();
                    if (!File.ReadAllBytes(entry.ConfigPath).SequenceEqual(configured))
                    {
                        Update(entry, null);
                    }
                }
            }
            report("Checking installation...");
            var installed = Install(entry);
            report("Starting...");
            system.Start(installed.Name);
            report("Running");
        }

        internal static bool NeedsRestart(ServiceEntry entry)
        {
            return entry.KeepRunning && entry.Available &&
                string.IsNullOrEmpty(entry.Problem) &&
                !string.IsNullOrEmpty(entry.ServiceName) &&
                entry.Status == "Stopped";
        }

        public void SetKeepRunning(ServiceEntry entry, bool enabled)
        {
            Access.RequireAdministrator();
            RequireNoMigration();
            if (enabled) { EnsureStarted(entry); }
            entry.KeepRunning = enabled;
            entry.Managed = true;
            Remember(entry);
        }

        public void RestartStoppedServices(Action<string> report)
        {
            foreach (var entry in Current.Items.Where(NeedsRestart))
            {
                try
                {
                    Control(entry, "Start");
                    store.Log("Keep running", entry.Id, "Restarted");
                    report("Keep running: " + entry.Company + " / " +
                        entry.Year + " restarted.");
                }
                catch (Exception error)
                {
                    store.Log("Keep running", entry.Id, error.Message);
                    report("Keep running: " + entry.Company + " / " +
                        entry.Year + ": " + error.Message);
                }
            }
        }

        public void Create(ServiceEntry entry)
        {
            Guard(entry, true, false);
            if (!Current.Complete)
            {
                throw new InvalidOperationException("Resolve inventory warnings before creating configurations.");
            }
            if (File.Exists(entry.ConfigPath))
            {
                throw new InvalidOperationException("Configuration already exists; use Update CFG.");
            }
            Settings.Validate();
            var candidate = entry.Copy();
            candidate.ExecutablePath = Settings.Executable;
            candidate.ConfigPath = Path.Combine(Settings.ArtsoftFolder,
                "ServicosXMLService", candidate.FileName);
            Paths.RequireLocalOrUnc(candidate.ConfigPath);
            if (File.Exists(candidate.ConfigPath))
            {
                throw new InvalidOperationException("The destination configuration already exists.");
            }
            candidate.Port = AllocatePort();
            var content = ConfigBuilder.Build(Settings, candidate, null).Bytes();
            Access.PrepareConfigDirectory(Path.GetDirectoryName(candidate.ConfigPath));
            AtomicFile.Write(candidate.ConfigPath, content, Access.PrivateFileSecurity());
            candidate.Managed = true;
            Remember(candidate);
            Current.ConfiguredPorts.Add(candidate.Port);
            entry.ConfigPath = candidate.ConfigPath;
            entry.ExecutablePath = candidate.ExecutablePath;
            entry.Port = candidate.Port;
            entry.Managed = true;
        }

        private int AllocatePort()
        {
            if (occupiedPorts == null) { occupiedPorts = system.OccupiedPorts(); }
            var occupied = occupiedPorts;
            occupied.UnionWith(Current.ConfiguredPorts);
            occupied.UnionWith(Settings.Entries.Where(x => x.Port > 0).Select(x => x.Port));
            while (true)
            {
                int candidate = PortAllocator.Next(Settings.PortStart, Settings.PortEnd, occupied);
                if (system.CanBind(candidate)) { return candidate; }
                occupied.Add(candidate);
            }
        }

        public ServiceSnapshot Install(ServiceEntry entry)
        {
            Guard(entry, true, true);
            ValidateConfigIdentity(entry);
            var services = system.Query();
            var existing = Registered(entry, services);
            if (existing != null) { return existing; }
            if (!system.CanBind(entry.Port))
            {
                throw new InvalidOperationException("Port is unavailable. Edit port before installing.");
            }
            var dependency = DatabaseDependency.Resolve(system, services);
            var installed = system.Install(entry.ExecutablePath, entry.ConfigPath, dependency);
            if (installed == null ||
                !installed.Matches(entry.ExecutablePath, entry.ConfigPath))
            {
                throw new InvalidOperationException("Installation was not confirmed by Windows.");
            }
            entry.ServiceName = installed.Name;
            Access.GrantServiceAccount(entry.ConfigPath, installed.Account);
            Remember(entry);
            Firewall.Apply(Settings, entry);
            return installed;
        }

        public void Control(ServiceEntry entry, string action)
        {
            Guard(entry, action != "Stop" && action != "Uninstall", true);
            var service = Registered(entry);
            if (service == null)
            {
                throw new InvalidOperationException("This service is not installed.");
            }
            if (action == "Start" || action == "Restart")
            {
                ValidateConfigIdentity(entry);
            }
            if (action == "Uninstall")
            {
                Access.RequireInstallationFile(entry.ExecutablePath, Settings);
                Access.RequireInstallationFile(entry.ConfigPath, Settings);
            }
            if (action == "Stop" || action == "Restart" || action == "Uninstall")
            {
                if (action != "Restart" && entry.KeepRunning)
                {
                    entry.KeepRunning = false;
                    Remember(entry);
                }
                system.Stop(service.Name);
            }
            if (action == "Start" || action == "Restart") { system.Start(service.Name); }
            if (action == "Uninstall")
            {
                system.Remove(entry.ExecutablePath, entry.ConfigPath);
                entry.ServiceName = "";
                Remember(entry);
                Firewall.Remove(entry);
            }
        }

        public void Update(ServiceEntry entry, int? newPort)
        {
            bool portConflict = newPort.HasValue && entry.Problem.StartsWith("Port ");
            var candidate = entry.Copy();
            if (portConflict) { candidate.Problem = ""; }
            Guard(candidate, true, true);
            Settings.Validate();
            ValidateConfigIdentity(candidate);
            var service = Registered(candidate);
            if (newPort.HasValue && newPort.Value != candidate.Port)
            {
                PortAllocator.ValidateRange(newPort.Value, newPort.Value);
                if (Current.ConfiguredPorts.Contains(newPort.Value) ||
                    system.OccupiedPorts().Contains(newPort.Value) ||
                    !system.CanBind(newPort.Value))
                {
                    throw new InvalidOperationException("The selected port is already used or reserved.");
                }
                candidate.Port = newPort.Value;
            }
            var content = ConfigBuilder.Build(Settings, candidate,
                CfgDocument.Read(candidate.ConfigPath)).Bytes();
            ConfigurationUpdate.Apply(candidate.ConfigPath, content, service, system);
            Remember(candidate);
            entry.Port = candidate.Port;
            entry.Problem = "";
            Current.ConfiguredPorts.Add(candidate.Port);
            if (service != null) { Firewall.Apply(Settings, entry); }
        }

        internal static void ValidateConfigIdentity(ServiceEntry entry)
        {
            var cfg = CfgDocument.Read(entry.ConfigPath);
            int year;
            int port;
            if (!int.TryParse(cfg.Get("CONFIG", "Year"), out year) ||
                !int.TryParse(cfg.Get("XMLSERVER", "XMLPort"), out port) ||
                Paths.Identity(cfg.Get("CONFIG", "PathDB"),
                    cfg.Get("CONFIG", "Company").Trim('\'', '"'), year) != entry.Id ||
                port != entry.Port)
            {
                throw new InvalidOperationException("CFG changed since discovery. Refresh before continuing.");
            }
        }

        public void ApplyFirewall(ServiceEntry entry)
        {
            Guard(entry, false, true);
            ValidateConfigIdentity(entry);
            if (Registered(entry) == null)
            {
                Firewall.Remove(entry);
                return;
            }
            Firewall.Apply(Settings, entry);
        }

        public void SaveSettings(ManagerSettings next)
        {
            Access.RequireAdministrator();
            RequireNoMigration();
            next.Validate();
            next.Revision = Settings.Revision;
            Refresh();
            if (!Paths.Equal(Settings.ArtsoftFolder, next.ArtsoftFolder))
            {
                RequireCompleteMigrationInventory(Current);
            }
            next.Entries = TrackedEntries();
            if (!Paths.Equal(Settings.ArtsoftFolder, next.ArtsoftFolder) &&
                next.Entries.Count > 0)
            {
                Migrate(next);
                return;
            }
            if (!next.RemoteEnabled)
            {
                foreach (var entry in next.Entries) { Firewall.Remove(entry); }
            }
            store.Save(next);
            Settings = next;
        }

        private void Migrate(ManagerSettings next)
        {
            Access.RequireInstallationFile(next.Executable, next);
            Access.PrepareConfigDirectory(Path.Combine(next.ArtsoftFolder, "ServicosXMLService"));
            foreach (var entry in next.Entries)
            {
                Access.RequireInstallationFile(entry.ConfigPath, Settings);
            }
            var journal = ServiceMigration.Prepare(Settings, next, system, next.Entries);
            store.WriteMigration(journal);
            ServiceMigration.Execute(journal, system, delegate
            {
                next.Entries = journal.Items.Select(x => x.After).ToList();
                store.Save(next);
            }, entry =>
            {
                Firewall.Apply(next, entry);
            }, entry => Firewall.Apply(Settings, entry));
            Settings = next;
            store.ClearMigration();
        }

        internal static void RequireCompleteMigrationInventory(Inventory inventory)
        {
            if (!inventory.Complete || inventory.Warnings.Count > 0 ||
                inventory.Items.Any(x => !string.IsNullOrEmpty(x.ConfigPath) &&
                    !string.IsNullOrEmpty(x.Problem)))
            {
                throw new InvalidOperationException(
                    "Resolve discovery warnings and configuration conflicts before moving " +
                    "the ARTSoft folder. All configurations must be included in the move.");
            }
        }

        private void RequireNoMigration()
        {
            if (store.HasPendingMigration)
            {
                throw new InvalidOperationException(
                    "Use Recover migration before changing services or settings.");
            }
        }

        public void CheckPermissions(string action, IEnumerable<ServiceEntry> entries)
        {
            if (action == "Create CFG" ||
                action == "Stop" || action == "Firewall") { return; }
            var paths = entries.SelectMany(entry => new[] {
                entry.ExecutablePath, entry.ConfigPath }).Where(path =>
                !string.IsNullOrEmpty(path));
            if (action == "Start") { paths = paths.Concat(new[] { Settings.Executable }); }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths) { Access.RequireInstallationFile(path, Settings); }
        }

        public void RecoverMigration()
        {
            Access.RequireAdministrator();
            if (!store.HasPendingMigration) { return; }
            var journal = store.ReadMigration();
            bool committed = Paths.Equal(Settings.ArtsoftFolder, journal.TargetFolder) &&
                journal.Items.All(x => Settings.Entries.Any(entry =>
                    entry.Id == x.After.Id && Paths.Equal(entry.ConfigPath, x.After.ConfigPath)));
            if (committed)
            {
                var services = system.Query();
                foreach (var plan in journal.Items)
                {
                    ValidateConfigIdentity(plan.After);
                    if (plan.Service != null && !services.Any(x => x.Name == plan.Service.Name &&
                        x.Matches(plan.After.ExecutablePath, plan.After.ConfigPath)))
                    {
                        throw new InvalidOperationException(
                            "Committed migration registration differs from Windows. Inspect the service before recovery.");
                    }
                }
            }
            else { ServiceMigration.Rollback(journal, system, entry => Firewall.Apply(Settings, entry)); }
            store.ClearMigration();
        }
    }
}
