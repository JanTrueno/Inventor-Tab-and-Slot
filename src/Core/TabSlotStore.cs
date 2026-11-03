using System;
using System.Collections.Generic;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>
    /// Persists the tab-and-slot sets on the assembly document (one attribute set per set, holding the serialized
    /// parameters) and keeps the face reference keys needed to re-select the faces when a set is edited.
    /// </summary>
    internal static class TabSlotStore
    {
        private const string DataAttribute = "Data";

        public static List<TabSlotParams> LoadAll(AssemblyDocument asm)
        {
            var list = new List<TabSlotParams>();
            foreach (AttributeSet set in asm.AttributeSets)
            {
                try
                {
                    if (!set.Name.StartsWith(TabSlotBuilder.AttrPrefix, StringComparison.Ordinal)) continue;
                    if (!set.NameIsUsed[DataAttribute]) continue;
                    list.Add(TabSlotParams.Parse(Convert.ToString(set[DataAttribute].Value)));
                }
                catch { /* unreadable set, ignore */ }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public static void Save(AssemblyDocument asm, TabSlotParams p)
        {
            string name = TabSlotBuilder.AttrPrefix + p.Id;
            AttributeSet set = asm.AttributeSets.NameIsUsed[name] ? asm.AttributeSets[name] : asm.AttributeSets.Add(name, false);
            if (set.NameIsUsed[DataAttribute]) set[DataAttribute].Value = p.Serialize();
            else set.Add(DataAttribute, ValueTypeEnum.kStringType, p.Serialize());
        }

        public static void Remove(AssemblyDocument asm, string id)
        {
            string name = TabSlotBuilder.AttrPrefix + id;
            if (asm.AttributeSets.NameIsUsed[name]) asm.AttributeSets[name].Delete();
        }

        /// <summary>Next free "Tab and Slot N" name in this assembly.</summary>
        public static string NextName(AssemblyDocument asm)
        {
            var used = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (TabSlotParams p in LoadAll(asm)) used.Add(p.Name);
            for (int n = 1; ; n++)
            {
                string candidate = "Tab and Slot " + n;
                if (!used.Contains(candidate)) return candidate;
            }
        }

        // ----- face reference keys --------------------------------------------------------------------------------

        public static void CaptureKeys(AssemblyDocument asm, TabSlotParams p, FaceInfo tab, FaceInfo slot)
        {
            ReferenceKeyManager rkm = asm.ReferenceKeyManager;
            int ctx = rkm.CreateKeyContext();
            try
            {
                byte[] key = new byte[0];
                tab.Proxy.GetReferenceKey(ref key, ctx);
                p.TabFaceKey = Convert.ToBase64String(key);

                key = new byte[0];
                slot.Proxy.GetReferenceKey(ref key, ctx);
                p.SlotFaceKey = Convert.ToBase64String(key);

                byte[] ctxData = new byte[0];
                rkm.SaveContextToArray(ctx, ref ctxData);
                p.KeyContext = Convert.ToBase64String(ctxData);
            }
            finally
            {
                try { rkm.ReleaseKeyContext(ctx); } catch { /* ignore */ }
            }
        }

        /// <summary>Re-binds a stored face key; returns null when the face can no longer be found.</summary>
        public static FaceProxy ResolveFace(AssemblyDocument asm, TabSlotParams p, string keyBase64)
        {
            if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(p.KeyContext)) return null;
            ReferenceKeyManager rkm = asm.ReferenceKeyManager;
            byte[] ctxData = Convert.FromBase64String(p.KeyContext);
            int ctx = rkm.LoadContextFromArray(ref ctxData);
            try
            {
                byte[] key = Convert.FromBase64String(keyBase64);
                if (!rkm.CanBindKeyToObject(ref key, ctx, out object obj, out object matchType)) return null;
                return obj as FaceProxy;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { rkm.ReleaseKeyContext(ctx); } catch { /* ignore */ }
            }
        }
    }
}
