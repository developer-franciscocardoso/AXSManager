using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AxsManager
{
    internal sealed class MainForm : Form
    {
        private readonly StateStore store;
        private ServiceManager manager;
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox search = new TextBox();
        private readonly ComboBox filter = new ComboBox();
        private readonly Label summary = new Label();
        private readonly Label status = new Label();
        private readonly TextBox details = new TextBox();
        private readonly RichTextBox activity = new RichTextBox();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
        private readonly ContextMenuStrip serviceMenu = new ContextMenuStrip();
        private readonly Dictionary<string, ToolStripMenuItem> serviceActions =
            new Dictionary<string, ToolStripMenuItem>();
        private readonly Button setup = new Button();
        private readonly Button xmlUsers = new Button();
        private Button recoveryButton;
        private readonly Button elevate = new Button();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer keepRunningTimer = new Timer();
        private bool busy;
        private bool configuring;
        private bool refreshing;
        private int operationVersion;
        private readonly Dictionary<string, string> operationErrors =
            new Dictionary<string, string>();
        private bool exiting;
        private readonly bool startInTray;
        private readonly bool openSetup;
        private readonly bool openXmlUsers;
        private readonly Color ink = Color.FromArgb(24, 41, 65);
        private readonly Color blue = Color.FromArgb(35, 99, 218);

        public MainForm(StateStore stateStore, ManagerSettings settings,
            bool hidden, bool setupOnStart = false, bool xmlUsersOnStart = false,
            string startRequest = null)
        {
            store = stateStore;
            manager = new ServiceManager(settings, store, new WindowsServices());
            startInTray = hidden;
            openSetup = setupOnStart;
            openXmlUsers = xmlUsersOnStart;
            Text = "AXS Manager — ARTSoft services";
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(1260, 850);
            MinimumSize = new Size(1050, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 246, 250);
            Icon = SystemIcons.Application;
            BuildLayout();
            BuildTray();
            Shown += async delegate
            {
                if (startInTray && manager.Settings.Roots.Count > 0) { Hide(); }
                await RefreshInventory();
                if (IsDisposed || Disposing) { return; }
                if (openXmlUsers) { await Configure(true); }
                else if (openSetup) { await Configure(); }
                else if (!string.IsNullOrEmpty(startRequest))
                {
                    var ids = new HashSet<string>(startRequest.Substring(8).Split(','));
                    grid.ClearSelection();
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        row.Selected = ids.Contains(((ServiceEntry)row.Tag).Id);
                    }
                    if (Selected().Count > 0) { await RunAction("Start", null); }
                }
                if (manager.Settings.Roots.Count == 0 && !startInTray)
                {
                    Append("Welcome. Open Setup to choose your ARTSoft installation and Fich folders.");
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing && !exiting)
                {
                    args.Cancel = true;
                    Hide();
                }
            };
            refreshTimer.Interval = 10000;
            refreshTimer.Tick += async delegate
            {
                if (!busy && !configuring && !serviceMenu.Visible && Visible && manager.Settings.Roots.Count > 0)
                {
                    await RefreshInventory(true);
                }
            };
            refreshTimer.Start();
            keepRunningTimer.Interval = 60000;
            keepRunningTimer.Tick += async delegate { await KeepServicesRunning(); };
            keepRunningTimer.Start();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 6, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(Header(), 0, 0);
            actions.Dock = DockStyle.Fill;
            actions.Padding = new Padding(14, 10, 10, 2);
            actions.Margin = Padding.Empty;
            actions.BackColor = Color.White;
            AddAction("Refresh", async delegate { await RefreshInventory(); });
            var start = AddAction("Start selected", async delegate { await RunAction("Start", null); });
            start.BackColor = blue;
            start.ForeColor = Color.White;
            AddAction("Check ports", async delegate { await CheckPorts(); });
            AddAction("Actions…", delegate(object sender, EventArgs args)
            {
                var button = (Control)sender;
                serviceMenu.Show(button, new Point(0, button.Height));
            });
            recoveryButton = AddAction("Recover migration", async delegate { await RecoverMigration(); });
            recoveryButton.Visible = store.HasPendingMigration;
            actions.Controls.Add(new Label
            {
                Text = "Right-click a row for actions. Ctrl / Shift selects multiple.",
                AutoSize = true, ForeColor = Color.FromArgb(90, 105, 125),
                Margin = new Padding(12, 9, 0, 0)
            });
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(SearchBar(), 0, 2);
            ConfigureGrid();
            layout.Controls.Add(grid, 0, 3);
            var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(16, 8, 16, 0) };
            var detailTab = new TabPage("Selection details");
            details.Multiline = true;
            details.ReadOnly = true;
            details.Dock = DockStyle.Fill;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = Color.White;
            details.ScrollBars = ScrollBars.Vertical;
            detailTab.Padding = new Padding(12);
            detailTab.Controls.Add(details);
            var activityTab = new TabPage("Activity");
            activity.Dock = DockStyle.Fill;
            activity.ReadOnly = true;
            activity.BackColor = Color.White;
            activity.BorderStyle = BorderStyle.None;
            activityTab.Controls.Add(activity);
            tabs.TabPages.Add(detailTab);
            tabs.TabPages.Add(activityTab);
            layout.Controls.Add(tabs, 0, 4);
            status.Dock = DockStyle.Fill;
            status.Padding = new Padding(18, 6, 0, 0);
            status.ForeColor = Color.FromArgb(90, 105, 125);
            status.Text = "Ready";
            layout.Controls.Add(status, 0, 5);
            Controls.Add(layout);
        }

        private Control Header()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = ink, Margin = Padding.Empty };
            panel.Controls.Add(new Label { Text = "AXS", ForeColor = Color.White,
                Font = new Font("Segoe UI", 27, FontStyle.Bold), Location = new Point(18, 13),
                AutoSize = true });
            panel.Controls.Add(new Label { Text = "ARTSOFT SERVICE MANAGER",
                ForeColor = Color.White, Font = new Font("Segoe UI", 13, FontStyle.Bold),
                Location = new Point(115, 20), AutoSize = true });
            panel.Controls.Add(new Label { Text = "Companies, years and XML services — in one place",
                ForeColor = Color.FromArgb(175, 194, 219), Location = new Point(117, 49),
                AutoSize = true });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 440,
                Padding = new Padding(0, 25, 10, 0), FlowDirection = FlowDirection.RightToLeft };
            setup.Text = "Settings";
            setup.Size = new Size(95, 36);
            setup.BackColor = Color.White;
            setup.FlatStyle = FlatStyle.Flat;
            setup.Click += async delegate { await Configure(); };
            xmlUsers.Text = "XML users";
            xmlUsers.Size = new Size(95, 36);
            xmlUsers.BackColor = Color.White;
            xmlUsers.FlatStyle = FlatStyle.Flat;
            xmlUsers.Click += async delegate { await Configure(true); };
            elevate.Text = Access.IsAdministrator ? "Administrator" : "Run as administrator";
            elevate.Size = new Size(195, 36);
            elevate.FlatStyle = FlatStyle.Flat;
            elevate.BackColor = blue;
            elevate.ForeColor = Color.White;
            elevate.Enabled = !Access.IsAdministrator;
            elevate.Click += delegate { EnsureAdministrator(); };
            buttons.Controls.Add(setup);
            buttons.Controls.Add(xmlUsers);
            buttons.Controls.Add(elevate);
            panel.Controls.Add(buttons);
            return panel;
        }

        private Button AddAction(string text, EventHandler handler)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 29,
                MinimumSize = new Size(72, 29), FlatStyle = FlatStyle.Flat,
                BackColor = Color.White, ForeColor = ink, Margin = new Padding(3) };
            button.FlatAppearance.BorderColor = Color.FromArgb(211, 220, 230);
            button.Click += handler;
            actions.Controls.Add(button);
            return button;
        }

        private Control SearchBar()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            search.Location = new Point(18, 10);
            search.Width = 320;
            search.TextChanged += delegate { Render(); };
            filter.Location = new Point(350, 10);
            filter.Width = 170;
            filter.DropDownStyle = ComboBoxStyle.DropDownList;
            filter.Items.AddRange(new object[] { "All services", "Running", "Not running", "Needs attention" });
            filter.SelectedIndex = 0;
            filter.SelectedIndexChanged += delegate { Render(); };
            var label = new Label { Text = "Search company, year, path or service", AutoSize = true,
                ForeColor = Color.FromArgb(100, 115, 135), Location = new Point(535, 13) };
            summary.Location = new Point(18, 46);
            summary.AutoSize = true;
            summary.ForeColor = ink;
            panel.Controls.Add(search);
            panel.Controls.Add(filter);
            panel.Controls.Add(label);
            panel.Controls.Add(summary);
            return panel;
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(16, 0, 16, 0);
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeight = 36;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(228, 235, 245);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ink;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 232, 253);
            grid.DefaultCellStyle.SelectionForeColor = ink;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.RowTemplate.Height = 32;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(235, 240, 246);
            AddColumn("Company", 100);
            AddColumn("Year", 55);
            AddColumn("Port", 65);
            AddColumn("Status", 150);
            AddColumn("TCP", 100);
            AddColumn("Keep running", 95);
            AddColumn("Fich folder", 235);
            AddColumn("Windows service", 180);
            grid.Columns.Add(new DataGridViewButtonColumn {
                Name = "ServiceAction", HeaderText = "Action", FillWeight = 70 });
            grid.CellContentClick += async delegate(object sender,
                DataGridViewCellEventArgs args)
            {
                if (busy || configuring || args.RowIndex < 0 ||
                    args.ColumnIndex != grid.Columns["ServiceAction"].Index) { return; }
                var row = grid.Rows[args.RowIndex];
                grid.ClearSelection();
                row.Selected = true;
                var entry = (ServiceEntry)row.Tag;
                await RunAction(entry.Status == "Running" ? "Stop" : "Start", null);
            };
            grid.SelectionChanged += delegate { SelectionDetails(); };
            ConfigureServiceMenu();
            grid.ContextMenuStrip = serviceMenu;
            grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs args)
            {
                if (args.Button != MouseButtons.Right) { return; }
                if (args.RowIndex < 0) { grid.ClearSelection(); return; }
                var row = grid.Rows[args.RowIndex];
                if (row.Selected) { return; }
                grid.ClearSelection();
                grid.CurrentCell = row.Cells[Math.Max(0, args.ColumnIndex)];
                row.Selected = true;
            };
            grid.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Right &&
                    grid.HitTest(args.X, args.Y).RowIndex < 0) { grid.ClearSelection(); }
            };
        }

        private void ConfigureServiceMenu()
        {
            foreach (var action in new[] { "Start", "Stop", "Restart" })
            {
                AddServiceAction(action);
            }
            AddServiceItem("Keep running", async delegate
            {
                await RunAction(Selected().All(x => x.KeepRunning) ?
                    "Disable keep running" : "Enable keep running", null);
            });
            serviceActions["Keep running"].ToolTipText =
                "Check every minute while this app runs as administrator, including in the tray.";
            serviceMenu.Items.Add(new ToolStripSeparator());
            AddServiceAction("Update CFG");
            AddServiceItem("View CFG", delegate { ViewConfig(); });
            AddServiceItem("Open log", delegate { OpenLog(); });
            AddServiceItem("Check ports", async delegate { await CheckPorts(); });
            var advanced = new ToolStripMenuItem("Advanced");
            serviceMenu.Items.Add(advanced);
            AddServiceAction("Create CFG", advanced.DropDownItems);
            AddServiceAction("Install", advanced.DropDownItems);
            AddServiceItem("Edit port", async delegate { await EditPort(); }, advanced.DropDownItems);
            AddServiceAction("Firewall", advanced.DropDownItems);
            serviceMenu.Items.Add(new ToolStripSeparator());
            AddServiceAction("Uninstall");
            serviceMenu.Opening += delegate(object sender, CancelEventArgs args)
            {
                var entries = Selected();
                args.Cancel = busy || entries.Count == 0;
                if (!args.Cancel) { UpdateServiceMenu(entries); }
            };
        }

        private void AddServiceAction(string action, ToolStripItemCollection destination = null)
        {
            AddServiceItem(action, async delegate { await RunAction(action, null); }, destination);
        }

        private void AddServiceItem(string name, EventHandler handler,
            ToolStripItemCollection destination = null)
        {
            var item = new ToolStripMenuItem(name);
            item.Click += handler;
            serviceActions.Add(name, item);
            (destination ?? serviceMenu.Items).Add(item);
        }

        private void UpdateServiceMenu(List<ServiceEntry> entries)
        {
            bool configured = entries.All(x => !string.IsNullOrEmpty(x.ConfigPath));
            bool installed = entries.All(x => !string.IsNullOrEmpty(x.ServiceName));
            bool available = entries.All(x => x.Available);
            bool clear = entries.All(x => string.IsNullOrEmpty(x.Problem));
            serviceActions["Keep running"].Checked = entries.All(x => x.KeepRunning);
            serviceActions["Keep running"].Enabled =
                entries.All(x => x.KeepRunning) || (clear && available);
            serviceActions["Create CFG"].Enabled = clear && available &&
                entries.All(x => string.IsNullOrEmpty(x.ConfigPath));
            serviceActions["Install"].Enabled = clear && configured && available &&
                entries.All(x => string.IsNullOrEmpty(x.ServiceName));
            serviceActions["Start"].Enabled = clear && available &&
                entries.Any(x => x.Status != "Running");
            serviceActions["Stop"].Enabled = clear && installed &&
                entries.Any(x => x.Status != "Stopped");
            serviceActions["Restart"].Enabled = clear && installed && available;
            serviceActions["Update CFG"].Enabled = clear && configured && available;
            serviceActions["Edit port"].Enabled = entries.Count == 1 && configured &&
                available && (clear || entries[0].Problem.StartsWith("Port "));
            serviceActions["Firewall"].Enabled = clear && configured;
            serviceActions["Uninstall"].Enabled = clear && installed;
            serviceActions["View CFG"].Enabled = entries.Count == 1 && configured;
            serviceActions["Open log"].Enabled = entries.Count == 1 && configured;
            serviceActions["Check ports"].Enabled = entries.Any(x => x.Port > 0);
        }

        private void AddColumn(string title, int weight)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title,
                FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic });
        }

        private void BuildTray()
        {
            tray.Icon = Icon;
            tray.Text = "AXS Manager — ARTSoft services";
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left) { ShowManager(); }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open manager", null, delegate { ShowManager(); });
            menu.Items.Add("Refresh", null, async delegate { if (!busy) { await RefreshInventory(); } });
            var startup = new ToolStripMenuItem("Start at sign-in") { CheckOnClick = false };
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                startup.Checked = key != null && key.GetValue("AxsManager") != null;
            }
            startup.Click += delegate
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        if (startup.Checked) { key.DeleteValue("AxsManager", false); }
                        else { key.SetValue("AxsManager", CommandLine.Quote(Application.ExecutablePath) + " --tray"); }
                    }
                    startup.Checked = !startup.Checked;
                }
                catch (Exception error) { ShowError(error); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit manager", null, delegate
            {
                if (busy)
                {
                    MessageBox.Show(this, "Wait for the current operation to finish.", "AXS Manager");
                    return;
                }
                exiting = true;
                Close();
            });
            tray.ContextMenuStrip = menu;
        }

        public void ShowManager()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private bool EnsureAdministrator(string resumeArgument = "")
        {
            if (Access.IsAdministrator) { return true; }
            if (busy) { return false; }
            try
            {
                Program.Elevate(resumeArgument);
                exiting = true;
                Close();
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 1223) { ShowError(error); }
            }
            return false;
        }

        private void SetBusy(bool value, string message)
        {
            if (IsDisposed || Disposing) { return; }
            busy = value;
            if (value) { operationVersion++; }
            if (value) { serviceMenu.Close(); }
            actions.Enabled = !value;
            setup.Enabled = !value;
            xmlUsers.Enabled = !value;
            elevate.Enabled = !value && !Access.IsAdministrator;
            status.Text = message;
            UseWaitCursor = value;
        }

        private async Task RefreshInventory(bool background = false)
        {
            if (busy || refreshing) { return; }
            refreshing = true;
            if (!background) { SetBusy(true, "Discovering company folders and Windows services…"); }
            int version = operationVersion;
            try
            {
                var result = await Task.Run(() =>
                {
                    var updated = new ServiceManager(store.Load(), store, new WindowsServices());
                    updated.Refresh();
                    return updated;
                });
                if (IsDisposed || Disposing) { return; }
                if (background && (version != operationVersion || busy || configuring)) { return; }
                manager = result;
                Render();
                foreach (var warning in manager.Current.Warnings) { Append("Discovery: " + warning); }
            }
            catch (Exception error)
            {
                if (background) { Append("Refresh: " + error.Message); }
                else { ShowError(error); }
            }
            finally
            {
                refreshing = false;
                if (!background) { SetBusy(false, "Last refresh: " + DateTime.Now.ToString("HH:mm:ss") +
                    (Access.IsAdministrator ? "  •  Administrator" : "  •  Read-only — elevate to make changes")); }
            }
        }

        private async Task KeepServicesRunning()
        {
            if (busy || configuring || serviceMenu.Visible ||
                !Access.IsAdministrator || IsDisposed || Disposing) { return; }
            SetBusy(true, "Checking services with Keep running enabled...");
            IProgress<string> progress = new Progress<string>(Append);
            try
            {
                var updated = await Task.Run(() =>
                {
                    using (store.AcquireWriteLock())
                    {
                        var current = new ServiceManager(store.Load(), store,
                            new WindowsServices());
                        if (!current.Settings.Entries.Any(x => x.KeepRunning))
                        {
                            return null;
                        }
                        current.Refresh();
                        current.RestartStoppedServices(progress.Report);
                        current.Refresh();
                        return current;
                    }
                });
                if (updated != null && !IsDisposed && !Disposing)
                {
                    manager = updated;
                    Render();
                }
            }
            catch (Exception error)
            {
                Append("Keep running: " + error.Message);
            }
            finally { SetBusy(false, "Keep running check finished"); }
        }

        private void Render()
        {
            var selected = new HashSet<string>(Selected().Select(x => x.Id));
            var text = search.Text.Trim();
            grid.Rows.Clear();
            foreach (var entry in manager.Current.Items)
            {
                if (filter.SelectedIndex == 1 && entry.Status != "Running") { continue; }
                if (filter.SelectedIndex == 2 && entry.Status == "Running") { continue; }
                if (filter.SelectedIndex == 3 && entry.Problem == "" && entry.Available) { continue; }
                var searchable = entry.Company + " " + entry.Year + " " + entry.Root + " " + entry.ServiceName;
                if (searchable.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                int index = grid.Rows.Add(entry.Company, entry.Year,
                    entry.Port == 0 ? "Auto" : entry.Port.ToString(),
                    entry.Problem == "" ? entry.Status : "Needs attention", entry.Connectivity,
                    entry.KeepRunning ? (Access.IsAdministrator ? "On" :
                        "Needs admin") : "Off", entry.Root, entry.ServiceName,
                    entry.Status == "Running" ? "Stop" : "Start");
                var row = grid.Rows[index];
                row.Tag = entry;
                string operationError;
                if (operationErrors.TryGetValue(entry.Id, out operationError))
                {
                    row.ErrorText = operationError;
                    row.Cells[3].ToolTipText = operationError;
                }
                if (entry.Problem != "") { row.DefaultCellStyle.ForeColor = Color.Firebrick; }
                else if (entry.Status == "Running")
                {
                    row.Cells[3].Style.ForeColor = Color.FromArgb(20, 125, 80);
                }
            }
            grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows)
            {
                row.Selected = selected.Contains(((ServiceEntry)row.Tag).Id);
            }
            summary.Text = manager.Current.Items.Count + " company/year pairs   •   " +
                manager.Current.Items.Count(x => x.Status == "Running") + " running   •   " +
                manager.Current.Items.Count(x => string.IsNullOrEmpty(x.ConfigPath)) + " ready to set up   •   " +
                manager.Current.Warnings.Count + " discovery warnings";
            recoveryButton.Visible = store.HasPendingMigration;
            SelectionDetails();
        }

        private List<ServiceEntry> Selected()
        {
            return grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(x => x.Tag as ServiceEntry).Where(x => x != null)
                .OrderBy(x => x.Company).ThenBy(x => x.Year).ToList();
        }

        private void SelectionDetails()
        {
            var entries = Selected();
            if (entries.Count == 0)
            {
                details.Text = "Right-click one or more company/year rows to manage services.\r\n" +
                    "Start automatically creates and installs missing services. Register credentials with the XML users button.\r\n" +
                    "Closing this window keeps the manager in the tray. Services continue after the manager exits.";
                return;
            }
            var entry = entries[0];
            details.Text = entries.Count > 1 ? entries.Count + " entries selected for bulk actions." :
                entry.Company + " / " + entry.Year + "    " + entry.Status + "\r\n" +
                "Configuration: " + (entry.ConfigPath == "" ? "Not created" : entry.ConfigPath) + "\r\n" +
                "Executable: " + entry.ExecutablePath + "\r\n" +
                "Service: " + entry.ServiceName + "    TCP: " + entry.Connectivity + "\r\n" +
                "Keep running: " + (entry.KeepRunning ? "On" : "Off") +
                " (every minute while the manager runs as administrator).\r\n" + entry.Problem;
            string lastError;
            if (entry.Id != null && operationErrors.TryGetValue(entry.Id, out lastError))
            {
                details.Text += "\r\nLast action failed: " + lastError;
            }
        }

        private async Task RunAction(string action, int? port)
        {
            if (busy) { return; }
            configuring = true;
            try { await RunSelectedAction(action, port); }
            finally { configuring = false; }
        }

        private async Task RunSelectedAction(string action, int? port)
        {
            var selected = Selected();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one company/year row.", "AXS Manager");
                return;
            }
            if (!EnsureAdministrator(action == "Start" ? "--start=" +
                string.Join(",", selected.Select(x => x.Id)) : "")) { return; }
            var permissionAction = action == "Enable keep running" ? "Start" :
                action == "Disable keep running" ? "Stop" : action;
            if (!await EnsurePermissionConsent(permissionAction, selected)) { return; }
            var explanation = action == "Uninstall" ?
                "Stop and unregister the selected services. CFG files and company data will be retained." :
                action == "Enable keep running" ?
                "Start the selected services and check every minute while the manager runs as administrator, even in the tray. Stopped services will be restarted. Choosing Stop disables Keep running." :
                action == "Disable keep running" ?
                "Disable automatic restarts. The services will keep their current state." :
                action == "Update CFG" ?
                "Apply the saved setup values. Running services restart only when their CFG changes. Backups are retained." :
                action == "Start" ?
                "Create configurations and install services where needed, then start them. Existing configurations are preserved." :
                action == "Firewall" ?
                "Reconcile AXS Manager's inbound TCP rules with the saved remote-access settings." :
                "Perform " + action + " for the selected company/year entries.";
            if (port.HasValue) { explanation += " New port: " + port.Value + "."; }
            var list = string.Join("\r\n", selected.Take(12).Select(x =>
                x.Company + " / " + x.Year + "  [" + x.Root + "]"));
            if (selected.Count > 12) { list += "\r\n… and " + (selected.Count - 12) + " more"; }
            if ((action == "Uninstall" || action == "Update CFG") &&
                MessageBox.Show(this, explanation + "\r\n\r\n" + list, action,
                MessageBoxButtons.OKCancel, action == "Uninstall" ?
                MessageBoxIcon.Warning : MessageBoxIcon.Information) != DialogResult.OK) { return; }
            SetBusy(true, action + " in progress…");
            int succeeded = 0;
            int failed = 0;
            IProgress<string> progress = new Progress<string>(Append);
            IProgress<Tuple<string, string, bool>> rowProgress =
                new Progress<Tuple<string, string, bool>>(update =>
                {
                    if (IsDisposed || Disposing) { return; }
                    if (update.Item3) { operationErrors[update.Item1] = update.Item2; }
                    else { operationErrors.Remove(update.Item1); }
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        var item = row.Tag as ServiceEntry;
                        if (item == null || item.Id != update.Item1) { continue; }
                        row.Cells[3].Value = update.Item3 ? "Failed" : update.Item2;
                        row.ErrorText = update.Item3 ? update.Item2 : "";
                        row.Cells[3].ToolTipText = update.Item2;
                    }
                    SelectionDetails();
                });
            try
            {
                await Task.Run(() =>
                {
                    using (store.AcquireWriteLock())
                    {
                        manager = new ServiceManager(store.Load(), store, new WindowsServices());
                        manager.Refresh();
                        foreach (var old in selected)
                        {
                            var identity = old.Company + " / " + old.Year;
                            try
                            {
                                var entry = manager.Current.Items.SingleOrDefault(x => x.Id == old.Id);
                                if (entry == null) { throw new InvalidOperationException("Entry no longer exists; refresh."); }
                                rowProgress.Report(Tuple.Create(entry.Id, action + "...", false));
                                if (action == "Start")
                                {
                                    manager.EnsureStarted(entry, stage => rowProgress.Report(
                                        Tuple.Create(entry.Id, stage, false)));
                                }
                                else { Execute(action, entry, port); }
                                succeeded++;
                                progress.Report(action + " — " + identity + ": completed");
                                store.Log(action, entry.Id, "Completed");
                            }
                            catch (Exception error)
                            {
                                failed++;
                                rowProgress.Report(Tuple.Create(old.Id, error.Message, true));
                                progress.Report(action + " — " + identity + ": " + error.Message);
                                store.Log(action, old.Id, error.Message);
                            }
                        }
                        manager.Refresh();
                    }
                });
                Render();
            }
            catch (Exception error) { ShowError(error); }
            finally { SetBusy(false, action + ": " + succeeded + " completed, " + failed + " failed"); }
        }

        private void Execute(string action, ServiceEntry entry, int? port)
        {
            switch (action)
            {
                case "Create CFG": manager.Create(entry); break;
                case "Start": manager.EnsureStarted(entry); break;
                case "Enable keep running": manager.SetKeepRunning(entry, true); break;
                case "Disable keep running": manager.SetKeepRunning(entry, false); break;
                case "Install": manager.Install(entry); break;
                case "Update CFG": manager.Update(entry, port); break;
                case "Firewall": manager.ApplyFirewall(entry); break;
                default: manager.Control(entry, action); break;
            }
        }

        private async Task<bool> EnsurePermissionConsent(string action,
            List<ServiceEntry> entries)
        {
            SetBusy(true, "Checking the selected installation…");
            try
            {
                InstallationPermissionsException permissionError;
                try
                {
                    await Task.Run(() => manager.CheckPermissions(action, entries));
                    return true;
                }
                catch (InstallationPermissionsException error)
                {
                    permissionError = error;
                }
                if (!Paths.Within(permissionError.TargetFile, manager.Settings.ArtsoftFolder))
                {
                    throw permissionError;
                }
                var folder = manager.Settings.ArtsoftFolder;
                var message = "This ARTSoft installation permits other Windows users to change " +
                    "its files:\r\n\r\n" + folder + "\r\n\r\n" +
                    "Those users may change code or configuration run by the services. " +
                    "Continue only if you trust everyone with write access.\r\n\r\n" +
                    "Keep the existing permissions and remember this choice for this installation? " +
                    "You can turn it off in Setup.";
                if (MessageBox.Show(this, message, "Existing ARTSoft permissions",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) { return false; }
                var expectedRevision = manager.Settings.Revision;
                var settings = await Task.Run(() =>
                {
                    using (store.AcquireWriteLock())
                    {
                        var current = store.Load();
                        if (!Paths.Equal(current.ArtsoftFolder, folder) ||
                            current.Revision != expectedRevision)
                        {
                            throw new InvalidOperationException(
                                "Setup changed in another session. Refresh and try again.");
                        }
                        current.TrustedPermissionsFolder = Paths.Canonical(folder);
                        store.Save(current);
                        return current;
                    }
                });
                manager = new ServiceManager(settings, store, new WindowsServices());
                manager.CheckPermissions(action, entries);
                Append("Existing permissions accepted for " + folder + ".");
                return true;
            }
            catch (Exception error) { ShowError(error); return false; }
            finally { SetBusy(false, "Ready"); }
        }

        private async Task EditPort()
        {
            var selected = Selected();
            if (selected.Count != 1)
            {
                MessageBox.Show(this, "Select one configured service to edit its port.", "Edit port");
                return;
            }
            using (var dialog = new Form { Text = "Edit XML port", Size = new Size(380, 170),
                Font = Font, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var number = new NumericUpDown { Minimum = 1024, Maximum = 65535,
                    Value = Math.Max(1024, selected[0].Port), Location = new Point(20, 20), Width = 320 };
                var save = new Button { Text = "Review change", DialogResult = DialogResult.OK,
                    Location = new Point(200, 65), Width = 140 };
                dialog.Controls.Add(number);
                dialog.Controls.Add(save);
                dialog.AcceptButton = save;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    await RunAction("Update CFG", (int)number.Value);
                }
            }
        }

        private async Task CheckPorts()
        {
            var entries = Selected();
            if (entries.Count == 0) { entries = manager.Current.Items.Where(x => x.Port > 0).ToList(); }
            SetBusy(true, "Checking localhost TCP connectivity…");
            try
            {
                await Task.Run(() => Parallel.ForEach(entries,
                    new ParallelOptions { MaxDegreeOfParallelism = 6 }, entry =>
                    {
                        entry.Connectivity = entry.Port > 0 && WindowsServices.IsReachable(entry.Port) ?
                            "TCP reachable" : "Not reachable";
                    }));
                Render();
                Append("TCP checks complete. An open port does not verify ARTSoft authentication or XML requests.");
            }
            catch (Exception error) { ShowError(error); }
            finally { SetBusy(false, "TCP check complete"); }
        }

        private async Task Configure(bool showXmlUsers = false)
        {
            if (!EnsureAdministrator(showXmlUsers ? "--xml-users" : "--setup")) { return; }
            configuring = true;
            var expectedRevision = manager.Settings.Revision;
            try
            {
              using (var dialog = new SetupForm(manager.Settings, showXmlUsers))
              {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                var next = dialog.Result;
                if (!Paths.Equal(next.ArtsoftFolder, manager.Settings.ArtsoftFolder) &&
                    manager.TrackedEntries().Count > 0 && MessageBox.Show(this,
                    "Migrate all discovered and tracked configurations to:\r\n" +
                    next.ArtsoftFolder + "\r\n\r\nRunning services will stop and restart. " +
                    "Service accounts, dependencies and startup settings are preserved. " +
                    "The new folder must already contain compatible ARTSoft binaries and DLLs. " +
                    "Original CFG files will be retained.", "Move service installation",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { return; }
                SetBusy(true, "Saving setup and reconciling service paths…");
                try
                {
                    await Task.Run(() =>
                    {
                        using (store.AcquireWriteLock())
                        {
                            if (store.Load().Revision != expectedRevision)
                            {
                                throw new InvalidOperationException("Settings changed in another session. Refresh and reopen Setup.");
                            }
                            manager.SaveSettings(next);
                            manager.Refresh();
                        }
                    });
                    Render();
                    Append("Setup saved. Use Update CFG to apply shared settings to existing services. " +
                        "Install/update reconciles inbound TCP rules; Firewall can also reconcile them explicitly.");
                }
                catch (Exception error) { ShowError(error); }
                finally { SetBusy(false, "Setup finished"); }
              }
            }
            finally { configuring = false; }
        }

        private async Task RecoverMigration()
        {
            if (!EnsureAdministrator()) { return; }
            if (!store.HasPendingMigration)
            {
                MessageBox.Show(this, "No pending migration recovery record.", "AXS Manager");
                return;
            }
            if (MessageBox.Show(this, "Recover the interrupted migration? " +
                "Uncommitted service moves will be restored to their original paths. " +
                "A fully committed migration will be verified and its recovery record cleared.",
                "Recover migration", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK) { return; }
            SetBusy(true, "Recovering migration…");
            try
            {
                await Task.Run(() =>
                {
                    using (store.AcquireWriteLock())
                    {
                        manager = new ServiceManager(store.Load(), store, new WindowsServices());
                        manager.RecoverMigration();
                        manager.Refresh();
                    }
                });
                Render();
                Append("Migration recovery completed.");
            }
            catch (Exception error) { ShowError(error); }
            finally { SetBusy(false, "Recovery operation finished"); }
        }

        private void ViewConfig()
        {
            var entry = Selected().FirstOrDefault();
            if (entry == null || !File.Exists(entry.ConfigPath)) { return; }
            try
            {
                var text = CfgDocument.Read(entry.ConfigPath).ToString();
                text = Regex.Replace(text, @"(?im)^(UsersList|LoginPswC?|LoginUsrC?)\s*=.*$", "$1=[hidden]");
                using (var dialog = new Form { Text = entry.ConfigPath, Size = new Size(800, 650),
                    StartPosition = FormStartPosition.CenterParent })
                {
                    dialog.Controls.Add(new TextBox { Text = text, Multiline = true,
                        ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
                        WordWrap = false, Font = new Font("Consolas", 10) });
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception error) { ShowError(error); }
        }

        private void OpenLog()
        {
            var entry = Selected().FirstOrDefault();
            if (entry == null || !File.Exists(entry.ConfigPath)) { return; }
            try
            {
                var log = CfgDocument.Read(entry.ConfigPath).Get("XMLSERVER", "LogFile");
                if (!Path.IsPathRooted(log)) { log = Path.Combine(Path.GetDirectoryName(entry.ConfigPath), log); }
                if (!File.Exists(log)) { throw new InvalidOperationException("The service log does not exist yet."); }
                Process.Start(new ProcessStartInfo(WindowsServices.SystemTool("notepad.exe"),
                    CommandLine.Quote(log)) { UseShellExecute = false });
            }
            catch (Exception error) { ShowError(error); }
        }

        private void Append(string message)
        {
            if (IsDisposed || Disposing) { return; }
            if (activity.TextLength > 100000) { activity.Clear(); }
            activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
            activity.SelectionStart = activity.TextLength;
            activity.ScrollToCaret();
        }

        private void ShowError(Exception error)
        {
            if (IsDisposed || Disposing) { return; }
            Append(error.Message);
            MessageBox.Show(this, error.Message, "AXS Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                keepRunningTimer.Dispose();
                serviceMenu.Dispose();
                tray.Visible = false;
                tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
