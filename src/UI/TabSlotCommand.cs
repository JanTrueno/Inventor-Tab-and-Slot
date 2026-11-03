using System;
using System.Windows.Forms;
using Inventor;
using TabSlotAddIn.Core;

namespace TabSlotAddIn.UI
{
    /// <summary>
    /// The interactive "Tab and Slot" command: shows the feature panel, lets the user pick the tab and slot faces in the
    /// graphics window, previews the result live and creates (or, in edit mode, rebuilds) the features on OK.
    /// </summary>
    internal sealed class TabSlotCommand
    {
        private static TabSlotCommand _active;

        private readonly Inventor.Application _app;
        private readonly AssemblyDocument _doc;
        private readonly Units _units;
        private readonly InventorWindow _owner;
        private readonly bool _editing;
        private readonly TabSlotParams _original;   // as stored, edit mode only
        private TabSlotParams _p;

        private TabSlotPanel _panel;
        private InteractionEvents _events;
        private SelectEvents _select;
        private PreviewGraphics _preview;
        private HighlightSet _hiTab, _hiSlot, _hiTabPart, _hiSlotPart;

        private FaceProxy _tabProxy, _slotProxy;
        private FaceProxy _tabPicked;          // what the user clicked; _tabProxy is the end face actually used
        private bool _tabNeedsResolve;
        private FaceInfo _tab, _slot;
        private bool _tabRotated;
        private PickTarget _target = PickTarget.None;
        private Plan _plan;
        private bool _finished;
        private bool _committed;

        /// <summary>Raised once the command is completely torn down (OK, Cancel, or terminated by Inventor).</summary>
        public event Action Finished;

        private TabSlotCommand(Inventor.Application app, AssemblyDocument doc, TabSlotParams existing)
        {
            _app = app;
            _doc = doc;
            _units = new Units((Document)doc);
            _owner = new InventorWindow(app.MainFrameHWND);
            _editing = existing != null;
            _original = existing;
            _p = existing != null ? existing.Clone() : new TabSlotParams { Name = TabSlotStore.NextName(doc) };
        }

        public static bool IsRunning => _active != null;

        /// <summary>Ends the running command, if any (used when the add-in unloads).</summary>
        public static void CancelActive() => _active?.Finish(false);

