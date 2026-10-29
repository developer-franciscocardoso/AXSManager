using System;
using System.IO;
using System.Linq;

namespace AxsManager.Tests
{
    internal static class TestRunner
    {
        private static int failures;

        [STAThread]
        public static int Main(string[] args)
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.ThrowException);
            if (args.Length > 0 && args[0] == "--prompt-fixture")
            {
                Console.Write("Pretende executar este servico com");
                return Console.ReadLine() == "S" ? 0 : 2;
            }
            if (args.Length > 0 && args[0] == "--failure-fixture")
            {
                Console.Error.Write("Controlled command failure");
                return 7;
            }
            Run("Discovery keeps roots distinct and ignores invalid years",
                Discovery);
            Run("CFG updates preserve unrelated settings", Configuration);
            Run("Duplicate CFG keys are rejected", DuplicateKeys);
            Run("Empty duplicate print sections are repaired", DuplicatePrintSection);
            Run("Port allocation skips assigned ports", Ports);
            Run("Firewall scope accepts only addresses and CIDRs", Scope);
            Run("Windows command line arguments round trip", Arguments);
            Run("Atomic updates retain a recoverable original", AtomicWrites);
            Run("Existing services require exact executable and CFG matches", Matching);
            Run("Configuration generation uses the chosen roots", Generation);
            Run("Failed restart restores the original CFG", FailedRestart);
            Run("Unchanged configuration does not restart services", UnchangedConfig);
            Run("Invalid ports remain visible and block duplicate creation", InvalidPortInventory);
            Run("Port conflicts cannot hide ambiguous configurations", AmbiguousInventory);
            Run("Migration uses unique target names", MigrationNames);
            Run("Failed migration commit restores registrations and files", MigrationCommitFailure);
            Run("Desktop and setup windows render", WindowSmoke);
            Run("Migration rejects transitional Windows services", PendingMigration);
            Run("Migration applies the requested XML access setting", MigrationAccess);
            Run("World-writable service inputs are rejected", WritableInputs);
            Run("CFG round trips preserve existing line endings", LineEndings);
            Run("Non-XML configurations are not adopted", UnrelatedConfig);
            Run("Vendor prompt receives confirmation without a newline", VendorPrompt);
            Run("Failed commands report the exit code", FailedCommand);
            Run("Windows service inventory can be read", WindowsInventory);
            Run("Drive-root paths stay absolute", DriveRoot);
            Run("Permission consent is limited to the selected installation",
                InstallationPermissionConsent);
            Run("Existing CFG directories keep their permissions", ExistingConfigDirectory);
            Run("Right-click actions preserve multiple selected rows", RowActions);
            Run("Migration does not transfer permission consent", MigrationPermissionConsent);
            Run("XML users open directly and stay masked", XmlUsersScreen);
            Run("XML credentials accept CFG lines and imports", XmlUsersImport);
            Run("Discovered configurations are tracked without adoption", AutomaticTracking);
            Run("Migration requires complete and unambiguous discovery", MigrationInventory);
            Run("Database detection prefers running engines and retries failures", DatabaseDetection);
            Run("Keep running only selects safe stopped registrations", KeepRunningSelection);
            Run("Bulk allocation scans ports once and still checks live binds", BatchPorts);
            Run("Startup stops waiting immediately when a service fails", StartupFailure);
            Run("Startup waits for confirmation while pending", StartupPending);
            Console.WriteLine(failures == 0 ? "All tests passed." :
                failures + " test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("FAIL " + name + ": " + error);
            }
        }

        private static void StartupFailure()
        {
            int reads = 0;
            try
            {
                WindowsServices.WaitUntilRunning("Example", () =>
                {
                    reads++;
                    return System.ServiceProcess.ServiceControllerStatus.Stopped;
                }, () => "Windows exit code 6: The handle is invalid.");
                throw new Exception("A stopped service was reported as running.");
            }
            catch (InvalidOperationException error)
            {
                Assert(reads == 1, "Kept polling a failed service.");
                Assert(error.Message.Contains("Stopped") &&
                    error.Message.Contains("exit code 6"), "Failure details were lost.");
            }
        }

        private static void StartupPending()
        {
            int reads = 0;
            WindowsServices.WaitUntilRunning("Example", () => ++reads == 1 ?
                System.ServiceProcess.ServiceControllerStatus.StartPending :
                System.ServiceProcess.ServiceControllerStatus.Running,
                () => { throw new Exception("Running service read failure details."); });
            Assert(reads == 2, "Start request was mistaken for confirmed startup.");
        }

        private static void DatabaseDetection()
        {
            var system = new FakeServices();
            system.Services.Add(new ServiceSnapshot {
                Name = "Pervasive.SQL (transactional)", State = "Stopped" });
            system.Services.Add(new ServiceSnapshot {
                Name = "psqlWGE", State = "Running" });
            Assert(DatabaseDependency.Resolve(system) == "psqlWGE",
                "A stopped engine was preferred over the running engine.");
            Assert(system.Queries == 1, "Database detection repeated the inventory query.");
            var queries = system.Queries;
            DatabaseDependency.Resolve(system, system.Services);
            Assert(system.Queries == queries, "An available inventory was ignored.");
            system.Services[1].State = "Stopped";
            system.FailNextStart = true;
            Assert(DatabaseDependency.Resolve(system) == "psqlWGE",
                "A failed candidate prevented trying the next engine.");
            system.Services.Clear();
            system.Services.Add(new ServiceSnapshot {
                Name = "zenengine", State = "Stopped" });
            Assert(DatabaseDependency.Resolve(system) == "zenengine",
                "Zen was not detected.");
            system.Services.Clear();
            system.Services.Add(new ServiceSnapshot {
                Name = "UnrelatedDatabase", State = "Running" });
            Throws(() => DatabaseDependency.Resolve(system));
        }

        private static void KeepRunningSelection()
        {
            var entry = new ServiceEntry { KeepRunning = true,
                ServiceName = "example", Status = "Stopped", Available = true };
            Assert(ServiceManager.NeedsRestart(entry), "Stopped service skipped.");
            Assert(entry.Copy().KeepRunning, "Toggle lost when copying settings.");
            foreach (var state in new[] { "Running", "Start Pending", "Paused",
                "Not installed" })
            {
                entry.Status = state;
                Assert(!ServiceManager.NeedsRestart(entry), "Unsafe state restarted.");
            }
            entry.Status = "Stopped";
            entry.Problem = "Conflicting configuration";
            Assert(!ServiceManager.NeedsRestart(entry), "Conflict ignored.");
            entry.Problem = "";
            entry.KeepRunning = false;
            Assert(!ServiceManager.NeedsRestart(entry), "Disabled toggle ignored.");
            entry.KeepRunning = true;
            entry.Available = false;
            Assert(!ServiceManager.NeedsRestart(entry), "Unavailable data ignored.");
        }

        private static void BatchPorts()
        {
            var system = new FakeServices();
            var manager = new ServiceManager(new ManagerSettings(),
                new StateStore(), system);
            var allocate = typeof(ServiceManager).GetMethod("AllocatePort",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            int first = (int)allocate.Invoke(manager, null);
            manager.Current.ConfiguredPorts.Add(first);
            system.BlockedPorts.Add(first + 1);
            int second = (int)allocate.Invoke(manager, null);
            Assert(second == first + 2, "A newly occupied port was reused.");
            Assert(system.PortScans == 1, "Port inventory repeated within a batch.");
            manager.Refresh();
            allocate.Invoke(manager, null);
            Assert(system.PortScans == 2, "Refresh retained stale port inventory.");
        }

        internal static void Assert(bool condition, string message)
        {
            if (!condition) { throw new Exception(message); }
        }

        internal static void Throws(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected validation failure.");
        }

        private static void Discovery()
        {
            using (var temp = new TempFolder())
            {
                var first = Path.Combine(temp.Path, "one");
                var second = Path.Combine(temp.Path, "two");
                Directory.CreateDirectory(Path.Combine(first, "001A.art", "2026"));
                Directory.CreateDirectory(Path.Combine(first, "001A.art", "notes"));
                Directory.CreateDirectory(Path.Combine(second, "001A.ART", "2026"));
                var result = FolderDiscovery.Scan(new[] { first, first, second });
                Assert(result.Items.Count == 2, "Expected two unique company/years.");
                Assert(result.Items[0].Id != result.Items[1].Id,
                    "Different roots must not share identity.");
                Assert(result.Items.All(x => x.Company == "001A" && x.Year == 2026),
                    "Company and year must come from directory names.");
                Assert(result.Items[0].FileName != result.Items[1].FileName,
                    "Config names must not collide across roots.");
            }
        }

        private static void Configuration()
        {
            var cfg = CfgDocument.Parse(";keep\r\n[CONFIG]\r\nYear=2025\r\n" +
                "Custom=untouched\r\n[XMLSERVER]\r\nXMLPort=50123\r\n");
            cfg.Set("CONFIG", "Year", "2026");
            Assert(cfg.Get("CONFIG", "Year") == "2026", "Year not updated.");
            Assert(cfg.ToString().Contains("Custom=untouched"), "Lost custom value.");
            Assert(cfg.ToString().Contains(";keep"), "Lost comment.");
            Assert(cfg.Get("XMLSERVER", "XMLPort") == "50123", "Changed port.");
            Throws(() => cfg.Set("CONFIG", "Company", "A\r\nYear=2000"));
        }

        private static void DuplicateKeys()
        {
            Throws(() => CfgDocument.Parse("[CONFIG]\nYear=2025\nyear=2026"));
        }

        private static void DuplicatePrintSection()
        {
            foreach (var newline in new[] { "\r\n", "\n", "\r" })
            {
                var expected = "[PRINTSVC]" + newline +
                    "#NrThreads =" + newline + "#WaitTime  =5" + newline +
                    "LogFile=print.log" + newline;
                var cfg = CfgDocument.Parse("[PRINTSVC]" + newline +
                    newline + expected);
                Assert(cfg.ToString() == expected,
                    "Duplicate header remained or print settings changed.");
                Assert(CfgDocument.Parse(cfg.ToString()).ToString() == expected,
                    "Repair was not stable.");
            }
            Throws(() => CfgDocument.Parse(
                "[PRINTSVC]\nWaitTime=5\n[PRINTSVC]\nWaitTime=10"));
            Throws(() => CfgDocument.Parse(
                "[PRINTSVC]\n[CONFIG]\n[PRINTSVC]"));
        }

        private static void Ports()
        {
            Assert(PortAllocator.Next(50000, 50003,
                new[] { 50000, 50002 }) == 50001, "Allocated occupied port.");
            Throws(() => PortAllocator.Next(50000, 50000, new[] { 50000 }));
            Throws(() => PortAllocator.Next(0, 65536, new int[0]));
        }

        private static void Scope()
        {
            Assert(RemoteAccess.ValidateAddresses("192.168.1.0/24,10.0.0.8") ==
                "192.168.1.0/24,10.0.0.8", "Changed valid scope.");
            Throws(() => RemoteAccess.ValidateAddresses("192.168.1.0/99"));
            Throws(() => RemoteAccess.ValidateAddresses("*"));
            Throws(() => RemoteAccess.ValidateAddresses("10.0.0.1 & calc.exe"));
        }

        private static void Arguments()
        {
            var values = new[] { "C:\\Program Files\\ART\\ArtExecSvc.exe",
                "C:\\data folder\\", "", "embedded\"quote", "a&b" };
            var parsed = CommandLine.Split(string.Join(" ",
                values.Select(CommandLine.Quote)));
            Assert(values.SequenceEqual(parsed), "Command line quoting changed arguments.");
        }

        private static void AtomicWrites()
        {
            using (var temp = new TempFolder())
            {
                var file = Path.Combine(temp.Path, "company.cfg");
                File.WriteAllText(file, "original");
                var backup = AtomicFile.Write(file,
                    System.Text.Encoding.UTF8.GetBytes("updated"));
                Assert(File.ReadAllText(file) == "updated", "New config missing.");
                Assert(File.ReadAllText(backup) == "original", "Original not backed up.");
                AtomicFile.Restore(file, backup);
                Assert(File.ReadAllText(file) == "original", "Restore failed.");
            }
        }

        private static void Matching()
        {
            var snapshot = new ServiceSnapshot
            {
                Command = "\"C:\\ART folder\\ArtExecSvc.exe\" " +
                    "\"C:\\ART folder\\configs\\2026.cfg\" -service"
            };
            Assert(snapshot.Matches(@"C:\ART folder\ArtExecSvc.exe",
                @"C:\ART folder\configs\2026.cfg"), "Exact service not recognized.");
            Assert(!snapshot.Matches(@"C:\Other\ArtExecSvc.exe",
                @"C:\ART folder\configs\2026.cfg"), "Matched unrelated executable.");
            Assert(!snapshot.Matches(@"C:\ART folder\ArtExecSvc.exe",
                @"C:\ART folder\configs\202.cfg"), "Matched CFG substring.");
        }

        private static void Generation()
        {
            var settings = new ManagerSettings
            {
                FormsFolder = @"D:\Forms", TempFolder = @"D:\Temp",
                ServerAddress = "erp.example:2500", UsersList = "vendor:encoded"
            };
            var entry = new ServiceEntry
            {
                Root = @"D:\Fich", Company = "001A", Year = 2026,
                ExecutablePath = @"C:\Service folder\ArtExecSvc.exe",
                ConfigPath = @"C:\Service folder\ServicosXMLService\2026.cfg",
                Port = 50009
            };
            var cfg = ConfigBuilder.Build(settings, entry, null);
            Assert(!cfg.HasSection("PRINTSVC"),
                "Generated an empty print section before vendor initialization.");
            Assert(cfg.Get("CONFIG", "PathDB") == @"D:\Fich", "Wrong root.");
            Assert(cfg.Get("CONFIG", "Company") == "'001A'", "Wrong company.");
            Assert(cfg.Get("CONFIG", "PathDLL") == @"C:\Service folder", "Wrong DLL folder.");
            Assert(cfg.Get("XMLSERVER", "XMLPort") == "50009", "Wrong port.");
            Assert(cfg.Get("CONFIG", "IPList") == "localhost", "Remote enabled by default.");
            var log = Path.ChangeExtension(entry.ConfigPath, ".log");
            Assert(cfg.Get("CONFIG", "PathLog") == log &&
                cfg.Get("XMLSERVER", "LogFile") == log, "New log does not match CFG.");
            cfg.Set("CONFIG", "PathLog", @"C:\shared.log");
            cfg.Set("XMLSERVER", "LogFile", @"C:\other.log");
            cfg = ConfigBuilder.Build(settings, entry, cfg);
            Assert(cfg.Get("CONFIG", "PathLog") == log &&
                cfg.Get("XMLSERVER", "LogFile") == log, "Update kept shared log paths.");
        }

        private static void FailedRestart()
        {
            using (var temp = new TempFolder())
            {
                var path = Path.Combine(temp.Path, "test.cfg");
                File.WriteAllText(path, "original");
                var system = new FakeServices { FailNextStart = true, Running = true };
                Throws(() => ConfigurationUpdate.Apply(path,
                    System.Text.Encoding.UTF8.GetBytes("updated"),
                    new ServiceSnapshot { Name = "test", State = "Running" }, system));
                Assert(File.ReadAllText(path) == "original", "Failed restart lost original CFG.");
                Assert(system.Running, "Original service was not restarted.");
            }
        }

        private static void UnchangedConfig()
        {
            using (var temp = new TempFolder())
            {
                var path = Path.Combine(temp.Path, "test.cfg");
                var bytes = System.Text.Encoding.UTF8.GetBytes("same");
                File.WriteAllBytes(path, bytes);
                var system = new FakeServices { Running = true };
                Assert(!ConfigurationUpdate.Apply(path, bytes,
                    new ServiceSnapshot { Name = "test", State = "Running" }, system),
                    "Identical configuration reported changes.");
                Assert(system.Running && system.Stops == 0, "No-op update interrupted service.");
            }
        }

        private static void InvalidPortInventory()
        {
            using (var temp = new TempFolder())
            {
                var root = Path.Combine(temp.Path, "Fich");
                Directory.CreateDirectory(Path.Combine(root, "A.art", "2026"));
                File.WriteAllText(Path.Combine(temp.Path, "broken.cfg"),
                    "[CONFIG]\r\nPathDB=" + root + "\r\nCompany='A'\r\nYear=2026\r\n" +
                    "[XMLSERVER]\r\nXMLPort=invalid\r\n");
                var settings = new ManagerSettings { ArtsoftFolder = temp.Path };
                settings.Roots.Add(root);
                var inventory = new ServiceManager(settings, new StateStore(), new FakeServices()).Refresh();
                var entry = inventory.Items.Single();
                Assert(entry.ConfigPath.EndsWith("broken.cfg"), "Existing CFG disappeared.");
                Assert(entry.Problem != "", "Invalid CFG was not flagged.");
                Assert(!inventory.Complete, "Inventory silently accepted invalid XML port.");
            }
        }

        private static void AmbiguousInventory()
        {
            using (var temp = new TempFolder())
            {
                var root = Path.Combine(temp.Path, "Fich");
                Directory.CreateDirectory(Path.Combine(root, "A.art", "2026"));
                Directory.CreateDirectory(Path.Combine(root, "B.art", "2026"));
                var prefix = "[CONFIG]\r\nPathDB=" + root + "\r\nYear=2026\r\nCompany='";
                File.WriteAllText(Path.Combine(temp.Path, "one.cfg"), prefix +
                    "A'\r\n[XMLSERVER]\r\nXMLPort=50001\r\n");
                File.Copy(Path.Combine(temp.Path, "one.cfg"), Path.Combine(temp.Path, "two.cfg"));
                File.WriteAllText(Path.Combine(temp.Path, "three.cfg"), prefix +
                    "B'\r\n[XMLSERVER]\r\nXMLPort=50001\r\n");
                var settings = new ManagerSettings { ArtsoftFolder = temp.Path };
                settings.Roots.Add(root);
                var inventory = new ServiceManager(settings, new StateStore(), new FakeServices()).Refresh();
                Assert(inventory.Items.Single(x => x.Company == "A").Problem.StartsWith("Multiple CFG"),
                    "Port editing would bypass multiple-CFG ambiguity.");
            }
        }

        private static void MigrationNames()
        {
            using (var temp = new TempFolder())
            {
                var settings = MigrationFixture(temp.Path);
                var next = new ManagerSettings { ArtsoftFolder = Path.Combine(temp.Path, "new") };
                var plan = ServiceMigration.Prepare(settings, next, new FakeServices());
                Assert(plan.Items.Select(x => x.After.ConfigPath).Distinct().Count() == 2,
                    "Migration targets collide for matching basenames.");
            }
        }

        private static ManagerSettings MigrationFixture(string path)
        {
            var settings = new ManagerSettings { ArtsoftFolder = path };
            for (int index = 0; index < 2; index++)
            {
                var folder = Path.Combine(path, "old" + index);
                Directory.CreateDirectory(folder);
                var entry = new ServiceEntry
                {
                    Root = Path.Combine(path, "Fich"), Company = "A" + index,
                    Year = 2026, ConfigPath = Path.Combine(folder, "same.cfg"),
                    ExecutablePath = Path.Combine(path, "ArtExecSvc.exe"),
                    Port = 50000 + index, Managed = true
                };
                entry.Id = Paths.Identity(entry.Root, entry.Company, entry.Year);
                File.WriteAllText(entry.ConfigPath, "[CONFIG]\r\nPathDB=" + entry.Root +
                    "\r\nCompany='" + entry.Company + "'\r\nYear=2026\r\n" +
                    "[XMLSERVER]\r\nXMLPort=" + entry.Port + "\r\n");
                settings.Entries.Add(entry);
            }
            return settings;
        }

        private static void MigrationCommitFailure()
        {
            using (var temp = new TempFolder())
            {
                var settings = MigrationFixture(temp.Path);
                var system = new FakeServices { Running = true };
                foreach (var entry in settings.Entries)
                {
                    system.Services.Add(new ServiceSnapshot { Name = entry.Id,
                        Command = CommandLine.Join(entry.ExecutablePath, entry.ConfigPath),
                        State = "Running" });
                }
                var original = system.Services.Select(x => x.Command).ToArray();
                var next = new ManagerSettings { ArtsoftFolder = Path.Combine(temp.Path, "new") };
                var plan = ServiceMigration.Prepare(settings, next, system);
                Throws(() => ServiceMigration.Execute(plan, system,
                    delegate { throw new InvalidOperationException("Disk full during settings commit."); },
                    delegate { }, delegate { }));
                Assert(system.Services.Select(x => x.Command).SequenceEqual(original),
                    "Service registrations were not rolled back.");
                Assert(plan.Items.All(x => !File.Exists(x.After.ConfigPath)),
                    "Failed migration files prevent a safe retry.");
                Assert(plan.Items.All(x => File.Exists(x.Before.ConfigPath)), "Original CFG removed.");
                Assert(system.Services.All(x => x.State == "Running"), "Original running states not restored.");
            }
        }

        private static void PendingMigration()
        {
            using (var temp = new TempFolder())
            {
                var settings = MigrationFixture(temp.Path);
                var entry = settings.Entries[0];
                var system = new FakeServices();
                system.Services.Add(new ServiceSnapshot { Name = entry.Id,
                    Command = CommandLine.Join(entry.ExecutablePath, entry.ConfigPath),
                    State = "Start Pending" });
                Throws(() => ServiceMigration.Prepare(settings,
                    new ManagerSettings { ArtsoftFolder = Path.Combine(temp.Path, "new") }, system));
            }
        }

        private static void MigrationAccess()
        {
            using (var temp = new TempFolder())
            {
                var plan = ServiceMigration.Prepare(MigrationFixture(temp.Path),
                    new ManagerSettings { ArtsoftFolder = Path.Combine(temp.Path, "new"),
                        RemoteEnabled = true, XmlAllowedClients = "10.0.0.8" }, new FakeServices());
                var cfg = CfgDocument.Parse(System.Text.Encoding.GetEncoding(1252)
                    .GetString(plan.Items[0].Content));
                Assert(cfg.Get("CONFIG", "IPList") == "10.0.0.8",
                    "Migration ignored the requested client allowlist.");
            }
        }

        private static void WritableInputs()
        {
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.WorldSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow));
            Throws(() => Access.ValidateProtectedAcl(acl, "fixture", false));
        }

        private static void LineEndings()
        {
            const string text = "[CONFIG]\nYear=2026\n\n";
            var cfg = CfgDocument.Parse(text);
            cfg.Set("CONFIG", "Year", "2026");
            Assert(cfg.ToString() == text, "No-op update changed formatting and would restart the service.");
        }

        private static void UnrelatedConfig()
        {
            using (var temp = new TempFolder())
            {
                var root = Path.Combine(temp.Path, "Fich");
                Directory.CreateDirectory(Path.Combine(root, "A.art", "2026"));
                File.WriteAllText(Path.Combine(temp.Path, "PortalColab.cfg"),
                    "[CONFIG]\nPathDB=" + root + "\nCompany='A'\nYear=2026\nPlugins=Portal.svc\n");
                var settings = new ManagerSettings { ArtsoftFolder = temp.Path };
                settings.Roots.Add(root);
                var inventory = new ServiceManager(settings, new StateStore(), new FakeServices()).Refresh();
                Assert(inventory.Items.Single().ConfigPath == "", "Unrelated service was included.");
                Assert(inventory.Complete, "Unrelated service prevented XML configuration creation.");
            }
        }

        private static void VendorPrompt()
        {
            var output = ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,
                new[] { "--prompt-fixture" }, null, true);
            Assert(output.Contains("Pretende executar"), "Prompt process did not finish correctly.");
        }

        private static void FailedCommand()
        {
            try
            {
                ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    new[] { "--failure-fixture" }, null, false);
            }
            catch (InvalidOperationException error)
            {
                Assert(error.Message.Contains("code 7"), "Lost process exit code.");
                return;
            }
            throw new Exception("Failed command was treated as successful.");
        }

        private static void WindowsInventory()
        {
            var services = new WindowsServices().Query();
            Assert(services.Count > 0 && services.All(x => !string.IsNullOrEmpty(x.Name)),
                "Windows service inventory was empty or malformed.");
        }

        private static void DriveRoot()
        {
            Assert(Paths.Canonical(@"C:\") == @"C:\", "Drive root became a relative drive path.");
        }

        private static void InstallationPermissionConsent()
        {
            using (var temp = new TempFolder())
            {
                var folder = Path.Combine(temp.Path, "ART");
                Directory.CreateDirectory(folder);
                var executable = Path.Combine(folder, "ArtExecSvc.exe");
                File.WriteAllText(executable, "fixture");
                var acl = File.GetAccessControl(executable);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                File.SetAccessControl(executable, acl);
                var settings = new ManagerSettings { ArtsoftFolder = folder };
                Throws(() => Access.RequireInstallationFile(executable, settings));
                settings.TrustedPermissionsFolder = folder;
                Access.RequireInstallationFile(executable, settings);
                settings.ArtsoftFolder = Path.Combine(temp.Path, "different");
                Throws(() => Access.RequireInstallationFile(executable, settings));
                settings.ArtsoftFolder = folder;
                var outside = Path.Combine(temp.Path, "outside.exe");
                File.Copy(executable, outside);
                acl = File.GetAccessControl(outside);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                File.SetAccessControl(outside, acl);
                Throws(() => Access.RequireInstallationFile(outside, settings));
            }
        }

        private static void MigrationPermissionConsent()
        {
            using (var temp = new TempFolder())
            {
                var original = MigrationFixture(temp.Path);
                original.TrustedPermissionsFolder = original.ArtsoftFolder;
                var source = original.Entries[0].ConfigPath;
                var acl = File.GetAccessControl(source);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                File.SetAccessControl(source, acl);
                var next = new ManagerSettings { ArtsoftFolder = Path.Combine(temp.Path, "new") };
                Throws(() => ServiceMigration.Prepare(original, next, new FakeServices()));
                next.TrustedPermissionsFolder = next.ArtsoftFolder;
                Assert(ServiceMigration.Prepare(original, next, new FakeServices()).Items.Count == 2,
                    "Explicit consent for the destination was ignored.");
            }
        }

        private static void ExistingConfigDirectory()
        {
            using (var temp = new TempFolder())
            {
                var folder = Path.Combine(temp.Path, "ServicosXMLService");
                Directory.CreateDirectory(folder);
                var acl = Directory.GetAccessControl(folder);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                Directory.SetAccessControl(folder, acl);
                var before = Directory.GetAccessControl(folder)
                    .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
                Access.PrepareConfigDirectory(folder);
                var after = Directory.GetAccessControl(folder)
                    .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
                Assert(before == after, "Changed permissions on an existing ARTSoft folder.");
            }
        }

        private static void RowActions()
        {
            using (var form = new MainForm(new StateStore(), new ManagerSettings(), false))
            {
                var grid = Descendants(form).OfType<System.Windows.Forms.DataGridView>().Single();
                Assert(grid.ContextMenuStrip != null, "Service rows have no right-click menu.");
                Assert(grid.Columns["ServiceAction"] is
                    System.Windows.Forms.DataGridViewButtonColumn,
                    "Service rows have no direct Start/Stop button.");
                grid.Rows.Add("A", 2026);
                grid.Rows.Add("B", 2026);
                grid.Rows[0].Tag = new ServiceEntry { Company = "A", Available = true };
                grid.Rows[1].Tag = new ServiceEntry { Company = "B", Available = true };
                grid.ClearSelection();
                grid.Rows[0].Selected = true;
                RightClickRow(grid, 1);
                Assert(grid.SelectedRows.Count == 1 && grid.Rows[1].Selected,
                    "Right-click did not target the clicked row.");
                grid.Rows[0].Selected = true;
                RightClickRow(grid, 1);
                Assert(grid.SelectedRows.Count == 2, "Right-click lost the bulk selection.");
                var cancel = new System.ComponentModel.CancelEventArgs();
                typeof(System.Windows.Forms.ToolStripDropDown).GetMethod("OnOpening",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(System.ComponentModel.CancelEventArgs) }, null)
                    .Invoke(grid.ContextMenuStrip, new object[] { cancel });
                var items = MenuItems(grid.ContextMenuStrip.Items).ToList();
                Assert(items.Single(x => x.Text == "Create CFG").Enabled,
                    "New entries cannot create configurations from the menu.");
                Assert(items.Single(x => x.Text == "Start").Enabled,
                    "New entries cannot use the combined start action.");
                Assert(!items.Any(x => x.Text == "Adopt"), "Adopt is still required in the UI.");
                Assert(!items.Single(x => x.Text == "View CFG").Enabled,
                    "Single-entry action enabled for multiple rows.");
            }
        }

        private static System.Collections.Generic.IEnumerable<System.Windows.Forms.ToolStripMenuItem>
            MenuItems(System.Windows.Forms.ToolStripItemCollection collection)
        {
            foreach (var item in collection.OfType<System.Windows.Forms.ToolStripMenuItem>())
            {
                yield return item;
                foreach (var nested in MenuItems(item.DropDownItems)) { yield return nested; }
            }
        }

        private static void XmlUsersScreen()
        {
            using (var form = new SetupForm(new ManagerSettings(), true))
            {
                var tabs = Descendants(form).OfType<System.Windows.Forms.TabControl>().Single();
                Assert(tabs.SelectedTab.Text == "XML users", "Credentials shortcut opens the wrong screen.");
                var field = Descendants(tabs.SelectedTab).OfType<System.Windows.Forms.TextBox>()
                    .Single(x => x.Name == "UsersList");
                Assert(field.UseSystemPasswordChar, "Credentials are exposed by default.");
                Assert(Descendants(tabs.SelectedTab).OfType<System.Windows.Forms.Button>()
                    .Any(x => x.Text == "Import from CFG…"), "CFG import is missing.");
                ShowOffscreen(form);
                using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xml-users-preview.png"));
                }
            }
        }

        private static void XmlUsersImport()
        {
            Assert(ConfigBuilder.NormalizeUsersList(" UsersList=demo:encoded-value ") ==
                "demo:encoded-value", "Pasting the whole CFG line duplicates the key.");
            Throws(() => ConfigBuilder.NormalizeUsersList("demo:value\nYear=2000"));
            using (var temp = new TempFolder())
            {
                var path = Path.Combine(temp.Path, "existing.cfg");
                File.WriteAllText(path, "[XMLSERVER]\nUsersList=demo:encoded-value\n");
                Assert(ConfigBuilder.ReadUsersList(path) == "demo:encoded-value", "Import changed the credential value.");
                File.WriteAllText(path, "[CONFIG]\nYear=2026\n");
                Throws(() => ConfigBuilder.ReadUsersList(path));
            }
        }

        private static void AutomaticTracking()
        {
            using (var temp = new TempFolder())
            {
                var settings = MigrationFixture(temp.Path);
                var entry = settings.Entries[0].Copy();
                settings.Roots.Add(entry.Root);
                Directory.CreateDirectory(Path.Combine(entry.Root, entry.Company + ".art", "2026"));
                File.Copy(entry.ConfigPath, Path.Combine(temp.Path, "existing.cfg"));
                settings.Entries.Clear();
                var manager = new ServiceManager(settings, new StateStore(), new FakeServices());
                manager.Refresh();
                var tracked = manager.TrackedEntries();
                Assert(tracked.Count == 1 && tracked[0].Managed, "Existing configuration still requires adoption.");
                Assert(settings.Entries.Count == 0, "Read-only discovery changed persisted state.");
            }
        }

        private static void MigrationInventory()
        {
            var inventory = new Inventory { Complete = false };
            Throws(() => ServiceManager.RequireCompleteMigrationInventory(inventory));
            inventory.Complete = true;
            inventory.Items.Add(new ServiceEntry { ConfigPath = @"C:\ART\one.cfg",
                Problem = "Port is assigned more than once." });
            Throws(() => ServiceManager.RequireCompleteMigrationInventory(inventory));
            inventory.Items[0].Problem = "";
            inventory.Warnings.Add("A Fich root is unavailable.");
            Throws(() => ServiceManager.RequireCompleteMigrationInventory(inventory));
            inventory.Warnings.Clear();
            ServiceManager.RequireCompleteMigrationInventory(inventory);
        }

        private static void RightClickRow(System.Windows.Forms.DataGridView grid, int row)
        {
            var args = new System.Windows.Forms.DataGridViewCellMouseEventArgs(0,
                row, 5, 5, new System.Windows.Forms.MouseEventArgs(
                    System.Windows.Forms.MouseButtons.Right, 1, 5, 5, 0));
            typeof(System.Windows.Forms.DataGridView).GetMethod("OnCellMouseDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(System.Windows.Forms.DataGridViewCellMouseEventArgs) }, null)
                .Invoke(grid, new object[] { args });
        }

        private static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control>
            Descendants(System.Windows.Forms.Control parent)
        {
            foreach (System.Windows.Forms.Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) { yield return nested; }
            }
        }

        private static void WindowSmoke()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            var output = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            using (var form = new MainForm(new StateStore(), new ManagerSettings(), false))
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
            {
                ShowOffscreen(form);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(System.IO.Path.Combine(output, "manager-preview.png"));
                Assert(bitmap.GetPixel(30, 70) != bitmap.GetPixel(30, 400),
                    "Main window rendered blank.");
            }
            using (var form = new SetupForm(new ManagerSettings()))
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
            {
                ShowOffscreen(form);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(System.IO.Path.Combine(output, "setup-preview.png"));
                Assert(form.Controls.OfType<System.Windows.Forms.TabControl>()
                    .Single().TabPages.Count == 4, "Setup is missing a step.");
            }
        }

        private static void ShowOffscreen(System.Windows.Forms.Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000);
            form.Opacity = 0;
            form.Show();
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
        }
    }

    internal sealed class FakeServices : IServiceSystem
    {
        public bool Running;
        public bool FailNextStart;
        public int Stops;
        public int Queries;
        public int PortScans;
        public System.Collections.Generic.HashSet<int> BlockedPorts =
            new System.Collections.Generic.HashSet<int>();
        public System.Collections.Generic.List<ServiceSnapshot> Services =
            new System.Collections.Generic.List<ServiceSnapshot>();
        public System.Collections.Generic.List<ServiceSnapshot> Query()
        {
            Queries++;
            return Services.Select(x => new ServiceSnapshot { Name = x.Name,
                Command = x.Command, State = x.State, Account = x.Account }).ToList();
        }
        public void Start(string name)
        {
            if (FailNextStart)
            {
                FailNextStart = false;
                throw new InvalidOperationException("Simulated startup failure.");
            }
            Running = true;
            var service = Services.SingleOrDefault(x => x.Name == name);
            if (service != null) { service.State = "Running"; }
        }
        public void Stop(string name)
        {
            Stops++;
            Running = false;
            var service = Services.SingleOrDefault(x => x.Name == name);
            if (service != null) { service.State = "Stopped"; }
        }
        public ServiceSnapshot Install(string executable, string config, string dependency)
        {
            return Services.SingleOrDefault(x => x.Matches(executable, config));
        }
        public void Remove(string executable, string config) { }
        public void SetCommand(string name, string command)
        {
            Services.Single(x => x.Name == name).Command = command;
        }
        public System.Collections.Generic.HashSet<int> OccupiedPorts()
        {
            PortScans++;
            return new System.Collections.Generic.HashSet<int>();
        }
        public bool CanBind(int port) { return !BlockedPorts.Contains(port); }
    }

    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; private set; }
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "AxsManager.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
