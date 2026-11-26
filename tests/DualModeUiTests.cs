using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using FastLinkDirectTool.Modes;
namespace FastLinkDirectTool.Tests
{
    internal static class DualModeUiTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private sealed class FakeSession : IDualModeUiSession
        {
            internal bool Active, Armed, Pending, FailStop, NullArm;
            internal int Arms, Starts, Stops, Validations, Reviews, Archives;
            internal bool ReviewAllowed, ArchiveFails;
            internal ModeRequest LastRequest;
            internal Action BeforeArm;
            internal Func<DualOperationResult> StopBehavior;
            internal Func<DualOperationResult> ValidateBehavior;
            internal bool LastStopApproval;
            public bool IsActive { get { return Active; } }
            public bool IsArmed { get { return Armed; } }
            public bool HasPending { get { return Pending; } }
            private DualOperationResult Result(bool ok) { return new DualOperationResult { Success=ok, Pending=Pending, Message=ok?"fake success":"fake retained cleanup" }; }
            public DualOperationResult ValidateCurrent(){Validations++;return ValidateBehavior==null?Result(!Pending):ValidateBehavior();}
            public DualReconciliationReport ReviewStaleSession(){Reviews++;return new DualReconciliationReport{CanArchive=ReviewAllowed,HasJournal=true,Message="fake read-only review"};}
            public DualOperationResult ArchiveStaleSession(bool allowed){if(!allowed)throw new Exception("missing archive approval");Archives++;if(!ArchiveFails)Pending=false;return Result(!ArchiveFails);}
            public DualOperationResult Arm(ModeRequest request,bool allowed){if(!allowed)throw new Exception("missing permission");LastRequest=request;Arms++;if(BeforeArm!=null)BeforeArm();if(NullArm)return null;Armed=true;return Result(true);}
            public DualOperationResult Start(ModeRequest request,bool allowed){if(!allowed)throw new Exception("missing permission");LastRequest=request;Starts++;Armed=Active=true;return Result(true);}
            public DualOperationResult Stop(bool allowEgressChange){Interlocked.Increment(ref Stops);LastStopApproval=allowEgressChange;if(StopBehavior!=null)return StopBehavior();if(FailStop){Pending=true;return Result(false);}Active=Armed=Pending=false;return Result(true);}
        }
        private static void Pump(Func<bool> done)
        {
            Stopwatch watch=Stopwatch.StartNew();
            while(!done()&&watch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}
            Application.DoEvents();if(!done())throw new Exception("UI callback timed out");
        }
        // Create an invisible HWND for BeginInvoke, but never Show/activate a
        // window on the user's desktop. Invoke only the isolated preview setup.
        private static void HiddenShow(Form form)
        {
            form.ShowInTaskbar=false;IntPtr handle=form.Handle;
            typeof(Form).GetMethod("OnShown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{EventArgs.Empty});Application.DoEvents();
            // Explicit fixture classification simulates the user's review. The
            // shipping migration must not embed company-specific allowlists.
            var dual=form as DualModeForm;
            if(dual!=null&&Field<bool>(dual,"injectedPreviewSession"))
                foreach(DataGridViewRow row in Controls(form).OfType<DataGridView>().Single().Rows)
                    if(!row.IsNewRow&&Convert.ToString(row.Cells[1].Value)=="待确认")row.Cells[1].Value="公司内网";
        }
        // Retain semantic aliases so the pre-existing cleanup cases exercise
        // the same actions after the daily UI labels have been simplified.
        private static Button Button(Form form,string text)
        {
            if(text=="启用双网分流")text="开始分流";
            if(text=="停用 / 恢复清理")text="停止分流";
            return Controls(form).OfType<Button>().Single(b=>b.Text==text);
        }
        private static void Click(Button button){if(button.Enabled)typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(button,new object[]{EventArgs.Empty});}
        private static void Click(Form form,string text){Click(Button(form,text));}
        private static void SessionCases(List<string> source)
        {
            FakeSession fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);
                foreach(CheckBox b in Controls(form).OfType<CheckBox>()) b.Checked=!b.Text.StartsWith("公司接口");
                Click(form,"准备公司与外网");
                Pump(()=>fake.Arms==1&&Button(form,"检查状态").Enabled);
                if(fake.Starts!=0)throw new Exception("Preparation started full mode");
                Click(form,"启用双网分流");Application.DoEvents();
                if(fake.Starts!=0)throw new Exception("Missing admission gate accepted");
                Controls(form).OfType<CheckBox>().Single(b=>b.Text.StartsWith("公司接口")).Checked=true;
                Click(form,"启用双网分流");Pump(()=>fake.Starts==1&&Button(form,"检查状态").Enabled);
                fake.FailStop=true;Click(form,"退出助手");Pump(()=>fake.Stops==1&&Button(form,"检查状态").Enabled);
                if(form.IsDisposed||form.RequestedApplicationExit||!form.HasOwnedState)throw new Exception("Failed cleanup falsely closed UI");
                fake.FailStop=false;Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(!form.RequestedApplicationExit||form.RequestedLegacyMode||fake.Stops!=2)throw new Exception("Safe exit handoff incorrect");
            }
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);
                var mode=Controls(form).OfType<ComboBox>().Single(c=>c.Items.Count==3&&c.Items[0] is string);
                mode.SelectedIndex=0;Application.DoEvents();
                if(fake.Arms!=0||fake.Starts!=0)throw new Exception("Mode selection activated network");
                Click(form,"切换到模式一");Pump(()=>form.IsDisposed);
                if(!form.RequestedLegacyMode||form.RequestedApplicationExit||fake.Starts!=0)throw new Exception("Legacy handoff activated or exited incorrectly");
            }
            fake=new FakeSession{Armed=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);form.Close();Application.DoEvents();
                if(form.IsDisposed||form.RequestedApplicationExit||fake.Stops!=0||!TrayState(form))throw new Exception("Window X cleaned instead of collecting to tray");
                Invoke(form,"RestoreFromTray");Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(!form.RequestedApplicationExit||fake.Stops!=1)throw new Exception("Explicit exit did not retain unified cleanup");
            }
        }
        private static int ExitCases(List<string> source)
        {
            int passed=0;
            FakeSession fake=new FakeSession{FailStop=true};int confirmations=0;
            using(var form=new DualModeForm(source,delegate{},true,fake,delegate{confirmations++;return false;}))
            {
                HiddenShow(form);Click(form,"停用 / 恢复清理");Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=0||confirmations!=0||!form.RequestedApplicationExit)throw new Exception("Clean exit called cleanup or asked unnecessary approval");
            }
            passed++;Console.WriteLine("PASS clean state closes without cleanup or approval");
            fake=new FakeSession{Armed=true};bool approve=false;confirmations=0;
            using(var form=new DualModeForm(source,delegate{},true,fake,delegate{confirmations++;return approve;}))
            {
                HiddenShow(form);Click(form,"退出助手");Application.DoEvents();
                if(form.IsDisposed||fake.Stops!=0||!Button(form,"检查状态").Enabled)throw new Exception("Cancelled exit changed network or stranded UI");
                approve=true;Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=1||!fake.LastStopApproval||confirmations!=2)throw new Exception("Approval was not scoped to each stop");
            }
            passed++;Console.WriteLine("PASS cancelled confirmation restores controls and explicit retry grants only this stop");
            fake=new FakeSession{Armed=true,Pending=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);
                for(int i=0;i<5;i++)
                { typeof(DualModeForm).GetMethod("RefreshReadOnly",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);Pump(()=>!Field<bool>(form,"working")); }
                if(fake.Stops!=0||fake.Validations!=5||!Button(form,"检查状态").Enabled||!Controls(form).OfType<TextBox>().Any(b=>b.Text.Contains("分流已暂停")&&b.Text.Contains("fake retained cleanup")))throw new Exception("Pending state repeatedly cleaned, hid its stored reason, or disabled UI");
            }
            passed++;Console.WriteLine("PASS pending state never schedules automatic cleanup");
            fake=new FakeSession{Armed=true,Active=true};
            fake.ValidateBehavior=delegate{return new DualOperationResult{Success=false,Pending=false,State="EgressConfirmationRequired",Message="fixture requires explicit approval"};};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);MethodInfo refresh=typeof(DualModeForm).GetMethod("RefreshReadOnly",BindingFlags.NonPublic|BindingFlags.Instance);
                refresh.Invoke(form,null);Pump(()=>fake.Validations==1&&Button(form,"检查状态").Enabled);
                for(int i=0;i<5;i++)refresh.Invoke(form,null);
                if(fake.Validations!=1||fake.Stops!=0||!Controls(form).OfType<TextBox>().Any(b=>b.Text.Contains("已暂停自动重试")))throw new Exception("Non-pending failure repeatedly retried");
            }
            passed++;Console.WriteLine("PASS rejected automatic operation pauses even without pending flag");
            foreach(string failure in new[]{"null","throw","pending"})
            {
                fake=new FakeSession{Armed=true};
                fake.StopBehavior=delegate{if(failure=="throw")throw new Exception("fixture stop error");return failure=="null"?null:new DualOperationResult{Success=true,Pending=true,Message="fixture contradictory result"};};
                using(var form=new DualModeForm(source,delegate{throw new Exception("fixture log error");},true,fake))
                {
                    HiddenShow(form);Click(form,"退出助手");Pump(()=>fake.Stops==1&&Button(form,"检查状态").Enabled);
                    if(form.IsDisposed||form.RequestedApplicationExit||!Button(form,"退出助手").Enabled)throw new Exception("Malformed cleanup result falsely exited or stranded controls");
                    fake.StopBehavior=null;Click(form,"退出助手");Pump(()=>form.IsDisposed);
                    if(fake.Stops!=2)throw new Exception("Explicit retry did not run exactly once");
                }
                passed++;Console.WriteLine("PASS cleanup "+failure+" result stays retryable and log errors do not freeze UI");
            }
            fake=new FakeSession{Armed=true};
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                fake.StopBehavior=delegate{entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture wait timeout");fake.Pending=true;return new DualOperationResult{Success=false,Pending=true,Message="fixture retained dependency"};};
                HiddenShow(form);Click(form,"退出助手");
                try
                {
                    Pump(()=>entered.WaitOne(0));Click(form,"退出助手");form.Close();Application.DoEvents();
                    if(fake.Stops!=1||form.IsDisposed||!Button(form,"退出助手").Enabled)throw new Exception("Busy exit repeated cleanup or disabled all feedback");
                }
                finally{release.Set();}
                Pump(()=>Button(form,"检查状态").Enabled);
                if(fake.Stops!=1||form.IsDisposed)throw new Exception("Failed exit retried without user action");
                fake.StopBehavior=null;Click(form,"退出助手");Pump(()=>form.IsDisposed);
            }
            passed++;Console.WriteLine("PASS blocked exit stays responsive and duplicate close requests do not repeat cleanup");
            fake=new FakeSession{Armed=true};
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                fake.StopBehavior=delegate{entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture wait timeout");fake.Active=fake.Armed=fake.Pending=false;return new DualOperationResult{Success=true,Message="fixture clean"};};
                HiddenShow(form);Click(form,"停用 / 恢复清理");
                try{Pump(()=>entered.WaitOne(0));Click(form,"退出助手");Application.DoEvents();if(fake.Stops!=1)throw new Exception("Queued exit started overlapping cleanup");}
                finally{release.Set();}
                Pump(()=>form.IsDisposed);
                if(fake.Stops!=1||!form.RequestedApplicationExit)throw new Exception("Queued exit cleaned an already clean session twice");
            }
            passed++;Console.WriteLine("PASS exit during successful manual cleanup waits once then closes without second cleanup");
            fake=new FakeSession{NullArm=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);foreach(CheckBox box in Controls(form).OfType<CheckBox>())box.Checked=!box.Text.StartsWith("公司接口");
                Click(form,"准备公司与外网");Pump(()=>fake.Arms==1&&Button(form,"检查状态").Enabled);
                if(fake.Starts!=0||!Button(form,"退出助手").Enabled)throw new Exception("Missing operation result stranded controls");
                Click(form,"退出助手");Pump(()=>form.IsDisposed);
            }
            passed++;Console.WriteLine("PASS missing operation result restores controls");
            fake=new FakeSession();
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                fake.BeforeArm=delegate{entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture wait timeout");};
                HiddenShow(form);foreach(CheckBox box in Controls(form).OfType<CheckBox>())box.Checked=!box.Text.StartsWith("公司接口");
                Click(form,"准备公司与外网");
                try{Pump(()=>entered.WaitOne(0));Click(form,"退出助手");Application.DoEvents();if(fake.Stops!=0)throw new Exception("Exit overlapped pending prepare");}
                finally{release.Set();}
                Pump(()=>form.IsDisposed);
                if(fake.Arms!=1||fake.Stops!=1||!fake.LastStopApproval)throw new Exception("Queued exit after prepare did not perform exactly one approved cleanup");
            }
            passed++;Console.WriteLine("PASS exit waits for preparation then performs one approved cleanup");
            return passed;
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                List<string> source = new List<string> { "portal.corp.example.test", ".youdao.com", "wiki.corp.example.test", "login.corp.example.test", "issues.corp.example.test" };
                string config = Environment.GetEnvironmentVariable("DUAL_UI_RULES_READONLY");
                if (!string.IsNullOrEmpty(config))
                {
                    if (new FileInfo(config).Length > 1024 * 1024) throw new Exception("Rule fixture too large");
                    var value = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(config));
                    source = ((System.Collections.IEnumerable)value["Rules"]).Cast<string>().ToList();
                }
                Application.EnableVisualStyles();
                using (var form = new DualModeForm(source, delegate { }, true))
                {
                    HiddenShow(form);
                    var all = Controls(form).ToList();
                    if (all.OfType<DataGridView>().Single().Rows.Count != source.Count + 1) throw new Exception("Lost imported rules");
                    if (all.OfType<Button>().Count() != 10) throw new Exception("Missing action buttons");
                    if (!all.OfType<TextBox>().Any(t => t.Text.Contains("未查询或更改真实网络"))) throw new Exception("Preview startup was not isolated");
                    foreach (Button b in all.OfType<Button>()) Click(b);
                    if (!all.OfType<TextBox>().Any(t => t.Text.Contains("未查询或更改真实网络"))) throw new Exception("Preview action changed state");
                    foreach(CheckBox box in all.OfType<CheckBox>()) box.Checked = true;
                    ComboBox mode = all.OfType<ComboBox>().First(c => c.Items.Count > 0 && c.Items[0] is string);
                    if(mode.Items.Count!=3||DualModeForm.SelectedDualMode(1)!=WorkMode.WiredCorporateWifiInternet||DualModeForm.SelectedDualMode(2)!=WorkMode.WifiCorporateWiredInternet)throw new Exception("Mode mapping changed");
                    mode.SelectedIndex = 2;
                    if(all.OfType<CheckBox>().Count(c => c.Checked) != 1) throw new Exception("Network selection reused old confirmations");
                    mode.SelectedIndex = 1;
                    if(args.Length == 1) using(Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.GetFullPath(args[0]), System.Drawing.Imaging.ImageFormat.Png); }
                    form.Close();
                }
                SessionCases(source);
                int exitCases=ExitCases(source)+ModeThreeAndArchiveCases(source)+RememberedPermissionCases(source)+TrayCases(source)+SimplifiedUiCases(source,args.Length==1?args[0]:null)+ExplicitIntentCases(source)+MainRuleEditorCases(source,args.Length==1?args[0]:null)+PreparedValidationCases(source);
                var migrated = DualRuleMigration.Propose(source);
                Console.WriteLine("DUAL_UI_TESTS_OK "+(11+exitCases)+"; rules=" + migrated.Count + "; corporate=" + migrated.Count(r => r.Target == RuleTarget.Corporate) + "; internet=" + migrated.Count(r => r.Target == RuleTarget.Internet) + "; review=" + migrated.Count(r => r.Target == RuleTarget.Review));
                return 0;
            }
            catch(Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        }
        private static int ModeThreeAndArchiveCases(List<string> source)
        {
            int passed=0;
            foreach(string scenario in new[]{"blocked","cancelled","approved","failed"})
            {
                var fake=new FakeSession{Pending=true,ReviewAllowed=scenario!="blocked",ArchiveFails=scenario=="failed"};
                using(var form=new DualModeForm(source,delegate{},true,fake,delegate{return scenario!="cancelled";}))
                {
                    HiddenShow(form);Click(form,"核对旧会话记录");
                    Pump(()=>fake.Reviews==1&&Button(form,"检查状态").Enabled);
                    bool attempted=scenario=="approved"||scenario=="failed";
                    if(fake.Archives!=(attempted?1:0)||fake.Stops!=0||fake.Arms!=0||fake.Starts!=0)
                        throw new Exception("Archive path performed a network action or ignored approval");
                    if(fake.Pending!=(scenario!="approved")||form.IsDisposed)
                        throw new Exception("Archive result incorrectly released ownership or exited");
                }
                passed++;Console.WriteLine("PASS old journal UI "+scenario+" uses read-only review and explicit archive only");
            }
            var modeFake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,modeFake))
            {
                HiddenShow(form);
                var modes=Controls(form).OfType<ComboBox>().Single(c=>c.Items.Count==3&&c.Items[0] is string);
                modes.SelectedIndex=2;
                var corporate=(ComboBox)typeof(DualModeForm).GetField("corporate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                var internet=(ComboBox)typeof(DualModeForm).GetField("internet",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                corporate.SelectedIndex=1;internet.SelectedIndex=0;
                foreach(CheckBox b in Controls(form).OfType<CheckBox>())b.Checked=!b.Text.StartsWith("公司接口");
                Click(form,"准备公司与外网");Pump(()=>modeFake.Arms==1&&Button(form,"检查状态").Enabled);
                if(modeFake.LastRequest.Mode!=WorkMode.WifiCorporateWiredInternet||modeFake.LastRequest.CorporateAdapterId!="22222222-2222-2222-2222-222222222222"||modeFake.LastRequest.InternetAdapterId!="11111111-1111-1111-1111-111111111111"||modeFake.Starts!=0)
                    throw new Exception("Mode three did not preserve the explicitly selected reversed roles");
                if(!Controls(form).OfType<Label>().Any(l=>l.Text.Contains("开始分流")&&l.Text.Contains("准入")))
                    throw new Exception("Start did not visibly explain the current admission assertion");
            }
            passed++;Console.WriteLine("PASS mode three prepares explicit Wi-Fi company and wired Internet roles without enabling");
            return passed;
        }
        private static T Field<T>(DualModeForm form,string name)
        { return (T)typeof(DualModeForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form); }
        private static void Invoke(DualModeForm form,string name,params object[] arguments)
        { typeof(DualModeForm).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,arguments); }
        private static void GrantPreviewPermission(DualModeForm form)
        {
            var profile=Field<DualModeProfile>(form,"profile");var platform=Field<DualNetworkWindowsPlatform>(form,"platform");
            profile.RecordPermission(platform.ScopeIdentity,profile.CorporateAdapterId,profile.InternetAdapterId,
                DateTime.UtcNow,DualPermissionSource.PriorExplicitConversationConsent);
        }
        private static void AssertNoSessionWork(FakeSession fake,string context)
        {
            if(fake.Arms!=0||fake.Starts!=0||fake.Stops!=0||fake.Validations!=0||fake.Reviews!=0||fake.Archives!=0)
                throw new Exception(context+" changed the fake network session");
        }
        private static void CaptureFixture(DualModeForm form,string path,string suffix)
        {
            if(string.IsNullOrEmpty(path))return;
            string target=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),Path.GetFileNameWithoutExtension(path)+suffix+".png");
            using(Bitmap bitmap=new Bitmap(form.Width,form.Height))
            { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(target,System.Drawing.Imaging.ImageFormat.Png); }
        }
        private static int SimplifiedUiCases(List<string> source,string imagePath)
        {
            int passed=0;var fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                // The runner launches this whole fake executable on a separate,
                // non-input desktop. A real visible form makes Visible checks
                // meaningful; it never instantiates the production MainForm.
                form.Opacity=0;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);
                form.Show();Application.DoEvents();
                foreach(DataGridViewRow row in Field<DataGridView>(form,"rules").Rows)
                    if(!row.IsNewRow&&Convert.ToString(row.Cells[1].Value)=="待确认")row.Cells[1].Value="公司内网";
                IntPtr original=form.Handle;
                if(Controls(form).OfType<CheckBox>().Any(b=>b.Visible)||Field<Panel>(form,"settingsPanel").Visible||Field<bool>(form,"settingsExpanded"))
                    throw new Exception("Daily UI exposed a checklist or expanded settings by default");
                if(!Field<Button>(form,"enableAction").Visible||!Field<Button>(form,"stopAction").Visible||!Field<Button>(form,"settingsAction").Visible)
                    throw new Exception("Collapsed UI hid a primary action");
                if(!Field<DataGridView>(form,"rules").Visible||Field<DataGridView>(form,"rules").Parent!=Field<TableLayoutPanel>(form,"rootLayout")||
                    !Field<Button>(form,"addRuleAction").Visible||!Field<Button>(form,"deleteRuleAction").Visible||!Field<Button>(form,"saveAction").Visible)
                    throw new Exception("Home screen did not keep rules and Add/Delete/Save visible");
                if(Field<Button>(form,"saveAction").Text!="保存规则")throw new Exception("Home rule Save label was not updated");
                if(Field<Button>(form,"enableAction").Text!="开始分流"||Field<Button>(form,"stopAction").Text!="停止分流")
                    throw new Exception("Daily action labels were not simplified");
                CaptureFixture(form,imagePath,"-collapsed");
                for(int i=0;i<3;i++)
                {
                    Click(Field<Button>(form,"settingsAction"));Application.DoEvents();
                    if(!Field<Panel>(form,"settingsPanel").Visible||!Field<bool>(form,"settingsExpanded")||!Field<DataGridView>(form,"rules").Visible)
                        throw new Exception("Settings did not expose the existing rule editor");
                    if(Controls(form).OfType<CheckBox>().Any(b=>b.Visible))throw new Exception("Expanded settings reintroduced checklists");
                    if(i==0)CaptureFixture(form,imagePath,"-settings");
                    Click(Field<Button>(form,"settingsAction"));Application.DoEvents();
                    if(Field<Panel>(form,"settingsPanel").Visible||Field<bool>(form,"settingsExpanded")||form.Handle!=original||!Field<DataGridView>(form,"rules").Visible)
                        throw new Exception("Settings collapse rebuilt the window or failed to collapse");
                    AssertNoSessionWork(fake,"Settings toggle");
                }
                if(Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"externalOk").Checked)
                    throw new Exception("Visibility changes fabricated admission or Internet proof");
                passed++;Console.WriteLine("PASS daily home keeps rules Add Delete Save visible while hiding checklists and settings toggles do zero network work");
                // Fake-only fixtures keep the existing runtime gate explicit;
                // no checkbox clicks or real admission/profile IO are used.
                Field<CheckBox>(form,"permitted").Checked=true;
                Field<CheckBox>(form,"admission").Checked=true;
                Field<CheckBox>(form,"externalOk").Checked=true;
                Invoke(form,"RequestFromUi");Invoke(form,"ConfirmExplicitRuleChoice");
                Click(Field<Button>(form,"enableAction"));Pump(()=>fake.Starts==1&&!Field<bool>(form,"working"));
                if(Field<bool>(form,"settingsExpanded")||!form.HasOwnedState||!Field<Button>(form,"stopAction").Visible)
                    throw new Exception("Collapsed Start lost active ownership or hid Stop");
                Click(Field<Button>(form,"stopAction"));Pump(()=>fake.Stops==1&&!Field<bool>(form,"working"));
                if(fake.Arms!=0||fake.Starts!=1||fake.Stops!=1||form.HasOwnedState||form.IsDisposed)
                    throw new Exception("Collapsed Start/Stop did not use exactly one fake operation each");
                passed++;Console.WriteLine("PASS collapsed Start and Stop remain usable with the existing fake runtime gate");
            }
            foreach(string bad in new[]{"review","invalid"})
            {
                fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake))
                {
                    HiddenShow(form);var grid=Field<DataGridView>(form,"rules");
                    if(bad=="review")grid.Rows[0].Cells[1].Value="待确认";else grid.Rows[0].Cells[0].Value="not a valid host";
                    Invoke(form,"RequestFromUi");bool rejected=false;
                    try{Invoke(form,"ConfirmExplicitRuleChoice");}catch(TargetInvocationException error){if(error.InnerException is InvalidOperationException)rejected=true;else throw;}
                    if(!rejected||Field<CheckBox>(form,"confirmed").Checked||Field<DualModeProfile>(form,"profile").RulesConfirmed)
                        throw new Exception("Explicit rule confirmation accepted "+bad+" classification");
                    AssertNoSessionWork(fake,"Rejected rule confirmation");
                }
                passed++;Console.WriteLine("PASS explicit rule confirmation rejects "+bad+" without profile or network writes");
            }
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);Invoke(form,"RequestFromUi");Invoke(form,"ConfirmExplicitRuleChoice");
                if(!Field<CheckBox>(form,"confirmed").Checked||!Field<DualModeProfile>(form,"profile").RulesConfirmed||Field<CheckBox>(form,"admission").Checked)
                    throw new Exception("Valid rule confirmation failed or implied admission");
                Field<DataGridView>(form,"rules").Rows[0].Cells[1].Value="互联网";
                if(Field<CheckBox>(form,"confirmed").Checked)throw new Exception("Rule edit silently retained prior acknowledgement");
                Invoke(form,"RequestFromUi");Invoke(form,"ConfirmExplicitRuleChoice");
                if(!Field<CheckBox>(form,"confirmed").Checked)throw new Exception("Explicit reconfirmation of edited classified rules failed");
                Click(Field<Button>(form,"saveAction"));AssertNoSessionWork(fake,"Offline Save");
            }
            passed++;Console.WriteLine("PASS classified rules can be explicitly reconfirmed and offline Save never writes or runs the session");
            return passed;
        }
        private static string GridDraft(DataGridView grid)
        {
            return string.Join("\n",grid.Rows.Cast<DataGridViewRow>().Where(r=>!r.IsNewRow)
                .Select(r=>Convert.ToString(r.Cells[0].Value)+"|"+Convert.ToString(r.Cells[1].Value)).ToArray());
        }
        private static void EditRuleText(DataGridView grid,int index,string value)
        {
            grid.CurrentCell=grid.Rows[index].Cells[0];
            if(!grid.BeginEdit(true)||!(grid.EditingControl is DataGridViewTextBoxEditingControl))
                throw new Exception("Fixture could not begin a real rule text cell edit");
            grid.EditingControl.Text=value;
            if(!grid.EndEdit())throw new Exception("Fixture could not commit a rule text cell edit");
            Application.DoEvents();
            if(Convert.ToString(grid.Rows[index].Cells[0].Value)!=value)throw new Exception("Rule cell did not retain the typed value");
        }
        private static void EditRuleTarget(DataGridView grid,int index,string value)
        {
            grid.CurrentCell=grid.Rows[index].Cells[1];
            if(!grid.BeginEdit(true)||!(grid.EditingControl is DataGridViewComboBoxEditingControl))
                throw new Exception("Fixture could not begin the target selector edit");
            ((DataGridViewComboBoxEditingControl)grid.EditingControl).SelectedItem=value;
            grid.NotifyCurrentCellDirty(true);
            if(!grid.EndEdit())throw new Exception("Fixture could not commit the target selector edit");
            Application.DoEvents();
            if(Convert.ToString(grid.Rows[index].Cells[1].Value)!=value)throw new Exception("Rule target selector lost its selected value");
        }
        private static int MainRuleEditorCases(List<string> source,string imagePath)
        {
            int passed=0;string originalSource=string.Join("\n",source.ToArray());var fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate{return true;},true))
            {
                form.Opacity=0;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);
                form.Show();Application.DoEvents();
                var grid=Field<DataGridView>(form,"rules");
                foreach(DataGridViewRow row in grid.Rows)if(!row.IsNewRow&&Convert.ToString(row.Cells[1].Value)=="待确认")row.Cells[1].Value="公司内网";
                Click(Field<Button>(form,"saveAction"));
                if(!Field<CheckBox>(form,"confirmed").Checked)throw new Exception("Initial fake rules were not saved/acknowledged");
                int originalCount=grid.Rows.Count;string originalDraft=GridDraft(grid);
                Click(Field<Button>(form,"addRuleAction"));
                int added=grid.CurrentCell.RowIndex;
                if(grid.Rows.Count!=originalCount+1||Convert.ToString(grid.Rows[added].Cells[0].Value)!=""||Convert.ToString(grid.Rows[added].Cells[1].Value)!="待确认"||Field<CheckBox>(form,"confirmed").Checked)
                    throw new Exception("Add did not append an unclassified blank rule and revoke acknowledgement");
                EditRuleText(grid,added,"new-service.example.test");
                if(Convert.ToString(grid.Rows[added].Cells[1].Value)!="待确认")throw new Exception("Typing a hostname inferred its network role");
                Click(Field<Button>(form,"saveAction"));
                if(Field<CheckBox>(form,"confirmed").Checked||!Field<TextBox>(form,"details").Text.Contains("保存失败"))
                    throw new Exception("Save accepted a new unclassified rule");
                AssertNoSessionWork(fake,"Add and rejected offline Save");
                passed++;Console.WriteLine("PASS home Add creates a blank Review rule and typing does not infer its route or start the session");
                var status=Field<Label>(form,"operationStatus");
                if(Field<bool>(form,"settingsExpanded")||!status.Visible||status.Parent!=Field<TableLayoutPanel>(form,"rootLayout")||!status.Text.Contains("保存失败"))
                    throw new Exception("Unclassified Save failure was not visible on the collapsed home screen");
                Application.DoEvents();CaptureFixture(form,imagePath,"-save-failed");
                passed++;Console.WriteLine("PASS collapsed home exposes unclassified Save failure without opening settings or logs");

                EditRuleTarget(grid,added,"互联网");Click(Field<Button>(form,"saveAction"));
                var profile=Field<DualModeProfile>(form,"profile");
                if(!Field<CheckBox>(form,"confirmed").Checked||!profile.Rules.Any(r=>r.Pattern=="new-service.example.test"&&r.Target==RuleTarget.Internet))
                    throw new Exception("Home Save lost the explicit new target classification");
                if(Field<bool>(form,"settingsExpanded")||!status.Visible||!status.Text.Contains("离线确认规则分类")||status.Text.Contains("保存失败"))
                    throw new Exception("Successful offline Save did not replace the prior failure on the collapsed home screen");
                Application.DoEvents();CaptureFixture(form,imagePath,"-save-confirmed");
                passed++;Console.WriteLine("PASS collapsed home visibly replaces the Save failure with the next successful rule confirmation");
                EditRuleText(grid,added,"renamed-service.example.test");
                if(Field<CheckBox>(form,"confirmed").Checked)throw new Exception("Text editing retained old classification acknowledgement");
                EditRuleTarget(grid,added,"公司内网");Click(Field<Button>(form,"saveAction"));
                if(!profile.Rules.Any(r=>r.Pattern=="renamed-service.example.test"&&r.Target==RuleTarget.Corporate)||profile.Rules.Any(r=>r.Pattern=="new-service.example.test"))
                    throw new Exception("Text/target edits were not captured by explicit Save");
                AssertNoSessionWork(fake,"Rule text target editing and offline Save");
                passed++;Console.WriteLine("PASS home text and combo edits persist only in the fake draft when explicitly saved");

                grid.ClearSelection();grid.Rows[added].Selected=true;Click(Field<Button>(form,"deleteRuleAction"));
                if(grid.Rows.Count!=originalCount||GridDraft(grid)!=originalDraft||Field<CheckBox>(form,"confirmed").Checked)
                    throw new Exception("Delete selected did not remove only the added rule or revoke acknowledgement");
                Click(Field<Button>(form,"saveAction"));
                if(profile.Rules.Any(r=>r.Pattern=="renamed-service.example.test"))throw new Exception("Deleted row survived draft Save");
                AssertNoSessionWork(fake,"Delete selected");
                passed++;Console.WriteLine("PASS home Delete removes only the selected rule and explicit Save captures deletion");

                string untouched=Convert.ToString(grid.Rows[1].Cells[0].Value);
                grid.ClearSelection();grid.Rows[0].Selected=true;grid.Rows[2].Selected=true;
                Click(Field<Button>(form,"deleteRuleAction"));
                if(grid.Rows.Count!=originalCount-2||!grid.Rows.Cast<DataGridViewRow>().Any(r=>!r.IsNewRow&&Convert.ToString(r.Cells[0].Value)==untouched))
                    throw new Exception("Multi-row deletion removed an unselected row or shifted indices incorrectly");
                string afterDelete=GridDraft(grid);grid.ClearSelection();Click(Field<Button>(form,"deleteRuleAction"));
                if(GridDraft(grid)!=afterDelete||!Field<TextBox>(form,"details").Text.Contains("请先选中"))throw new Exception("Empty selection Delete changed the draft");
                AssertNoSessionWork(fake,"Multi-row and empty selection Delete");
                passed++;Console.WriteLine("PASS Delete supports disjoint selected rows and leaves an empty selection unchanged");

                int typed=grid.NewRowIndex;EditRuleText(grid,typed,"typed-row.example.test");
                if(Convert.ToString(grid.Rows[typed].Cells[1].Value)!="待确认")throw new Exception("Direct new-row editing did not default to Review");
                Click(Field<Button>(form,"saveAction"));
                if(Field<CheckBox>(form,"confirmed").Checked)throw new Exception("Save accepted an unclassified directly typed row");
                AssertNoSessionWork(fake,"Direct new-row typing");
                passed++;Console.WriteLine("PASS direct new-row typing defaults to Review just like the Add button");
                if(string.Join("\n",source.ToArray())!=originalSource)throw new Exception("Rule editor changed the original legacy rule source list");
            }
            foreach(string state in new[]{"busy","running","armed","pending","legacy","closing"})
            {
                fake=new FakeSession{Active=state=="running",Armed=state=="running"||state=="armed",Pending=state=="pending"};
                using(var form=new DualModeForm(source,delegate{},true,fake))
                {
                    HiddenShow(form);var grid=Field<DataGridView>(form,"rules");
                    if(state=="legacy")Field<ComboBox>(form,"mode").SelectedIndex=0;
                    if(state=="busy")Invoke(form,"SetWorking",true);
                    if(state=="closing")typeof(DualModeForm).GetField("closing",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,true);
                    string before=GridDraft(grid);int beforeCount=grid.Rows.Count;
                    if(state!="closing"&&(!grid.ReadOnly||grid.AllowUserToAddRows||grid.AllowUserToDeleteRows||Field<Button>(form,"addRuleAction").Enabled||Field<Button>(form,"deleteRuleAction").Enabled))
                        throw new Exception("Rule editing gate was open in state "+state);
                    if(state=="busy"&&Field<Button>(form,"saveAction").Enabled)throw new Exception("Save stayed enabled during pending work");
                    grid.ClearSelection();if(grid.Rows.Count>0)grid.Rows[0].Selected=true;
                    Click(Field<Button>(form,"addRuleAction"));Click(Field<Button>(form,"deleteRuleAction"));
                    // Direct invocation also checks the action's own defensive
                    // gate, not merely the button's current Enabled property.
                    Invoke(form,"AddRule");Invoke(form,"DeleteSelectedRules");
                    if(GridDraft(grid)!=before||grid.Rows.Count!=beforeCount)throw new Exception("Protected rule actions changed the draft in state "+state);
                    if(state!="closing")
                    { grid.CurrentCell=grid.Rows[0].Cells[0];if(grid.BeginEdit(true))throw new Exception("Read-only cell accepted editing in state "+state); }
                    if(state=="legacy"&&!Field<Label>(form,"ruleHint").Text.Contains("双网草稿"))throw new Exception("Legacy mode obscured that the shown table is the dual-mode draft");
                    AssertNoSessionWork(fake,"Protected editing "+state);
                }
                passed++;Console.WriteLine("PASS rule Add Delete and cell editing are protected without session work state="+state);
            }
            return passed;
        }
        private static int ExplicitIntentCases(List<string> source)
        {
            int passed=0;
            foreach(bool allowAfterExit in new[]{false,true})
            {
                var fake=new FakeSession();DualModeForm current=null;int prompts=0;
                using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate
                {
                    prompts++;Click(Field<Button>(current,"exitAction"));
                    if(!Field<bool>(current,"closeQueued"))throw new Exception("Permission fixture failed to queue explicit Exit");
                    return allowAfterExit;
                },true))
                {
                    current=form;HiddenShow(form);Click(Field<Button>(form,"enableAction"));Application.DoEvents();
                    if(prompts!=1||fake.Arms!=0||fake.Starts!=0||fake.Stops!=0||!form.RequestedApplicationExit||!form.IsDisposed)
                        throw new Exception("Permission response after queued Exit started the session or failed to close safely allow="+allowAfterExit);
                }
                passed++;Console.WriteLine("PASS permission response after queued Exit never starts a network session allow="+allowAfterExit);
            }
            foreach(bool allow in new[]{false,true})
            {
                int prompts=0;var fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate{prompts++;return allow;},true))
                {
                    HiddenShow(form);
                    if(Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"confirmed").Checked)
                        throw new Exception("Opening an explicit-intent fixture fabricated acknowledgement");
                    Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");Click(Field<Button>(form,"settingsAction"));Click(Field<Button>(form,"settingsAction"));
                    if(prompts!=0||Field<CheckBox>(form,"admission").Checked)throw new Exception("Visibility requested company permission or admission");
                    Click(Field<Button>(form,"enableAction"));
                    if(allow)Pump(()=>fake.Starts==1&&!Field<bool>(form,"working"));else Application.DoEvents();
                    if(prompts!=1||fake.Starts!=(allow?1:0)||fake.Arms!=0||fake.Stops!=0)
                        throw new Exception("Explicit Start did not scope company permission to the action");
                    var platform=Field<DualNetworkWindowsPlatform>(form,"platform");
                    if(Field<CheckBox>(form,"admission").Checked!=allow||platform.CorporateAdmissionConfirmed!=allow||Field<CheckBox>(form,"permitted").Checked!=allow)
                        throw new Exception("Start approval/cancellation fabricated or lost admission intent");
                    if(!Field<CheckBox>(form,"confirmed").Checked||!Field<DualModeProfile>(form,"profile").RulesConfirmed||platform.InternetConfirmed||Field<CheckBox>(form,"externalOk").Checked)
                        throw new Exception("Explicit Start missed rule acknowledgement or invented Internet proof");
                    if(allow)
                    {
                        Click(Field<Button>(form,"stopAction"));Pump(()=>fake.Stops==1&&!Field<bool>(form,"working"));
                        Click(Field<Button>(form,"enableAction"));Pump(()=>fake.Starts==2&&!Field<bool>(form,"working"));
                        if(prompts!=1)throw new Exception("Repeated Start for the same in-memory scope repeated company permission");
                    }
                }
                passed++;Console.WriteLine("PASS explicit Start "+(allow?"accepts":"cancels")+" company permission without hidden checklist or invented Internet verification");
            }
            foreach(bool enable in new[]{false,true})
            {
                int prompts=0;var fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate{prompts++;return true;},true))
                {
                    HiddenShow(form);Field<DataGridView>(form,"rules").Rows[0].Cells[1].Value="待确认";
                    Click(Field<Button>(form,enable?"enableAction":"prepareAction"));Application.DoEvents();
                    if(prompts!=0||!Field<bool>(form,"settingsExpanded")||Field<CheckBox>(form,"confirmed").Checked||Field<CheckBox>(form,"admission").Checked)
                        throw new Exception("Unclassified explicit action asked permission or claimed admission");
                    AssertNoSessionWork(fake,"Unclassified explicit action");
                }
                passed++;Console.WriteLine("PASS unclassified "+(enable?"Start":"Prepare")+" opens settings without a permission prompt or session work");
            }
            {
                int prompts=0;var fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate{prompts++;return true;},true))
                {
                    HiddenShow(form);Click(Field<Button>(form,"prepareAction"));Pump(()=>fake.Arms==1&&!Field<bool>(form,"working"));
                    if(prompts!=1||fake.Starts!=0||Field<CheckBox>(form,"admission").Checked||Field<DualNetworkWindowsPlatform>(form,"platform").CorporateAdmissionConfirmed)
                        throw new Exception("Explicit Prepare was mistaken for admission/full Start");
                }
                passed++;Console.WriteLine("PASS explicit Prepare confirms rules and scope but never claims current company admission");
            }
            foreach(bool preview in new[]{false,true})
            {
                bool rejected=false;
                try{using(var form=new DualModeForm(source,delegate{},preview,null,null,false,delegate{return true;},true)){} }
                catch(ArgumentException){rejected=true;}
                if(!rejected)throw new Exception("Intent injection escaped the offline fake-only boundary");
                passed++;Console.WriteLine("PASS explicit intent injection rejected without an offline fake session preview="+preview);
            }
            foreach(bool classified in new[]{false,true})
            {
                int prompts=0;var fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake,null,false,delegate{prompts++;return true;},true))
                {
                    HiddenShow(form);
                    if(!classified)Field<DataGridView>(form,"rules").Rows[0].Cells[1].Value="待确认";
                    Click(Field<Button>(form,"saveAction"));Application.DoEvents();
                    if(Field<CheckBox>(form,"confirmed").Checked!=classified||Field<DualModeProfile>(form,"profile").RulesConfirmed!=classified)
                        throw new Exception("Save did not acknowledge only fully classified rules");
                    if(prompts!=0||Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"permitted").Checked||Field<CheckBox>(form,"externalOk").Checked)
                        throw new Exception("Saving rules claimed permission, admission or live Internet proof");
                    if(!Controls(form).OfType<TextBox>().Any(t=>t.Text.Contains(classified?"离线确认规则分类":"保存失败")))
                        throw new Exception("Offline Save did not exercise the explicit rule confirmation branch");
                    AssertNoSessionWork(fake,"Explicit offline Save");
                }
                passed++;Console.WriteLine("PASS explicit Save classified="+classified+" checks rules without profile IO or admission assertion");
            }
            foreach(bool lockBusy in new[]{false,true})
            {
                var fake=new FakeSession{Active=true,Armed=true,Pending=lockBusy};
                fake.ValidateBehavior=delegate{return new DualOperationResult{Success=false,Pending=false,State="ReadDeferred",Message="fake transient read busy"};};
                using(var form=new DualModeForm(source,delegate{},true,fake))
                {
                    HiddenShow(form);Field<CheckBox>(form,"admission").Checked=Field<CheckBox>(form,"externalOk").Checked=true;
                    for(int i=0;i<2;i++){Invoke(form,"RefreshReadOnly");Pump(()=>!Field<bool>(form,"working"));}
                    if(fake.Validations!=2||fake.Stops!=0||Field<bool>(form,"automaticChecksPaused")||!Field<CheckBox>(form,"admission").Checked||!Field<CheckBox>(form,"externalOk").Checked)
                        throw new Exception("ReadDeferred falsely revoked topology confirmation or paused the UI");
                }
                passed++;Console.WriteLine("PASS transient ReadDeferred does not masquerade as topology change or clean the network lockBusy="+lockBusy);
            }
            {
                var fake=new FakeSession{Active=true,Armed=true,Pending=true};
                using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                using(var form=new DualModeForm(source,delegate{},true,fake))
                {
                    HiddenShow(form);
                    fake.ValidateBehavior=delegate
                    { entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture pending read timeout");return new DualOperationResult{Success=false,Pending=true,State="InternetProtected",Message="original fixture failure retained"}; };
                    Stopwatch watch=Stopwatch.StartNew();Invoke(form,"RefreshReadOnly");
                    try
                    {
                        if(watch.ElapsedMilliseconds>500)throw new Exception("Pending status synchronously blocked the UI");
                        Pump(()=>entered.WaitOne(0));Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");
                        if(fake.Validations!=1||fake.Stops!=0||form.IsDisposed||!Field<bool>(form,"working"))
                            throw new Exception("Pending read blocked tray UI or initiated cleanup");
                    }
                    finally{release.Set();}
                    Pump(()=>!Field<bool>(form,"working"));
                    if(!Field<TextBox>(form,"details").Text.Contains("original fixture failure retained"))
                        throw new Exception("Pending asynchronous status lost the original failure evidence");
                }
                passed++;Console.WriteLine("PASS pending status uses asynchronous fake read and leaves tray operations responsive");
            }
            return passed;
        }
        private static int PreparedValidationCases(List<string> source)
        {
            int passed = 0;
            {
                var fake = new FakeSession { Armed = true };
                using (var form = new DualModeForm(source, delegate { }, true, fake))
                {
                    HiddenShow(form); GrantPreviewPermission(form);
                    var profile = Field<DualModeProfile>(form, "profile");
                    var snapshot = new NetworkSnapshot { Adapters = new List<AdapterSnapshot> {
                        new AdapterSnapshot { Id = profile.CorporateAdapterId, Name = "fixture corporate connected", Kind = LinkKind.Ethernet,
                            IsPhysical = true, IsUp = true, Address = "192.0.2.20", PrefixLength = 24, Gateway = "192.0.2.1" },
                        new AdapterSnapshot { Id = profile.InternetAdapterId, Name = "fixture external", Kind = LinkKind.Wifi,
                            IsPhysical = true, IsUp = true, Address = "198.51.100.20", PrefixLength = 24, Gateway = "198.51.100.1" } } };
                    fake.ValidateBehavior = delegate { return new DualOperationResult { Success = true, State = "Armed",
                        Message = "fixture verified connected adapters", VerifiedPreparationSnapshot = snapshot }; };
                    typeof(DualModeForm).GetField("confirmedTopology", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, "old disconnected snapshot");
                    Field<CheckBox>(form, "admission").Checked = Field<CheckBox>(form, "externalOk").Checked = true;
                    Invoke(form, "RefreshReadOnly"); Pump(() => !Field<bool>(form, "working"));
                    if (!Field<Label>(form, "connectionSummary").Text.Contains("192.0.2.20") ||
                        Field<CheckBox>(form, "admission").Checked || Field<CheckBox>(form, "externalOk").Checked ||
                        fake.Validations != 1 || fake.Starts != 0 || fake.Arms != 0 || fake.Stops != 0)
                        throw new Exception("Prepared display did not refresh verified link state or reused admission after topology change");
                }
                passed++; Console.WriteLine("PASS prepared verified snapshot updates interface display without granting admission or changing network");
            }
            foreach (string outcome in new[] { "verified", "deferred", "drift", "error" })
            {
                var fake = new FakeSession { Armed = true };
                fake.ValidateBehavior = delegate
                {
                    if (outcome == "error") throw new InvalidOperationException("fixture prepared read failed");
                    if (outcome == "drift") { fake.Armed = false; fake.Pending = true; }
                    return new DualOperationResult { Success = outcome == "verified", Pending = outcome == "drift",
                        State = outcome == "deferred" ? "ReadDeferred" : outcome == "drift" ? "PreparedInvalidated" : "Armed",
                        Message = outcome == "verified" ? "fixture prepared metrics verified" : "fixture preparation unverified " + outcome };
                };
                using (var form = new DualModeForm(source, delegate { }, true, fake))
                {
                    HiddenShow(form);
                    Invoke(form, "RefreshReadOnly"); Pump(() => !Field<bool>(form, "working"));
                    if (fake.Validations != 1 || fake.Arms != 0 || fake.Starts != 0 || fake.Stops != 0)
                        throw new Exception("Prepared status bypassed runtime validation or performed automatic network work");
                    string summary = Field<Label>(form, "modeNotice").Text;
                    if (summary.Contains("已准备") != (outcome == "verified"))
                        throw new Exception("Prepared success label did not reflect read verification: " + outcome);
                    bool shouldPause = outcome == "drift" || outcome == "error";
                    if (Field<bool>(form, "automaticChecksPaused") != shouldPause)
                        throw new Exception("Prepared read failure used the wrong retry policy: " + outcome);
                    if (outcome == "deferred")
                    {
                        if (!summary.Contains("待核验") || !Field<Label>(form, "operationStatus").Text.Contains("unverified"))
                            throw new Exception("Deferred preparation warning is not visible on the home page");
                        fake.ValidateBehavior = delegate { return new DualOperationResult { Success = true, State = "Armed", Message = "fixture verified after retry" }; };
                        Invoke(form, "RefreshReadOnly"); Pump(() => !Field<bool>(form, "working"));
                        if (!Field<Label>(form, "modeNotice").Text.Contains("已准备") || fake.Validations != 2 || fake.Stops != 0)
                            throw new Exception("Successful prepared retry did not clear only the UI warning");
                    }
                }
                passed++; Console.WriteLine("PASS prepared UI validation " + outcome + " is read-only and never claims unverified success");
            }
            {
                var fake = new FakeSession { Armed = true, Pending = true };
                fake.ValidateBehavior = delegate { return new DualOperationResult { Success = false, State = "ReadDeferred", Message = "fixture prepared lock busy" }; };
                using (var form = new DualModeForm(source, delegate { }, true, fake))
                {
                    HiddenShow(form); Invoke(form, "RefreshReadOnly"); Pump(() => !Field<bool>(form, "working"));
                    string status = Field<Label>(form, "modeNotice").Text;
                    if (fake.Validations != 1 || fake.Stops != 0 || Field<bool>(form, "automaticChecksPaused") ||
                        status.Contains("已准备") || status.Contains("需要检查恢复记录") || !status.Contains("待核验"))
                        throw new Exception("Conservative lock-busy gate cleaned or claimed prepared success");
                }
                passed++; Console.WriteLine("PASS prepared lock-busy gate remains asynchronous with no cleanup or success claim");
            }
            return passed;
        }
        private static int RememberedPermissionCases(List<string> source)
        {
            int passed=0;
            var fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);GrantPreviewPermission(form);
                var platform=Field<DualNetworkWindowsPlatform>(form,"platform");
                Field<CheckBox>(form,"admission").Checked=Field<CheckBox>(form,"externalOk").Checked=true;
                platform.CorporateAdmissionConfirmed=platform.InternetConfirmed=true;
                Invoke(form,"ResetNetworkConfirmations");
                if(!Field<CheckBox>(form,"permitted").Checked||!platform.DualConnectionConfirmed)
                    throw new Exception("Scoped company permission was revoked by a network reset");
                if(Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"externalOk").Checked||platform.CorporateAdmissionConfirmed||platform.InternetConfirmed)
                    throw new Exception("Remembered permission retained dynamic admission or Internet facts");
                if(fake.Arms!=0||fake.Starts!=0||fake.Stops!=0)throw new Exception("Confirmation reset performed a network operation");
            }
            passed++;Console.WriteLine("PASS scoped saved permission survives reset while admission and Internet facts are revoked");
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);GrantPreviewPermission(form);
                Field<ComboBox>(form,"corporate").SelectedIndex=1;Field<ComboBox>(form,"internet").SelectedIndex=0;
                if(!Field<CheckBox>(form,"permitted").Checked||Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"externalOk").Checked)
                    throw new Exception("Role reversal lost same-pair permission or reused dynamic facts");
            }
            passed++;Console.WriteLine("PASS reversed roles retain only the same hardware-pair company permission");
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);GrantPreviewPermission(form);
                var profile=Field<DualModeProfile>(form,"profile");var platform=Field<DualNetworkWindowsPlatform>(form,"platform");
                string oldCorp=profile.CorporateAdapterId,oldInternet=profile.InternetAdapterId,third="33333333-3333-3333-3333-333333333333";
                Invoke(form,"UpdateChoices",new NetworkSnapshot{Adapters=new List<AdapterSnapshot>{
                    new AdapterSnapshot{Id=oldCorp,Name="fixture company",Kind=LinkKind.Ethernet,IsPhysical=true},
                    new AdapterSnapshot{Id=oldInternet,Name="fixture internet",Kind=LinkKind.Wifi,IsPhysical=true},
                    new AdapterSnapshot{Id=third,Name="fixture replacement",Kind=LinkKind.Ethernet,IsPhysical=true}}});
                Field<CheckBox>(form,"admission").Checked=Field<CheckBox>(form,"externalOk").Checked=true;
                Field<ComboBox>(form,"corporate").SelectedIndex=2;
                if(Field<CheckBox>(form,"permitted").Checked||platform.DualConnectionConfirmed||Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"externalOk").Checked)
                    throw new Exception("New GUID inherited old permission or dynamic confirmations");
                if(!profile.IsPermissionConfirmed(platform.ScopeIdentity,oldCorp,oldInternet)||profile.IsPermissionConfirmed(platform.ScopeIdentity,third,oldInternet))
                    throw new Exception("Role selection expanded or destroyed the original explicit scope");
            }
            passed++;Console.WriteLine("PASS replacement adapter invalidates active permission without expanding the saved pair");
            foreach(string grant in new[]{"none","foreign-scope","foreign-pair"})
            {
                fake=new FakeSession();
                using(var form=new DualModeForm(source,delegate{},true,fake))
                {
                    HiddenShow(form);var profile=Field<DualModeProfile>(form,"profile");var platform=Field<DualNetworkWindowsPlatform>(form,"platform");
                    if(grant!="none")profile.RecordPermission(grant=="foreign-scope"?"different-machine":platform.ScopeIdentity,
                        profile.CorporateAdapterId,grant=="foreign-pair"?"33333333-3333-3333-3333-333333333333":profile.InternetAdapterId,DateTime.UtcNow,DualPermissionSource.UserAction);
                    Field<CheckBox>(form,"confirmed").Checked=Field<CheckBox>(form,"permitted").Checked=true;
                    Field<CheckBox>(form,"externalOk").Checked=true;
                    Invoke(form,"Change",false,true);Application.DoEvents();
                    if(fake.Arms!=0||fake.Starts!=0||fake.Stops!=0||!Button(form,"检查状态").Enabled||Field<bool>(form,"working"))
                        throw new Exception("Explicit launch preparation bypassed persisted permission scope: "+grant);
                }
                passed++;Console.WriteLine("PASS one-shot launch preparation rejects "+grant+" permission despite checked UI boxes");
            }
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);GrantPreviewPermission(form);Invoke(form,"ResetNetworkConfirmations");
                Field<CheckBox>(form,"confirmed").Checked=true;
                Invoke(form,"Change",false,true);Pump(()=>fake.Arms==1&&Button(form,"检查状态").Enabled);
                if(fake.Starts!=0||Field<CheckBox>(form,"admission").Checked||Field<CheckBox>(form,"externalOk").Checked)
                    throw new Exception("Fake launch preparation enabled full mode or fabricated dynamic proof");
            }
            passed++;Console.WriteLine("PASS scoped launch preparation invokes only fake Arm and does not invent MOKA admission");
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);GrantPreviewPermission(form);Invoke(form,"ResetNetworkConfirmations");
                Field<CheckBox>(form,"confirmed").Checked=true;
                Field<TextBox>(form,"cidrs").Text="10.0.0.0/8";
                if(Field<CheckBox>(form,"confirmed").Checked||!Field<CheckBox>(form,"permitted").Checked)
                    throw new Exception("CIDR edit retained rule approval or revoked unrelated company permission");
                Invoke(form,"RequestFromUi");
                if(Field<DualModeProfile>(form,"profile").RulesConfirmed)throw new Exception("CIDR rule invalidation not captured in profile draft");
                Click(form,"准备公司与外网");Application.DoEvents();
                if(fake.Arms!=0||fake.Starts!=0)throw new Exception("Changed CIDRs prepared without renewed rules review");
            }
            passed++;Console.WriteLine("PASS CIDR changes revoke rules approval but retain separate scoped company permission");
            using(var form=new DualModeForm(source,delegate{},true,null,null,true))
            {
                HiddenShow(form);
                if(Field<bool>(form,"working")||!Controls(form).OfType<TextBox>().Any(t=>t.Text.Contains("未查询或更改真实网络")))
                    throw new Exception("Preview launch flag escaped isolated startup");
            }
            passed++;Console.WriteLine("PASS preview prepare-on-open remains isolated without storage or real network commands");
            return passed;
        }
        private static bool TrayState(DualModeForm form)
        {
            PropertyInfo property=typeof(DualModeForm).GetProperty("IsMinimizedToTray",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(property==null)throw new Exception("Missing tray-state API");
            return (bool)property.GetValue(form,null);
        }
        private static void AssertPreviewHasNoTrayIcon(DualModeForm form)
        {
            foreach(FieldInfo field in typeof(DualModeForm).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                if(typeof(NotifyIcon).IsAssignableFrom(field.FieldType)&&field.GetValue(form)!=null)
                    throw new Exception("Offline preview created a real NotifyIcon");
        }
        private static int TrayCases(List<string> source)
        {
            int passed=0;var fake=new FakeSession{Armed=true,Active=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);AssertPreviewHasNoTrayIcon(form);
                for(int i=0;i<4;i++)
                {
                    Invoke(form,"MinimizeToTray");Application.DoEvents();
                    if(!TrayState(form)||form.ShowInTaskbar||form.IsDisposed||form.RequestedApplicationExit||form.RequestedLegacyMode)
                        throw new Exception("Minimize escaped tray state or requested close");
                    Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");Application.DoEvents();
                    if(TrayState(form)||form.ShowInTaskbar||form.WindowState!=FormWindowState.Normal)
                        throw new Exception("Tray restore did not restore ordinary window state");
                    if(fake.Arms!=0||fake.Starts!=0||fake.Stops!=0||fake.Validations!=0||!form.HasOwnedState)
                        throw new Exception("Repeated tray operations changed the active session");
                    AssertPreviewHasNoTrayIcon(form);
                }
            }
            passed++;Console.WriteLine("PASS repeated tray minimize and restore never arm start stop or validate network");
            fake=new FakeSession{Armed=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");form.Close();Application.DoEvents();
                if(fake.Stops!=0||form.IsDisposed||form.RequestedApplicationExit||form.RequestedLegacyMode||!TrayState(form))
                    throw new Exception("Window X did not retain the running session in tray");
                Invoke(form,"RestoreFromTray");Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=1||!form.RequestedApplicationExit)throw new Exception("Explicit exit after window X failed to clean");
            }
            passed++;Console.WriteLine("PASS window X retains session in tray and only explicit exit performs verified cleanup");
            fake=new FakeSession{Armed=true,FailStop=true};
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                HiddenShow(form);Invoke(form,"MinimizeToTray");Click(form,"退出助手");Pump(()=>fake.Stops==1&&Button(form,"检查状态").Enabled);
                if(form.IsDisposed||form.RequestedApplicationExit||!form.HasOwnedState)
                    throw new Exception("Tray state bypassed failed red-button cleanup");
                Invoke(form,"RestoreFromTray");
                if(TrayState(form)||form.ShowInTaskbar||!Button(form,"退出助手").Enabled)
                    throw new Exception("Failed cleanup left the window inaccessible");
                fake.FailStop=false;Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=2||!form.RequestedApplicationExit)throw new Exception("Explicit retry did not safely exit");
            }
            passed++;Console.WriteLine("PASS red exit still retains failed cleanup and can be restored and retried");
            fake=new FakeSession();
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                fake.BeforeArm=delegate{entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture tray prepare timeout");};
                HiddenShow(form);foreach(CheckBox box in Controls(form).OfType<CheckBox>())box.Checked=!box.Text.StartsWith("公司接口");
                Click(form,"准备公司与外网");
                try
                {
                    Pump(()=>entered.WaitOne(0));Click(form,"退出助手");
                    if(!Field<bool>(form,"closeQueued"))throw new Exception("Fixture did not queue exit");
                    form.Close();Application.DoEvents();
                    if(Field<bool>(form,"closeQueued")||fake.Stops!=0||form.IsDisposed)
                        throw new Exception("Window X did not cancel only the not-yet-started queued exit");
                }
                finally{release.Set();}
                Pump(()=>Button(form,"检查状态").Enabled);
                if(fake.Arms!=1||fake.Stops!=0||form.IsDisposed||!form.HasOwnedState||!TrayState(form))
                    throw new Exception("Preparation completion unexpectedly executed the cancelled exit");
                Invoke(form,"RestoreFromTray");Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=1)throw new Exception("Later explicit exit was not one cleanup");
            }
            passed++;Console.WriteLine("PASS window X during pending work cancels only an exit that has not started");
            fake=new FakeSession{Armed=true};
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(var form=new DualModeForm(source,delegate{},true,fake))
            {
                fake.StopBehavior=delegate{entered.Set();if(!release.WaitOne(4000))throw new Exception("fixture tray cleanup timeout");fake.Active=fake.Armed=fake.Pending=false;return new DualOperationResult{Success=true,Message="fake clean"};};
                HiddenShow(form);Click(form,"退出助手");
                try
                {
                    Pump(()=>entered.WaitOne(0));Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");
                    if(fake.Stops!=1||!Field<bool>(form,"closing")||form.IsDisposed)
                        throw new Exception("Minimize cancelled or restarted an already-running cleanup");
                }
                finally{release.Set();}
                Pump(()=>form.IsDisposed);
                if(fake.Stops!=1||!form.RequestedApplicationExit)throw new Exception("Running cleanup lost its safe exit completion");
            }
            passed++;Console.WriteLine("PASS tray changes cannot cancel or duplicate cleanup already in progress");
            fake=new FakeSession();
            using(var form=new DualModeForm(source,delegate{},true,fake))
            using(var ticks=new System.Windows.Forms.Timer())
            {
                // Real modal loop, but the fixture is fully transparent and has
                // no taskbar/tray icon. Never Show the running production helper.
                form.Opacity=0;form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;
                form.Location=new Point(-30000,-30000);
                bool modalReturned=false,observed=false;int stage=0,shown=0;Exception failure=null;
                form.Shown+=delegate{shown++;};
                ticks.Interval=60;
                ticks.Tick+=delegate
                {
                    try
                    {
                        if(stage++==0)
                        {
                            if(!form.Visible)throw new Exception("Fixture modal never became visible");
                            Invoke(form,"MinimizeToTray");
                            return;
                        }
                        if(modalReturned||!form.Visible||!TrayState(form)||form.ShowInTaskbar||shown!=1)
                            throw new Exception("Minimize hid/restarted the dialog or ended its modal loop");
                        AssertPreviewHasNoTrayIcon(form);observed=true;ticks.Stop();Click(form,"退出助手");
                    }
                    catch(Exception error){failure=error;ticks.Stop();form.Dispose();}
                };
                ticks.Start();form.ShowDialog();modalReturned=true;ticks.Stop();
                if(failure!=null)throw failure;
                if(!observed||shown!=1||fake.Arms!=0||fake.Starts!=0||fake.Stops!=0||!form.RequestedApplicationExit)
                    throw new Exception("Transparent modal minimize did not retain its loop until explicit safe exit");
            }
            passed++;Console.WriteLine("PASS transparent modal stays visible and running while minimized without repeated Shown or network work");
            Exception nativeFailure=null;
            foreach(bool active in new[]{false,true})
            foreach(bool busy in new[]{false,true})
            {
                try{NativeOwnedModalMinimizeCase(source,active,busy);passed++;}
                catch(Exception error){if(nativeFailure==null)nativeFailure=error;}
            }
            if(nativeFailure!=null)throw nativeFailure;
            return passed;
        }
        private static void NativeOwnedModalMinimizeCase(List<string> source,bool active,bool busy)
        {
            var fake=new FakeSession{Armed=active,Active=active};
            var trace=new List<string>();var watch=Stopwatch.StartNew();
            using(var owner=new Form())
            using(var form=new DualModeForm(source,delegate{},true,fake))
            using(var ticks=new System.Windows.Forms.Timer())
            {
                owner.Text="Offline tray owner fixture";owner.Opacity=0;
                owner.StartPosition=FormStartPosition.Manual;owner.Location=new Point(-30000,-30000);
                owner.ShowInTaskbar=true;
                if(form.ShowInTaskbar||form.IsHandleCreated)throw new Exception("Modal taskbar style was not fixed before handle creation");
                form.Opacity=0;form.StartPosition=FormStartPosition.Manual;
                form.Location=new Point(-30000,-30000);
                int stage=0,shown=0,rounds=0,created=0,destroyed=0;IntPtr originalHandle=IntPtr.Zero;
                bool observed=false,explicitExit=false;Exception failure=null;
                Action<string> record=delegate(string e){trace.Add(watch.ElapsedMilliseconds+":"+e+" visible="+form.Visible+" state="+form.WindowState+" taskbar="+form.ShowInTaskbar+" result="+form.DialogResult+" exit="+form.RequestedApplicationExit+" stops="+fake.Stops);};
                owner.HandleCreated+=delegate{record("owner.HandleCreated");};
                owner.HandleDestroyed+=delegate{record("owner.HandleDestroyed");};
                owner.FormClosing+=delegate(object s,FormClosingEventArgs e){record("owner.FormClosing:"+e.CloseReason);};
                form.HandleCreated+=delegate{created++;record("child.HandleCreated");};
                form.HandleDestroyed+=delegate{destroyed++;record("child.HandleDestroyed");};
                form.Shown+=delegate{shown++;record("child.Shown");};
                form.FormClosing+=delegate(object s,FormClosingEventArgs e){record("child.FormClosing:"+e.CloseReason+":cancel="+e.Cancel);};
                form.FormClosed+=delegate{record("child.FormClosed");};
                ticks.Interval=80;
                ticks.Tick+=delegate
                {
                    try
                    {
                        if(watch.ElapsedMilliseconds>5000)throw new Exception("Native minimize fixture timed out");
                        if(owner.Visible||!form.Visible||form.ShowInTaskbar||shown!=1||created!=1||destroyed!=0||form.DialogResult!=DialogResult.None||form.RequestedApplicationExit||fake.Stops!=0||Field<bool>(form,"closeQueued"))
                            throw new Exception("Native tray cycle rebuilt, closed or cleaned the real owned modal");
                        if(originalHandle==IntPtr.Zero)originalHandle=form.Handle;
                        if(form.Handle!=originalHandle)throw new Exception("Native tray cycle changed the modal handle");
                        if(stage==0)
                        {
                            if(busy&&rounds==0)Invoke(form,"SetWorking",true);
                            stage=1;
                            record("send.SC_MINIMIZE");
                            SendMessage(form.Handle,0x0112,new IntPtr(0xF020),IntPtr.Zero);
                            record("return.SC_MINIMIZE");
                            return;
                        }
                        if(stage==1)
                        {
                            if(!TrayState(form)||form.WindowState!=FormWindowState.Minimized)
                                throw new Exception("Native minimize ended or cleaned the real owned modal");
                            stage=2;Invoke(form,"RestoreFromTray");record("restore");return;
                        }
                        if(stage==2)
                        {
                            if(TrayState(form)||form.WindowState!=FormWindowState.Normal)throw new Exception("Native minimized modal could not restore");
                            stage=3;record("send.SC_CLOSE");
                            SendMessage(form.Handle,0x0112,new IntPtr(0xF060),IntPtr.Zero);
                            record("return.SC_CLOSE");return;
                        }
                        if(stage==3)
                        {
                            if(!TrayState(form)||form.WindowState!=FormWindowState.Minimized)throw new Exception("Window X did not collect the modal to tray");
                            stage=4;Invoke(form,"RestoreFromTray");record("restore.after.X");return;
                        }
                        if(TrayState(form)||form.WindowState!=FormWindowState.Normal)
                            throw new Exception("Native minimized modal did not survive handle recreation and restore");
                        rounds++;
                        if(rounds<3){stage=0;return;}
                        if(fake.Arms!=0||fake.Starts!=0||fake.Validations!=0||form.HasOwnedState!=active)
                            throw new Exception("Native tray cycles modified the runtime session");
                        if(busy)Invoke(form,"SetWorking",false);
                        AssertPreviewHasNoTrayIcon(form);observed=true;explicitExit=true;ticks.Stop();
                        record("explicit.exit");Click(form,"退出助手");
                    }
                    catch(Exception error){failure=error;ticks.Stop();form.Dispose();}
                };
                // Match the production host's UI-only operation: its legacy
                // owner is hidden before ShowDialog and remains the owner.
                // Do not construct MainForm: that would initialize real runtime.
                owner.Show();Application.DoEvents();owner.Hide();
                ticks.Start();DialogResult result=form.ShowDialog(owner);ticks.Stop();record("ShowDialog.return:"+result);
                if(!observed||!explicitExit||failure!=null)
                {
                    Console.WriteLine("NATIVE_MODAL_TRACE active="+active+" busy="+busy+" "+string.Join(" || ",trace.ToArray()));
                    throw failure??new Exception("Native minimize unexpectedly returned before explicit exit; active="+active);
                }
                if(owner.Visible||fake.Stops!=(active?1:0)||!form.RequestedApplicationExit)
                    throw new Exception("Native modal final explicit cleanup mismatch");
                Console.WriteLine("NATIVE_MODAL_TRACE active="+active+" busy="+busy+" "+string.Join(" || ",trace.ToArray()));
            }
            Console.WriteLine("PASS hidden owner and three native minimize X restore cycles retain modal active="+active+" busy="+busy);
        }
        private static IEnumerable<Control> Controls(Control parent)
        { foreach(Control child in parent.Controls) { yield return child; foreach(Control desc in Controls(child)) yield return desc; } }
    }
}
