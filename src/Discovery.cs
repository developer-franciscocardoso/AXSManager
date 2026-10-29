using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AxsManager
{
    internal sealed class DiscoveryResult
    {
        public List<ServiceEntry> Items = new List<ServiceEntry>();
        public List<string> Warnings = new List<string>();
    }

    internal static class FolderDiscovery
    {
        public static DiscoveryResult Scan(IEnumerable<string> roots)
        {
            var result = new DiscoveryResult();
            foreach (var root in roots.Select(Paths.Canonical)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { ScanRoot(root, result); }
                catch (IOException error) { result.Warnings.Add(root + ": " + error.Message); }
                catch (UnauthorizedAccessException error)
                {
                    result.Warnings.Add(root + ": " + error.Message);
                }
            }
            result.Items = result.Items.OrderBy(x => x.Company)
                .ThenByDescending(x => x.Year).ThenBy(x => x.Root).ToList();
            return result;
        }

        private static void ScanRoot(string root, DiscoveryResult result)
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (!directory.EndsWith(".art", StringComparison.OrdinalIgnoreCase) ||
                    Paths.IsReparsePoint(directory)) { continue; }
                var company = Path.GetFileName(directory);
                company = company.Substring(0, company.Length - 4);
                if (company.Length == 0 || company.IndexOfAny(new[] { '\'', '"',
                    '\r', '\n', '\t' }) >= 0) { continue; }
                try { ScanCompany(root, directory, company, result); }
                catch (IOException error)
                {
                    result.Warnings.Add(directory + ": " + error.Message);
                }
                catch (UnauthorizedAccessException error)
                {
                    result.Warnings.Add(directory + ": " + error.Message);
                }
            }
        }

        private static void ScanCompany(string root, string directory,
            string company, DiscoveryResult result)
        {
            foreach (var yearFolder in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(yearFolder);
                int year;
                if (name.Length != 4 || !name.All(c => c >= '0' && c <= '9') ||
                    !int.TryParse(name, NumberStyles.None,
                        CultureInfo.InvariantCulture, out year) ||
                    year < 1000 || Paths.IsReparsePoint(yearFolder)) { continue; }
                result.Items.Add(new ServiceEntry
                {
                    Id = Paths.Identity(root, company, year),
                    Root = root, Company = company, Year = year, Available = true
                });
            }
        }
    }

    internal static class PortAllocator
    {
        public static void ValidateRange(int start, int end)
        {
            if (start < 1024 || end > 65535 || start > end)
            {
                throw new InvalidOperationException(
                    "Port range must be between 1024 and 65535, start <= end.");
            }
        }

        public static int Next(int start, int end, IEnumerable<int> occupied)
        {
            ValidateRange(start, end);
            var used = new HashSet<int>(occupied);
            for (int port = start; port <= end; port++)
            {
                if (!used.Contains(port)) { return port; }
            }
            throw new InvalidOperationException("No free port remains in the selected range.");
        }
    }
}
