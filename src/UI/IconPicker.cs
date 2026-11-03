using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabSlotAddIn.UI
{
    /// <summary>A searchable gallery of Inventor's own command icons; returns the internal name of the chosen command.</summary>
    internal sealed class IconPicker : Form
    {
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Top };
        private readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false };
        private readonly ImageList _images = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
        private readonly Label _hint = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
        private readonly List<(string Name, string Display, int Image)> _all = new List<(string, string, int)>();

        /// <summary>Shows the picker; returns the chosen command name, or null if cancelled.</summary>
        public static string Choose(string current)
        {
            using (var picker = new IconPicker(current))
                return picker.ShowDialog() == DialogResult.OK ? picker.Selected : null;
        }

        public string Selected { get; private set; }

        private IconPicker(string current)
        {
            Text = "Choose an icon";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(480, 320);

            _list.LargeImageList = _images;
            _hint.Text = "Type to filter by name. Double-click an icon to use it.";
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(_list);
            Controls.Add(buttons);
            Controls.Add(_hint);
            Controls.Add(_search);
            AcceptButton = ok;
            CancelButton = cancel;

            foreach (var entry in InventorCommandIcons.RibbonCommands())
            {
                int image = _images.Images.Count;
                _images.Images.Add(entry.Icon);
                _all.Add((entry.Name, entry.Display, image));
            }

            _search.TextChanged += (s, e) => Fill(current);
            _list.DoubleClick += (s, e) => { if (_list.SelectedItems.Count == 1) { Selected = (string)_list.SelectedItems[0].Tag; DialogResult = DialogResult.OK; } };
            _list.SelectedIndexChanged += (s, e) => { if (_list.SelectedItems.Count == 1) Selected = (string)_list.SelectedItems[0].Tag; };
            Fill(current);
            Shown += (s, e) => _search.Focus();
        }

        private void Fill(string current)
        {
            string filter = _search.Text.Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in _all)
            {
                if (filter.Length > 0 && e.Display.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = new ListViewItem(e.Display, e.Image) { Tag = e.Name, ToolTipText = e.Name };
                _list.Items.Add(item);
                if (e.Name == current) { item.Selected = true; item.EnsureVisible(); }
            }
            _list.EndUpdate();
        }
    }
}
