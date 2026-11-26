using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FastLinkDirectTool.Modes
{
    // UI-only boundary: offline tests never construct a real DNS service or
    // perform a network operation when exercising the legacy handoff.
    internal interface IDualModeUiSession
    {
        bool IsActive { get; }
        bool IsArmed { get; }
        bool HasPending { get; }
        DualOperationResult ValidateCurrent();
        DualOperationResult Start(ModeRequest request, bool manualAllowed);
        DualOperationResult Arm(ModeRequest request, bool manualAllowed);
        DualOperationResult Stop(bool allowEgressChange);
        DualReconciliationReport ReviewStaleSession();
        DualOperationResult ArchiveStaleSession(bool manualAllowed);
    }

    internal sealed class DualModeForm : Form
    {
        private readonly DualNetworkWindowsPlatform platform;
        private readonly IDualModeUiSession session;
        private readonly ComboBox mode = new ComboBox(), corporate = new ComboBox(), internet = new ComboBox();
        private readonly DataGridView rules = new DataGridView();
        private readonly TextBox cidrs = new TextBox(), details = new TextBox();
        private readonly CheckBox confirmed = new CheckBox(), admission = new CheckBox(), permitted = new CheckBox(), externalOk = new CheckBox();
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Action<string> log;
        private readonly bool offlinePreview;
        private readonly bool injectedPreviewSession;
        private readonly Func<bool> previewStopApproval;
        private readonly Func<bool> previewPermissionApproval;
        private readonly bool previewExplicitIntent;
        private readonly Label modeNotice = new Label();
        private readonly Label connectionSummary = new Label();
        private readonly Label ruleHint = new Label();
        private readonly Label operationStatus = new Label();
        private readonly Panel settingsPanel = new Panel();
        private readonly TableLayoutPanel rootLayout = new TableLayoutPanel();
        private readonly Button settingsAction = new Button();
        private bool settingsExpanded;
        private DateTime workingSinceUtc;
        private readonly Button checkAction, saveAction, prepareAction, enableAction, stopAction, archiveAction, exitAction, addRuleAction, deleteRuleAction;
        private DualModeProfile profile;
        private bool working, closing, allowClose, closeQueued, automaticChecksPaused;
        private bool preparedVerificationPending;
        private bool updatingChoices;
        private string confirmedTopology;
        private string lastMessage;
        private NotifyIcon modeTrayIcon;
        private ContextMenuStrip modeTrayMenu;
        private bool trayTransitioning, minimizedToTray, shownInitialized;
        internal bool IsMinimizedToTray { get { return minimizedToTray; } }
        public bool HasOwnedState { get { return session.IsActive || session.IsArmed || session.HasPending; } }
        public bool RequestedLegacyMode { get; private set; }
        public bool RequestedApplicationExit { get; private set; }
        private bool LegacySelected { get { return mode.SelectedIndex == 0; } }

        internal DualModeForm(IEnumerable<string> sourceRules, Action<string> logger, bool previewOnly = false, IDualModeUiSession previewSession = null, Func<bool> previewStopApproval = null, bool prepareOnOpen = false, Func<bool> previewPermissionApproval = null, bool previewExplicitIntent = false)
        {
            // Keep this modal window's taskbar style fixed before creating its
            // handle. Changing ShowInTaskbar while ShowDialog is running can
            // recreate the handle and turn DialogResult into Cancel, which
            // would incorrectly turn minimizing into an application exit.
            ShowInTaskbar = false;
            if (previewSession != null && !previewOnly) throw new ArgumentException("Session injection is only supported by offline UI tests.");
            if (previewStopApproval != null && !previewOnly) throw new ArgumentException("Approval injection is only supported by offline UI tests.");
            if ((previewPermissionApproval != null || previewExplicitIntent) && (!previewOnly || previewSession == null)) throw new ArgumentException("Explicit intent injection requires an offline fake session.");
            offlinePreview = previewOnly;
            injectedPreviewSession = previewSession != null;
            this.previewStopApproval = previewStopApproval;
            this.previewPermissionApproval = previewPermissionApproval;
            this.previewExplicitIntent = previewExplicitIntent;
            log = logger ?? delegate { };
            profile = offlinePreview ? new DualModeProfile { Rules = DualRuleMigration.Propose(sourceRules) } : DualProfileStore.Load(sourceRules);
            platform = offlinePreview ? new DualNetworkWindowsPlatform(delegate { throw new InvalidOperationException("Offline UI cannot execute commands."); },
                delegate { return false; }, delegate { return DateTime.UtcNow; }, "offline-ui") : new DualNetworkWindowsPlatform();
            session = offlinePreview ? (previewSession ?? new PreviewSession()) :
                (IDualModeUiSession)new RuntimeSession(new DualNetworkRuntime(platform, new DualFileJournal(), new DualNetworkDns()));
            Text = "直连助手 · 网络模式";
            Font = new Font("Microsoft YaHei UI", 9F);
            Size = new Size(940, 640); MinimumSize = new Size(860, 600);
            StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel root = rootLayout;
            root.Dock = DockStyle.Fill; root.Padding = new Padding(14); root.ColumnCount = 1; root.RowCount = 9;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(root);
            modeNotice.Dock = DockStyle.Fill; modeNotice.Padding = new Padding(8); modeNotice.BackColor = Color.FromArgb(255, 244, 214);
            root.Controls.Add(modeNotice, 0, 0);
            TableLayoutPanel modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            TableLayoutPanel roles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            roles.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); roles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mode.DropDownStyle = corporate.DropDownStyle = internet.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Dock = corporate.Dock = internet.Dock = DockStyle.Fill;
            mode.Items.AddRange(new object[] { "模式一：FastLink 代理 + 指定目标直连", "模式二：有线公司内网 + Wi-Fi 外网", "模式三：Wi-Fi 公司内网 + 有线外网" });
            mode.SelectedIndex = profile.Mode == WorkMode.WifiCorporateWiredInternet ? 2 : 1;
            modeRow.Controls.Add(new Label { Text = "工作模式", AutoSize = true }, 0, 0); modeRow.Controls.Add(mode, 1, 0);
            roles.Controls.Add(new Label { Text = "公司接口", AutoSize = true }, 0, 0); roles.Controls.Add(corporate, 1, 0);
            roles.Controls.Add(new Label { Text = "互联网接口", AutoSize = true }, 0, 1); roles.Controls.Add(internet, 1, 1);
            root.Controls.Add(modeRow, 0, 1);
            connectionSummary.Dock = DockStyle.Fill; connectionSummary.Padding = new Padding(3, 5, 0, 0);
            root.Controls.Add(connectionSummary, 0, 2);
            settingsPanel.Dock = DockStyle.Fill; settingsPanel.Visible = false;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage interfacesPage = new TabPage("接口设置"), logPage = new TabPage("运行日志");
            tabs.TabPages.Add(interfacesPage); tabs.TabPages.Add(logPage); settingsPanel.Controls.Add(tabs);
            var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(5) };
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            interfacesPage.Controls.Add(settingsLayout); settingsLayout.Controls.Add(roles, 0, 0);
            root.Controls.Add(settingsPanel, 0, 6);
            foreach (ComboBox selector in new[] { corporate, internet })
                selector.SelectedIndexChanged += delegate { if (!updatingChoices) ResetNetworkConfirmations(); };
            // Kept as private state for the existing runtime gate and fake tests;
            // daily use has no checklist. Explicit Start/Save actions provide the
            // current user intent; live Internet verification stays automatic.
            FlowLayoutPanel checks = new FlowLayoutPanel { Visible = false, Width = 1, Height = 1 };
            permitted.Text = "已确认公司允许这台电脑双网并用（记住本机）";
            permitted.Checked = profile.IsPermissionConfirmed(platform.ScopeIdentity, profile.CorporateAdapterId, profile.InternetAdapterId);
            externalOk.Text = "外网连通性：操作时自动检测";
            admission.Text = "公司接口已通过 MOKA 准入（接线后确认）";
            confirmed.Text = "已确认下方规则的内外网分类"; confirmed.Checked = profile.RulesConfirmed;
            foreach (CheckBox box in new[] { permitted, externalOk, admission, confirmed }) { box.AutoSize = true; box.Margin = new Padding(6, 6, 12, 4); checks.Controls.Add(box); }
            Controls.Add(checks);
            rules.Dock = DockStyle.Fill; rules.BackgroundColor = Color.White;
            rules.RowHeadersVisible = false; rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            rules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "域名 / IP（双网规则）", FillWeight = 72 });
            DataGridViewComboBoxColumn target = new DataGridViewComboBoxColumn { HeaderText = "目标网络", FillWeight = 28 };
            target.Items.AddRange("公司内网", "互联网", "待确认"); rules.Columns.Add(target);
            foreach (ModeRule rule in profile.Rules) rules.Rows.Add(rule.Pattern, TargetText(rule.Target));
            rules.DefaultValuesNeeded += delegate(object s, DataGridViewRowEventArgs e) { e.Row.Cells[1].Value = "待确认"; };
            rules.CellValueChanged += delegate { confirmed.Checked = false; };
            rules.UserDeletedRow += delegate { confirmed.Checked = false; };
            root.Controls.Add(rules, 0, 4);
            FlowLayoutPanel ranges = new FlowLayoutPanel { Dock = DockStyle.Fill };
            ranges.Controls.Add(new Label { Text = "额外公司网段（可空，用逗号分隔）", AutoSize = true, Margin = new Padding(3, 8, 4, 0) });
            cidrs.Width = 430; cidrs.Text = string.Join(",", profile.CorporateCidrs.ToArray()); ranges.Controls.Add(cidrs);
            cidrs.TextChanged += delegate { confirmed.Checked = false; };
            settingsLayout.Controls.Add(ranges, 0, 1);
            actions.Dock = DockStyle.Fill;
            checkAction = AddAction("检查状态", delegate { automaticChecksPaused = false; RefreshReadOnly(); });
            saveAction = AddAction("保存规则", SaveProfile);
            prepareAction = AddAction("准备公司与外网", delegate { Change(false); });
            enableAction = AddAction("启用双网分流", delegate { if (LegacySelected) RequestLegacyHandoff(); else Change(true); });
            stopAction = AddAction("停用 / 恢复清理", Stop);
            archiveAction = AddAction("核对旧会话记录", ReviewOldSession);
            exitAction = AddAction("退出助手", delegate { RequestSafeClose(false); });
            exitAction.BackColor = Color.FromArgb(180, 45, 45); exitAction.ForeColor = Color.White;
            addRuleAction = AddAction("新增规则", AddRule);
            deleteRuleAction = AddAction("删除所选", DeleteSelectedRules);
            TableLayoutPanel ruleTools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            ruleTools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            ruleTools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            ruleTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ruleTools.Controls.Add(addRuleAction, 0, 0); ruleTools.Controls.Add(deleteRuleAction, 1, 0);
            ruleHint.Dock = DockStyle.Fill; ruleHint.Padding = new Padding(10, 8, 0, 0);
            ruleTools.Controls.Add(ruleHint, 2, 0); root.Controls.Add(ruleTools, 0, 3);
            settingsAction.Text = "设置与日志"; settingsAction.AutoSize = true; settingsAction.Height = 32; settingsAction.Margin = new Padding(12, 6, 5, 0);
            settingsAction.Click += delegate { ToggleSettings(); }; actions.Controls.Add(settingsAction);
            var advancedActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            foreach (Button button in new[] { checkAction, prepareAction, archiveAction, exitAction }) advancedActions.Controls.Add(button);
            settingsLayout.Controls.Add(advancedActions, 0, 2);
            actions.Controls.SetChildIndex(enableAction, 0); actions.Controls.SetChildIndex(stopAction, 1); actions.Controls.SetChildIndex(saveAction, 2);
            root.Controls.Add(actions, 0, 5);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            logPage.Controls.Add(details);
            operationStatus.Dock = DockStyle.Fill; operationStatus.AutoEllipsis = true;
            operationStatus.Padding = new Padding(0, 4, 0, 0); operationStatus.ForeColor = Color.DarkBlue;
            operationStatus.Text = "最近操作：尚无；详细结果可在运行日志中查看。";
            root.Controls.Add(operationStatus, 0, 7);
            root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "× / 最小化仅收起，分流继续。再次打开桌面“直连助手”即可显示；真正退出请用托盘或设置中的“退出助手”。", Padding = new Padding(0, 5, 0, 0) }, 0, 8);
            mode.SelectedIndexChanged += delegate
            {
                if (updatingChoices) return;
                // Keep the last mode-two/three draft in memory. Selecting mode
                // one must never serialize FastLink into dual-profile.json.
                CaptureDraft(LegacySelected ? profile.Mode : SelectedDualMode(mode.SelectedIndex));
                ResetNetworkConfirmations();
                SetWorking(working);
                if (LegacySelected) Tell(LegacyNotice);
            };
            SetWorking(false);
            if (!offlinePreview)
            {
                modeTrayMenu = new ContextMenuStrip();
                modeTrayMenu.Items.Add("打开网络模式", null, delegate { RestoreFromTray(); });
                modeTrayMenu.Items.Add("退出并清理", null, delegate { RestoreFromTray(); RequestSafeClose(false); });
                modeTrayIcon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "直连助手 · 双网模式", ContextMenuStrip = modeTrayMenu };
                modeTrayIcon.DoubleClick += delegate { RestoreFromTray(); };
            }
            Resize += delegate
            {
                if (!trayTransitioning && WindowState == FormWindowState.Minimized) MinimizeToTray();
            };
            timer.Interval = 10000;
            timer.Tick += delegate { if (working) UpdateStatusSummary(); else RefreshReadOnly(); };
            Shown += delegate
            {
                // Toggling taskbar visibility may recreate the native handle.
                // Restoring the UI must never run the initial preparation twice.
                if (shownInitialized) return;
                shownInitialized = true;
                if (modeTrayIcon != null) modeTrayIcon.Visible = true;
                if (offlinePreview)
                {
                    profile.CorporateAdapterId = "11111111-1111-1111-1111-111111111111";
                    profile.InternetAdapterId = "22222222-2222-2222-2222-222222222222";
                    UpdateChoices(new NetworkSnapshot { Adapters = new List<AdapterSnapshot> {
                        new AdapterSnapshot { Id = profile.CorporateAdapterId, Name = "示例公司有线", Kind = LinkKind.Ethernet, IsPhysical = true },
                        new AdapterSnapshot { Id = profile.InternetAdapterId, Name = "示例外网 Wi-Fi", Kind = LinkKind.Wifi, IsPhysical = true, IsUp = true, Address = "192.0.2.10" } } });
                    Tell("离线界面测试：未查询或更改真实网络。以下接口仅为示例。\r\n规则 " + profile.Rules.Count + " 条：公司内网 " + profile.Rules.Count(r => r.Target == RuleTarget.Corporate) + " 条，互联网 " + profile.Rules.Count(r => r.Target == RuleTarget.Internet) + " 条，待确认 " + profile.Rules.Count(r => r.Target == RuleTarget.Review) + " 条。\r\n此截图不能证明现场双网已经可用。");
                    return;
                }
                Tell("正在读取网卡和网络状态，请稍候；读取本身不会启用分流。当前仍未启用双网模式。");
                // This is an explicit, one-shot launch action, never a logon
                // default. It still requires saved, scoped user permission.
                if (prepareOnOpen) Change(false, true); else RefreshReadOnly();
                timer.Start();
            };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (allowClose) return;
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    // X / Alt+F4 has the same background-running behavior as
                    // the legacy window. Never hide a live modal form.
                    e.Cancel = true;
                    MinimizeToTray();
                    return;
                }
                if (working) { e.Cancel = true; RequestSafeClose(false); return; }
                if (offlinePreview && !injectedPreviewSession) return;
                // System shutdown and other non-user close requests still use
                // the verified cleanup path, never the minimize-to-tray path.
                e.Cancel = true;
                RequestSafeClose(false);
            };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
        }

        internal void MinimizeToTray()
        {
            if (IsDisposed || Disposing || trayTransitioning) return;
            if (closing)
            {
                RestoreFromTray();
                Tell("安全退出已经开始，不能改为后台运行；清理未完成时窗口和托盘会保留。");
                return;
            }
            // The user's newer minimize request cancels only an exit queued
            // behind a read/operation, never a cleanup that has already begun.
            closeQueued = false;
            trayTransitioning = true;
            try
            {
                // Never Hide, Close or recreate this modal form's handle:
                // ShowDialog must retain the live DNS/runtime owner.
                WindowState = FormWindowState.Minimized;
                minimizedToTray = true;
            }
            finally { trayTransitioning = false; }
        }

        internal void RestoreFromTray()
        {
            if (IsDisposed || Disposing || trayTransitioning) return;
            trayTransitioning = true;
            try
            {
                WindowState = FormWindowState.Normal;
                minimizedToTray = false;
                if (!offlinePreview) Activate();
            }
            finally { trayTransitioning = false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (modeTrayIcon != null) { modeTrayIcon.Visible = false; modeTrayIcon.Dispose(); modeTrayIcon = null; }
                if (modeTrayMenu != null) { modeTrayMenu.Dispose(); modeTrayMenu = null; }
            }
            base.Dispose(disposing);
        }

        private const string LegacyNotice = "模式一沿用主界面的 FastLink 直连规则，需要 FastLink 接通。\r\n这里只选择模式，不启动 FastLink 或直连；点击“切换到模式一”安全返回后，再到主界面手动开启。双网草稿保留不变。";
        private static string TargetText(RuleTarget target) { return target == RuleTarget.Corporate ? "公司内网" : target == RuleTarget.Internet ? "互联网" : "待确认"; }
        internal static WorkMode SelectedDualMode(int index)
        {
            if (index == 1) return WorkMode.WiredCorporateWifiInternet;
            if (index == 2) return WorkMode.WifiCorporateWiredInternet;
            throw new InvalidOperationException("模式一不属于双网配置；请在主界面管理其规则。");
        }
        private sealed class RuntimeSession : IDualModeUiSession
        {
            private readonly DualNetworkRuntime value;
            internal RuntimeSession(DualNetworkRuntime runtime) { value = runtime; }
            public bool IsActive { get { return value.IsActive; } }
            public bool IsArmed { get { return value.IsArmed; } }
            public bool HasPending { get { return value.HasPending; } }
            public DualOperationResult ValidateCurrent() { return value.ValidateCurrent(); }
            public DualOperationResult Start(ModeRequest request, bool manualAllowed) { return value.Start(request, manualAllowed); }
            public DualOperationResult Arm(ModeRequest request, bool manualAllowed) { return value.Arm(request, manualAllowed); }
            public DualOperationResult Stop(bool allowEgressChange) { return value.Stop(allowEgressChange); }
            public DualReconciliationReport ReviewStaleSession() { return value.ReviewStaleSession(); }
            public DualOperationResult ArchiveStaleSession(bool manualAllowed) { return value.ArchiveStaleSession(manualAllowed); }
        }
        private sealed class PreviewSession : IDualModeUiSession
        {
            public bool IsActive { get { return false; } }
            public bool IsArmed { get { return false; } }
            public bool HasPending { get { return false; } }
            public DualOperationResult ValidateCurrent() { throw new InvalidOperationException("Preview cannot inspect runtime."); }
            public DualOperationResult Start(ModeRequest request, bool manualAllowed) { throw new InvalidOperationException("Preview cannot start."); }
            public DualOperationResult Arm(ModeRequest request, bool manualAllowed) { throw new InvalidOperationException("Preview cannot prepare."); }
            public DualOperationResult Stop(bool allowEgressChange) { throw new InvalidOperationException("Preview cannot stop."); }
            public DualReconciliationReport ReviewStaleSession() { throw new InvalidOperationException("Preview cannot review a real journal."); }
            public DualOperationResult ArchiveStaleSession(bool manualAllowed) { throw new InvalidOperationException("Preview cannot archive."); }
        }
        private Button AddAction(string text, Action action)
        {
            Button b = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(3, 6, 5, 0) };
            b.Click += delegate
            {
                if (working && b != exitAction) return;
                // Only a deliberately injected fake may exercise session actions.
                // Preview checking/saving never reaches platform or profile IO.
                if (offlinePreview && b != addRuleAction && b != deleteRuleAction && !(injectedPreviewSession &&
                    (b == prepareAction || b == enableAction || b == stopAction || b == archiveAction || b == exitAction || (previewExplicitIntent && b == saveAction)))) return;
                action();
            };
            actions.Controls.Add(b);
            return b;
        }
        private void Tell(string message)
        {
            if (lastMessage == message) return;
            lastMessage = message;
            operationStatus.Text = "最近操作：" + (message ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            details.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
            if (details.TextLength > 14000) details.Text = details.Text.Substring(details.TextLength - 10000);
            // An optional log sink must never strand the operation gate.
            try { log(message); } catch { }
            UpdateStatusSummary();
        }
        private void ToggleSettings()
        {
            settingsExpanded = !settingsExpanded;
            settingsPanel.Visible = settingsExpanded;
            settingsAction.Text = settingsExpanded ? "收起设置" : "设置与日志";
            rootLayout.RowStyles[6].Height = settingsExpanded ? 220 : 0;
            Size = new Size(Width, settingsExpanded ? 860 : 640);
        }
        private void ShowSettings()
        { if (!settingsExpanded) ToggleSettings(); }
        private void UpdateStatusSummary()
        {
            int elapsed = Math.Max(0, (int)(DateTime.UtcNow - workingSinceUtc).TotalSeconds);
            string state = working ? "正在处理，已用 " + elapsed + " 秒（请勿重复启动）" : session.IsActive ? "分流运行中" :
                session.IsArmed && preparedVerificationPending ? "准备状态待核验，尚未确认有效" : session.HasPending ? "需要检查恢复记录" :
                session.IsArmed ? "已准备，尚未开始分流" : "尚未开始";
            string corp = corporate.SelectedItem == null ? "未选择（请在设置中指定）" : corporate.SelectedItem.ToString();
            string net = internet.SelectedItem == null ? "未选择（请在设置中指定）" : internet.SelectedItem.ToString();
            connectionSummary.Text = "公司内网：" + corp + "\r\n互联网：" + net;
            modeNotice.Text = LegacySelected ? LegacyNotice : state + "\r\n点击“开始分流”表示公司网络已完成准入；外网连通性由程序检测。模式二 / 三不与 FastLink / aTrust 同时开启。";
            ruleHint.Text = LegacySelected ? "这里保留双网草稿；切换到模式一后管理原 FastLink 规则。" :
                working || HasOwnedState ? "规则可直接查看；修改前请先停止分流并完成清理。" :
                "直接修改下表并保存。新增规则需自行选择“公司内网”或“互联网”。";
        }
        private void ResetNetworkConfirmations()
        {
            admission.Checked = externalOk.Checked = false;
            platform.CorporateAdmissionConfirmed = platform.InternetConfirmed = false;
            string corp = corporate.SelectedItem is AdapterChoice ? ((AdapterChoice)corporate.SelectedItem).Id : profile.CorporateAdapterId;
            string net = internet.SelectedItem is AdapterChoice ? ((AdapterChoice)internet.SelectedItem).Id : profile.InternetAdapterId;
            permitted.Checked = profile.IsPermissionConfirmed(platform.ScopeIdentity, corp, net);
            platform.DualConnectionConfirmed = permitted.Checked;
        }
        private void SetWorking(bool value)
        {
            if (value && !working) workingSinceUtc = DateTime.UtcNow;
            working = value; actions.Enabled = true;
            bool editable = !value && !HasOwnedState;
            mode.Enabled = editable;
            editable = editable && !LegacySelected;
            corporate.Enabled = internet.Enabled = cidrs.Enabled = editable;
            rules.ReadOnly = !editable; rules.AllowUserToAddRows = rules.AllowUserToDeleteRows = editable;
            addRuleAction.Enabled = deleteRuleAction.Enabled = editable;
            confirmed.Enabled = editable;
            permitted.Enabled = !value && !LegacySelected && !session.IsActive;
            externalOk.Enabled = offlinePreview && !value && !LegacySelected && !session.IsActive;
            admission.Enabled = !value && !LegacySelected && !session.IsActive;
            checkAction.Enabled = saveAction.Enabled = prepareAction.Enabled = !value && !LegacySelected;
            enableAction.Enabled = !value && (!session.IsActive || LegacySelected);
            stopAction.Enabled = !value && HasOwnedState;
            archiveAction.Enabled = !value && !session.IsActive && !session.IsArmed && session.HasPending;
            exitAction.Enabled = true;
            enableAction.Text = LegacySelected ? "切换到模式一" : "开始分流";
            stopAction.Text = "停止分流";
            UpdateStatusSummary();
            if (!value && closeQueued && !allowClose && !IsDisposed)
            {
                closeQueued = false;
                RequestSafeClose(false);
            }
        }

        private void CompleteOnUi(Action completion)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed || Disposing) return;
                    try { completion(); }
                    catch (Exception error)
                    {
                        closing = false; closeQueued = false; automaticChecksPaused = true;
                        Tell("操作结果未能核验，未视为清理成功；可重试：" + error.Message);
                    }
                    finally { if (!IsDisposed && !Disposing && !allowClose) SetWorking(false); }
                });
            }
            catch (InvalidOperationException) { if (!IsDisposed && !Disposing) throw; }
        }
        private void AddRule()
        {
            if (working || closing || HasOwnedState || LegacySelected) return;
            rules.EndEdit();
            int index = rules.Rows.Add(string.Empty, TargetText(RuleTarget.Review));
            rules.ClearSelection();
            rules.Rows[index].Selected = true;
            rules.CurrentCell = rules.Rows[index].Cells[0];
            confirmed.Checked = false;
            rules.Focus(); rules.BeginEdit(true);
        }
        private void DeleteSelectedRules()
        {
            if (working || closing || HasOwnedState || LegacySelected) return;
            rules.EndEdit();
            List<DataGridViewRow> selected = rules.SelectedRows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow).OrderByDescending(row => row.Index).ToList();
            if (selected.Count == 0) { Tell("请先选中要删除的规则。"); return; }
            foreach (DataGridViewRow row in selected) rules.Rows.Remove(row);
            confirmed.Checked = false;
        }
        private ModeRequest RequestFromUi()
        {
            if (LegacySelected) throw new InvalidOperationException("模式一沿用主界面规则，不生成双网请求。");
            CaptureDraft(SelectedDualMode(mode.SelectedIndex));
            platform.CorporateAdapterId = profile.CorporateAdapterId;
            platform.InternetAdapterId = profile.InternetAdapterId;
            platform.CorporateAdmissionConfirmed = admission.Checked;
            platform.DualConnectionConfirmed = permitted.Checked;
            // Live reachability is not a user checkbox and is not persisted.
            if (offlinePreview) platform.InternetConfirmed = externalOk.Checked;
            return profile.ToRequest();
        }
        private void CaptureDraft(WorkMode dualMode)
        {
            rules.EndEdit();
            profile.Mode = dualMode;
            if (corporate.SelectedItem is AdapterChoice) profile.CorporateAdapterId = ((AdapterChoice)corporate.SelectedItem).Id;
            if (internet.SelectedItem is AdapterChoice) profile.InternetAdapterId = ((AdapterChoice)internet.SelectedItem).Id;
            profile.Rules = new List<ModeRule>();
            foreach (DataGridViewRow row in rules.Rows)
            {
                if (row.IsNewRow) continue;
                string target = Convert.ToString(row.Cells[1].Value);
                profile.Rules.Add(new ModeRule { Pattern = Convert.ToString(row.Cells[0].Value), Target = target == "公司内网" ? RuleTarget.Corporate : target == "互联网" ? RuleTarget.Internet : RuleTarget.Review });
            }
            profile.CorporateCidrs = cidrs.Text.Split(new[] { ',', ';', ' ', '\r', '\n', '，' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            profile.RulesConfirmed = confirmed.Checked;
        }
        private void SaveProfile()
        {
            if (working || closing || allowClose || IsDisposed || Disposing || LegacySelected) return;
            try
            {
                RequestFromUi();
                if (!offlinePreview || previewExplicitIntent) ConfirmExplicitRuleChoice();
                if (offlinePreview) { Tell("离线确认规则分类：未写文件、未改网络。"); return; }
                RememberPermission(); DualProfileStore.Save(profile);
                Tell("双网配置已单独保存；未改原 FastLink 规则，也未启用网络分流。");
            }
            catch (Exception ex) { Tell("保存失败：" + ex.Message); }
        }
        private void ConfirmExplicitRuleChoice()
        {
            if (profile.Rules.Any(r => r.Target == RuleTarget.Review))
                throw new InvalidOperationException("还有规则未选择公司内网或互联网；请先完成分类。");
            foreach (ModeRule rule in profile.Rules)
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) throw new InvalidOperationException(error);
            }
            // Saving or explicitly starting is the confirmation of the visible
            // classifications, not an inference from the destination's name.
            profile.RulesConfirmed = confirmed.Checked = true;
        }
        private sealed class AdapterChoice
        {
            public string Id, Text;
            public override string ToString() { return Text; }
        }
        private void UpdateChoices(NetworkSnapshot snapshot)
        {
            updatingChoices = true;
            try { foreach (ComboBox box in new[] { corporate, internet })
            {
                string selected = box.SelectedItem is AdapterChoice ? ((AdapterChoice)box.SelectedItem).Id : box == corporate ? profile.CorporateAdapterId : profile.InternetAdapterId;
                box.Items.Clear();
                foreach (AdapterSnapshot a in snapshot.Adapters.Where(a => a.IsPhysical))
                    box.Items.Add(new AdapterChoice { Id = a.Id, Text = a.Name + " / " + (a.Kind == LinkKind.Wifi ? "Wi-Fi" : "有线") + " / " + (a.IsUp ? a.Address + "，已连接" : "未连接") });
                for (int i = 0; i < box.Items.Count; i++) if (((AdapterChoice)box.Items[i]).Id == selected) box.SelectedIndex = i;
                // Never automatically substitute a different adapter by name or guessed role.
            } } finally { updatingChoices = false; }
        }
        private static string Topology(ModeRequest request, NetworkSnapshot snapshot)
        {
            return string.Join("|", snapshot.Adapters.Where(a => a.Id == request.CorporateAdapterId || a.Id == request.InternetAdapterId)
                .OrderBy(a => a.Id).Select(a => a.Id + ":" + a.IsUp + ":" + a.Address + ":" + a.PrefixLength + ":" + a.Gateway + ":" + string.Join(",", a.DnsServers.ToArray()) + ":" + a.Ipv6RouteSignature).ToArray());
        }
        private void RefreshReadOnly()
        {
            if (working || closing) return;
            if (session.HasPending)
            {
                // HasPending can conservatively mean that a route callback owns
                // the runtime lock. Never wait for that lock on the UI thread.
                SetWorking(true);
                Task.Run(delegate { return session.ValidateCurrent(); }).ContinueWith(t => CompleteOnUi(delegate
                {
                    preparedVerificationPending = !session.IsActive && session.IsArmed && (t.IsFaulted || !t.Result.Success);
                    if (t.IsFaulted || (!t.IsFaulted && t.Result.Pending)) { automaticChecksPaused = true; closeQueued = false; }
                    if (t.IsFaulted) Tell("状态核验未完成：" + t.Exception.GetBaseException().Message);
                    else Tell((t.Result.Pending ? "分流已暂停；" : "") + t.Result.Message +
                        (t.Result.Pending ? " 可在设置中查看日志或停止并恢复。" : ""));
                }));
                return;
            }
            if (automaticChecksPaused)
            {
                Tell("上次状态检查或自动处理未完成，已暂停自动重试；请点击“检查状态”重新检查，或手动停止 / 恢复清理。");
                return;
            }
            if (LegacySelected) { Tell(LegacyNotice); return; }
            ModeRequest request;
            try { request = RequestFromUi(); } catch (Exception ex) { Tell(ex.Message); return; }
            SetWorking(true);
            bool invalidated = false;
            bool preparationUnverified = false;
            Task.Run(delegate
            {
                if (session.IsActive || session.IsArmed)
                {
                    DualOperationResult validity = session.ValidateCurrent();
                    preparationUnverified = !session.IsActive && session.IsArmed && !validity.Success;
                    invalidated = validity.State != "ReadDeferred" && (!validity.Success || (!session.IsActive && !session.IsArmed));
                    return Tuple.Create(validity.VerifiedPreparationSnapshot, validity.Message);
                }
                NetworkSnapshot snapshot = platform.Capture();
                ModePlan plan = NetworkModePlanner.Build(request, snapshot);
                string state = session.HasPending ? "存在待恢复记录，请先点“停用 / 恢复清理”。" : session.IsActive ? "双网分流运行中；尚需实际业务验收。" : session.IsArmed ? "已执行准备，尚未启用域名分流；请核实公司路由与准入后，手动点击启用。" : "未启用：" + string.Join("；", plan.Blockers.Take(5).ToArray());
                return Tuple.Create(snapshot, state);
            }).ContinueWith(t => CompleteOnUi(delegate
            {
                // HasPending also conservatively returns true while DNS owns
                // the runtime lock. A live session busy on a read is not a
                // cleanup failure and must not disable future validation.
                bool actualPending = !session.IsActive && !session.IsArmed && session.HasPending;
                preparedVerificationPending = preparationUnverified || (t.IsFaulted && !session.IsActive && session.IsArmed);
                if (t.IsFaulted || invalidated || actualPending) automaticChecksPaused = true;
                if (t.IsFaulted || actualPending || invalidated) closeQueued = false;
                if (!t.IsFaulted)
                {
                    if (t.Result.Item1 != null)
                    {
                        string topology = Topology(request, t.Result.Item1);
                        if (confirmedTopology != null && confirmedTopology != topology) invalidated = true;
                        confirmedTopology = topology;
                        UpdateChoices(t.Result.Item1);
                    }
                    if (invalidated) ResetNetworkConfirmations();
                    Tell(t.Result.Item2 + (invalidated ? " 网络条件变化，需重新核验准入/外网；本机双网许可保持记忆。" : ""));
                }
                else Tell("只读检查未完成：" + t.Exception.GetBaseException().Message);
            }));
        }
        private void RememberPermission()
        {
            if (permitted.Checked)
            {
                if (!profile.IsPermissionConfirmed(platform.ScopeIdentity, profile.CorporateAdapterId, profile.InternetAdapterId))
                    profile.RecordPermission(platform.ScopeIdentity, profile.CorporateAdapterId, profile.InternetAdapterId, DateTime.UtcNow, DualPermissionSource.UserAction);
            }
            else profile.ClearPermission();
        }
        private void Change(bool enable, bool explicitLaunchAction = false)
        {
            if (working || closing || allowClose || IsDisposed || Disposing) return;
            if (offlinePreview && !injectedPreviewSession) return;
            if (LegacySelected) { Tell("模式一不执行双网预备或启用，请点击“切换到模式一”。"); return; }
            ModeRequest request;
            try { request = RequestFromUi(); } catch (Exception ex) { Tell(ex.Message); return; }
            if (!PrepareExplicitIntent(enable, explicitLaunchAction)) return;
            // The permission dialog pumps UI messages. Its completion can drain
            // a queued explicit exit, so do not persist or schedule work after
            // that exit won the race, even if the dialog returned Yes.
            if (closing || allowClose || IsDisposed || Disposing) return;
            if (!confirmed.Checked || profile.Rules.Any(r => r.Target == RuleTarget.Review)) { Tell("请先确认规则分类；待确认项不会自动分配出口。"); return; }
            if (!permitted.Checked) { Tell("请首次确认公司允许这台电脑双网并用；同一台电脑和这对接口会记住许可。"); return; }
            if (enable && !admission.Checked) { Tell("请在连接公司接口并通过 MOKA 后，确认准入；不需要再次安装安全软件。"); return; }
            // A modal MessageBox pumps timer events; hold the gate before showing
            // it so a topology poll cannot race the user's confirmed operation.
            SetWorking(true);
            if (explicitLaunchAction && (enable || HasOwnedState || !profile.IsPermissionConfirmed(platform.ScopeIdentity, profile.CorporateAdapterId, profile.InternetAdapterId)))
            { Tell("启动准备被拒绝：只允许已保存许可且没有未完成会话的全新准备。"); SetWorking(false); return; }
            if (!offlinePreview && !explicitLaunchAction && !enable && MessageBox.Show(this, "将自动验证外网，再优先使用外网；公司接口已连接时准备公司路由。不代表已通过公司准入，也不会启用域名分流。继续准备？", "确认网络操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { SetWorking(false); return; }
            try { if (!offlinePreview) { RememberPermission(); DualProfileStore.Save(profile); } } catch (Exception ex) { Tell("配置保存失败，未改网络：" + ex.Message); SetWorking(false); return; }
            Tell("正在自动验证所选外网与网络稳定性；不切换网卡、不启动 VPN。验证通过后才执行准备。");
            bool verified = false;
            Task.Run(delegate
            {
                if (!offlinePreview)
                {
                    platform.InternetConfirmed = false;
                    NetworkSnapshot before = platform.Capture();
                    string beforeTopology = Topology(request, before);
                    if (before.StableSeconds < 8) System.Threading.Thread.Sleep((8 - before.StableSeconds) * 1000);
                    NetworkSnapshot ready = platform.Capture();
                    if (beforeTopology != Topology(request, ready) || ready.StableSeconds < 8)
                        throw new InvalidOperationException("网络仍在变化，未执行本次操作；请稳定后重试。");
                    if (ready.SystemProxyActive || ready.TunnelActive || ready.OtherDnsPolicyPresent || NetworkModePlanner.Ipv6CompatibilityBlocker(request, ready) != null)
                        throw new InvalidOperationException("存在代理、隧道、其他 DNS 策略或不兼容的 IPv6 出口，未执行准备。");
                    DualInternetProbeResult proof = new DualInternetProbe().Verify(request.InternetAdapterId, ready);
                    if (!proof.Success) throw new InvalidOperationException(proof.Message);
                    NetworkSnapshot after = platform.Capture();
                    if (beforeTopology != Topology(request, after) || after.StableSeconds < 8)
                        throw new InvalidOperationException("外网验证期间接口变化，验证已作废；未执行准备。");
                    platform.ConfirmInternetForCurrentTopology();
                    verified = true;
                }
                DualOperationResult r = enable ? session.Start(request, true) : session.Arm(request, true);
                return r;
            }).ContinueWith(t => CompleteOnUi(delegate
            {
                bool ok = !t.IsFaulted && t.Result != null && t.Result.Success && !t.Result.Pending;
                if (!ok) closeQueued = false;
                automaticChecksPaused = !ok;
                if (!offlinePreview) externalOk.Checked = verified;
                Tell(t.IsFaulted ? "操作未完成：" + t.Exception.GetBaseException().Message : t.Result == null ? "未收到操作结果；未视为成功，可重试。" : t.Result.Message);
            }));
        }
        private bool PrepareExplicitIntent(bool enable, bool explicitLaunchAction)
        {
            if (closing || allowClose || IsDisposed || Disposing) return false;
            if ((offlinePreview && !previewExplicitIntent) || explicitLaunchAction) return true;
            try { ConfirmExplicitRuleChoice(); }
            catch (Exception error) { ShowSettings(); Tell(error.Message); return false; }
            if (!permitted.Checked)
            {
                SetWorking(true);
                bool allowed = offlinePreview ? previewPermissionApproval != null && previewPermissionApproval() :
                    MessageBox.Show(this, "是否已获得公司许可，在这台电脑同时使用所选公司网络和外网？\r\n同一用户和同一对网卡只确认一次；不会更改公司的安全策略。", "首次使用这对网络", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                bool exitWasQueued = closeQueued;
                SetWorking(false);
                // SetWorking(false) may have synchronously completed a queued
                // explicit exit. A stale Yes must not revive the closed form.
                if (exitWasQueued || closing || allowClose || IsDisposed || Disposing) return false;
                if (!allowed) return false;
                permitted.Checked = platform.DualConnectionConfirmed = true;
            }
            // Start is the current user assertion described next to the button,
            // not an automatic claim that MOKA has authenticated. It is never
            // persisted and is revoked by any observed role/topology change.
            if (enable) admission.Checked = platform.CorporateAdmissionConfirmed = true;
            return true;
        }
        private void RequestLegacyHandoff()
        { RequestSafeClose(true); }
        private void RequestSafeClose(bool toLegacy)
        {
            if (toLegacy && !LegacySelected) return;
            if (offlinePreview && !injectedPreviewSession) return;
            if (closing) { Tell("正在等待本次安全清理完成；不会重复清理或强制退出。"); return; }
            if (working)
            {
                closeQueued = true;
                Tell("退出请求已记录，等待当前操作完成；不会重复启动清理，也不会强制退出。");
                return;
            }
            closing = true;
            SetWorking(true);
            try
            {
                // Selection alone never saves. On this explicit handoff, retain
                // the last dual-mode draft before closing; never persist mode one.
                if (toLegacy && !offlinePreview) DualProfileStore.Save(profile);
            }
            catch (Exception error)
            {
                Tell("双网草稿保存失败，尚未切换：" + error.Message); closing = false; SetWorking(false); return;
            }
            if (!HasOwnedState) { FinishClose(toLegacy); return; }
            if (!ConfirmStopRisk()) { closing = false; closeQueued = false; SetWorking(false); return; }
            string target = toLegacy ? "切换到模式一" : "退出助手";
            Tell("正在核验安全清理；完成后" + target + "，不会启动 FastLink 或自动开启分流。");
            Task.Run(delegate { return session.Stop(true); }).ContinueWith(t => CompleteOnUi(delegate
            {
                bool clean = !t.IsFaulted && t.Result != null && t.Result.Success && !t.Result.Pending && !HasOwnedState;
                if (!clean)
                {
                    Tell("安全清理未确认完成，保持本窗口供重试，尚未" + target + "：" +
                        (t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result == null ? "缺少清理结果" : t.Result.Message));
                    closing = false; closeQueued = false; automaticChecksPaused = true; return;
                }
                FinishClose(toLegacy);
            }));
        }
        private void FinishClose(bool toLegacy)
        {
            RequestedLegacyMode = toLegacy;
            RequestedApplicationExit = !toLegacy;
            closeQueued = false; allowClose = true; working = false;
            Close();
        }
        private bool ConfirmStopRisk()
        {
            if (offlinePreview) return previewStopApproval == null || previewStopApproval();
            return MessageBox.Show(this,
                "安全停止会恢复原网络优先级，可能让公司网络接管外网。建议先断开公司接口（模式二为公司网线，模式三为 TCL Wi-Fi），保留外网接口。\r\n已确认继续恢复并清理吗？",
                "确认恢复网络优先级", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        private void ReviewOldSession()
        {
            if (working || closing || session.IsActive || session.IsArmed || !session.HasPending) return;
            SetWorking(true);
            Tell("正在只读核对旧记录与当前网络；不会恢复旧优先级，也不会关闭 aTrust。");
            Task.Run(delegate { return session.ReviewStaleSession(); }).ContinueWith(t =>
            {
                if (IsDisposed || Disposing) return;
                BeginInvoke((Action)delegate
                {
                    try
                    {
                        if (t.IsFaulted || t.Result == null || !t.Result.CanArchive)
                        {
                            Tell("旧记录仍须保留，未改网络：" + (t.IsFaulted ? t.Exception.GetBaseException().Message :
                                t.Result == null ? "没有有效核对结果。" : t.Result.Message));
                            closeQueued = false; automaticChecksPaused = true; SetWorking(false); return;
                        }
                        Tell(t.Result.Message);
                        bool approved = offlinePreview ? previewStopApproval == null || previewStopApproval() :
                            MessageBox.Show(this, "核对表明旧路由/DNS 已消失，旧优先级也已不再生效。\r\n仅归档旧记录及证据，保留当前网络，不恢复旧设置。继续？",
                                "确认归档旧记录（不改网络）", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                        if (!approved) { closeQueued = false; SetWorking(false); return; }
                        // Archive repeats all reads and checks the journal again;
                        // the review displayed above is never used as write authority.
                        Task.Run(delegate { return session.ArchiveStaleSession(true); }).ContinueWith(a => CompleteOnUi(delegate
                        {
                            bool ok = !a.IsFaulted && a.Result != null && a.Result.Success && !a.Result.Pending && !HasOwnedState;
                            closeQueued = false; automaticChecksPaused = !ok;
                            Tell(a.IsFaulted ? "归档未完成，保留记录：" + a.Exception.GetBaseException().Message :
                                a.Result == null ? "未收到归档结果，未视为成功。" : a.Result.Message);
                        }));
                    }
                    catch (Exception error)
                    {
                        closeQueued = false; automaticChecksPaused = true;
                        Tell("旧记录核对未完成：" + error.Message); SetWorking(false);
                    }
                });
            });
        }
        private void Stop()
        {
            if (working || closing || (offlinePreview && !injectedPreviewSession)) return;
            if (!HasOwnedState) { Tell("没有本模式待清理的设置。需要关闭时请点击“退出助手”。"); return; }
            SetWorking(true);
            if (!ConfirmStopRisk()) { closeQueued = false; SetWorking(false); return; }
            Task.Run(delegate { return session.Stop(true); }).ContinueWith(t => CompleteOnUi(delegate
            {
                bool clean = !t.IsFaulted && t.Result != null && t.Result.Success && !t.Result.Pending && !HasOwnedState;
                if (!clean) closeQueued = false;
                automaticChecksPaused = !clean;
                Tell(clean ? t.Result.Message : "安全清理未确认完成，保留程序供重试：" +
                    (t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result == null ? "缺少清理结果" : t.Result.Message));
            }));
        }
    }
}
