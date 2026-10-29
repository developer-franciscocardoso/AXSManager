using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AxsManager
{
    internal static class RemoteAccess
    {
        public static string ValidateAddresses(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Enter permitted addresses or CIDRs.");
            }
            var addresses = value.Split(',').Select(x => x.Trim()).ToArray();
            foreach (var address in addresses)
            {
                var parts = address.Split('/');
                IPAddress ip;
                int prefix;
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out ip) ||
                    (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) ||
                    prefix < 0 || prefix >
                    (ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))))
                {
                    throw new InvalidOperationException(
                        "Use comma-separated IP addresses or CIDRs, not hostnames or wildcards.");
                }
            }
            return string.Join(",", addresses);
        }

        public static string Scope(ManagerSettings settings)
        {
            if (settings.RemoteScope == "LocalSubnet") { return "LocalSubnet"; }
            if (settings.RemoteScope == "Any") { return "*"; }
            if (settings.RemoteScope == "Specific")
            {
                return ValidateAddresses(settings.RemoteAddresses);
            }
            throw new InvalidOperationException("Choose a remote access scope.");
        }
    }
}
