using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLinkDirectTool.Tests
{
    // No Form, production entry point, file/configuration I/O or network object
    // is constructed. Named-event tests use an unrelated random test-only name.
    internal static class WindowActivationTests
    {
        private static int passed;

        private static int Main()
        {
            try
            {
                Test("scope is deterministic", delegate
                {
                    Assert(WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1001", 7) == WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1001", 7));
                });
                Test("scope separates users", delegate
                {
                    Assert(WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1001", 7) != WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1002", 7));
                });
                Test("scope separates sessions", delegate
                {
                    Assert(WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1001", 7) != WindowActivationListener.GetEventName("S-1-5-21-11-22-33-1001", 8));
                });
                Test("scope is local", delegate { Assert(WindowActivationListener.GetEventName("S-1-5-18", 1).StartsWith("Local\\FastLinkDirectTool.ShowWindow.v1.", StringComparison.Ordinal)); });
                Test("invalid session rejected", delegate { Throws<ArgumentOutOfRangeException>(delegate { WindowActivationListener.GetEventName("S-1-5-18", -1); }); });
                Test("invalid SID rejected", delegate { Throws<ArgumentException>(delegate { WindowActivationListener.GetEventName("invalid", 1); }); });
                Test("null request rejected", delegate { Throws<ArgumentNullException>(delegate { new WindowActivationListener(null, delegate { }, delegate { }); }); });
                Test("null dispatcher rejected", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(false))
                        Throws<ArgumentNullException>(delegate { new WindowActivationListener(request, null, delegate { }); });
                });
                Test("null restore rejected", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(false))
                        Throws<ArgumentNullException>(delegate { new WindowActivationListener(request, delegate { }, null); });
                });
                Test("legacy restore selected when no child", delegate
                {
                    int legacy = 0;
                    WindowActivationListener.RestoreCurrentWindow(null, delegate { legacy++; });
                    Assert(legacy == 1);
                });
                Test("child restore never reveals owner", delegate
                {
                    int legacy = 0, child = 0;
                    WindowActivationListener.RestoreCurrentWindow(delegate { child++; }, delegate { legacy++; });
                    Assert(child == 1 && legacy == 0);
                });
                Test("child failure does not reveal owner", delegate
                {
                    int legacy = 0;
                    Throws<InvalidOperationException>(delegate
                    {
                        WindowActivationListener.RestoreCurrentWindow(delegate { throw new InvalidOperationException(); }, delegate { legacy++; });
                    });
                    Assert(legacy == 0);
                });
                Test("named notification is marshalled to fake UI", delegate
                {
                    string testName = "Local\\FastLinkDirectTool.TestActivation." + Guid.NewGuid().ToString("N");
                    using (EventWaitHandle request = new EventWaitHandle(false, EventResetMode.AutoReset, testName))
                    using (FakeUi ui = new FakeUi())
                    {
                        int restored = 0, callbackThread = 0;
                        using (WindowActivationListener listener = new WindowActivationListener(request, ui.Post, delegate { restored++; callbackThread = Thread.CurrentThread.ManagedThreadId; }))
                        using (EventWaitHandle sender = EventWaitHandle.OpenExisting(testName))
                        {
                            sender.Set();
                            ui.WaitForPost();
                            Assert(restored == 0);
                            ui.PumpOne();
                            Assert(restored == 1 && callbackThread == Thread.CurrentThread.ManagedThreadId);
                        }
                    }
                });
                Test("startup notification is retained", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(true))
                    using (FakeUi ui = new FakeUi())
                    {
                        int restored = 0;
                        using (WindowActivationListener listener = new WindowActivationListener(request, ui.Post, delegate { restored++; }))
                        {
                            ui.WaitForPost(); ui.PumpOne(); Assert(restored == 1);
                        }
                    }
                });
                Test("repeated launch notifications restore only", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(false))
                    using (FakeUi ui = new FakeUi())
                    {
                        int restored = 0;
                        using (WindowActivationListener listener = new WindowActivationListener(request, ui.Post, delegate { restored++; }))
                        {
                            for (int i = 0; i < 4; i++) { request.Set(); ui.WaitForPost(); ui.PumpOne(); }
                            Assert(restored == 4);
                        }
                    }
                });
                Test("queued notification cancelled after disposal", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(false))
                    using (FakeUi ui = new FakeUi())
                    {
                        int restored = 0;
                        WindowActivationListener listener = new WindowActivationListener(request, ui.Post, delegate { restored++; });
                        try { request.Set(); ui.WaitForPost(); }
                        finally { listener.Dispose(); }
                        ui.PumpOne(); Assert(restored == 0); listener.Dispose();
                    }
                });
                Test("UI shutdown rejects dispatch safely", delegate
                {
                    using (AutoResetEvent request = new AutoResetEvent(false))
                    using (AutoResetEvent dispatched = new AutoResetEvent(false))
                    {
                        int restored = 0;
                        using (WindowActivationListener listener = new WindowActivationListener(request, delegate(Action action)
                        {
                            dispatched.Set(); throw new ObjectDisposedException("FakeUi");
                        }, delegate { restored++; }))
                        {
                            request.Set(); Assert(dispatched.WaitOne(3000));
                            request.Set(); Assert(dispatched.WaitOne(3000));
                            Assert(restored == 0);
                        }
                    }
                });
                Test("current child is selected at dispatch time", delegate { VerifyTargetChange(true); });
                Test("legacy handoff is selected at dispatch time", delegate { VerifyTargetChange(false); });
                Console.WriteLine("WINDOW_ACTIVATION_TESTS_OK " + passed);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void VerifyTargetChange(bool childBecomesActive)
        {
            using (AutoResetEvent request = new AutoResetEvent(false))
            using (FakeUi ui = new FakeUi())
            {
                bool childActive = !childBecomesActive;
                int child = 0, legacy = 0;
                using (WindowActivationListener listener = new WindowActivationListener(request, ui.Post, delegate
                {
                    Action restoreChild = childActive ? (Action)delegate { child++; } : null;
                    WindowActivationListener.RestoreCurrentWindow(restoreChild, delegate { legacy++; });
                }))
                {
                    request.Set(); ui.WaitForPost(); childActive = childBecomesActive; ui.PumpOne();
                    Assert(child == (childBecomesActive ? 1 : 0) && legacy == (childBecomesActive ? 0 : 1));
                }
            }
        }

        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private sealed class FakeUi : IDisposable
        {
            private readonly Queue<Action> pending = new Queue<Action>();
            private readonly AutoResetEvent posted = new AutoResetEvent(false);
            internal void Post(Action action) { lock (pending) { pending.Enqueue(action); } posted.Set(); }
            internal void WaitForPost() { Assert(posted.WaitOne(3000)); }
            internal void PumpOne() { Action action; lock (pending) { action = pending.Dequeue(); } action(); }
            public void Dispose() { posted.Dispose(); }
        }
    }
}
