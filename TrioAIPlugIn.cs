using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Trio.SharedLibrary;

namespace TrioAI.MPPlugIn
{
    public class TrioAIPlugIn : PlugInBase
    {
        private static readonly TrioAIPlugIn _instance = new TrioAIPlugIn();
        public static TrioAIPlugIn Instance => _instance;

        private ApiServer _server;

        private TrioAIPlugIn()
            : base(new ResourceDictionary[0], new IFactoryBase[] { (IFactoryBase)ChatPanel.Factory })
        {
            AiService.PerfLog("TrioAIPlugIn static ctor: enter");
            AiService.PerfLog("TrioAIPlugIn static ctor: exit");
        }

        protected override void OnInitialize()
        {
            AiService.PerfLog("OnInitialize: enter");

            // Hook assembly loads so we can see what gets loaded during the slow 21s gap.
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                try { AiService.PerfLog("AssemblyLoad: " + e.LoadedAssembly.GetName().Name); } catch { }
            };

            base.OnInitialize();
            AiService.PerfLog("OnInitialize: base.OnInitialize done");
            DispatcherHelper.Capture();
            AiService.PerfLog("OnInitialize: DispatcherHelper.Capture done");
            _server = new ApiServer();
            AiService.PerfLog("OnInitialize: ApiServer constructed");
            _server.Start();
            AiService.PerfLog("OnInitialize: ApiServer.Start done");

            // Probe: tick every 1s at idle priority. If ticks stop, the UI thread is blocked.
            var probe = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
            var tickCount = 0;
            probe.Tick += (s, e) =>
            {
                tickCount++;
                if (tickCount % 5 == 0) AiService.PerfLog("DispatcherTimer idle tick #" + tickCount);
                if (tickCount >= 60) { probe.Stop(); AiService.PerfLog("DispatcherTimer: stopping after 60s"); AiService.PerfLogFlush(); }
            };
            probe.Start();
            AiService.PerfLog("OnInitialize: DispatcherTimer probe started");

            // Pre-warm WPF control templates + JIT at startup so first tool click is fast.
            // Templates normally load lazily on first use → that's the 21s freeze we measured.
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(Prewarm), DispatcherPriority.ApplicationIdle);
            AiService.PerfLog("OnInitialize: scheduled Prewarm");
            AiService.PerfLogFlush();
        }

        // Force-load WPF control templates + pre-JIT our methods.
        // Runs on UI thread at ApplicationIdle priority so MP startup isn't blocked.
        private void Prewarm()
        {
            AiService.PerfLog("Prewarm: enter");

            // 1) Touch each WPF control type used by ChatPanel.BuildUI to load its ControlTemplate.
            //    ApplyTemplate() forces the template to instantiate (loads BAML, JITs template code).
            try
            {
                PrewarmControl(new Button());
                PrewarmControl(new TextBox());
                PrewarmControl(new TextBlock());
                PrewarmControl(new ScrollViewer());
                PrewarmControl(new ItemsControl());
                PrewarmControl(new Border());
                PrewarmControl(new DockPanel());
                PrewarmControl(new StackPanel());
                PrewarmControl(new CheckBox());
                AiService.PerfLog("Prewarm: WPF controls done");
            }
            catch (Exception ex) { AiService.PerfLog("Prewarm WPF threw: " + ex.Message); }

            // 2) Pre-JIT our startup-critical methods so first click doesn't pay JIT cost.
            try
            {
                var types = new[] { typeof(ChatPanel), typeof(AiService), typeof(ApiServer), typeof(Handlers) };
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var t in types)
                {
                    foreach (var m in t.GetMethods(flags))
                    {
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); } catch { }
                    }
                    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(c.MethodHandle); } catch { }
                    }
                }
                AiService.PerfLog("Prewarm: JIT prepare done");
            }
            catch (Exception ex) { AiService.PerfLog("Prewarm JIT threw: " + ex.Message); }

            AiService.PerfLog("Prewarm: exit");
            AiService.PerfLogFlush();
        }

        private static void PrewarmControl(Control c)
        {
            c.Measure(new Size(0, 0));
            c.Arrange(new Rect(0, 0, 0, 0));
            c.ApplyTemplate();
        }

        private static void PrewarmControl(FrameworkElement e)
        {
            e.Measure(new Size(0, 0));
            e.Arrange(new Rect(0, 0, 0, 0));
        }

        protected override void OnDispose()
        {
            _server?.Stop();
            _server = null;
            base.OnDispose();
        }
    }
}

