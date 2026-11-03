using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TabSlotAddIn.Core;

namespace TabSlotAddIn.UI
{
    internal enum PickTarget { None, Tab, Slot }

    /// <summary>One line of the "Existing Tab and Slots" list.</summary>
    internal sealed class SetRow
    {
        public string Id, Name, Parts, Status;
        public int Count;
    }

    /// <summary>
    /// The Tab and Slot feature panel, built like Inventor's own property panels: section bars with chevrons, flat dark
    /// fields, icon toggle rows, selector fields with a clear button, and OK / Cancel / "+" at the bottom.
    /// It only edits <see cref="TabSlotParams"/>; picking, preview and creation are driven by <see cref="TabSlotCommand"/>.
    /// </summary>
    internal sealed class TabSlotPanel : UserControl
    {
        private const int DebounceMs = 250;
        private const int LabelWidth = 80;

        private readonly Units _units;
        private readonly Theme _theme;
        private readonly Timer _debounce = new Timer { Interval = DebounceMs };
        private readonly Dictionary<Control, Label> _rowLabels = new Dictionary<Control, Label>();
        private bool _loading;

        // input geometry
        private readonly SelectorField _pickTab;
        private readonly SelectorField _pickSlot;


        // tabs
        private readonly FieldBox _length, _count, _pitch, _height, _chamferSize;
        private readonly IconToggleGroup _spacing, _walls;
        private bool _rotateValue;      // set by picking an edge; not shown in the panel
        private bool _heightEdited;     // false: the height follows the slot sheet thickness
        private bool _clearanceEdited;  // false: the oversize follows the tolerance table
        private bool _reliefEdited;    // false: the relief radius follows the plate thickness table

        // slot
        private readonly FieldBox _clearance, _reliefRadius;

        // existing sets
        private readonly ListView _list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Scrollable = false,
            HeaderStyle = ColumnHeaderStyle.None, Height = 120
        };
        private IconButton _editSet, _deleteSet;
        private readonly FieldBox _name;   // shown only while editing a set
        private readonly Label _listInfo = new Label { AutoSize = true, Tag = "keep" };

