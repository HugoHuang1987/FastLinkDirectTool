using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using FastLinkDirectTool.Modes;
namespace FastLinkDirectTool.Tests
{
    internal static class DualRuleMigrationTests
    {
        private static int count;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        public static int Main()
        {
            try
            {
                foreach(string s in new[]{"portal.corp.example.test",".corp.example.test","download.corp.example.test",".wiki.example.test","login.corp.example.test","issues.corp.example.test","192.0.2.156"})
                    Check(DualRuleMigration.Propose(new[]{s})[0].Target == RuleTarget.Review, "first import requires corporate review " + s);
                foreach(string s in new[]{".youdao.com",".huawei.com",".teams.microsoft.com",".net.anydesk.com",".zoom.us","api.typeless.com"})
                    Check(DualRuleMigration.Propose(new[]{s})[0].Target == RuleTarget.Internet, "confirmed internet " + s);
                foreach(string s in new[]{"10.77.9.1","unclassified.example.test","corp.example.test.evil.test","other.example.test"})
                    Check(DualRuleMigration.Propose(new[]{s})[0].Target == RuleTarget.Review, "unknown must not be guessed " + s);
                var inputs = new List<string>{"PORTAL.CORP.EXAMPLE.TEST", "portal.corp.example.test", "https://portal.corp.example.test/path"};
                Check(DualRuleMigration.Propose(inputs).Count == 1, "dedup uses shared normalized parser");
                Check(inputs[0] == "PORTAL.CORP.EXAMPLE.TEST" && inputs.Count == 3, "migration leaves source untouched");
                var p = new DualModeProfile { Rules = DualRuleMigration.Propose(new[]{"portal.corp.example.test"}) };
                var r = p.ToRequest(); r.Rules[0].Pattern = "elsewhere.test";
                Check(p.Rules[0].Pattern == "portal.corp.example.test", "request clone cannot rewrite profile");
                Check(!p.RulesConfirmed, "migration requires explicit review");
                const string scope = "fixture-machine-and-user";
                const string first = "11111111-1111-1111-1111-111111111111";
                const string second = "abcdefab-cdef-abcd-efab-cdefabcdefab";
                const string third = "33333333-3333-3333-3333-333333333333";
                DateTime approved = DateTime.UtcNow.AddMinutes(-1);
                Check(p.PermissionConfirmation == null && !p.IsPermissionConfirmed(scope, first, second), "missing permission defaults to denial");
                p.RecordPermission(scope, second.ToUpperInvariant(), "{" + first + "}", approved, DualPermissionSource.UserAction);
                Check(p.IsPermissionConfirmed(scope, first, second) && p.PermissionConfirmation.InterfaceId1 == first && p.PermissionConfirmation.InterfaceId2 == second, "explicit permission stores canonical unordered GUID pair");
                p.Mode = WorkMode.WifiCorporateWiredInternet;
                Check(p.IsPermissionConfirmed(scope, second, first), "same pair with reversed roles retains company permission");
                Check(!p.IsPermissionConfirmed("another-machine-or-user", first, second), "permission cannot move to another scope");
                Check(!p.IsPermissionConfirmed(scope, first, third) && !p.IsPermissionConfirmed(scope, third, second), "permission does not expand to another adapter");
                Check(!p.IsPermissionConfirmed(scope, first, first) && !p.IsPermissionConfirmed(scope, "bad-guid", second) && !p.IsPermissionConfirmed(scope, Guid.Empty.ToString(), second), "invalid or duplicate requested GUID rejects permission");
                var serializer = new JavaScriptSerializer();
                var roundTrip = serializer.Deserialize<DualModeProfile>(serializer.Serialize(p));
                Check(roundTrip.IsPermissionConfirmed(scope, second, first) && roundTrip.PermissionConfirmation.Source == DualPermissionSource.UserAction, "permission JSON round trip preserves binding and explicit source");
                Check(!serializer.Serialize(p.PermissionConfirmation).Contains("InternetConfirmed") && !serializer.Serialize(p.PermissionConfirmation).Contains("Admission"), "permission stores no dynamic external or MOKA confirmation");
                p.RecordPermission(scope, first, second, approved, DualPermissionSource.PriorExplicitConversationConsent);
                Check(p.IsPermissionConfirmed(scope, first, second) && p.PermissionConfirmation.Source == DualPermissionSource.PriorExplicitConversationConsent, "prior explicit conversation consent is an auditable source");
                foreach (Action invalid in new Action[] {
                    delegate { p.RecordPermission(scope, first, first, approved, DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission(scope, "bad-guid", second, approved, DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission("", first, second, approved, DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission(scope, first, second, DateTime.MinValue, DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission(scope, first, second, DateTime.SpecifyKind(approved, DateTimeKind.Unspecified), DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission(scope, first, second, DateTime.UtcNow.AddDays(1), DualPermissionSource.UserAction); },
                    delegate { p.RecordPermission(scope, first, second, approved, DualPermissionSource.None); },
                    delegate { p.RecordPermission(scope, first, second, approved, (DualPermissionSource)99); }
                })
                {
                    string before = serializer.Serialize(p.PermissionConfirmation); bool failed = false;
                    try { invalid(); } catch (ArgumentException) { failed = true; }
                    Check(failed && serializer.Serialize(p.PermissionConfirmation) == before, "invalid record request preserves prior explicit permission");
                }
                foreach (Action<DualPermissionConfirmation> corrupt in new Action<DualPermissionConfirmation>[] {
                    delegate(DualPermissionConfirmation x) { x.InterfaceId2 = x.InterfaceId1; },
                    delegate(DualPermissionConfirmation x) { x.InterfaceId1 = "not-a-guid"; },
                    delegate(DualPermissionConfirmation x) { x.Source = DualPermissionSource.None; },
                    delegate(DualPermissionConfirmation x) { x.ConfirmedAtUtc = DateTime.MinValue; },
                    delegate(DualPermissionConfirmation x) { x.ScopeIdentity = null; }
                })
                {
                    var malformed = serializer.Deserialize<DualModeProfile>(serializer.Serialize(p)); corrupt(malformed.PermissionConfirmation);
                    Check(!malformed.IsPermissionConfirmed(scope, first, second), "malformed persisted permission fails closed");
                }
                p.RulesConfirmed = true; p.ClearPermission();
                Check(!p.IsPermissionConfirmed(scope, first, second) && p.PermissionConfirmation == null && p.RulesConfirmed && p.Rules.Count == 1, "clear permission preserves rules and their separate review");
                var legacy = serializer.Deserialize<DualModeProfile>("{\"Mode\":2,\"Rules\":[],\"CorporateCidrs\":[],\"RulesConfirmed\":true}");
                Check(legacy.RulesConfirmed && !legacy.IsPermissionConfirmed(scope, first, second), "legacy rules review cannot manufacture company permission");
                string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                string leaf = "dual-profile-fixture-" + Guid.NewGuid().ToString("N");
                string directory = Path.Combine(temporaryRoot, leaf); Directory.CreateDirectory(directory);
                try
                {
                    var saved = new DualModeProfile { Mode = WorkMode.WifiCorporateWiredInternet, CorporateAdapterId = first,
                        InternetAdapterId = second, RulesConfirmed = true, CorporateCidrs = new List<string> { "10.44.0.0/24" },
                        Rules = Enumerable.Range(0,46).Select(i => new ModeRule { Pattern = "service" + i + ".corp.example.test", Target = RuleTarget.Corporate }).ToList() };
                    saved.RecordPermission(scope, first, second, approved, DualPermissionSource.UserAction);
                    string path = Path.Combine(directory, "dual-profile.json");
                    File.WriteAllText(path, serializer.Serialize(saved), new UTF8Encoding(false)); byte[] before = File.ReadAllBytes(path);
                    // Null would make Propose reject input: existing-file load must
                    // never enumerate legacy rules or reclassify saved targets.
                    DualModeProfile loaded = DualProfileStore.LoadFromPath(path, new string[] { null });
                    Check(loaded.Rules.Count == 46 && loaded.Rules.All(x => x.Target == RuleTarget.Corporate) &&
                        loaded.Rules.Select(x => x.Pattern).SequenceEqual(saved.Rules.Select(x => x.Pattern)), "saved 46 corporate rules are never reclassified by first-import migration");
                    Check(loaded.RulesConfirmed && loaded.Mode == saved.Mode && loaded.IsPermissionConfirmed(scope, first, second) &&
                        loaded.CorporateCidrs.SequenceEqual(saved.CorporateCidrs), "saved profile retains review permission roles and CIDRs");
                    Check(before.SequenceEqual(File.ReadAllBytes(path)), "saved profile readback does not rewrite its bytes");
                    string missing = Path.Combine(directory, "missing-profile.json");
                    DualModeProfile imported = DualProfileStore.LoadFromPath(missing, new[] { "portal.corp.example.test", ".youdao.com" });
                    Check(imported.Rules[0].Target == RuleTarget.Review && imported.Rules[1].Target == RuleTarget.Internet && !imported.RulesConfirmed &&
                        imported.PermissionConfirmation == null && !File.Exists(missing), "only absent profile proposes public services and leaves other targets for review without writing");
                }
                finally
                {
                    if (Path.GetDirectoryName(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar) != temporaryRoot || Path.GetFileName(directory) != leaf) throw new Exception("Unsafe fixture cleanup path");
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                Console.WriteLine("DUAL_RULE_MIGRATION_TESTS_OK " + count); return 0;
            }
            catch(Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
        }
    }
}
