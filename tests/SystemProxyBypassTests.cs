using System;
using System.Collections.Generic;
using System.Linq;

namespace FastLinkDirectTool.Tests
{
    internal static class SystemProxyBypassTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Reject(Action action)
        {
            bool failed = false;
            try { action(); } catch (InvalidOperationException) { failed = true; }
            Assert(failed, "Expected a bounded failure without silent success.");
        }
        private static RuleSet Rules(params string[] rules) { return new RuleSet(rules); }

        private sealed class FakeStorage : ISystemProxyBypassStorage
        {
            public ProxyBypassSnapshot Current = new ProxyBypassSnapshot
            { ProxyEnable = 1, ProxyServer = "127.0.0.1:7890", AutoConfigHash = "empty", HasOverride = true, Override = "localhost;existing.example" };
            public ProxyBypassJournal Journal;
            public int Writes, Notifications, Deletes, Saves;
            public bool FailRead, FailLoad, FailSave, FailWrite, FailNotify, RaceWrite, CorruptReadback;

            public ProxyBypassSnapshot Read()
            {
                if (FailRead) throw new InvalidOperationException("read failed");
                return new ProxyBypassSnapshot { ProxyEnable=Current.ProxyEnable, ProxyServer=Current.ProxyServer,
                    AutoConfigHash=Current.AutoConfigHash, HasOverride=Current.HasOverride, Override=Current.Override };
            }
            public void CompareAndWrite(ProxyBypassSnapshot expected, bool hasOverride, string value)
            {
                if (RaceWrite) { Current.Override += ";external-race.example"; RaceWrite=false; }
                if (!SystemProxyBypassManager.SameSnapshot(Current, expected)) throw new InvalidOperationException("concurrent change");
                Assert(Journal != null && Journal.PendingAfterHash != null, "Ownership was not journaled before registry write.");
                if (FailWrite) throw new InvalidOperationException("write failed");
                Writes++;
                Current.HasOverride = hasOverride; Current.Override = value;
                if (CorruptReadback) throw new InvalidOperationException("readback failed");
            }
            public void NotifyChanged()
            {
                Notifications++;
                if (FailNotify) throw new InvalidOperationException("notify failed");
            }
            public ProxyBypassJournal Load()
            {
                if (FailLoad) throw new InvalidOperationException("journal read failed");
                return Clone(Journal);
            }
            public void Save(ProxyBypassJournal journal)
            {
                Saves++;
                if (FailSave) throw new InvalidOperationException("journal write failed");
                Journal = Clone(journal);
            }
            public void Delete() { Deletes++; Journal=null; }
            private static ProxyBypassJournal Clone(ProxyBypassJournal value)
            {
                if(value==null)return null;
                return new ProxyBypassJournal { Version=value.Version, InitialOverrideExists=value.InitialOverrideExists,
                    OwnedTokens=value.OwnedTokens==null?null:new List<string>(value.OwnedTokens),
                    PendingBeforeHash=value.PendingBeforeHash, PendingAfterHash=value.PendingAfterHash,
                    PendingOwnedTokens=value.PendingOwnedTokens==null?null:new List<string>(value.PendingOwnedTokens) };
            }
        }

