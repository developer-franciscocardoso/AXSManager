using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace AxsManager
{
    internal sealed class SetupForm : Form
    {
        private readonly Dictionary<string, TextBox> fields =
            new Dictionary<string, TextBox>();
        private readonly ListBox roots = new ListBox();
        private readonly CheckBox remote = new CheckBox();
        private readonly ComboBox scope = new ComboBox();
        private readonly CheckedListBox profiles = new CheckedListBox();
        private readonly CheckBox existingPermissions = new CheckBox();
        private readonly ManagerSettings original;
        public ManagerSettings Result { get; private set; }

        public SetupForm(ManagerSettings settings, bool showXmlUsers = false)
        {
            original = settings;
            Text = "AXS Manager | Setup";
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(850, 760);
            MinimumSize = new Size(760, 650);
            BackColor = Color.White;
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 60,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12)
            };
            var save = new Button { Text = "Save settings", Width = 140, Height = 32 };
            var cancel = new Button { Text = "Cancel", Width = 100, Height = 32,
                DialogResult = DialogResult.Cancel };
            save.Click += delegate { Save(); };
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8) };
            tabs.TabPages.Add(Folders(settings));
            tabs.TabPages.Add(XmlUsers(settings));
            tabs.TabPages.Add(Artsoft(settings));
            tabs.TabPages.Add(Network(settings));
            if (showXmlUsers) { tabs.SelectedIndex = 1; }
            Controls.Add(tabs);
            Controls.Add(footer);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static TabPage Page(string title)
        {
            return new TabPage(title) { BackColor = Color.White, Padding = new Padding(20),
                AutoScroll = true };
        }

        private TabPage Folders(ManagerSettings settings)
        {
            var page = Page("Folders");
            var table = Table();
            AddNote(table, "Use the existing ARTSoft installation. No company data is copied or changed.");
            AddField(table, "ArtsoftFolder", "ARTSoft service folder", settings.ArtsoftFolder, true);
            existingPermissions.Text = "Keep this installation's existing folder permissions";
            existingPermissions.AutoSize = true;
            existingPermissions.Checked = !string.IsNullOrEmpty(settings.TrustedPermissionsFolder) &&
                Paths.Equal(settings.TrustedPermissionsFolder, settings.ArtsoftFolder);
            fields["ArtsoftFolder"].TextChanged += delegate
            {
                existingPermissions.Checked = false;
            };
            AddWide(table, existingPermissions, 32);
            AddNote(table, "Enable only if you trust everyone with write access to this installation. " +
                "They may change the code or configuration run by Windows services. " +
                "This choice applies only to the folder selected above.");
            AddNote(table, "Select every Fich root containing company.art folders with year subfolders. " +
                "Use UNC paths for network data; mapped drives may not be visible to Windows services.");
            roots.Height = 160;
            roots.Dock = DockStyle.Fill;
            roots.HorizontalScrollbar = true;
            foreach (var root in settings.Roots) { roots.Items.Add(root); }
            table.Controls.Add(roots, 0, table.RowCount);
            table.SetColumnSpan(roots, 2);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
            table.RowCount++;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 42 };
            var add = new Button { Text = "Add Fich folder", Width = 140, Height = 32 };
            var remove = new Button { Text = "Remove selected", Width = 150, Height = 32 };
            add.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { Description = "Select a Fich root folder" })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) { return; }
                    if (!roots.Items.Cast<string>().Any(x => Paths.Equal(x, picker.SelectedPath)))
                    {
                        roots.Items.Add(picker.SelectedPath);
                    }
                }
            };
            remove.Click += delegate
            {
                if (roots.SelectedIndex >= 0) { roots.Items.RemoveAt(roots.SelectedIndex); }
            };
            buttons.Controls.Add(add);
            buttons.Controls.Add(remove);
            AddWide(table, buttons, 45);
            AddField(table, "FormsFolder", "Forms folder", settings.FormsFolder, true);
            AddField(table, "TempFolder", "Temp folder", settings.TempFolder, true);
            AddNote(table, "Changing the ARTSoft folder later requires migration of managed configurations " +
                "and service paths. The app will show a confirmation before stopping any service.");
            page.Controls.Add(table);
            return page;
        }

        private TabPage Artsoft(ManagerSettings settings)
        {
            var page = Page("Connection & advanced");
            var table = Table();
            AddNote(table, "Enter the values for this installation. Saving changes does not overwrite " +
                "existing services; select them and choose Update CFG when ready.");
            AddField(table, "ServerAddress", "Server address (host:port)", settings.ServerAddress, false);
            AddField(table, "ServerName", "Server name", settings.ServerName, false);
            AddNote(table, "The database service is detected automatically: " +
                "Pervasive transactional, PSQL Workgroup or Actian Zen.");
            AddField(table, "CommandPage", "Command page (optional)", settings.CommandPage, false);
            AddField(table, "Sender", "Sender email (optional)", settings.Sender, false);
            AddField(table, "XmlServerName", "XML server name", settings.XmlServerName, false);
            AddField(table, "Libraries", "XML DLL list", settings.Libraries, false);
            AddField(table, "PortStart", "Automatic ports: first", settings.PortStart.ToString(), false);
            AddField(table, "PortEnd", "Automatic ports: last", settings.PortEnd.ToString(), false);
            AddNote(table, "Leave both range fields blank to use 50000–59999. Assigned ports remain stable. " +
                "Use Edit port in the manager to override an individual service.");
            page.Controls.Add(table);
            return page;
        }

        private TabPage XmlUsers(ManagerSettings settings)
        {
            var page = Page("XML users");
            var table = Table();
            AddWide(table, new Label
            {
                Text = "XML service credentials", Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(24, 41, 65)
            }, 35);
            AddNote(table, "Register the UsersList used by new XML services here. " +
                "Paste the value from ARTSoft, or import it from a working configuration.");
            AddField(table, "UsersList", "UsersList", settings.UsersList, false);
            fields["UsersList"].UseSystemPasswordChar = true;
            var reveal = new CheckBox { Text = "Show credentials", AutoSize = true };
            reveal.CheckedChanged += delegate
            {
                fields["UsersList"].UseSystemPasswordChar = !reveal.Checked;
            };
            AddWide(table, reveal, 34);
            var import = new Button { Text = "Import from CFG…", AutoSize = true };
            import.Click += delegate
            {
                using (var picker = new OpenFileDialog
                {
                    Title = "Import XML UsersList", Filter = "ARTSoft configuration (*.cfg)|*.cfg",
                    CheckFileExists = true
                })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) { return; }
                    try { fields["UsersList"].Text = ConfigBuilder.ReadUsersList(picker.FileName); }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, error.Message, "Import credentials",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };
            var buttons = new FlowLayoutPanel();
            buttons.Controls.Add(import);
            AddWide(table, buttons, 45);
            AddNote(table, "Use ARTSoft's encoded UsersList, not a plain password. " +
                "You can paste either the value or the full UsersList= line. " +
                "The manager encrypts its saved copy.");
            AddNote(table, "Save settings to use these credentials for new services. " +
                "For existing services, select the rows and choose Update CFG. " +
                "Leaving this field blank preserves their current credentials.");
            page.Controls.Add(table);
            return page;
        }

        private TabPage Network(ManagerSettings settings)
        {
            var page = Page("Remote access");
            var table = Table();
            remote.Text = "Allow remote access through Windows Firewall";
            remote.AutoSize = true;
            remote.Checked = settings.RemoteEnabled;
            AddWide(table, remote, 40);
            AddNote(table, "The manager creates inbound TCP rules only, restricted to each managed " +
                "service's executable and XML port. Unrelated firewall rules remain unchanged.");
            scope.DropDownStyle = ComboBoxStyle.DropDownList;
            scope.Items.AddRange(new object[] { "Local subnet", "Specific IP addresses / CIDRs", "Any remote address" });
            scope.SelectedIndex = settings.RemoteScope == "Specific" ? 1 :
                settings.RemoteScope == "Any" ? 2 : 0;
            AddControl(table, "Allow connections from", scope, 42);
            AddField(table, "RemoteAddresses", "Addresses (comma-separated)", settings.RemoteAddresses, false);
            AddNote(table, "Example: 192.168.1.0/24,10.0.0.8. Choose Any only when you intend " +
                "to allow all remote addresses on the selected network profiles.");
            profiles.Items.Add("Domain", (settings.FirewallProfiles & 1) != 0);
            profiles.Items.Add("Private", (settings.FirewallProfiles & 2) != 0);
            profiles.Items.Add("Public", (settings.FirewallProfiles & 4) != 0);
            profiles.CheckOnClick = true;
            AddControl(table, "Network profiles", profiles, 85);
            AddField(table, "XmlAllowedClients", "ARTSoft IPList", settings.XmlAllowedClients, false);
            AddNote(table, "ARTSoft has its own client allowlist. Enter the IPList syntax supported by " +
                "your ARTSoft version; firewall CIDRs are not automatically translated. " +
                "Other existing restrictions (such as HostsList or GrantList) are preserved. " +
                "Update CFG before applying remote access to existing services.");
            remote.CheckedChanged += delegate { ToggleRemote(); };
            scope.SelectedIndexChanged += delegate { ToggleRemote(); };
            page.Controls.Add(table);
            ToggleRemote();
            return page;
        }

        private void ToggleRemote()
        {
            scope.Enabled = remote.Checked;
            profiles.Enabled = remote.Checked;
            fields["RemoteAddresses"].Enabled = remote.Checked && scope.SelectedIndex == 1;
            fields["XmlAllowedClients"].Enabled = remote.Checked;
        }

        private static TableLayoutPanel Table()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 2, RowCount = 0, Padding = new Padding(0, 4, 0, 12) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }

        private void AddField(TableLayoutPanel table, string key, string label,
            string value, bool folder)
        {
            var text = new TextBox { Name = key, Text = value, Dock = DockStyle.Fill,
                Margin = new Padding(3, 7, 3, 3) };
            fields.Add(key, text);
            if (!folder) { AddControl(table, label, text, 44); return; }
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var browse = new Button { Text = "…", Dock = DockStyle.Fill,
                Margin = new Padding(3, 5, 0, 5) };
            browse.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { SelectedPath = text.Text,
                    Description = label })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK) { text.Text = picker.SelectedPath; }
                }
            };
            panel.Controls.Add(text, 0, 0);
            panel.Controls.Add(browse, 1, 0);
            AddControl(table, label, panel, 46);
        }

        private static void AddControl(TableLayoutPanel table, string label,
            Control control, int height)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            control.Dock = DockStyle.Fill;
            table.Controls.Add(control, 1, row);
        }

        private static void AddWide(TableLayoutPanel table, Control control, int height)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
            control.Dock = DockStyle.Fill;
        }

        private static void AddNote(TableLayoutPanel table, string text)
        {
            AddWide(table, new Label { Text = text, ForeColor = Color.FromArgb(85, 100, 120),
                Padding = new Padding(0, 7, 0, 7), AutoEllipsis = false },
                text.Length > 270 ? 105 : text.Length > 150 ? 75 : 55);
        }

        private string Value(string key) { return fields[key].Text.Trim(); }

        private void Save()
        {
            try
            {
                ManagerSettings settings;
                using (var buffer = new MemoryStream())
                {
                    var serializer = new XmlSerializer(typeof(ManagerSettings));
                    serializer.Serialize(buffer, original);
                    buffer.Position = 0;
                    settings = (ManagerSettings)serializer.Deserialize(buffer);
                }
                settings.ArtsoftFolder = Paths.Canonical(Value("ArtsoftFolder"));
                settings.TrustedPermissionsFolder = existingPermissions.Checked ?
                    settings.ArtsoftFolder : "";
                settings.Roots = roots.Items.Cast<string>().Select(Paths.Canonical).ToList();
                settings.FormsFolder = Value("FormsFolder");
                settings.TempFolder = Value("TempFolder");
                settings.ServerAddress = Value("ServerAddress");
                settings.ServerName = Value("ServerName");
                settings.CommandPage = Value("CommandPage");
                settings.Sender = Value("Sender");
                settings.XmlServerName = Value("XmlServerName");
                settings.Libraries = Value("Libraries");
                settings.UsersList = ConfigBuilder.NormalizeUsersList(Value("UsersList"));
                int start;
                int end;
                if (Value("PortStart") == "" && Value("PortEnd") == "")
                {
                    start = 50000;
                    end = 59999;
                }
                else if (!int.TryParse(Value("PortStart"), out start) ||
                    !int.TryParse(Value("PortEnd"), out end))
                {
                    throw new InvalidOperationException("Enter both port limits, or leave both blank.");
                }
                settings.PortStart = start;
                settings.PortEnd = end;
                settings.RemoteEnabled = remote.Checked;
                settings.RemoteScope = scope.SelectedIndex == 1 ? "Specific" :
                    scope.SelectedIndex == 2 ? "Any" : "LocalSubnet";
                settings.RemoteAddresses = Value("RemoteAddresses");
                settings.FirewallProfiles = 0;
                for (int index = 0; index < 3; index++)
                {
                    if (profiles.GetItemChecked(index)) { settings.FirewallProfiles |= 1 << index; }
                }
                settings.XmlAllowedClients = Value("XmlAllowedClients");
                settings.Validate();
                Result = settings;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Check setup values",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
