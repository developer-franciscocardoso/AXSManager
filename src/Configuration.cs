using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AxsManager
{
    internal sealed class CfgDocument
    {
        private readonly List<string> lines;
        private readonly string newline;
        public Encoding Encoding { get; private set; }

        private CfgDocument(string text, Encoding encoding)
        {
            Encoding = encoding;
            newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\r") ? "\r" : "\n";
            lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n').ToList();
            RemoveEmptyPrintDuplicates();
            ValidateStructure();
        }

        private void RemoveEmptyPrintDuplicates()
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string section;
                if (!IsSection(lines[i], out section) ||
                    !string.Equals(section, "PRINTSVC",
                        StringComparison.OrdinalIgnoreCase)) { continue; }
                int next = i + 1;
                while (next < lines.Count &&
                    string.IsNullOrWhiteSpace(lines[next])) { next++; }
                string following;
                if (next < lines.Count &&
                    IsSection(lines[next], out following) &&
                    string.Equals(section, following,
                        StringComparison.OrdinalIgnoreCase))
                {
                    lines.RemoveRange(i, next - i);
                }
            }
        }

        public static CfgDocument Parse(string text)
        {
            return new CfgDocument(text, Encoding.GetEncoding(1252,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
        }

        public static CfgDocument Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            using (var reader = new StreamReader(new MemoryStream(bytes),
                Encoding.GetEncoding(1252), true))
            {
                var text = reader.ReadToEnd();
                return new CfgDocument(text, reader.CurrentEncoding);
            }
        }

        private void ValidateStructure()
        {
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (var line in lines)
            {
                string key;
                string value;
                if (IsSection(line, out value))
                {
                    section = value;
                    if (!sections.Add(section))
                    {
                        throw new InvalidOperationException("Duplicate CFG section: " + section);
                    }
                }
                else if (TryKey(line, out key, out value) &&
                    !keys.Add(section + "\0" + key))
                {
                    throw new InvalidOperationException("Duplicate CFG key: " + key);
                }
            }
        }

        public string Get(string section, string key)
        {
            int index = Find(section, key);
            if (index < 0) { return ""; }
            string found;
            string value;
            TryKey(lines[index], out found, out value);
            return value;
        }

        public bool HasSection(string section)
        {
            return lines.Any(line =>
            {
                string found;
                return IsSection(line, out found) &&
                    string.Equals(found, section, StringComparison.OrdinalIgnoreCase);
            });
        }

        public void Set(string section, string key, string value)
        {
            ValidateValue(value);
            int index = Find(section, key);
            if (index >= 0)
            {
                if (Get(section, key) != value) { lines[index] = key + "=" + value; }
                return;
            }
            int insert = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string found;
                if (IsSection(lines[i], out found) &&
                    string.Equals(found, section, StringComparison.OrdinalIgnoreCase))
                {
                    insert = i + 1;
                    break;
                }
            }
            if (insert < 0)
            {
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
            }
            else { lines.Insert(insert, key + "=" + value); }
        }

        public static void ValidateValue(string value)
        {
            if (value == null || value.Any(c => char.IsControl(c)))
            {
                throw new InvalidOperationException(
                    "Configuration values must be single-line text.");
            }
        }

        private int Find(string section, string key)
        {
            string current = "";
            for (int i = 0; i < lines.Count; i++)
            {
                string found;
                string value;
                if (IsSection(lines[i], out value)) { current = value; }
                else if (string.Equals(current, section,
                    StringComparison.OrdinalIgnoreCase) &&
                    TryKey(lines[i], out found, out value) &&
                    string.Equals(found, key, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsSection(string line, out string section)
        {
            line = line.Trim();
            section = "";
            if (!line.StartsWith("[") || !line.EndsWith("]")) { return false; }
            section = line.Substring(1, line.Length - 2).Trim();
            return true;
        }

        private static bool TryKey(string line, out string key, out string value)
        {
            line = line.Trim();
            key = "";
            value = "";
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith("#") || line.StartsWith(";") ||
                line.StartsWith("?")) { return false; }
            key = line.Substring(0, equals).Trim();
            value = line.Substring(equals + 1).Trim();
            return true;
        }

        public byte[] Bytes()
        {
            var strict = (Encoding)Encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            return strict.GetPreamble().Concat(strict.GetBytes(ToString())).ToArray();
        }

        public override string ToString()
        {
            return string.Join(newline, lines);
        }
    }

    internal static class ConfigBuilder
    {
        public static string NormalizeUsersList(string value)
        {
            CfgDocument.ValidateValue(value);
            value = value.Trim();
            if (value.StartsWith("UsersList=", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("UsersList=".Length).Trim();
            }
            return value;
        }

        public static string ReadUsersList(string path)
        {
            var value = NormalizeUsersList(CfgDocument.Read(path).Get("XMLSERVER", "UsersList"));
            if (value.Length == 0)
            {
                throw new InvalidOperationException("This CFG has no XMLSERVER UsersList value.");
            }
            return value;
        }

        public static CfgDocument Build(ManagerSettings settings,
            ServiceEntry entry, CfgDocument existing)
        {
            var cfg = existing ?? CfgDocument.Parse(
                "[CONFIG]\r\nUseSSL=N\r\nThreads=3\r\nLoadLibs=\r\n" +
                "Plugins=ArtXMLServer.svc;PrintSvc.svc\r\n\r\n" +
                "[XMLSERVER]\r\nDigest=SHA1\r\nEncoding=Windows1252\r\n" +
                "Encript=N\r\n");
            cfg.Set("CONFIG", "PathDB", entry.Root);
            cfg.Set("CONFIG", "Company", "'" + entry.Company + "'");
            cfg.Set("CONFIG", "Year", entry.Year.ToString(CultureInfo.InvariantCulture));
            cfg.Set("CONFIG", "PathDLL", Path.GetDirectoryName(entry.ExecutablePath));
            cfg.Set("CONFIG", "PathCfg", Path.GetDirectoryName(entry.ConfigPath));
            cfg.Set("CONFIG", "PathTbg", entry.Root);
            cfg.Set("CONFIG", "PathFrm", settings.FormsFolder);
            cfg.Set("CONFIG", "PathTemp", settings.TempFolder);
            cfg.Set("CONFIG", "ServerAddr", settings.ServerAddress);
            cfg.Set("CONFIG", "ServerName", settings.ServerName);
            cfg.Set("CONFIG", "CmdPage", settings.CommandPage);
            cfg.Set("CONFIG", "Sender", settings.Sender);
            cfg.Set("CONFIG", "IPList", settings.RemoteEnabled ?
                settings.XmlAllowedClients : "localhost");
            cfg.Set("XMLSERVER", "ServerNm", settings.XmlServerName);
            cfg.Set("XMLSERVER", "ListaDLL", settings.Libraries);
            if (!string.IsNullOrWhiteSpace(settings.UsersList))
            {
                cfg.Set("XMLSERVER", "UsersList", settings.UsersList);
            }
            if (string.IsNullOrWhiteSpace(cfg.Get("XMLSERVER", "UsersList")))
            {
                throw new InvalidOperationException(
                    "Enter a vendor-formatted XML UsersList in Setup. " +
                    "Plain passwords cannot be converted by this manager.");
            }
            cfg.Set("XMLSERVER", "XMLPort", entry.Port.ToString(CultureInfo.InvariantCulture));
            var log = Path.ChangeExtension(entry.ConfigPath, ".log");
            cfg.Set("CONFIG", "PathLog", log);
            cfg.Set("XMLSERVER", "LogFile", log);
            return cfg;
        }
    }
}
