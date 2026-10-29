using System;
using System.Collections.Generic;

namespace AxsManager
{
    internal static class Firewall
    {
        private static string Name(ServiceEntry entry)
        {
            return "AXS Manager XML " + entry.Id;
        }

        public static void Apply(ManagerSettings settings, ServiceEntry entry)
        {
            Access.RequireAdministrator();
            if (!settings.RemoteEnabled) { Remove(entry); return; }
            var scope = RemoteAccess.Scope(settings);
            var cfg = CfgDocument.Read(entry.ConfigPath);
            if (cfg.Get("CONFIG", "IPList") != settings.XmlAllowedClients)
            {
                throw new InvalidOperationException(
                    "Update CFG first so the ARTSoft IPList matches Setup. " +
                    "The firewall cannot override ARTSoft client restrictions.");
            }
            var profiles = new List<string>();
            if ((settings.FirewallProfiles & 1) != 0) { profiles.Add("domain"); }
            if ((settings.FirewallProfiles & 2) != 0) { profiles.Add("private"); }
            if ((settings.FirewallProfiles & 4) != 0) { profiles.Add("public"); }
            if (profiles.Count == 0)
            {
                throw new InvalidOperationException("Choose at least one firewall profile.");
            }
            Remove(entry);
            ProcessRunner.Run(WindowsServices.SystemTool("netsh.exe"), new[]
            {
                "advfirewall", "firewall", "add", "rule", "name=" + Name(entry),
                "dir=in", "action=allow", "protocol=TCP", "localport=" + entry.Port,
                "program=" + entry.ExecutablePath, "remoteip=" + scope,
                "profile=" + string.Join(",", profiles), "enable=yes"
            }, null, false);
        }

        public static void Remove(ServiceEntry entry)
        {
            Access.RequireAdministrator();
            // COM removal is idempotent; netsh reports an error for a missing rule.
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true);
            object policy = Activator.CreateInstance(type);
            object rules = null;
            try
            {
                rules = type.InvokeMember("Rules",
                    System.Reflection.BindingFlags.GetProperty, null, policy, null);
                rules.GetType().InvokeMember("Remove",
                    System.Reflection.BindingFlags.InvokeMethod, null, rules,
                    new object[] { Name(entry) });
            }
            finally
            {
                if (rules != null) { System.Runtime.InteropServices.Marshal.ReleaseComObject(rules); }
                System.Runtime.InteropServices.Marshal.ReleaseComObject(policy);
            }
        }
    }
}
