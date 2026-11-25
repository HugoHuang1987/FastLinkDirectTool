using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FastLinkDirectTool.Modes
{
    // Separate entry point. Never instantiate Program/MainForm/RuntimeController here.
    // This preview can read a rules file and export a screenshot; it has no network writer.
    internal static class NetworkModePreviewProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (NetworkModePreviewForm form = new NetworkModePreviewForm())
            {
                int import = Array.IndexOf(args, "--rules-config");
                if (import >= 0 && import + 1 < args.Length) form.ImportRules(args[import + 1]);
                int scene = Array.IndexOf(args, "--scenario");
                int sceneNumber;
                if (scene >= 0 && scene + 1 < args.Length && int.TryParse(args[scene + 1], out sceneNumber)) form.SelectScenario(sceneNumber);
                int render = Array.IndexOf(args, "--render");
                if (render >= 0 && render + 1 < args.Length)
                {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.CreateControl();
                    form.Show();
                    Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.GetFullPath(args[render + 1]), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Hide();
                    return;
                }
                Application.Run(form);
            }
        }
    }

    internal sealed class NetworkModePreviewForm : Form
    {
        private readonly ComboBox mode = new ComboBox();
        private readonly ComboBox scenario = new ComboBox();
        private readonly DataGridView rules = new DataGridView();
        private readonly TextBox result = new TextBox();
        private readonly Label roleSummary = new Label();
        private readonly TextBox host = new TextBox();
        private readonly TextBox answers = new TextBox();

        public NetworkModePreviewForm()
        {
            Text = "直连助手 · 多网络模式设计预览（不改网络）";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            Size = new Size(1060, 850);
            MinimumSize = new Size(920, 740);
            BackColor = Color.FromArgb(247, 249, 252);
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18);
            root.ColumnCount = 1;
            root.RowCount = 8;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);
            Label banner = new Label();
            banner.Text = "离线内测版：所有网卡、网关和认证状态都是模拟数据\r\n不会修改路由 / DNS / 代理 / MOKA，不接管正在运行的助手，也没有“应用到系统”按钮。";
            banner.Dock = DockStyle.Fill;
            banner.BackColor = Color.FromArgb(255, 244, 214);
            banner.Padding = new Padding(10, 6, 6, 0);
            root.Controls.Add(banner, 0, 0);

            FlowLayoutPanel select = Row();
            select.Controls.Add(Caption("工作模式"));
            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Width = 340;
            mode.Items.AddRange(new object[] { "普通模式：使用系统当前网络", "模式一：单网络 + FastLink 规则分流", "模式二：有线内网 + Wi-Fi 外网", "模式三（预留）：Wi-Fi 内网 + 有线外网" });
            mode.SelectedIndex = 2;
            select.Controls.Add(mode);
            select.Controls.Add(Caption("切换策略：手动选择，自动检测只给建议"));
            root.Controls.Add(select, 0, 1);
            FlowLayoutPanel scenes = Row();
            scenes.Controls.Add(Caption("模拟场景"));
            scenario.DropDownStyle = ComboBoxStyle.DropDownList;
            scenario.Width = 340;
            scenario.Items.AddRange(new object[] { "当前情形：仅外网口，内网线未接", "双网齐备、已获准入和并用许可", "插线后 Wi-Fi 被关闭", "公司准入未通过 / 尚未确认", "仍有系统代理或 VPN 占用", "IPv6 默认出口未验证", "公司 10/8 与外网子网重叠", "刚插入网线，网络尚未稳定", "外网首选出口尚未确认", "旧模式清理未完成" });
            scenario.SelectedIndex = 0;
            scenes.Controls.Add(scenario);
            Button evaluate = ActionButton("运行模拟", delegate { Simulate(); });
            scenes.Controls.Add(evaluate);
            root.Controls.Add(scenes, 0, 2);
            roleSummary.Dock = DockStyle.Fill;
            roleSummary.Padding = new Padding(4, 4, 0, 0);
            root.Controls.Add(roleSummary, 0, 3);

            GroupBox group = new GroupBox();
            group.Text = "共享规则草稿：旧“直连”不等于公司内网；导入后逐条确认目标（仅内存，不保存）";
            group.Dock = DockStyle.Fill;
            TableLayoutPanel gridLayout = new TableLayoutPanel();
            gridLayout.Dock = DockStyle.Fill;
            gridLayout.RowCount = 2;
            gridLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel buttons = Row();
            buttons.Controls.Add(ActionButton("读取现有规则", delegate { ChooseRules(); }));
            buttons.Controls.Add(ActionButton("恢复演示规则", delegate { DemoRules(); Simulate(); }));
            buttons.Controls.Add(ActionButton("所选 → 内网", delegate { AssignSelected("公司内网"); }));
            buttons.Controls.Add(ActionButton("所选 → 外网", delegate { AssignSelected("互联网"); }));
            gridLayout.Controls.Add(buttons, 0, 0);
            rules.Dock = DockStyle.Fill;
            rules.BackgroundColor = Color.White;
            rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            rules.RowHeadersVisible = false;
            rules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            rules.AllowUserToAddRows = true;
            rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "pattern", HeaderText = "精确域名 / 后缀 / 通配符 / IP / URL", FillWeight = 72 });
            DataGridViewComboBoxColumn target = new DataGridViewComboBoxColumn();
            target.Name = "target";
            target.HeaderText = "双网模式的目标";
            target.FillWeight = 28;
            target.Items.AddRange("待确认", "公司内网", "互联网");
            rules.Columns.Add(target);
            rules.DefaultValuesNeeded += delegate(object s, DataGridViewRowEventArgs e) { e.Row.Cells[1].Value = "待确认"; };
            rules.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            gridLayout.Controls.Add(rules, 0, 1);
            group.Controls.Add(gridLayout);
            root.Controls.Add(group, 0, 4);

            FlowLayoutPanel lookup = Row();
            lookup.Controls.Add(Caption("目标验证"));
            host.Width = 245;
            host.Text = "portal.corp.example.test";
            lookup.Controls.Add(host);
            lookup.Controls.Add(Caption("模拟 DNS 答案"));
            answers.Width = 165;
            answers.Text = "192.0.2.156";
            lookup.Controls.Add(answers);
            lookup.Controls.Add(ActionButton("查看命中", delegate { ExplainHost(); }));
            root.Controls.Add(lookup, 0, 5);
            result.Dock = DockStyle.Fill;
            result.Multiline = true;
            result.ReadOnly = true;
            result.ScrollBars = ScrollBars.Vertical;
            result.BackColor = Color.White;
            root.Controls.Add(result, 0, 6);
            Label footer = new Label();
            footer.Dock = DockStyle.Fill;
            footer.Text = "模拟通过 ≠ 现场可用。网线接入后的 DHCP、准入、双网共存、IPv6、真实业务和恢复都需单独验收。";
            footer.Padding = new Padding(3, 9, 0, 0);
            root.Controls.Add(footer, 0, 7);
            mode.SelectedIndexChanged += delegate { Simulate(); };
            scenario.SelectedIndexChanged += delegate { Simulate(); };
            DemoRules();
            Simulate();
        }

        private static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0), WrapContents = false };
        }
        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 5, 12, 0) };
        }
        private static Button ActionButton(string text, EventHandler click)
        {
            Button button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(5, 0, 5, 0) };
            button.Click += click;
            return button;
        }
        private void DemoRules()
        {
            rules.Rows.Clear();
            rules.Rows.Add(".corp.example.test", "公司内网");
            rules.Rows.Add("192.0.2.156", "公司内网");
            rules.Rows.Add(".youdao.com", "互联网");
            rules.Rows.Add("*.huawei.com", "互联网");
        }
        private void AssignSelected(string target)
        {
            rules.EndEdit();
            foreach (DataGridViewRow row in rules.SelectedRows) if (!row.IsNewRow) row.Cells[1].Value = target;
            Simulate();
        }
        private List<ModeRule> ReadRules()
        {
            rules.EndEdit();
            List<ModeRule> list = new List<ModeRule>();
            foreach (DataGridViewRow row in rules.Rows)
            {
                if (row.IsNewRow) continue;
                string text = Convert.ToString(row.Cells[0].Value);
                string target = Convert.ToString(row.Cells[1].Value);
                list.Add(new ModeRule { Pattern = text, Target = target == "公司内网" ? RuleTarget.Corporate : target == "互联网" ? RuleTarget.Internet : RuleTarget.Review });
            }
            return list;
        }
        private void ChooseRules()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "只读导入规则，不改变原配置";
                dialog.Filter = "助手配置 (*.json)|*.json";
                dialog.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastLinkDirectTool");
                if (dialog.ShowDialog(this) == DialogResult.OK) ImportRules(dialog.FileName);
            }
        }
        public void ImportRules(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidOperationException("未找到配置，或文件超过 1 MiB，已停止读取。");
                Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                object items;
                if (data == null || !data.TryGetValue("Rules", out items) || !(items is System.Collections.IEnumerable) || items is string)
                    throw new InvalidOperationException("配置中没有 Rules 列表。");
                List<string> texts = new List<string>();
                foreach (object item in (System.Collections.IEnumerable)items)
                {
                    if (!(item is string)) throw new InvalidOperationException("规则列表包含非文本值。");
                    if (texts.Count >= 1000) throw new InvalidOperationException("超过 1000 条，已停止导入。");
                    texts.Add((string)item);
                }
                rules.Rows.Clear();
                foreach (string text in texts) rules.Rows.Add(text, "待确认");
                Simulate();
                result.AppendText("\r\n只读导入 " + texts.Count + " 条旧规则；未修改原配置，全部等待确认目标。\r\n");
            }
            catch (Exception ex) { result.Text = "导入失败，未修改任何配置：" + ex.Message; }
        }
        public void SelectScenario(int index)
        {
            if (index < 0 || index >= scenario.Items.Count) throw new ArgumentOutOfRangeException("index");
            scenario.SelectedIndex = index;
        }
        private ModeRequest MakeRequest()
        {
            return new ModeRequest
            {
                Mode = (WorkMode)Math.Max(0, mode.SelectedIndex),
                CorporateAdapterId = "sim-corporate",
                InternetAdapterId = "sim-internet",
                Rules = ReadRules(),
                CorporateCidrs = scenario.SelectedIndex == 6 ? new List<string> { "10.0.0.0/8" } : new List<string>()
            };
        }
        private NetworkSnapshot MakeSnapshot()
        {
            bool reverse = mode.SelectedIndex == 3;
            AdapterSnapshot corp = new AdapterSnapshot
            {
                Id = "sim-corporate", Name = "模拟公司口", Kind = reverse ? LinkKind.Wifi : LinkKind.Ethernet,
                IsPhysical = true, IsUp = scenario.SelectedIndex != 0,
                Address = "192.0.2.94", PrefixLength = 24, Gateway = "192.0.2.1",
                DnsServers = new List<string> { "192.0.2.53", "203.0.113.53" }
            };
            AdapterSnapshot outside = new AdapterSnapshot
            {
                Id = "sim-internet", Name = "模拟外网口", Kind = reverse ? LinkKind.Ethernet : LinkKind.Wifi,
                IsPhysical = true, IsUp = true,
                Address = "198.51.100.121", PrefixLength = 24, Gateway = "198.51.100.1",
                DnsServers = new List<string> { "198.51.100.53" }
            };
            // Synthetic private LAN only for the broad-CIDR overlap scenario.
            if (scenario.SelectedIndex == 6) { outside.Address = "10.77.9.121"; outside.Gateway = "10.77.9.1"; }
            if (scenario.SelectedIndex == 2) { if (reverse) corp.IsUp = false; else outside.IsUp = false; }
            outside.HasIpv6Default = scenario.SelectedIndex == 5;
            return new NetworkSnapshot
            {
                Adapters = new List<AdapterSnapshot> { corp, outside }, StableSeconds = scenario.SelectedIndex == 7 ? 3 : 15,
                CorporateAdmissionVerified = scenario.SelectedIndex != 3,
                DualConnectionAllowed = true, InternetReachabilityVerified = true,
                ExternalDefaultPreferred = scenario.SelectedIndex != 8,
                SystemProxyActive = scenario.SelectedIndex == 4,
                CleanupPending = scenario.SelectedIndex == 9,
                Ipv6SafetyVerified = false
            };
        }
        private void Simulate()
        {
            if (scenario.SelectedIndex < 0) return;
            bool reverse = mode.SelectedIndex == 3;
            roleSummary.Text = reverse ? "模拟角色：公司 = Wi-Fi；互联网 = 有线。接口身份须绑定，不能只凭名称猜测。" : "模拟角色：公司 = 有线；互联网 = Wi-Fi。只有外网时应等待，不抢默认出口。";
            ModePlan plan = NetworkModePlanner.Build(MakeRequest(), MakeSnapshot());
            StringBuilder text = new StringBuilder();
            text.AppendLine("模拟结果：" + StatusName(plan.Status));
            text.AppendLine("真实网络操作：0（本程序没有网络执行器）");
            foreach (string item in plan.Blockers) text.AppendLine("停止原因：" + item);
            foreach (string item in plan.Warnings) text.AppendLine("提醒：" + item);
            foreach (PlanIntent item in plan.Intents)
                text.AppendLine("计划（未执行）：" + IntentName(item.Kind) + " / " + item.Destination + " → " + (item.InterfaceId == "sim-corporate" ? "公司内网口" : "互联网口") + "\r\n  " + item.Description);
            text.AppendLine("下一阶段必须先确认现场双网能共存，再进行可回滚的真实接入测试。");
            result.Text = text.ToString();
        }
        private static string IntentName(string kind)
        {
            switch (kind)
            {
                case "ProtectInternetDefault": return "保护互联网默认出口";
                case "InternetDnsFallback": return "普通域名使用外网 DNS";
                case "CorporateDnsHostRoute": return "公司 DNS 经公司口访问";
                case "CorporateIpRoute": return "指定 IP 走公司网";
                case "InternetIpRoute": return "指定 IP 走外网";
                case "CorporateSplitDns": return "公司域名解析";
                case "InternetSplitDns": return "互联网域名解析";
                case "CorporateDynamicIpRoute": return "域名解析后走公司口";
                case "InternetDynamicIpRoute": return "域名解析后走外网口";
                case "CorporateCidrRoute": return "指定公司网段";
                default: return kind;
            }
        }
        private static string StatusName(string status)
        {
            switch (status)
            {
                case "NormalPreserved": return "普通模式，不接管系统";
                case "FastLinkPreserved": return "保留原 FastLink 分流；此预览不启停它";
                case "Waiting": return "等待内网接口，不应改当前外网";
                case "ReadyForSimulation": return "纯逻辑条件通过，尚未做真实网络验收";
                default: return "条件未满足，不应切换";
            }
        }
        private void ExplainHost()
        {
            ModeRequest request = MakeRequest();
            NetworkSnapshot snapshot = MakeSnapshot();
            ModePlan plan = NetworkModePlanner.Build(request, snapshot);
            RouteDecision choice = RuleRouter.Decide(request.Rules, host.Text, answers.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList(), request, snapshot);
            result.AppendText("\r\n命中演示（输入的 IP 仅为假设）：" + (choice.Blocked ? "拒绝" : choice.Target == RuleTarget.Corporate ? "公司内网" : choice.Target == RuleTarget.Internet ? "互联网" : "待确认") + "；" + choice.Reason + "\r\n");
            if (!plan.CanSimulate) result.AppendText("整机模式条件尚未满足，上述命中不表示任何连接已生效。\r\n");
        }
    }
}
