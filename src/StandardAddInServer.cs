using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using TabSlotAddIn.UI;

namespace TabSlotAddIn
{
    /// <summary>
    /// Inventor add-in entry point: one "Tab and Slot" button on the Assemble tab of the assembly ribbon. It opens a panel
    /// docked in Inventor's left-hand area for creating tabs and slots and for editing or deleting the existing ones.
    /// </summary>
    [ComVisible(true)]
    [Guid(ClientId)]
    [ProgId("TabSlotAddIn.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        public const string ClientId = "b3d4f1a8-6c27-4e59-9a0d-2f81c7e5a346";
        private const string PanelName = "TabSlotAddIn.Panel";

        private Inventor.Application _app;
        private ButtonDefinition _button;
        private UserInterfaceEvents _uiEvents;

        public object Automation => null;

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _app = addInSiteObject.Application;
            string clientGuid = "{" + ClientId + "}";
            UI.InventorCommandIcons.Init(_app);

            _button = _app.CommandManager.ControlDefinitions.AddButtonDefinition(
                "Tab and Slot", "TabSlotAddIn.Create", CommandTypesEnum.kNonShapeEditCmdType, clientGuid,
                "Create, edit and delete tabs and matching slots",
                "Tab and Slot\nCreates tabs on the edge of one part with matching slots in another part, and lists the existing ones for editing or deleting.",
                Icons.CreateTabSlot(16), Icons.CreateTabSlot(32),
                ButtonDisplayEnum.kDisplayTextInLearningMode);
            _button.OnExecute += OnExecute;

            _uiEvents = _app.UserInterfaceManager.UserInterfaceEvents;
            _uiEvents.OnResetRibbonInterface += OnResetRibbon;

            DockHost.Init(_app, clientGuid);
            AddToRibbon();
        }

        public void Deactivate()
        {
            try { _uiEvents.OnResetRibbonInterface -= OnResetRibbon; } catch { }
            try { _button.OnExecute -= OnExecute; } catch { }
            try { TabSlotCommand.CancelActive(); } catch { }
            try { DockHost.Shutdown(); } catch { }
            try { _button?.Delete(); } catch { }
            _button = null;
            _uiEvents = null;
            _app = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void ExecuteCommand(int commandID) { }

        // ----- ribbon -------------------------------------------------------------------------------------------

        private void OnResetRibbon(NameValueMap context) => AddToRibbon();

        private void AddToRibbon()
        {
            Ribbon ribbon = _app.UserInterfaceManager.Ribbons["Assembly"];
            RibbonTab tab = ribbon.RibbonTabs["id_TabAssemble"];

            RibbonPanel panel;
            try { panel = tab.RibbonPanels[PanelName]; }
            catch
            {
                panel = tab.RibbonPanels.Add("Tab & Slot", PanelName, "{" + ClientId + "}", "", false);
            }

            try { panel.CommandControls.AddButton(_button, true, true, "", false); } catch { /* already added */ }
        }

        // ----- command ------------------------------------------------------------------------------------------

        private void OnExecute(NameValueMap context)
        {
            var owner = new InventorWindow(_app.MainFrameHWND);
            var doc = _app.ActiveDocument as AssemblyDocument;
            if (doc == null)
            {
                MessageBox.Show(owner, "Open an assembly first.", "Tab and Slot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            TabSlotCommand cmd = TabSlotCommand.Start(_app, doc, null, out string error);
            if (cmd == null && error != null)
                MessageBox.Show(owner, error, "Tab and Slot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
