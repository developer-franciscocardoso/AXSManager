using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AxsManager
{
    public sealed class ServiceSnapshot
    {
        public string Name = "";
        public string DisplayName = "";
        public string Command = "";
        public string State = "";
        public string Account = "";

        public void RequireStableState()
        {
            if (State != "Running" && State != "Stopped")
            {
                throw new InvalidOperationException("Service " + Name + " is " + State +
                    ". Wait for it to settle, or stop it, then retry.");
            }
        }

        public bool ReferencesConfig(string path)
        {
            var args = CommandLine.Split(Command);
            return args.Length > 1 && args.Skip(1).Any(arg =>
                arg.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) &&
                Paths.Equal(ResolveConfig(args[0], arg), path));
        }

        public bool Matches(string executable, string config)
        {
            var args = CommandLine.Split(Command);
            if (args.Length < 2 || !Paths.Equal(args[0], executable)) { return false; }
            return args.Skip(1).Any(arg =>
                string.Equals(Path.GetExtension(arg), ".cfg", StringComparison.OrdinalIgnoreCase) &&
                Paths.Equal(ResolveConfig(args[0], arg), config));
        }

        public static string ResolveConfig(string executable, string config)
        {
            return Path.IsPathRooted(config) ? config :
                Path.Combine(Path.GetDirectoryName(executable), config);
        }

        public string Rebind(string oldExe, string oldCfg, string newExe, string newCfg)
        {
            if (!Matches(oldExe, oldCfg))
            {
                throw new InvalidOperationException("Service registration changed; refresh first.");
            }
            var args = CommandLine.Split(Command);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) &&
                    Paths.Equal(ResolveConfig(oldExe, args[i]), oldCfg))
                {
                    args[i] = newCfg;
                }
            }
            args[0] = newExe;
            return CommandLine.Join(args);
        }
    }

    internal interface IServiceSystem
    {
        List<ServiceSnapshot> Query();
        void Start(string name);
        void Stop(string name);
        ServiceSnapshot Install(string executable, string config, string dependency);
        void Remove(string executable, string config);
        void SetCommand(string name, string command);
        HashSet<int> OccupiedPorts();
        bool CanBind(int port);
    }

    internal static class DatabaseDependency
    {
        public static string Resolve(IServiceSystem system,
            IEnumerable<ServiceSnapshot> services = null)
        {
            var names = new[] { "Pervasive.SQL (transactional)",
                "psqlWGE", "zenengine" };
            var candidates = (services ?? system.Query()).Where(x => names.Any(name =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.State == "Running")
                .ThenBy(x => Array.FindIndex(names, name => string.Equals(
                    x.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
            var errors = new List<string>();
            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.State != "Running")
                    {
                        system.Start(candidate.Name);
                    }
                    return candidate.Name;
                }
                catch (Exception error)
                {
                    errors.Add(candidate.Name + ": " + error.Message);
                }
            }
            throw new InvalidOperationException(candidates.Count == 0 ?
                "No supported database service was found. Install Pervasive, " +
                "PSQL Workgroup or Actian Zen as a Windows service, then retry." :
                "Could not start a database engine. " + string.Join("; ", errors));
        }
    }

    internal sealed class WindowsServices : IServiceSystem
    {
        public List<ServiceSnapshot> Query()
        {
            var options = new EnumerationOptions
            {
                Timeout = TimeSpan.FromSeconds(20), ReturnImmediately = false
            };
            using (var query = new ManagementObjectSearcher("root\\cimv2",
                "SELECT Name, DisplayName, PathName, State, StartName FROM Win32_Service",
                options))
            using (var services = query.Get())
            {
                return services.Cast<ManagementObject>().Select(service =>
                    new ServiceSnapshot
                    {
                        Name = Convert.ToString(service["Name"]),
                        DisplayName = Convert.ToString(service["DisplayName"]),
                        Command = Convert.ToString(service["PathName"]),
                        State = Convert.ToString(service["State"]),
                        Account = Convert.ToString(service["StartName"])
                    }).ToList();
            }
        }

        public void Start(string name)
        {
            Access.RequireAdministrator();
            using (var service = new ServiceController(name))
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running) { return; }
                if (service.Status == ServiceControllerStatus.StopPending)
                {
                    service.WaitForStatus(ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(30));
                }
                if (service.Status != ServiceControllerStatus.StartPending) { service.Start(); }
                WaitUntilRunning(name, () =>
                {
                    service.Refresh();
                    return service.Status;
                }, () => StartupDetails(name));
            }
        }

        internal static void WaitUntilRunning(string name,
            Func<ServiceControllerStatus> readStatus, Func<string> details)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var state = readStatus();
                if (state == ServiceControllerStatus.Running) { return; }
                if (state != ServiceControllerStatus.StartPending)
                {
                    throw new InvalidOperationException("Service " + name +
                        " failed to start; state: " + state + ". " + details());
                }
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(45))
                {
                    throw new InvalidOperationException("Service " + name +
                        " is still StartPending after 45 seconds. " +
                        "The start request was accepted, but startup is not confirmed. " +
                        "Refresh to check its state. " + details());
                }
                Thread.Sleep(250);
            }
        }

        private static string StartupDetails(string name)
        {
            try
            {
                var escaped = name.Replace("\\", "\\\\").Replace("'", "\\'");
                using (var query = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT ExitCode, ServiceSpecificExitCode FROM Win32_Service " +
                    "WHERE Name='" + escaped + "'", new EnumerationOptions {
                        Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false }))
                using (var results = query.Get())
                {
                    var service = results.Cast<ManagementObject>().FirstOrDefault();
                    if (service == null) { return "Service registration is no longer present."; }
                    uint code = Convert.ToUInt32(service["ExitCode"]);
                    uint specific = Convert.ToUInt32(service["ServiceSpecificExitCode"]);
                    return "Windows exit code " + code + ": " +
                        new System.ComponentModel.Win32Exception((int)code).Message +
                        "; service-specific code " + specific +
                        ". Check the ARTSoft log and Windows System event log.";
                }
            }
            catch (Exception error)
            {
                return "Could not read Windows exit details: " + error.Message;
            }
        }

        public void Stop(string name)
        {
            Access.RequireAdministrator();
            using (var service = new ServiceController(name))
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped) { return; }
                if (service.DependentServices.Any(x =>
                    x.Status != ServiceControllerStatus.Stopped))
                {
                    throw new InvalidOperationException(
                        "Other running services depend on this service; stop them explicitly first.");
                }
                if (service.Status == ServiceControllerStatus.StartPending)
                {
                    service.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromSeconds(45));
                }
                if (service.Status != ServiceControllerStatus.StopPending) { service.Stop(); }
                service.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromSeconds(45));
            }
        }

        public ServiceSnapshot Install(string executable, string config, string dependency)
        {
            Access.RequireAdministrator();
            Paths.RequireNoReparseAncestors(executable);
            Paths.RequireNoReparseAncestors(config);
            var args = new List<string> { config, "-install" };
            if (!string.IsNullOrWhiteSpace(dependency)) { args.Add(dependency); }
            ProcessRunner.Run(executable, args.ToArray(), Path.GetDirectoryName(executable), true);
            var installed = WaitForRegistration(executable, config, true);
            ProcessRunner.Run(SystemTool("sc.exe"),
                new[] { "config", installed.Name, "start=", "delayed-auto" }, null, false);
            return installed;
        }

        public void Remove(string executable, string config)
        {
            Access.RequireAdministrator();
            Paths.RequireNoReparseAncestors(executable);
            Paths.RequireNoReparseAncestors(config);
            ProcessRunner.Run(executable, new[] { config, "-remove" },
                Path.GetDirectoryName(executable), true);
            WaitForRegistration(executable, config, false);
        }

        private ServiceSnapshot WaitForRegistration(string executable,
            string config, bool present)
        {
            var timeout = Stopwatch.StartNew();
            do
            {
                var matches = Query().Where(x => x.Matches(executable, config)).ToList();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException("Multiple services reference this CFG.");
                }
                if (present && matches.Count == 1) { return matches[0]; }
                if (!present && matches.Count == 0) { return null; }
                Thread.Sleep(500);
            } while (timeout.Elapsed < TimeSpan.FromSeconds(30));
            throw new InvalidOperationException(
                "Windows did not confirm the requested service registration change.");
        }

        public void SetCommand(string name, string command)
        {
            Access.RequireAdministrator();
            ProcessRunner.Run(SystemTool("sc.exe"),
                new[] { "config", name, "binPath=", command }, null, false);
        }

        internal static string SystemTool(string name)
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.System), name);
        }

        public HashSet<int> OccupiedPorts()
        {
            var result = new HashSet<int>(IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Select(x => x.Port));
            foreach (var family in new[] { "ipv4", "ipv6" })
            {
                var output = ProcessRunner.Run(SystemTool("netsh.exe"),
                    new[] { "interface", family, "show", "excludedportrange", "protocol=tcp" },
                    null, false);
                foreach (Match match in Regex.Matches(output,
                    @"(?m)^\s*(\d+)\s+(\d+)\s*\*?\s*$"))
                {
                    int start = int.Parse(match.Groups[1].Value);
                    int end = int.Parse(match.Groups[2].Value);
                    if (start < 1 || end > 65535 || end < start) { continue; }
                    for (int port = start; port <= end; port++) { result.Add(port); }
                }
            }
            return result;
        }

        public bool CanBind(int port)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.ExclusiveAddressUse = true;
                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                    return true;
                }
            }
            catch (SocketException) { return false; }
        }

        public static bool IsReachable(int port)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    using (connect.AsyncWaitHandle)
                    {
                        if (!connect.AsyncWaitHandle.WaitOne(1500)) { return false; }
                        client.EndConnect(connect);
                        return true;
                    }
                }
                catch (SocketException) { return false; }
            }
        }
    }

    internal static class ProcessRunner
    {
        public static string Run(string executable, string[] arguments,
            string workingDirectory, bool vendor)
        {
            var start = new ProcessStartInfo(executable, CommandLine.Join(arguments))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ??
                    Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                var output = Task.Run(() => ReadOutput(process, vendor));
                var errors = Task.Run(() => ReadBounded(process.StandardError));
                if (!process.WaitForExit(vendor ? 90000 : 20000))
                {
                    process.Kill();
                    process.WaitForExit(3000);
                    throw new InvalidOperationException(Path.GetFileName(executable) +
                        " timed out. Refresh service status before retrying; " +
                        "the vendor may require an unsupported interactive prompt.");
                }
                if (!Task.WaitAll(new Task[] { output, errors }, 3000))
                {
                    throw new InvalidOperationException("Command output did not close.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(Path.GetFileName(executable) +
                        " exited with code " + process.ExitCode +
                        (vendor ? ". Check the ARTSoft service log." :
                        ": " + output.Result + errors.Result));
                }
                return output.Result;
            }
        }

        private static string ReadOutput(Process process, bool vendor)
        {
            var output = new StringBuilder();
            var window = new StringBuilder();
            bool answered = false;
            int value;
            while ((value = process.StandardOutput.Read()) >= 0)
            {
                char character = (char)value;
                if (output.Length < 16384) { output.Append(character); }
                window.Append(character);
                if (window.Length > 512) { window.Remove(0, 256); }
                if (vendor && !answered && window.ToString().IndexOf(
                    "Pretende executar este servico com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    process.StandardInput.WriteLine("S");
                    process.StandardInput.Flush();
                    answered = true;
                }
            }
            return output.ToString();
        }

        private static string ReadBounded(StreamReader reader)
        {
            var output = new StringBuilder();
            var buffer = new char[1024];
            int length;
            while ((length = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length < 16384)
                {
                    output.Append(buffer, 0, Math.Min(length, 16384 - output.Length));
                }
            }
            return output.ToString();
        }
    }
}
