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
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            DispatcherHelper.Capture();
            _server = new ApiServer();
            _server.Start();

            // Pre-warm WPF control templates + JIT at startup so first tool click is fast.
            // Templates normally load lazily on first use → that's the 21s freeze we measured.
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(Prewarm), DispatcherPriority.ApplicationIdle);
        }

        // Force-load WPF control templates + pre-JIT our methods.
        // Runs on UI thread at ApplicationIdle priority so MP startup isn't blocked.
        private void Prewarm()
        {
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
            }
            catch (Exception ex) { AiService.LogException("Prewarm WPF", ex); }

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
            }
            catch (Exception ex) { AiService.LogException("Prewarm JIT", ex); }
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