        private readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8, 2, 8, 0), Tag = "keep", Visible = false };
        private InventorButton _ok, _cancel;

        public event Action<PickTarget> PickClicked;
        public event Action<PickTarget> ClearRequested;
        public event Action ParamsChanged;
        public event Action OkClicked;
        public event Action CancelClicked;
        public event Action<string> EditRequested;
        public event Action<string> DeleteRequested;

        public TabSlotPanel(Units units, Theme theme)
        {
            _units = units;
            _theme = theme;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = theme.Back;
            ForeColor = theme.Text;
            Dock = DockStyle.Fill;

            _name = new FieldBox(theme);
            _pickTab = new SelectorField(theme);
            _pickSlot = new SelectorField(theme);
            _length = new FieldBox(theme); _pitch = new FieldBox(theme); _height = new FieldBox(theme); _chamferSize = new FieldBox(theme);
            _count = new FieldBox(theme); _count.AsInteger(1, 99); _count.IntValue = 1;
            _spacing = new IconToggleGroup(theme, ("Equal spacing", ToggleIcons.Cmd("SpacingEqual", ToggleIcons.EqualSpacing)), ("Fixed pitch", ToggleIcons.Cmd("SpacingPitch", ToggleIcons.FixedPitch)));
            _walls = new IconToggleGroup(theme, ("All four walls", ToggleIcons.TubeAll), ("Two walls", ToggleIcons.TubePairA), ("Two walls, turned 90°", ToggleIcons.TubePairB));
            _clearance = new FieldBox(theme); _reliefRadius = new FieldBox(theme);
            _ok = new InventorButton(theme, "OK", true);
            _cancel = new InventorButton(theme, "Cancel", false);

            BuildLayout();
            theme.Apply(this);
            StyleButtons();
            WireEvents();
            SetFaceText(PickTarget.Tab, "", false);
            SetFaceText(PickTarget.Slot, "", false);
            UpdateVisibility();
            ShowRow(_name, false);

            _debounce.Tick += (s, e) => { _debounce.Stop(); ParamsChanged?.Invoke(); };
        }

        // ----- public API ---------------------------------------------------------------------------------------

        /// <summary>Reads all controls into <paramref name="p"/>; returns an error message, or null when everything parsed.</summary>
        public string ReadInto(TabSlotParams p)
        {
            string error = null;
            double Len(FieldBox box, string what, bool allowZero = false, bool allowNegative = false)
            {
                bool ok = _units.TryParse(box.Text, out double cm) && (allowNegative || cm > 0 || allowZero && cm >= 0);
                box.Invalid = !ok;
                if (ok) return cm;
                if (error == null) error = what + " is not a valid length.";
                return 0;
            }

            p.TabLength = Len(_length, "Tab length");
            p.Count = _count.IntValue;
            p.Spacing = _spacing.SelectedIndex == 1 ? SpacingMode.Pitch : SpacingMode.Equal;
            p.TubeWalls = Math.Max(0, _walls.SelectedIndex);


            p.RunAlongShortEdge = _rotateValue;
            if (p.Spacing == SpacingMode.Pitch)
            {
                p.Pitch = Len(_pitch, "Pitch");
            }

            p.Width = WidthMode.FullThickness;           // the tab is always as wide as the sheet is thick

            // Height follows the thickness of the slot sheet until a value is typed; clearing the field goes back to that.
            if (_heightEdited && _height.Text.Trim().Length > 0)
            {
                p.Height = HeightMode.Distance;
                p.HeightValue = Len(_height, "Tab height");
            }
            else
            {
                p.Height = HeightMode.Flush;
                p.HeightOffset = 0;
                _height.Invalid = false;
            }

            // Chamfer and relief are plain fields: a size above zero switches them on, zero switches them off.
            double chamfer = Len(_chamferSize, "Chamfer", allowZero: true);
            p.Chamfer = chamfer > 0;
            if (p.Chamfer) p.ChamferSize = chamfer;
            // Oversize: empty follows the tolerance table, a typed value is used as is.
            p.ClearanceAuto = !(_clearanceEdited && _clearance.Text.Trim().Length > 0);
            if (!p.ClearanceAuto) p.Clearance = Len(_clearance, "Slot oversize", true);
            else _clearance.Invalid = false;
            // Relief: empty follows the plate thickness table, a size is used as typed, zero switches it off.
            if (_reliefEdited && _reliefRadius.Text.Trim().Length > 0)
            {
                double relief = Len(_reliefRadius, "Relief", allowZero: true);
                p.ReliefAuto = false;
                p.Relief = relief > 0;
                if (p.Relief) p.ReliefRadius = relief;
            }
            else
            {
                p.ReliefAuto = true;
                p.Relief = true;
                _reliefRadius.Invalid = false;
            }
            p.SlotThrough = true;                         // slots always cut through the sheet
            return error;
        }

        public void LoadParams(TabSlotParams p)
        {
            _loading = true;
            try
            {
                _length.Text = _units.Format(p.TabLength);
                _count.IntValue = p.Count;
                _spacing.SelectedIndex = p.Spacing == SpacingMode.Pitch ? 1 : 0;
                _walls.SelectedIndex = p.TubeWalls >= 0 && p.TubeWalls <= 2 ? p.TubeWalls : 0;
                _pitch.Text = _units.Format(p.Pitch);
                _rotateValue = p.RunAlongShortEdge;
                _heightEdited = p.Height == HeightMode.Distance;
                _height.Text = _heightEdited ? _units.Format(p.HeightValue) : "";
                _chamferSize.Text = _units.Format(p.Chamfer ? p.ChamferSize : 0);
                _clearanceEdited = !p.ClearanceAuto;
                _clearance.Text = _clearanceEdited ? _units.Format(p.Clearance) : "";
                _reliefEdited = !p.ReliefAuto;
                _reliefRadius.Text = _reliefEdited ? _units.Format(p.Relief ? p.ReliefRadius : 0) : "";
            }
            finally { _loading = false; }
            UpdateVisibility();
        }

        /// <summary>Editing a set: shows the Name field so the set can be renamed.</summary>
        public void ShowName(string name)
        {
            _loading = true;
            _name.Text = name ?? "";
            _loading = false;
            ShowRow(_name, true);
        }

        /// <summary>The name typed in the Name field, or null when it is hidden or empty.</summary>
        public string ReadName()
        {
            string t = _name.Text.Trim();
            return _name.Visible && t.Length > 0 ? t : null;
        }

        public bool ReadRotate() => _rotateValue;

        public void SetRotate(bool value)
        {
            _loading = true;
            _rotateValue = value;
            _loading = false;
        }

        public void SetPicker(PickTarget active)
        {
            _pickTab.Active = active == PickTarget.Tab;
            _pickSlot.Active = active == PickTarget.Slot;
        }

        public void SetFaceText(PickTarget which, string text, bool set)
        {
            (which == PickTarget.Tab ? _pickTab : _pickSlot).SetValue(text, set);
        }

        /// <summary>While no height has been typed, the field shows the value in use: the thickness of the slot sheet.</summary>
        public void ShowComputedHeight(double cm)
        {
            if (_heightEdited) return;
            _loading = true;
            _height.Text = _units.Format(cm);
            _loading = false;
        }

        /// <summary>While no relief has been typed, the field shows the radius from the thickness table.</summary>
        public void ShowComputedClearance(double cm)
        {
            if (_clearanceEdited) return;
            _loading = true;
            _clearance.Text = _units.Format(cm);
            _loading = false;
        }

        public void ShowComputedRelief(double cm)
        {
            if (_reliefEdited) return;
            _loading = true;
            _reliefRadius.Text = _units.Format(cm);
            _loading = false;
        }

        /// <summary>Kept for the command; the slot always goes through, so there is no depth to show.</summary>
        public void SetSlotDepth(double cm) { }

        /// <summary>Shows a message above the buttons; an empty text hides the line.</summary>
        public void SetStatus(string text, bool isError)
        {
            _status.Text = text ?? "";
            _status.Visible = !string.IsNullOrEmpty(text);
            _status.ForeColor = isError ? Color.FromArgb(240, 110, 100) : _theme.Dim;
        }

        public void SetCanCommit(bool can)
        {
            _ok.Enabled = can;
        }


        // ----- existing sets ------------------------------------------------------------------------------------

        public void SetSets(IList<SetRow> rows)
        {
            string keep = SelectedId();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (SetRow r in rows)
            {
                var item = new ListViewItem(r.Name) { Tag = r };
                item.SubItems.Add(r.Count.ToString());
                item.SubItems.Add(r.Status);
                item.ToolTipText = r.Parts;
                if (r.Status != "OK") item.ForeColor = r.Status == "Suppressed" ? _theme.Dim : Color.FromArgb(240, 110, 100);
                _list.Items.Add(item);
                if (r.Id == keep) item.Selected = true;
            }
            _list.EndUpdate();
            _listInfo.Text = rows.Count == 0 ? "None in this assembly yet." : rows.Count + " in this assembly.";
            UpdateListButtons();
        }

        /// <summary>While a set is being edited the list is read-only so it cannot change under the command.</summary>
        public void SetListEnabled(bool enabled)
        {
            _list.Enabled = enabled;
            UpdateListButtons();
            if (!enabled) _listInfo.Text = "Finish editing to manage other sets.";
        }

        private string SelectedId() => _list.SelectedItems.Count == 1 ? (_list.SelectedItems[0].Tag as SetRow)?.Id : null;

        private void UpdateListButtons()
        {
            bool any = _list.Enabled && SelectedId() != null;
            foreach (Control b in new Control[] { _editSet, _deleteSet }) b.Enabled = any;
        }

        // ----- layout -------------------------------------------------------------------------------------------

        private void BuildLayout()
        {
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddSection(stack, "Input Geometry", FacesTable(), true);
            AddSection(stack, "Tabs", TabsTable(), true);
            AddSection(stack, "Slot", SlotTable(), true);
            AddSection(stack, "Existing Tab and Slots", ExistingTable(), false);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(stack);

            // OK and Cancel on the left, as in Inventor.
            var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 38, ColumnCount = 3, Padding = new Padding(8, 7, 8, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.Controls.Add(_ok, 0, 0);
            buttons.Controls.Add(_cancel, 1, 0);
            _ok.Margin = _cancel.Margin = Padding.Empty;

            Controls.Add(scroll);
            Controls.Add(_status);
            Controls.Add(buttons);
        }

        private void AddSection(TableLayoutPanel stack, string title, Control body, bool open)
        {
            var header = new SectionHeader(_theme, title, open) { Font = Font, Dock = DockStyle.Fill, Margin = Padding.Empty };
            body.Dock = DockStyle.Fill;
            body.Margin = new Padding(8, 6, 8, 6);
            body.Visible = open;
            header.Toggled += isOpen => body.Visible = isOpen;
            int r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(header, 0, r);
            r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(body, 0, r);
        }

        private static TableLayoutPanel Table()
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        private TableLayoutPanel FacesTable()
        {
            var t = Table();
            Row(t, "Name", _name);
            Row(t, "Tab face", _pickTab);
            Row(t, "Slot face", _pickSlot);
            return t;
        }

        /// <summary>Tab length, count and spacing together with the tab's width and height.</summary>
        private TableLayoutPanel TabsTable()
        {
            var t = Table();
            Row(t, "Length", _length);
            Row(t, "Amount", _count);
            Row(t, "Walls", _walls);
            Row(t, "Spacing", _spacing);
            Row(t, "Pitch", _pitch);
            Row(t, "Height", _height);
            Row(t, "Chamfer", _chamferSize);
            return t;
        }

        private TableLayoutPanel SlotTable()
        {
            var t = Table();
            Row(t, "Oversize", _clearance);
            Row(t, "Relief", _reliefRadius);
            return t;
        }

        /// <summary>The list of existing sets: two-line rows (name and tab count, parts and status) and icon buttons below.</summary>
        private Control ExistingTable()
        {
            _list.Columns.Add("Set", 240);
            _list.OwnerDraw = true;
            _list.BorderStyle = BorderStyle.None;
            _list.SmallImageList = new ImageList { ImageSize = new Size(1, 40) };   // sets the row height
            _list.DrawItem += DrawSetRow;
            _list.Resize += (s, e) => { if (_list.Columns.Count > 0) _list.Columns[0].Width = Math.Max(60, _list.ClientSize.Width); };

            _editSet = new IconButton(_theme, "Edit the selected tab and slot", ActionIcons.Edit);
            _deleteSet = new IconButton(_theme, "Delete the selected tab and slot", ActionIcons.Delete);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, WrapContents = false };
            foreach (Control b in new Control[] { _editSet, _deleteSet }) { b.Margin = new Padding(0, 6, 4, 0); buttons.Controls.Add(b); }

            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _list.Dock = DockStyle.Top;
            t.Controls.Add(_list, 0, 0);
            t.Controls.Add(buttons, 0, 1);
            t.Controls.Add(_listInfo, 0, 2);
            return t;
        }

        private void DrawSetRow(object sender, DrawListViewItemEventArgs e)
        {
            var row = e.Item.Tag as SetRow;
            Rectangle b = e.Bounds;
            bool selected = e.Item.Selected;
            using (var fill = new SolidBrush(selected ? _theme.AccentFill : _theme.Field)) e.Graphics.FillRectangle(fill, b);
            using (var line = new Pen(_theme.Back)) e.Graphics.DrawLine(line, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            if (row == null) return;

            Color statusColor = row.Status == "OK" ? _theme.Dim : row.Status == "Suppressed" ? _theme.Dim : Color.FromArgb(240, 110, 100);
            TextRenderer.DrawText(e.Graphics, row.Name, _list.Font, new Rectangle(b.X + 8, b.Y + 3, b.Width - 70, 18), _theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, row.Count + (row.Count == 1 ? " tab" : " tabs"), _list.Font, new Rectangle(b.Right - 62, b.Y + 3, 56, 18), _theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, row.Parts, _list.Font, new Rectangle(b.X + 8, b.Y + 21, b.Width - 100, 16), _theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, row.Status, _list.Font, new Rectangle(b.Right - 92, b.Y + 21, 86, 16), statusColor,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            c.Margin = new Padding(0, 3, 0, 3);
            if (c is FieldBox || c is SelectorField) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
            _rowLabels[c] = l;
        }

        private void ShowRow(Control c, bool visible)
        {
            c.Visible = visible;
            if (_rowLabels.TryGetValue(c, out Label l)) l.Visible = visible;
        }

        private void StyleButtons()
        {
            _list.BackColor = _theme.Field;
            _list.ForeColor = _theme.Text;
        }

        // ----- behaviour ----------------------------------------------------------------------------------------

        private void WireEvents()
        {
            _pickTab.Clicked += () => PickClicked?.Invoke(_pickTab.Active ? PickTarget.None : PickTarget.Tab);
            _pickSlot.Clicked += () => PickClicked?.Invoke(_pickSlot.Active ? PickTarget.None : PickTarget.Slot);
            _pickTab.ClearClicked += () => ClearRequested?.Invoke(PickTarget.Tab);
            _pickSlot.ClearClicked += () => ClearRequested?.Invoke(PickTarget.Slot);

            foreach (FieldBox box in new[] { _length, _count, _pitch, _chamferSize })
                box.TextChanged += (s, e) => Changed();
            _clearance.TextChanged += (s, e) =>
            {
                if (_loading) return;
                _clearanceEdited = _clearance.Text.Trim().Length > 0;
                Changed();
            };
            _reliefRadius.TextChanged += (s, e) =>
            {
                if (_loading) return;                       // the computed value being shown is not an edit
                _reliefEdited = _reliefRadius.Text.Trim().Length > 0;
                Changed();
            };
            _height.TextChanged += (s, e) =>
            {
                if (_loading) return;                       // the computed value being shown is not an edit
                _heightEdited = _height.Text.Trim().Length > 0;
                Changed();
            };
            _spacing.SelectedIndexChanged += (s, e) => { UpdateVisibility(); Changed(); };
            _walls.SelectedIndexChanged += (s, e) => Changed();
            _list.SelectedIndexChanged += (s, e) => UpdateListButtons();
            _list.DoubleClick += (s, e) => { string id = SelectedId(); if (id != null) EditRequested?.Invoke(id); };
            _editSet.Click += (s, e) => { string id = SelectedId(); if (id != null) EditRequested?.Invoke(id); };
            _deleteSet.Click += (s, e) => { string id = SelectedId(); if (id != null) DeleteRequested?.Invoke(id); };

            _ok.Click += (s, e) => OkClicked?.Invoke();
            _cancel.Click += (s, e) => CancelClicked?.Invoke();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CancelClicked?.Invoke(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Changed()
        {
            if (_loading) return;
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>Only the rows that apply to the current choices are shown, so there is nothing greyed out to read.</summary>
        private void UpdateVisibility()
        {
            bool pitch = _spacing.SelectedIndex == 1;
            ShowRow(_pitch, pitch);
            ShowRow(_walls, _isTube);
        }

        private bool _isTube;

        /// <summary>The Walls choice only applies when the tab face is a square tube.</summary>
        public void SetTabIsTube(bool tube)
        {
            if (tube == _isTube) return;
            _isTube = tube;
            UpdateVisibility();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _debounce.Dispose();
            base.Dispose(disposing);
        }
    }
}