        /// <summary>Starts the command; pass an existing set to edit it. Returns null if another instance is already running.</summary>
        public static TabSlotCommand Start(Inventor.Application app, AssemblyDocument doc, TabSlotParams existing, out string error)
        {
            error = null;
            if (_active != null) { error = "The Tab and Slot command is already running."; return null; }
            var cmd = new TabSlotCommand(app, doc, existing);
            _active = cmd;
            try
            {
                cmd.Run(out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (error != null) { cmd.Finish(false); return null; }
            return cmd;
        }

        // ----- start up -----------------------------------------------------------------------------------------

        private void Run(out string error)
        {
            error = null;
            _panel = new TabSlotPanel(_units, Theme.Detect(_app));
            _preview = new PreviewGraphics(_app, _doc);

            if (_editing)
            {
                FeatureHealth health = TabSlotBuilder.GetHealth(_doc, _original);
                if (health == FeatureHealth.PartNotLoaded)
                {
                    error = "The parts of this tab and slot are not loaded. Open them (or load the assembly fully) and try again.";
                    return;
                }
                // Roll the parts back to before the features so the original faces are whole again.
                TabSlotBuilder.SetSuppressed(_doc, _original, true);
                _tabProxy = TabSlotStore.ResolveFace(_doc, _original, _original.TabFaceKey);
                _tabPicked = _tabProxy;
                _slotProxy = TabSlotStore.ResolveFace(_doc, _original, _original.SlotFaceKey);
                _panel.ShowName(_p.Name);
            }
            _panel.LoadParams(_p);
            if (_editing)
            {
                RebuildFaceInfo(PickTarget.Tab);
                RebuildFaceInfo(PickTarget.Slot);
            }

            _panel.PickClicked += OnPickClicked;
            _panel.ParamsChanged += Recompute;
            _panel.OkClicked += () => { if (Commit()) Finish(true); };
            _panel.CancelClicked += () => Finish(false);

            _panel.ClearRequested += OnClearPick;
            _panel.EditRequested += OnEditSet;
            _panel.DeleteRequested += OnDeleteSet;

            StartInteraction();
            RefreshFaceTexts();
            AdvanceTarget();

            DockHost.Show(_panel, _editing ? "Edit Tab and Slot" : "Tab and Slot");
            SetSeeThrough(true);   // components are always see-through while the command runs
            RefreshSets();
            if (_editing) _panel.SetListEnabled(false);
            Recompute();
        }


        private void OnClearPick(PickTarget which)
        {
            if (which == PickTarget.Tab) { _tab = null; _tabProxy = null; _tabPicked = null; }
            else { _slot = null; _slotProxy = null; }
            RefreshFaceTexts();
            AdvanceTarget();
            Recompute();
        }

        // ----- see-through while picking ------------------------------------------------------------------------

        private readonly System.Collections.Generic.List<ComponentOccurrence> _madeTransparent = new System.Collections.Generic.List<ComponentOccurrence>();

        /// <summary>Makes every component transparent so touching faces and edges can be seen and picked; undone when the command ends.</summary>
        private void SetSeeThrough(bool on)
        {
            try
            {
                if (on)
                {
                    foreach (ComponentOccurrence occ in _doc.ComponentDefinition.Occurrences)
                    {
                        try
                        {
                            if (occ.Transparent) continue;
                            occ.Transparent = true;
                            _madeTransparent.Add(occ);
                        }
                        catch { /* occurrence cannot be changed */ }
                    }
                }
                else RestoreTransparency();
                _app.ActiveView.Update();
            }
            catch (Exception ex)
            {
                _panel.SetStatus("See-through failed: " + ex.Message, true);
            }
        }

        private void RestoreTransparency()
        {
            foreach (ComponentOccurrence occ in _madeTransparent)
            {
                try { occ.Transparent = false; } catch { }
            }
            _madeTransparent.Clear();
        }
        // ----- existing sets (management) -----------------------------------------------------------------------

        private void RefreshSets()
        {
            var rows = new System.Collections.Generic.List<SetRow>();
            foreach (TabSlotParams s in TabSlotStore.LoadAll(_doc))
            {
                rows.Add(new SetRow
                {
                    Id = s.Id, Name = s.Name, Count = s.Count,
                    Parts = System.IO.Path.GetFileNameWithoutExtension(s.TabPartFile) + " > " + System.IO.Path.GetFileNameWithoutExtension(s.SlotPartFile),
                    Status = StatusText(TabSlotBuilder.GetHealth(_doc, s))
                });
            }
            _panel.SetSets(rows);
        }

        private static string StatusText(FeatureHealth h)
        {
            switch (h)
            {
                case FeatureHealth.Ok: return "OK";
                case FeatureHealth.Suppressed: return "Suppressed";
                case FeatureHealth.Missing: return "Missing";
                case FeatureHealth.PartNotLoaded: return "Not loaded";
                default: return "Check";
            }
        }

        private TabSlotParams FindSet(string id)
        {
            foreach (TabSlotParams s in TabSlotStore.LoadAll(_doc)) if (s.Id == id) return s;
            return null;
        }

        /// <summary>Closes this command and reopens the panel on the chosen set (deferred so the panel is not disposed from its own click).</summary>
        private void OnEditSet(string id)
        {
            TabSlotParams set = FindSet(id);
            if (set == null) return;
            _panel.BeginInvoke(new Action(() =>
            {
                Finish(false);
                TabSlotCommand cmd = Start(_app, _doc, set, out string error);
                if (cmd == null && error != null)
                    MessageBox.Show(_owner, error, "Tab and Slot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }));
        }



        private void OnDeleteSet(string id)
        {
            TabSlotParams set = FindSet(id);
            if (set == null) return;
            DialogResult answer = MessageBox.Show(_owner,
                "Delete \"" + set.Name + "\"?\n\nThe tab is removed from the tab part and the slot from the slot part.",
                "Tab and Slot", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;

            Transaction tx = _app.TransactionManager.StartTransaction((_Document)_doc, "Delete Tab and Slot");
            try
            {
                _preview.Clear();
                TabSlotBuilder.DeleteFeatures(_doc, set);
                TabSlotStore.Remove(_doc, set.Id);
                _doc.Update();
                tx.End();
            }
            catch (Exception ex)
            {
                try { tx.Abort(); } catch { }
                MessageBox.Show(_owner, "Could not delete: " + ex.Message, "Tab and Slot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // The model changed under any picked faces; start the picks afresh.
            _tab = _slot = null;
            _tabProxy = _slotProxy = null; _tabPicked = null;
            RefreshFaceTexts();
            AdvanceTarget();
            RefreshSets();
            Recompute();
            try { _app.ActiveView.Update(); } catch { }
        }

        private void StartInteraction()
        {
            // Whole parts in blue (tab) and orange (slot); the picked faces on top in brighter cyan and yellow.
            _hiTabPart = _doc.CreateHighlightSet();
            _hiTabPart.Color = _app.TransientObjects.CreateColor(0, 120, 215);
            _hiSlotPart = _doc.CreateHighlightSet();
            _hiSlotPart.Color = _app.TransientObjects.CreateColor(235, 130, 20);
            _hiTab = _doc.CreateHighlightSet();
            _hiTab.Color = _app.TransientObjects.CreateColor(0, 235, 255);
            _hiSlot = _doc.CreateHighlightSet();
            _hiSlot.Color = _app.TransientObjects.CreateColor(255, 235, 0);

            _events = _app.CommandManager.CreateInteractionEvents();
            _events.InteractionDisabled = false;
            _select = _events.SelectEvents;
            _select.AddSelectionFilter(SelectionFilterEnum.kPartFaceFilter);
            _select.AddSelectionFilter(SelectionFilterEnum.kPartEdgeLinearFilter);
            _select.SingleSelectEnabled = true;
            _select.OnSelect += OnSelect;
            _events.OnTerminate += OnTerminate;
            _events.Start();
        }

        // ----- picking ------------------------------------------------------------------------------------------

        private void OnPickClicked(PickTarget wanted)
        {
            SetTarget(wanted);
        }

        private void SetTarget(PickTarget target)
        {
            _target = target;
            _panel.SetPicker(target);
            try
            {
                _select.Enabled = target != PickTarget.None;
                _events.StatusBarText = target == PickTarget.Tab ? "Select the face the tab grows from"
                                      : target == PickTarget.Slot ? "Select the face the slot is cut into"
                                      : "Tab and Slot";
            }
            catch { /* events already stopped */ }
        }

        private void AdvanceTarget()
        {
            if (_tab == null) SetTarget(PickTarget.Tab);
            else if (_slot == null) SetTarget(PickTarget.Slot);
            else SetTarget(PickTarget.None);
        }

        private void OnSelect(ObjectsEnumerator justSelected, SelectionDeviceEnum device, Point modelPosition,
                              Point2d viewPosition, Inventor.View view)
        {
            if (_finished || _target == PickTarget.None || justSelected.Count == 0) return;
            try
            {
                object picked = justSelected[1];
                picked = PickThrough(picked, modelPosition, viewPosition, view);
                try { _doc.SelectSet.Clear(); } catch { /* ignore */ }

                // Picking an edge of a sheet selects the thin end face it belongs to, and runs the tabs along that edge.
                bool fromEdge = picked is EdgeProxy;
                if (fromEdge)
                {
                    picked = EndFaceOfEdge((EdgeProxy)picked, out bool alongShort, out string edgeError);
                    if (picked == null) { _panel.SetStatus(edgeError, true); return; }
                    _panel.SetRotate(alongShort);
                }

                _p.RunAlongShortEdge = _panel.ReadRotate();
                FaceInfo info = FaceInfo.FromSelection(picked, _p.RunAlongShortEdge, out string error);
                if (info == null) { _panel.SetStatus(error, true); return; }

                // A broad sheet face can only be the slot face, whichever selector was active.
                PickTarget role = fromEdge || info.IsTube ? PickTarget.Tab : info.IsBroadSheetFace ? PickTarget.Slot : _target;
                // A plain face picked first while a tube or sheet edge is picked now: the earlier face was the slot.
                if (role == PickTarget.Tab && (fromEdge || info.IsTube) && _tab != null && _slot == null &&
                    !_tab.IsTube && !ReferenceEquals(_tab.PartDoc, info.PartDoc))
                {
                    _slotProxy = _tab.Proxy; _slot = _tab;
                }
                // A plain face while the tab is already a tube or edge pick: it can only be the slot.
                else if (role == PickTarget.Tab && !fromEdge && !info.IsTube && _tab != null && _slot == null &&
                         (_tab.IsTube || _tabFromEdge) && !ReferenceEquals(_tab.PartDoc, info.PartDoc))
                {
                    role = PickTarget.Slot;
                }
                if (role == PickTarget.Tab) _tabFromEdge = fromEdge || info.IsTube;
                if (role == PickTarget.Tab)
                {
                    _tabProxy = info.Proxy; _tab = info; _tabRotated = _p.RunAlongShortEdge;
                    _tabPicked = info.Proxy; _tabNeedsResolve = true;
                }
                else
                {
                    _slotProxy = info.Proxy; _slot = info; _tabNeedsResolve = true;
                    _panel.SetSlotDepth(Math.Max(info.DepthIntoBody(), 0.01));
                }
                AutoOrder();
                RefreshFaceTexts();
                AdvanceTarget();
                Recompute();
            }
            catch (Exception ex)
            {
                _panel.SetStatus("Could not use that selection: " + ex.Message, true);
            }
        }

        // ----- picking through parts -----------------------------------------------------------------------------

        private readonly System.Collections.Generic.List<FaceProxy> _rayFaces = new System.Collections.Generic.List<FaceProxy>();
        private int _rayIndex;
        private Point2d _rayClick;
        private PickTarget _rayTarget;
        private DateTime _rayTime;
        private bool _tabFromEdge;   // the tab was picked as an edge or tube ring, so it is certainly the tab

        /// <summary>
        /// A normal click picks the front face. Clicking the same spot again steps to the next face behind it along the
        /// line of sight, through touching and overlapping parts, like Inventor's "Select Other".
        /// </summary>
        private object PickThrough(object picked, Point modelPosition, Point2d viewPosition, Inventor.View view)
        {
            if (!(picked is FaceProxy)) { _rayFaces.Clear(); return picked; }

            bool sameSpot = _rayFaces.Count > 1 && _rayClick != null &&
                            Math.Abs(viewPosition.X - _rayClick.X) < 5 && Math.Abs(viewPosition.Y - _rayClick.Y) < 5 &&
                            (DateTime.Now - _rayTime).TotalSeconds < 2;   // a later click there is a new pick, not "next face"
            _rayTime = DateTime.Now;
            if (!sameSpot)
            {
                CollectRayFaces(modelPosition, view);
                _rayIndex = 0;
                _rayClick = viewPosition;
                _rayTarget = _target;
                if (_rayFaces.Count > 1)
                    _panel.SetStatus(_rayFaces.Count + " faces under the cursor. Click the same spot again to pick the next one.", false);
                return picked;
            }

            // the first pick already moved on to the next selector; the repeated click replaces that same pick
            if (_target != _rayTarget) SetTarget(_rayTarget);
            _rayIndex = (_rayIndex + 1) % _rayFaces.Count;
            FaceProxy next = _rayFaces[_rayIndex];
            string name;
            try { name = next.ContainingOccurrence.Name; } catch { name = "?"; }
            _panel.SetStatus("Face " + (_rayIndex + 1) + " of " + _rayFaces.Count + ": " + name, false);
            return next;
        }

        private void CollectRayFaces(Point modelPosition, Inventor.View view)
        {
            _rayFaces.Clear();
            try
            {
                Camera cam = view.Camera;
                V3 eye = V3.From(cam.Eye), target = V3.From(cam.Target), hit = V3.From(modelPosition);
                V3 dir = cam.Perspective ? (hit - eye).Unit() : (target - eye).Unit();
                V3 start = hit - dir * 5000;   // far in front of the model (cm)

                TransientGeometry tg = _app.TransientGeometry;
                ObjectsEnumerator found, points;
                _doc.ComponentDefinition.FindUsingRay(start.ToPoint(tg), tg.CreateUnitVector(dir.X, dir.Y, dir.Z), 0.02, out found, out points, false);

                var seen = new System.Collections.Generic.HashSet<int>();
                foreach (object o in found)
                {
                    var fp = o as FaceProxy;
                    if (fp == null || fp.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                    if (seen.Add(fp.TransientKey)) _rayFaces.Add(fp);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Ray pick failed: " + ex.Message);
                _rayFaces.Clear();
            }
        }

        /// <summary>The thinnest planar face next to an edge (the end face of a sheet), plus whether the edge runs across its long direction.</summary>
        private FaceProxy EndFaceOfEdge(EdgeProxy edge, out bool alongShort, out string error)
        {
            alongShort = false;
            error = null;
            if (edge.GeometryType != CurveTypeEnum.kLineSegmentCurve)
            {
                error = "Pick a straight edge.";
                return null;
            }

            FaceProxy best = null;
            FaceInfo bestInfo = null;
            foreach (Face f in edge.Faces)
            {
                var fp = f as FaceProxy;
                if (fp == null) continue;
                FaceInfo fi = FaceInfo.FromSelection(fp, false, out string ignore);
                if (fi == null) continue;
                if (bestInfo == null || fi.MinExtent < bestInfo.MinExtent) { best = fp; bestInfo = fi; }
            }
            if (best == null)
            {
                error = "That edge has no flat face to grow a tab from.";
                return null;
            }

            V3 dir = (V3.From(edge.StopVertex.Point) - V3.From(edge.StartVertex.Point)).Unit();
            // a tube end uses all four walls, so the run direction does not apply to it
            alongShort = !bestInfo.IsTube && Math.Abs(dir.Dot(bestInfo.U)) < 0.99;
            return best;
        }
        /// <summary>
        /// The tab grows from a narrow edge face and the slot is cut into a broad face. If the two were picked the
        /// other way round, swap them so the order of picking does not matter.
        /// </summary>
        private void AutoOrder()
        {
            if (_tab == null || _slot == null) return;
            if (_tab.MinExtent <= _slot.MinExtent * 1.5) return;

            FaceProxy tmp = _tabProxy; _tabProxy = _slotProxy; _slotProxy = tmp;
            _tabPicked = _tabProxy; _tabNeedsResolve = true;
            RebuildFaceInfo(PickTarget.Tab);
            RebuildFaceInfo(PickTarget.Slot);
            if (_slot != null) _panel.SetSlotDepth(Math.Max(_slot.DepthIntoBody(), 0.01));
        }

        private void RebuildFaceInfo(PickTarget which)
        {
            FaceProxy proxy = which == PickTarget.Tab ? _tabProxy : _slotProxy;
            if (proxy == null) return;
            FaceInfo info = FaceInfo.FromSelection(proxy, _p.RunAlongShortEdge, out string error);
            if (which == PickTarget.Tab) { _tab = info; _tabRotated = _p.RunAlongShortEdge; }
            else _slot = info;
        }

        private void RefreshFaceTexts()
        {
            _panel.SetFaceText(PickTarget.Tab, _tab != null ? "1 face (" + _tab.DisplayName + ")" : "Select Face", _tab != null);
            _panel.SetFaceText(PickTarget.Slot, _slot != null ? "1 face (" + _slot.DisplayName + ")" : "Select Face", _slot != null);

            _hiTabPart.Clear();
            _hiSlotPart.Clear();
            _hiTab.Clear();
            _hiSlot.Clear();
            try { if (_tab != null) _hiTabPart.AddItem(_tab.Occurrence); } catch { }
            try { if (_slot != null) _hiSlotPart.AddItem(_slot.Occurrence); } catch { }
            if (_tabProxy != null) _hiTab.AddItem(_tabProxy);
            if (_slotProxy != null) _hiSlot.AddItem(_slotProxy);
        }

        // ----- preview ------------------------------------------------------------------------------------------

        private void Recompute()
        {
            if (_finished) return;
            _plan = null;

            ResolveTab();
            string readError = _panel.ReadInto(_p);
            if (readError != null) { Fail(readError); return; }
            _panel.SetTabIsTube(_tab != null && _tab.IsTube);
            if (_tab == null || _slot == null)
            {
                _preview.Clear();
                _panel.SetCanCommit(false);
                _panel.SetStatus("", false);
                return;
            }
            if (_tabRotated != _p.RunAlongShortEdge) RebuildFaceInfo(PickTarget.Tab);
            if (_tab == null) { Fail("The tab face could not be read."); return; }

            try
            {
                _plan = Plan.Create(_p, _tab, _slot);
            }
            catch (Exception ex)
            {
                Fail("Could not compute the tabs: " + ex.Message);
                return;
            }
            if (!_plan.IsValid) { Fail(_plan.Error); return; }

            _panel.ShowComputedHeight(_plan.TabHeight);
            _panel.ShowComputedClearance(_plan.Clearance);
            _panel.ShowComputedRelief(_p.ReliefFor(_slot.DepthIntoBody()));
            try
            {
                _preview.Show(_plan);
            }
            catch (Exception ex)
            {
                // A failed preview must not block the feature itself.
                _preview.Clear();
                _panel.SetCanCommit(true);
                _panel.SetStatus("Preview unavailable (" + ex.Message + "), the feature can still be created.", true);
                return;
            }
            _panel.SetCanCommit(true);
            _panel.SetStatus((_tab.IsTube ? "Square tube: " + _tab.Strips.Count + " walls. " : "") + $"{_plan.Tabs.Count} tab(s), {_units.Format(_p.TabLength)} long, {_units.Format(_plan.TabHeight)} high.", false);

            void Fail(string message)
            {
                _preview.Clear();
                _panel.SetCanCommit(false);
                _panel.SetStatus(message, true);
            }
        }

        /// <summary>
        /// The clicked tab face may not be the end that meets the slot part (a tube wall, or the far end). Once the slot face is
        /// known, switch to the end face that points at it; the part keeps being highlighted either way.
        /// </summary>
        private void ResolveTab()
        {
            if (!_tabNeedsResolve || _tabPicked == null || _slot == null) return;
            _tabNeedsResolve = false;
            try
            {
                FaceInfo picked = FaceInfo.FromSelection(_tabPicked, _p.RunAlongShortEdge, out string error);
                if (picked == null) return;

                FaceProxy end = _tabPicked;
                if (!EndFaceFinder.PointsAtSlot(picked, _slot))
                {
                    FaceProxy found = EndFaceFinder.Find(_tabPicked, _slot);
                    if (found != null) end = found;
                }
                _tabProxy = end;
                _tab = ReferenceEquals(end, _tabPicked) ? picked : FaceInfo.FromSelection(end, _p.RunAlongShortEdge, out string e2);
                _tabRotated = _p.RunAlongShortEdge;
                RefreshFaceTexts();
            }
            catch (Exception ex)
            {
                _panel.SetStatus("Could not find the tab end: " + ex.Message, true);
            }
        }

        // ----- commit -------------------------------------------------------------------------------------------

        private bool Commit()
        {
            Recompute();
            if (_plan == null || !_plan.IsValid)
            {
                Log.Write("Commit skipped: " + (_plan == null ? "no plan" : _plan.Error));
                return false;
            }
            Log.Write($"Commit: tab part={_tab.DisplayName} tube={_tab.IsTube} strips={_tab.Strips.Count} tabs={_plan.Tabs.Count} height={_plan.TabHeight:0.###}cm slot part={_slot.DisplayName} sheet={TabSlotBuilder.IsSheetMetal(_slot)} edit={_editing}");

            Transaction tx = _app.TransactionManager.StartTransaction((_Document)_doc, _editing ? "Edit Tab and Slot" : "Tab and Slot");
            try
            {
                _preview.Clear();
                var p = _p;
                string newName = _panel.ReadName();
                if (newName != null) p.Name = newName;
                p.TabPartFile = _tab.PartDoc.FullDocumentName;
                p.SlotPartFile = _slot.PartDoc.FullDocumentName;
                p.TabOccName = _tab.DisplayName;
                p.SlotOccName = _slot.DisplayName;
                TabSlotStore.CaptureKeys(_doc, p, _tab, _slot);

                FaceInfo tab = _tab, slot = _slot;
                Plan plan = _plan;
                if (_editing)
                {
                    TabSlotBuilder.DeleteFeatures(_doc, _original);
                    // The model was rebuilt; fetch fresh face objects from the keys captured just before.
                    tab = FaceInfo.FromSelection(TabSlotStore.ResolveFace(_doc, p, p.TabFaceKey), p.RunAlongShortEdge, out string e1);
                    slot = FaceInfo.FromSelection(TabSlotStore.ResolveFace(_doc, p, p.SlotFaceKey), p.RunAlongShortEdge, out string e2);
                    if (tab == null || slot == null) throw new InvalidOperationException("The faces could not be found again after removing the old features.");
                    plan = Plan.Create(p, tab, slot);
                    if (!plan.IsValid) throw new InvalidOperationException(plan.Error);
                }

                TabSlotBuilder.Create(_app, p, tab, slot, plan);
                TabSlotStore.Save(_doc, p);
                _doc.Update();
                tx.End();
                _committed = true;
                Log.Write("Commit OK");
                return true;
            }
            catch (Exception ex)
            {
                try { tx.Abort(); } catch { /* nothing to undo */ }
                Log.Write("Commit FAILED: " + ex);
                _panel.SetStatus("Could not create the tab and slot: " + ex.Message, true);
                MessageBox.Show(_owner, "Could not create the tab and slot:\n\n" + ex.Message + "\n\nDetails: " + Log.Path,
                                "Tab and Slot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Recompute();
                return false;
            }
        }


        // ----- tear down ----------------------------------------------------------------------------------------

        private void OnTerminate()
        {
            // Inventor ended the interaction (Esc, or another command started): treat as cancel.
            if (!_finished) Finish(false);
        }

        private void Finish(bool committed)
        {
            if (_finished) return;
            _finished = true;

            try { if (_select != null) _select.OnSelect -= OnSelect; } catch { }
            try { if (_events != null) { _events.OnTerminate -= OnTerminate; _events.Stop(); } } catch { }
            try { RestoreTransparency(); } catch { }
            try { _preview?.Dispose(); } catch { }
            try { _hiTabPart?.Delete(); } catch { }
            try { _hiSlotPart?.Delete(); } catch { }
            try { _hiTab?.Delete(); } catch { }
            try { _hiSlot?.Delete(); } catch { }

            if (_editing && !_committed)
            {
                try { TabSlotBuilder.SetSuppressed(_doc, _original, false); } catch { }
            }

            try { DockHost.Hide(_panel); } catch { }
            try { _app.ActiveView.Update(); } catch { }

            _active = null;
            Finished?.Invoke();
        }
    }
}
