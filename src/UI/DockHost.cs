using System;
using System.Windows.Forms;
using Inventor;

namespace TabSlotAddIn.UI
{
    /// <summary>
    /// Owns the one dockable window (in Inventor's left-hand panel area, next to Model / Properties) that hosts the
    /// feature panel while the Tab and Slot command runs, the way Inventor's own feature commands do.
    /// </summary>
    internal static class DockHost
    {
        private const string WindowName = "TabSlotAddIn.DockableWindow";

        private sealed class HostControl : UserControl
        {
            public HostControl() { Dock = DockStyle.Fill; }
        }

        private static DockableWindow _window;
        private static HostControl _host;

        public static void Init(Inventor.Application app, string clientGuid)
        {
            try { app.UserInterfaceManager.DockableWindows[WindowName].Delete(); } catch { /* none left from before */ }

            _host = new HostControl();
            _ = _host.Handle; // make sure the native window exists before Inventor adopts it
            _window = app.UserInterfaceManager.DockableWindows.Add(clientGuid, WindowName, "Tab and Slot");
            _window.ShowVisibilityCheckBox = false;
            _window.DisableCloseButton = true;
            _window.SetMinimumSize(250, 300);
            _window.AddChild(_host.Handle.ToInt32());
            _window.DockingState = DockingStateEnum.kDockLeft;
            _window.Visible = false;

            // the tab's cross still hides the window even with the close button disabled: treat it as Cancel
            _events = app.UserInterfaceManager.DockableWindows.Events;
            _events.OnHide += OnHide;
        }

        private static DockableWindowsEvents _events;
        private static bool _hiding;

        private static void OnHide(DockableWindow window, EventTimingEnum before, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (_hiding || before != EventTimingEnum.kAfter || !TabSlotCommand.IsRunning) return;
            try { if (window.InternalName != WindowName) return; } catch { return; }
            // finish outside Inventor's event so the command's own clean-up can touch the window again
            _host?.BeginInvoke(new Action(TabSlotCommand.CancelActive));
        }

        public static void Show(TabSlotPanel panel, string title)
        {
            if (_window == null) throw new InvalidOperationException("The docked panel is not available.");
            _host.Controls.Clear();
            panel.Dock = DockStyle.Fill;
            _host.Controls.Add(panel);
            try { _window.Title = title; } catch { /* cosmetic */ }
            _window.Visible = true;
        }

        public static void Hide(TabSlotPanel panel)
        {
            _hiding = true;
            try { if (_window != null) _window.Visible = false; } catch { }
            finally { _hiding = false; }
            try { _host?.Controls.Remove(panel); } catch { }
            try { panel?.Dispose(); } catch { }
        }

        public static void Shutdown()
        {
            try { if (_events != null) _events.OnHide -= OnHide; } catch { }
            _events = null;
            try { _window?.Delete(); } catch { }
            try { _host?.Dispose(); } catch { }
            _window = null;
            _host = null;
        }
    }
}
