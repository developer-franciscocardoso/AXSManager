using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace AxsManager
{
    public sealed class ManagerSettings
    {
        public int Version { get; set; }
        public long Revision { get; set; }
        public string SecretFile { get; set; }
        public string ArtsoftFolder { get; set; }
        public string TrustedPermissionsFolder { get; set; }
        public List<string> Roots { get; set; }
        public string ServerAddress { get; set; }
        public string ServerName { get; set; }
        public string FormsFolder { get; set; }
        public string TempFolder { get; set; }
        public string CommandPage { get; set; }
        public string Sender { get; set; }
        public string Dependency { get; set; }
        public string XmlServerName { get; set; }
        public string Libraries { get; set; }
        public int PortStart { get; set; }
        public int PortEnd { get; set; }
        public bool RemoteEnabled { get; set; }
        public string RemoteScope { get; set; }
        public string RemoteAddresses { get; set; }
        public int FirewallProfiles { get; set; }
        public string XmlAllowedClients { get; set; }
        public List<ServiceEntry> Entries { get; set; }
        [XmlIgnore]
        public string UsersList { get; set; }

        public ManagerSettings()
        {
            Version = 1;
            SecretFile = "users.bin";
            Roots = new List<string>();
            Entries = new List<ServiceEntry>();
            ArtsoftFolder = "";
            TrustedPermissionsFolder = "";
            ServerAddress = "";
            ServerName = Environment.MachineName;
            FormsFolder = "";
            TempFolder = "";
            CommandPage = "";
            Sender = "";
            Dependency = "Pervasive.SQL (transactional)";
            XmlServerName = "www.artsoft.pt";
            Libraries = "ArtDB_DEx.dll;ArtDB_AEx.dll;ArtDB_BEx.dll;" +
                "ArtDB_TEx.dll;ArtDB_PEx.dll;TI_PDFEx.dll;TI_ListEx.dll;" +
                "ArtDB_SEx.dll;SQLQryEx.dll;DocPrintSvc.dll;ARTXMLPreAutoVenda.dll";
            UsersList = "";
            PortStart = 50000;
            PortEnd = 59999;
            RemoteScope = "LocalSubnet";
            RemoteAddresses = "";
            FirewallProfiles = 3;
            XmlAllowedClients = "localhost";
        }

        public string Executable
        {
            get { return Path.Combine(ArtsoftFolder, "ArtExecSvc.exe"); }
        }

        public void Validate()
        {
            if (!File.Exists(Executable))
            {
                throw new InvalidOperationException(
                    "Select the folder containing ArtExecSvc.exe.");
            }
            foreach (var root in Roots)
            {
                if (!Directory.Exists(root))
                {
                    throw new InvalidOperationException("Folder unavailable: " + root);
                }
                Paths.RequireLocalOrUnc(root);
            }
            Paths.RequireLocalOrUnc(ArtsoftFolder);
            if (ArtsoftFolder.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The ARTSoft executable folder must be on a local disk.");
            }
            if (string.IsNullOrWhiteSpace(ServerAddress) ||
                string.IsNullOrWhiteSpace(ServerName) ||
                string.IsNullOrWhiteSpace(FormsFolder) ||
                string.IsNullOrWhiteSpace(TempFolder))
            {
                throw new InvalidOperationException(
                    "Server address/name and Forms/Temp folders are required.");
            }
            Paths.RequireLocalOrUnc(FormsFolder);
            Paths.RequireLocalOrUnc(TempFolder);
            PortAllocator.ValidateRange(PortStart, PortEnd);
            foreach (var value in new[] { ServerAddress, ServerName, FormsFolder,
                TempFolder, CommandPage, Sender, Dependency, XmlServerName,
                Libraries, UsersList, XmlAllowedClients })
            {
                CfgDocument.ValidateValue(value);
            }
            if (RemoteEnabled)
            {
                RemoteAccess.Scope(this);
                if (string.IsNullOrWhiteSpace(XmlAllowedClients))
                {
                    throw new InvalidOperationException(
                        "Enter the ARTSoft IPList value for permitted clients.");
                }
                if (FirewallProfiles < 1 || FirewallProfiles > 7)
                {
                    throw new InvalidOperationException("Select firewall profiles.");
                }
            }
        }
    }

    public sealed class ServiceEntry
    {
        public string Id { get; set; }
        public string Root { get; set; }
        public string Company { get; set; }
        public int Year { get; set; }
        public string ConfigPath { get; set; }
        public string ExecutablePath { get; set; }
        public string ServiceName { get; set; }
        public int Port { get; set; }
        public bool Managed { get; set; }
        public bool KeepRunning { get; set; }
        [XmlIgnore]
        public string Status { get; set; }
        [XmlIgnore]
        public string Problem { get; set; }
        [XmlIgnore]
        public bool Available { get; set; }
        [XmlIgnore]
        public string Connectivity { get; set; }

        public ServiceEntry()
        {
            ConfigPath = "";
            ExecutablePath = "";
            ServiceName = "";
            Status = "Not created";
            Problem = "";
            Connectivity = "Not checked";
        }

        [XmlIgnore]
        public string FileName
        {
            get
            {
                var safe = new string(Company.Select(c =>
                    char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
                    .Take(50).ToArray());
                return Year + "_" + safe + "_" + Id.Substring(0, 16) + ".cfg";
            }
        }

        public ServiceEntry Copy()
        {
            return (ServiceEntry)MemberwiseClone();
        }
    }

    internal static class Paths
    {
        public static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return ""; }
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return full.Length == root.Length ? root : full.TrimEnd('\\', '/');
        }

        public static bool Equal(string first, string second)
        {
            return string.Equals(Canonical(first), Canonical(second),
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool Within(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(directory)) { return false; }
            var root = Canonical(directory).TrimEnd('\\') + "\\";
            return Canonical(path).StartsWith(root,
                StringComparison.OrdinalIgnoreCase);
        }

        public static void RequireLocalOrUnc(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
                path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 ||
                path.IndexOf('\n') >= 0 || path.StartsWith(@"\\?\") ||
                path.StartsWith(@"\\.\"))
            {
                throw new InvalidOperationException("Use a full disk or UNC path.");
            }
            if (path.Length > 240)
            {
                throw new InvalidOperationException(
                    "Choose a shorter path for compatibility with older Windows.");
            }
        }

        public static string Identity(string root, string company, int year)
        {
            var text = Canonical(root).ToUpperInvariant() + "|" +
                company.ToUpperInvariant() + "|" + year;
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        public static void RequireNoReparseAncestors(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    IsReparsePoint(current))
                {
                    throw new InvalidOperationException(
                        "Linked paths cannot be used for privileged writes: " + current);
                }
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