        public static void Run(Action<string, Action> test)
        {
            const string verifiedIdentity="http=127.0.0.1:7890;https=127.0.0.1:7890";
            test("proxy bypass rejects missing verified identity before all writes", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate {m.Apply(Rules("one.example"),false,null);});
                Reject(delegate {m.Apply(Rules("one.example"),false,"");});
                Assert(s.Saves==0 && s.Writes==0 && s.Notifications==0,"Unverified apply caused writes.");
            });
            test("proxy bypass rejects a changed loopback port before journal or registry write", delegate
            {
                FakeStorage s=new FakeStorage();s.Current.ProxyServer="127.0.0.1:9999";
                Reject(delegate {new SystemProxyBypassManager(s).Apply(Rules("one.example"),false,verifiedIdentity);});
                Assert(s.Saves==0 && s.Writes==0 && s.Journal==null,"Another endpoint received our bypass rules.");
            });
            test("proxy bypass rejects a changed loopback address before journal or registry write", delegate
            {
                FakeStorage s=new FakeStorage();s.Current.ProxyServer="127.0.0.2:7890";
                Reject(delegate {new SystemProxyBypassManager(s).Apply(Rules("one.example"),false,verifiedIdentity);});
                Assert(s.Saves==0 && s.Writes==0 && s.Journal==null,"Another address received our bypass rules.");
            });
            test("proxy bypass rejects non-loopback or malformed proxy before all writes", delegate
            {
                foreach(string server in new[]{"192.0.2.10:7890","proxy.example:7890","user:password@127.0.0.1:7890"})
                {
                    FakeStorage s=new FakeStorage();s.Current.ProxyServer=server;
                    Reject(delegate {new SystemProxyBypassManager(s).Apply(Rules("one.example"),false,verifiedIdentity);});
                    Assert(s.Saves==0 && s.Writes==0 && s.Notifications==0,"Unsupported endpoint caused a write.");
                }
            });
            test("proxy bypass equivalent normalized mapping is accepted and maintained", delegate
            {
                FakeStorage s=new FakeStorage();s.Current.ProxyServer="https=127.0.0.1:7890; http=127.0.0.1:7890";
                SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),false,verifiedIdentity);
                s.Current.ProxyServer="127.0.0.1:7890";s.Current.Override="localhost;existing.example";
                Assert(m.EnsureCurrent() && s.Current.Override.EndsWith("one.example"),"Equivalent mapping caused maintenance to stop.");
            });
            test("proxy bypass exact suffix wildcard IPv4 conversion", delegate
            {
                List<string> tokens=SystemProxyBypassManager.BuildTokens(Rules("exact.example.com", ".suffix.example.com", "*.only.example.com", "http://10.1.2.3/path"), false);
                Assert(tokens.SequenceEqual(new[] {"exact.example.com","suffix.example.com","*.suffix.example.com","*.only.example.com","10.1.2.3"}), "Rule semantics changed.");
            });
            test("proxy bypass preserves user entries and only removes owned tokens", delegate
            {
                FakeStorage s=new FakeStorage(); string original=s.Current.Override;
                SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules(".new.example.com","existing.example"),false);
                Assert(m.OwnedTokenCount==2 && s.Current.Override.StartsWith(original+";"), "Pre-existing token was claimed or overwritten.");
                s.Current.Override += ";user-added.example";
                m.Remove();
                Assert(s.Current.Override==original+";user-added.example", "Cleanup removed a user entry.");
                Assert(s.Journal==null, "Verified cleanup left stale ownership.");
            });
            test("proxy bypass case-insensitive duplicate preserves original bytes", delegate
            {
                FakeStorage s=new FakeStorage(); s.Current.Override=" Existing.Example ;localhost";
                SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("existing.example"),false);
                Assert(m.OwnedTokenCount==0 && s.Writes==0 && s.Current.Override==" Existing.Example ;localhost", "Duplicate or user formatting was changed.");
                m.Remove(); Assert(s.Current.Override==" Existing.Example ;localhost", "Original entry was deleted.");
            });
            test("proxy bypass restores absent override property", delegate
            {
                FakeStorage s=new FakeStorage(); s.Current.HasOverride=false;s.Current.Override="";
                SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("new.example"),false);m.Remove();
                Assert(!s.Current.HasOverride && s.Current.Override=="", "An originally absent property was not restored.");
            });
            test("proxy bypass restores originally empty existing property", delegate
            {
                FakeStorage s=new FakeStorage();s.Current.Override="";
                SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("new.example"),false);m.Remove();
                Assert(s.Current.HasOverride && s.Current.Override=="", "An existing empty property was deleted.");
            });
            test("proxy bypass hot rule removal leaves unrelated values", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example","two.example"),false);
                m.Apply(Rules("two.example","three.example"),false);
                Assert(s.Current.Override=="localhost;existing.example;two.example;three.example", "Hot update merged incorrectly.");
                Assert(m.OwnedTokenCount==2, "Hot update ownership count is wrong.");
            });
            test("proxy bypass maintenance repairs overwritten entries without losing new external values", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),false);
                s.Current.Override="localhost;new-fastlink-default.example";
                Assert(m.EnsureCurrent(), "Lost owned token was not repaired.");
                Assert(s.Current.Override=="localhost;new-fastlink-default.example;one.example", "Maintenance overwrote updated defaults.");
                int writes=s.Writes;int saves=s.Saves;int notifications=s.Notifications;
                Assert(!m.EnsureCurrent() && writes==s.Writes && saves==s.Saves && notifications==s.Notifications,
                    "Unchanged maintenance rewrote settings/journal or notified Windows.");
                m.Remove();Assert(s.Current.Override=="localhost;new-fastlink-default.example","Cleanup lost external defaults.");
            });
            test("proxy bypass pre-existing desired entries require no ownership file", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("existing.example"),false);
                Assert(s.Saves==0 && s.Journal==null && s.Writes==0 && s.Notifications==0,
                    "An entirely pre-existing bypass list caused unnecessary ownership/settings writes.");
                Assert(!m.EnsureCurrent() && s.Saves==0,"Pre-existing no-op maintenance wrote a journal.");
                m.Remove();Assert(s.Current.Override=="localhost;existing.example","No-op ownership cleanup changed user entries.");
            });
            test("proxy bypass ownership removal is persisted even if external software already removed token", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example","two.example"),false);
                s.Current.Override="localhost;existing.example;two.example";
                int writes=s.Writes;int saves=s.Saves;
                m.Apply(Rules("two.example"),false);
                Assert(s.Writes==writes && s.Saves==saves+1 && s.Journal.OwnedTokens.SequenceEqual(new[]{"two.example"}),
                    "No-op optimization lost a required ownership-only journal update.");
            });
            test("proxy bypass maintenance does not modify another proxy endpoint", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),false);s.Current.ProxyServer="127.0.0.1:9999";s.Current.Override="external.example";
                int writes=s.Writes;Assert(!m.EnsureCurrent() && s.Writes==writes,"Maintenance touched a different proxy.");
                m.Remove();Assert(s.Current.ProxyServer=="127.0.0.1:9999" && s.Current.Override=="external.example","Cleanup changed another proxy's settings.");
            });
            test("proxy bypass never changes server enablement or PAC identity", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),true);m.Remove();
                Assert(s.Current.ProxyEnable==1 && s.Current.ProxyServer=="127.0.0.1:7890" && s.Current.AutoConfigHash=="empty", "Connection settings were changed.");
            });
            test("proxy bypass private ranges exactly respect RFC1918 boundary", delegate
            {
                List<string> tokens=SystemProxyBypassManager.BuildTokens(Rules("one.example"),true);
                Assert(tokens.Contains("172.16.*") && tokens.Contains("172.31.*") && !tokens.Contains("172.32.*") && !tokens.Contains("172.*"), "Private network range broadened.");
                Assert(!SystemProxyBypassManager.BuildTokens(Rules("one.example"),false).Contains("10.*"), "Disabled private option changed bypass.");
            });
            test("proxy bypass unsupported single-character wildcard fails before write", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("api-?.example.com"),false);});
                Assert(s.Writes==0 && s.Saves==0 && s.Journal==null,"Unsupported rule caused writes or scope widening.");
            });
            test("proxy bypass write failure retains journal and safe cleanup resolves no-write", delegate
            {
                FakeStorage s=new FakeStorage();s.FailWrite=true;SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("one.example"),false);});
                Assert(s.Journal!=null && s.Journal.PendingBeforeHash!=null,"Write failure lost recovery journal.");
                s.FailWrite=false;m.Remove();Assert(s.Current.Override=="localhost;existing.example" && s.Journal==null,"Failed write cleanup changed original settings.");
            });
            test("proxy bypass journal save failure prevents registry writes", delegate
            {
                FakeStorage s=new FakeStorage();s.FailSave=true;SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("one.example"),false);});Assert(s.Writes==0,"Unjournaled registry write occurred.");
            });
            test("proxy bypass notification failure is not success and remains recoverable", delegate
            {
                FakeStorage s=new FakeStorage();s.FailNotify=true;SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("one.example"),false);});
                Assert(s.Current.Override.Contains("one.example") && s.Journal.PendingAfterHash!=null,"Notification failure was silently committed.");
                s.FailNotify=false;new SystemProxyBypassManager(s).Remove();
                Assert(s.Current.Override=="localhost;existing.example" && s.Journal==null,"Restart cleanup could not resolve completed write.");
            });
            test("proxy bypass ambiguous concurrent write never deletes unproven ownership", delegate
            {
                FakeStorage s=new FakeStorage();s.RaceWrite=true;SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("one.example"),false);});
                Assert(s.Writes==0 && s.Current.Override.EndsWith("external-race.example"),"Concurrent change was overwritten.");
                Reject(delegate{m.Remove();});Assert(s.Deletes==0 && s.Current.Override.EndsWith("external-race.example"),"Ambiguous journal was silently cleaned.");
            });
            test("proxy bypass registry or journal read failures never look like successful cleanup", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),false);int writes=s.Writes;
                s.FailRead=true;Reject(delegate{m.Remove();});s.FailRead=false;s.FailLoad=true;Reject(delegate{m.Remove();});
                Assert(s.Writes==writes && s.Deletes==0,"Failed reads caused cleanup writes.");
            });
            test("proxy bypass readback failure keeps a recoverable journal", delegate
            {
                FakeStorage s=new FakeStorage();s.CorruptReadback=true;SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                Reject(delegate{m.Apply(Rules("one.example"),false);});
                Assert(s.Journal.PendingAfterHash!=null,"Readback failure lost its journal.");
                s.CorruptReadback=false;m.Remove();Assert(s.Current.Override=="localhost;existing.example","Recovery from readback error failed.");
            });
            test("proxy bypass cleanup preserves an externally deleted override property", delegate
            {
                FakeStorage s=new FakeStorage();SystemProxyBypassManager m=new SystemProxyBypassManager(s);
                m.Apply(Rules("one.example"),false);s.Current.HasOverride=false;s.Current.Override="";
                int writes=s.Writes;m.Remove();Assert(!s.Current.HasOverride && s.Writes==writes,"Cleanup recreated a property deleted externally.");
            });
            test("proxy bypass disabled proxy is not activated", delegate
            {
                FakeStorage s=new FakeStorage();s.Current.ProxyEnable=0;
                Reject(delegate{new SystemProxyBypassManager(s).Apply(Rules("one.example"),false);});
                Assert(s.Current.ProxyEnable==0 && s.Writes==0,"Disabled proxy was activated.");
            });
            test("proxy bypass invalid persisted ownership refuses cleanup", delegate
            {
                FakeStorage s=new FakeStorage();s.Journal=new ProxyBypassJournal {Version=1,OwnedTokens=new List<string>{"<local>"}};
                Reject(delegate{new SystemProxyBypassManager(s).Remove();});Assert(s.Writes==0 && s.Deletes==0,"Unknown ownership record was used.");
            });
        }
    }
}
