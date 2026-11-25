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
            public DualOperationResult Start(ModeRequest request,bool allowed){if(!allowed)throw new Exception("missing permission");Starts++;Armed=Active=true;return Result(true);}
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
        private static Button Button(Form form,string text){return Controls(form).OfType<Button>().Single(b=>b.Text==text);}
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
                HiddenShow(form);form.Close();Pump(()=>form.IsDisposed);
                if(!form.RequestedApplicationExit||fake.Stops!=1)throw new Exception("Window X bypassed unified cleanup");
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
                for(int i=0;i<5;i++)typeof(DualModeForm).GetMethod("RefreshReadOnly",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
                if(fake.Stops!=0||!Button(form,"检查状态").Enabled||!Controls(form).OfType<TextBox>().Any(b=>b.Text.Contains("自动检查与自动清理已暂停")))throw new Exception("Pending state repeatedly cleaned or disabled UI");
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
                try{Pump(()=>entered.WaitOne(0));Click(form,"退出助手");form.Close();Application.DoEvents();if(fake.Stops!=1)throw new Exception("Queued exit started overlapping cleanup");}
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
                try{Pump(()=>entered.WaitOne(0));Click(form,"退出助手");form.Close();Application.DoEvents();if(fake.Stops!=0)throw new Exception("Exit overlapped pending prepare");}
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
                    if (all.OfType<Button>().Count() != 7) throw new Exception("Missing action buttons");
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
                int exitCases=ExitCases(source)+ModeThreeAndArchiveCases(source)+RememberedPermissionCases(source)+TrayCases(source);
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
                if(!Controls(form).OfType<Label>().Any(l=>l.Text.Contains("IPv4")&&l.Text.Contains("IPv6")))
                    throw new Exception("IPv4-only company split limitation was not visible");
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
                HiddenShow(form);Invoke(form,"MinimizeToTray");Invoke(form,"RestoreFromTray");form.Close();Pump(()=>form.IsDisposed);
                if(fake.Stops!=1||!form.RequestedApplicationExit||form.RequestedLegacyMode)
                    throw new Exception("Window X became minimize instead of safe exit");
            }
            passed++;Console.WriteLine("PASS window X still performs verified cleanup after tray restoration");
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
                    Invoke(form,"MinimizeToTray");
                    if(Field<bool>(form,"closeQueued")||fake.Stops!=0||form.IsDisposed)
                        throw new Exception("Minimize did not cancel only the not-yet-started queued exit");
                }
                finally{release.Set();}
                Pump(()=>Button(form,"检查状态").Enabled);
                if(fake.Arms!=1||fake.Stops!=0||form.IsDisposed||!form.HasOwnedState||!TrayState(form))
                    throw new Exception("Preparation completion unexpectedly executed the cancelled exit");
                Invoke(form,"RestoreFromTray");Click(form,"退出助手");Pump(()=>form.IsDisposed);
                if(fake.Stops!=1)throw new Exception("Later explicit exit was not one cleanup");
            }
            passed++;Console.WriteLine("PASS minimizing during work cancels only an exit that has not started");
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
            {
                try{NativeOwnedModalMinimizeCase(source,active);passed++;}
                catch(Exception error){if(nativeFailure==null)nativeFailure=error;}
            }
            if(nativeFailure!=null)throw nativeFailure;
            return passed;
        }
        private static void NativeOwnedModalMinimizeCase(List<string> source,bool active)
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
                        if(!form.Visible||form.ShowInTaskbar||shown!=1||created!=1||destroyed!=0||form.DialogResult!=DialogResult.None||form.RequestedApplicationExit||fake.Stops!=0)
                            throw new Exception("Native tray cycle rebuilt, closed or cleaned the real owned modal");
                        if(originalHandle==IntPtr.Zero)originalHandle=form.Handle;
                        if(form.Handle!=originalHandle)throw new Exception("Native tray cycle changed the modal handle");
                        if(stage==0)
                        {
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
                        if(TrayState(form)||form.WindowState!=FormWindowState.Normal)
                            throw new Exception("Native minimized modal did not survive handle recreation and restore");
                        rounds++;
                        if(rounds<3){stage=0;return;}
                        if(fake.Arms!=0||fake.Starts!=0||fake.Validations!=0||form.HasOwnedState!=active)
                            throw new Exception("Native tray cycles modified the runtime session");
                        AssertPreviewHasNoTrayIcon(form);observed=true;explicitExit=true;ticks.Stop();
                        record("explicit.exit");Click(form,"退出助手");
                    }
                    catch(Exception error){failure=error;ticks.Stop();form.Dispose();}
                };
                owner.Show();Application.DoEvents();owner.ShowInTaskbar=false;
                ticks.Start();DialogResult result=form.ShowDialog(owner);ticks.Stop();record("ShowDialog.return:"+result);
                if(!observed||!explicitExit||failure!=null)
                {
                    Console.WriteLine("NATIVE_MODAL_TRACE active="+active+" "+string.Join(" || ",trace.ToArray()));
                    throw failure??new Exception("Native minimize unexpectedly returned before explicit exit; active="+active);
                }
                if(fake.Stops!=(active?1:0)||!form.RequestedApplicationExit)
                    throw new Exception("Native modal final explicit cleanup mismatch");
                Console.WriteLine("NATIVE_MODAL_TRACE active="+active+" "+string.Join(" || ",trace.ToArray()));
            }
            Console.WriteLine("PASS three native SC_MINIMIZE and restore cycles retain owned modal handle active="+active);
        }
        private static IEnumerable<Control> Controls(Control parent)
        { foreach(Control child in parent.Controls) { yield return child; foreach(Control desc in Controls(child)) yield return desc; } }
    }
}
