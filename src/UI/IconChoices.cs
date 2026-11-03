using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TabSlotAddIn.Core;

namespace TabSlotAddIn.UI
{
    /// <summary>
    /// Which Inventor command's icon each control uses. Chosen by right-clicking the icon in the panel and saved in
    /// %LOCALAPPDATA%\TabSlotAddIn\icons.txt as <c>Name = InventorCommandName</c>; a missing line keeps the default.
    /// </summary>
    internal static class IconChoices
    {
        private static readonly (string Slot, string Default, string What)[] Slots =
        {
            ("Ribbon", "SheetMetalFaceCmd", "ribbon button (restart Inventor after changing it)"),
            ("SpacingEqual", "PartRectangularPatternCmd", "spacing: equal"),
            ("SpacingPitch", "SketchGeneralDimensionCmd", "spacing: fixed pitch"),
            ("Edit", "PartDirectCmd", "Edit button in the list"),
            ("Delete", "", "Delete button in the list (empty = a plain cross)")
        };

        private static Dictionary<string, string> _chosen;

        public static string FilePath => Path.Combine(Path.GetDirectoryName(Log.Path), "icons.txt");

        /// <summary>The Inventor command whose icon the control <paramref name="slot"/> should use; empty for none.</summary>
        public static string Command(string slot)
        {
            Load();
            return _chosen.TryGetValue(slot, out string command) ? command : "";
        }

        /// <summary>Remembers a new choice for <paramref name="slot"/> and writes it to icons.txt.</summary>
        public static void Set(string slot, string command)
        {
            Load();
            _chosen[slot] = command;
            try
            {
                var lines = File.Exists(FilePath) ? new List<string>(File.ReadAllLines(FilePath)) : new List<string>();
                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string code = lines[i].Split('#')[0];
                    int eq = code.IndexOf('=');
                    if (eq > 0 && string.Equals(code.Substring(0, eq).Trim(), slot, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = slot.PadRight(16) + "= " + command;
                        replaced = true;
                        break;
                    }
                }
                if (!replaced) lines.Add(slot.PadRight(16) + "= " + command);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("icons.txt could not be written: " + ex.Message); }
        }

        private static void Load()
        {
            if (_chosen != null) return;
            _chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Slots) _chosen[s.Slot] = s.Default;

            try
            {
                if (!File.Exists(FilePath)) { WriteDefaultFile(); return; }
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string slot = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (value.Length > 0 && _chosen.ContainsKey(slot)) _chosen[slot] = value;
                }
                // choices made with earlier versions of the add-in
                if (_chosen["Delete"] == "PartDeleteFaceCmd") _chosen["Delete"] = "";
            }
            catch (Exception ex) { Log.Write("icons.txt could not be read: " + ex.Message); }
        }

        private static void WriteDefaultFile()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Icons of the Tab and Slot add-in. Right-click an icon in the panel to choose another one.");
            sb.AppendLine("# One line per control: Name = Inventor command name. Remove a line to use its default.");
            sb.AppendLine();
            foreach (var s in Slots) sb.AppendLine(s.Slot.PadRight(16) + "= " + s.Default.PadRight(30) + "# " + s.What);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }
    }
}
