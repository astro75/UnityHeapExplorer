//
// Heap Explorer for Unity. Copyright (c) 2019-2020 Peter Schraut (www.console-dev.de). See LICENSE.md
// https://github.com/pschraut/UnityHeapExplorer/
//
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.IMGUI.Controls;
using UnityEditor;
using System.Linq;
using System.Text;
using static HeapExplorer.Option;

namespace HeapExplorer
{
    public class ObjectProxy
    {
        public PackedMemorySnapshot snapshot;
        public Option<RichNativeObject> native;
        public Option<RichManagedObject> managed;
        public Option<RichGCHandle> gcHandle;
        public Option<RichStaticField> staticField;

        public System.Int64 id
        {
            get
            {
                if (native.isSome)
                    return (1 << 62) + native.__unsafeGet.packed.nativeObjectsArrayIndex;

                if (managed.isSome)
                    return (1 << 61) + managed.__unsafeGet.packed.managedObjectsArrayIndex.index;

                if (gcHandle.isSome)
                    return (1 << 60) + gcHandle.__unsafeGet.packed.gcHandlesArrayIndex;

                if (staticField.isSome)
                    return (1 << 59) + staticField.__unsafeGet.packed.staticFieldsArrayIndex;

                return 0;
            }
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedNativeUnityEngineObject packed)
        {
            snapshot = snp;
            native = Some(new RichNativeObject(snp, packed.nativeObjectsArrayIndex));
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedManagedObject packed)
        {
            snapshot = snp;
            managed = Some(new RichManagedObject(snp, packed.managedObjectsArrayIndex));
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedGCHandle packed)
        {
            snapshot = snp;
            gcHandle = Some(new RichGCHandle(snp, packed.gcHandlesArrayIndex));
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedManagedStaticField packed)
        {
            snapshot = snp;
            staticField = Some(new RichStaticField(snp, packed.staticFieldsArrayIndex));
        }

        public override string ToString()
        {
            if (native.isSome)
                return $"Native, {native.__unsafeGet}";

            if (managed.isSome)
                return $"Managed, {managed.__unsafeGet}";

            if (gcHandle.isSome)
                return $"GCHandle, {gcHandle.__unsafeGet}";

            if (staticField.isSome)
                return $"StaticField, {staticField.__unsafeGet}";

            return base.ToString();
        }
    }

    public enum RootPathReason
    {
        // The order of elements here reflects how rootpaths are
        // sorted in the RootPath view. Things that keep objects alive
        // are at the bottom of this enum.

        None = 0,
        AssetBundle,
        Component,
        GameObject,
        UnityManager,
        DontUnloadUnusedAsset,
        DontDestroyOnLoad,
        Static,
        Unknown, // make most important, so I easily spot if I forgot to support something
    }

    public class RootPath : System.IComparable<RootPath>
    {
        public int count
        {
            get
            {
                return m_Items.Length;
            }
        }

        public RootPathReason reason
        {
            get;
            private set;
        }

        public string reasonString
        {
            get
            {
                switch(reason)
                {
                    case RootPathReason.None:
                        return "";
                    case RootPathReason.AssetBundle:
                        return "this object is an assetbundle, which is never unloaded automatically, but only through an explicit .Unload() call.";
                    case RootPathReason.Component:
                        return "this object is a component, which lives on a gameobject. it will be unloaded on next scene load.";
                    case RootPathReason.GameObject:
                        return "this is a gameobject, that is either part of the loaded scene, or was generated by script. It will be unloaded on next scene load if nobody is referencing it";
                    case RootPathReason.UnityManager:
                        return "this is an internal unity'manager' style object, which is a global object that will never be unloaded";
                    case RootPathReason.DontUnloadUnusedAsset:
                        return "the DontUnloadUnusedAsset hideflag is set on this object. Unity's builtin resources set this flag. Users can also set the flag themselves";
                    case RootPathReason.DontDestroyOnLoad:
                        return "DontDestroyOnLoad() was called on this object, so it will never be unloaded";
                    case RootPathReason.Static:
                        return "Static fields are global variables. Anything they reference will not be unloaded.";
                    case RootPathReason.Unknown:
                        return "This object is a root, but the memory profiler UI does not yet understand why";
                }

                return "???";
            }
        }

        readonly ObjectProxy[] m_Items;

        public RootPath(RootPathReason reason, ObjectProxy[] path) {
            this.reason = reason;
            this.m_Items = path;
        }

        public ObjectProxy this[int index]
        {
            get
            {
                return m_Items[index];
            }
        }

        public int CompareTo(RootPath other)
        {
            var x = ((long)reason << 40) + Mathf.Max(0, int.MaxValue - count);
            var y = ((long)other.reason << 40) + Mathf.Max(0, int.MaxValue - other.count);
            return y.CompareTo(x);
        }
    }

    public class RootPathUtility
    {
        List<RootPath> m_Items = new List<RootPath>();
        bool m_Abort;
        bool m_IsBusy;
        int m_ScanCount;

        static readonly int s_IterationLoopGuard = 1000000;

        public bool isBusy
        {
            get
            {
                return m_IsBusy;
            }
        }

        /// <summary>
        /// Gets the number of scanned objects. Updates as the RootPathUtility is busy.
        /// </summary>
        public int scanned
        {
            get
            {
                return m_ScanCount;
            }
        }

        /// <summary>
        /// Gets the number of root paths that were found.
        /// </summary>
        public int count
        {
            get
            {
                return m_Items.Count;
            }
        }

        public RootPath this[int index]
        {
            get
            {
                return m_Items[index];
            }
        }

        /// <summary>
        /// Gets the shortest root path.
        /// </summary>
        public RootPath shortestPath
        {
            get
            {
                RootPath value = null;

                if (m_Items.Count > 0)
                {
                    value = m_Items[0];

                    // Find the shortest path
                    foreach (var p in m_Items)
                    {
                        if (p.count < value.count && value.reason != RootPathReason.Static)
                            value = p;

                        // Assign if it's a path to static
                        if (p.reason == RootPathReason.Static && value.reason != RootPathReason.Static)
                            value = p;

                        // Find the shortest path to static
                        if (p.reason == RootPathReason.Static && p.count < value.count)
                            value = p;
                    }
                }

                if (value == null)
                    value = new RootPath(RootPathReason.None, new ObjectProxy[0]);

                return value;
            }
        }

        public void Abort()
        {
            m_Abort = true;
        }

        public void Find(ObjectProxy obj)
        {
            m_IsBusy = true;
            m_Items = new List<RootPath>();
            var seen = new HashSet<long>();

            var queue = new Queue<List<ObjectProxy>>();
            queue.Enqueue(new List<ObjectProxy> { obj });

            int issues = 0;
            int guard = 0;
            while (queue.Any())
            {
                if (m_Abort)
                    break;

                if (++guard > s_IterationLoopGuard)
                {
                    Debug.LogWarning("RootPath iteration loop guard kicked in.");
                    //m_Items = new List<RootPath>();
                    break;
                }

                var pop = queue.Dequeue();
                var tip = pop.Last();

                if (IsRoot(tip, out var reason))
                {
                    m_Items.Add(new RootPath(reason, pop.ToArray()));
                    continue;
                }

                var referencedBy = GetReferencedBy(tip, ref issues);
                foreach (var next in referencedBy)
                {
                    if (seen.Contains(next.id))
                        continue;
                    seen.Add(next.id);

                    var dupe = new List<ObjectProxy>(pop) { next };
                    queue.Enqueue(dupe);

                    m_ScanCount++;
                }
            }

            m_Items.Sort();
            m_IsBusy = false;

            if (issues > 0)
                Debug.LogWarningFormat("{0} issues have been detected while finding root-paths. This is most likely related to an earlier bug in Heap Explorer, that started to occur with Unity 2019.3 and causes that (some) object connections are invalid. Please capture a new memory snapshot.", issues);
        }

        static List<ObjectProxy> GetReferencedBy(ObjectProxy obj, ref int issues)
        {
            var referencedBy = new List<PackedConnection>(32);

            {if (obj.staticField.valueOut(out var staticField))
                obj.snapshot.GetConnections(staticField.packed, null, referencedBy);}

            {if (obj.native.valueOut(out var nativeObject))
                obj.snapshot.GetConnections(nativeObject.packed, null, referencedBy);}

            {if (obj.managed.valueOut(out var managedObject))
                obj.snapshot.GetConnections(managedObject.packed, null, referencedBy);}

            if (obj.gcHandle.isSome)
                obj.snapshot.GetConnections(obj.gcHandle.__unsafeGet.packed, null, referencedBy);

            var value = new List<ObjectProxy>(referencedBy.Count);
            foreach (var c in referencedBy)
            {
                switch (c.from.kind)
                {
                    case PackedConnection.Kind.Native:
                        if (c.from.index < 0 || c.from.index >= obj.snapshot.nativeObjects.Length)
                        {
                            issues++;
                            continue;
                        }
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.nativeObjects[c.from.index]));
                        break;

                    case PackedConnection.Kind.Managed:
                        if (c.from.index < 0 || c.from.index >= obj.snapshot.managedObjects.Length)
                        {
                            issues++;
                            continue;
                        }
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.managedObjects[c.from.index]));
                        break;

                    case PackedConnection.Kind.GCHandle:
                        if (c.from.index < 0 || c.from.index >= obj.snapshot.gcHandles.Length)
                        {
                            issues++;
                            continue;
                        }
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.gcHandles[c.from.index]));
                        break;

                    case PackedConnection.Kind.StaticField:
                        if (c.from.index < 0 || c.from.index >= obj.snapshot.managedStaticFields.Length)
                        {
                            issues++;
                            continue;
                        }
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.managedStaticFields[c.from.index]));
                        break;
                }
            }
            return value;
        }


        bool IsRoot(ObjectProxy thing, out RootPathReason reason)
        {
            reason = RootPathReason.None;

            if (thing.staticField.isSome)
            {
                reason = RootPathReason.Static;
                return true;
            }

            if (thing.managed.isSome)
            {
                return false;
            }

            if (thing.gcHandle.isSome)
            {
                return false;
            }

            if (!thing.native.valueOut(out var native))
                throw new System.ArgumentException("Unknown type: " + thing.GetType());

            if (native.isManager)
            {
                reason = RootPathReason.UnityManager;
                return true;
            }

            if (native.isDontDestroyOnLoad)
            {
                reason = RootPathReason.DontDestroyOnLoad;
                return true;
            }

            if ((native.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0)
            {
                reason = RootPathReason.DontUnloadUnusedAsset;
                return true;
            }

            if (native.isPersistent)
            {
                return false;
            }

            if (native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeComponent))
            {
                reason = RootPathReason.Component;
                return true;
            }

            if (native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeGameObject))
            {
                reason = RootPathReason.GameObject;
                return true;
            }

            if (native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeAssetBundle))
            {
                reason = RootPathReason.AssetBundle;
                return true;
            }

            reason = RootPathReason.Unknown;
            return true;
        }
    }

    public class RootPathControl : AbstractTreeView
    {
        public System.Action<RootPath> onSelectionChange;

        PackedMemorySnapshot m_Snapshot;
        int m_UniqueId = 1;

        enum Column
        {
            Type,
            Name,
            Depth,
            Address,
        }

        public RootPathControl(HeapExplorerWindow window, string editorPrefsKey, TreeViewState state)
            : base(window, editorPrefsKey, state, new MultiColumnHeader(
                new MultiColumnHeaderState(new[]
                {
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Type"), width = 200, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("C++ Name"), width = 200, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Depth"), width = 80, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Address"), width = 200, autoResize = true },
                })))
        {
            columnIndexForTreeFoldouts = 0;
            multiColumnHeader.canSort = false;

            Reload();
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);
            if (onSelectionChange == null)
                return;

            if (selectedIds == null || selectedIds.Count == 0)
            {
                onSelectionChange.Invoke(null);
                return;
            }

            var selectedItem = FindItem(selectedIds[0], rootItem) as Item;
            if (selectedItem == null)
            {
                onSelectionChange.Invoke(null);
                return;
            }

            onSelectionChange.Invoke(selectedItem.rootPath);
        }

        protected override int OnSortItem(TreeViewItem x, TreeViewItem y)
        {
            return 0;
        }

        public TreeViewItem BuildTree(PackedMemorySnapshot snapshot, RootPathUtility paths)
        {
            m_Snapshot = snapshot;
            m_UniqueId = 1;

            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            if (m_Snapshot == null || paths == null || paths.count == 0)
            {
                root.AddChild(new TreeViewItem { id = 1, depth = -1, displayName = "" });
                return root;
            }

            for (var j=0; j< paths.count; ++j)
            {
                if (window.isClosing) // the window is closing
                    break;

                var path = paths[j];
                var parent = root;

                for (var n = path.count - 1; n >= 0; --n)
                {
                    var obj = path[n];
                    Item newItem = null;

                    if (obj.native.isSome)
                        newItem = AddNativeUnityObject(parent, obj.native.__unsafeGet.packed);
                    else if (obj.managed.isSome)
                        newItem = AddManagedObject(parent, obj.managed.__unsafeGet.packed);
                    else if (obj.gcHandle.isSome)
                        newItem = AddGCHandle(parent, obj.gcHandle.__unsafeGet.packed);
                    else if (obj.staticField.isSome)
                        newItem = AddStaticField(parent, obj.staticField.__unsafeGet.packed);

                    if (parent == root)
                    {
                        parent = newItem;
                        newItem.rootPath = path;
                    }
                }
            }

            return root;
        }

        Item AddGCHandle(TreeViewItem parent, PackedGCHandle gcHandle)
        {
            var item = new GCHandleItem
            {
                id = m_UniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_Snapshot, gcHandle.gcHandlesArrayIndex);
            parent.AddChild(item);
            return item;
        }

        Item AddManagedObject(TreeViewItem parent, PackedManagedObject managedObject)
        {
            var item = new ManagedObjectItem
            {
                id = m_UniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_Snapshot, managedObject.managedObjectsArrayIndex);
            parent.AddChild(item);
            return item;
        }

        Item AddNativeUnityObject(TreeViewItem parent, PackedNativeUnityEngineObject nativeObject)
        {
            var item = new NativeObjectItem
            {
                id = m_UniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_Snapshot, nativeObject);
            parent.AddChild(item);
            return item;
        }

        Item AddStaticField(TreeViewItem parent, PackedManagedStaticField staticField)
        {
            var item = new ManagedStaticFieldItem
            {
                id = m_UniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_Snapshot, staticField.staticFieldsArrayIndex);
            parent.AddChild(item);
            return item;
        }


        ///////////////////////////////////////////////////////////////////////////
        // TreeViewItem's
        ///////////////////////////////////////////////////////////////////////////

        class Item : AbstractTreeViewItem
        {
            public RootPath rootPath;

            protected RootPathControl m_Owner;
            protected string m_Value;
            protected System.UInt64 m_Address;

            public override void GetItemSearchString(string[] target, out int count, out string type, out string label)
            {
                base.GetItemSearchString(target, out count, out type, out label);

                type = displayName;
                target[count++] = displayName;
                target[count++] = m_Value;
                target[count++] = string.Format(StringFormat.Address, m_Address);
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0 && depth == 0)
                {
                    switch(rootPath.reason)
                    {
                        case RootPathReason.Static:
                        case RootPathReason.DontDestroyOnLoad:
                        case RootPathReason.DontUnloadUnusedAsset:
                        case RootPathReason.UnityManager:
                            GUI.Box(HeEditorGUI.SpaceL(ref position, position.height), new GUIContent(HeEditorStyles.warnImage, rootPath.reasonString), HeEditorStyles.iconStyle);
                            break;
                    }
                }

                switch ((Column)column)
                {
                    case Column.Type:
                        HeEditorGUI.TypeName(position, displayName);
                        break;

                    case Column.Name:
                        EditorGUI.LabelField(position, m_Value);
                        break;

                    case Column.Address:
                        if (m_Address != 0) // statics dont have an address in PackedMemorySnapshot and I don't want to display a misleading 0
                            HeEditorGUI.Address(position, m_Address);
                        break;

                    case Column.Depth:
                        if (rootPath != null)
                            EditorGUI.LabelField(position, rootPath.count.ToString());
                        break;
                }

                if (column == 0) {
                    var e = Event.current;
                    if (e.type == EventType.ContextClick && position.Contains(e.mousePosition))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Copy path"), on: false, (GenericMenu.MenuFunction2)delegate (object userData)
                        {
                            var rootPath = (RootPath) userData;
                            var text = new StringBuilder(rootPath.reasonString);
                            text.Append("\n\n");
                            var count = rootPath.count;
                            for (var idx = 0; idx < count; idx++) {
                                // Go backwards to mimic the view in Unity.
                                var objectProxy = rootPath[count - idx - 1];
                                text.AppendFormat("[{0}] {1}\n", idx, objectProxy);
                            }
                            EditorGUIUtility.systemCopyBuffer = text.ToString();
                        }, rootPath);
                        menu.ShowAsContext();
                    }
                }
            }
        }

        // ------------------------------------------------------------------------

        class GCHandleItem : Item
        {
            PackedMemorySnapshot m_Snapshot;
            RichGCHandle m_GCHandle;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, int gcHandleArrayIndex)
            {
                m_Owner = owner;
                m_Snapshot = snapshot;
                m_GCHandle = new RichGCHandle(m_Snapshot, gcHandleArrayIndex);

                displayName = "GCHandle";
                m_Value = m_GCHandle.managedObject.fold("", _ => _.type.name);
                m_Address = m_GCHandle.managedObjectAddress;
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_Owner.window.OnGoto(new GotoCommand(m_GCHandle));
                    }

                    {if (m_GCHandle.nativeObject.valueOut(out var nativeObject)) {
                        if (HeEditorGUI.CppButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(nativeObject));
                        }
                    }}

                    {if (m_GCHandle.managedObject.valueOut(out var managedObject)) {
                        if (HeEditorGUI.CsButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(managedObject));
                        }
                    }}
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class ManagedObjectItem : Item
        {
            RichManagedObject m_ManagedObject;

            public void Initialize(
                RootPathControl owner, PackedMemorySnapshot snapshot, PackedManagedObject.ArrayIndex arrayIndex
            )
            {
                m_Owner = owner;
                m_ManagedObject = new RichManagedObject(snapshot, arrayIndex);

                displayName = m_ManagedObject.type.name;
                m_Address = m_ManagedObject.address;
                m_Value = m_ManagedObject.nativeObject.fold("", _ => _.name);
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CsButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_Owner.window.OnGoto(new GotoCommand(m_ManagedObject));
                    }

                    {if (m_ManagedObject.gcHandle.valueOut(out var gcHandle)) {
                        if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(gcHandle));
                        }
                    }}

                    {if (m_ManagedObject.nativeObject.valueOut(out var nativeObject)) {
                        if (HeEditorGUI.CppButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(nativeObject));
                        }
                    }}
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class ManagedStaticFieldItem : Item
        {
            PackedMemorySnapshot m_Snapshot;
            PackedManagedStaticField m_StaticField;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, int arrayIndex)
            {
                m_Owner = owner;
                m_Snapshot = snapshot;
                m_StaticField = m_Snapshot.managedStaticFields[arrayIndex];

                var staticClassType = m_Snapshot.managedTypes[m_StaticField.managedTypesArrayIndex];
                var staticField = staticClassType.fields[m_StaticField.fieldIndex];

                m_Address = 0;
                displayName = staticClassType.name;
                m_Value = "static " + staticField.name;
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CsStaticButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_Owner.window.OnGoto(new GotoCommand(new RichStaticField(m_Snapshot, m_StaticField.staticFieldsArrayIndex)));
                    }
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class NativeObjectItem : Item
        {
            PackedMemorySnapshot m_Snapshot;
            RichNativeObject m_NativeObject;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, PackedNativeUnityEngineObject nativeObject)
            {
                m_Owner = owner;
                m_Snapshot = snapshot;
                m_NativeObject = new RichNativeObject(snapshot, nativeObject.nativeObjectsArrayIndex);

                m_Value = m_NativeObject.name;
                m_Address = m_NativeObject.address;
                displayName = m_NativeObject.type.name;

                // If it's a MonoBehaviour or ScriptableObject, use the C# typename instead
                // It makes it easier to understand what it is, otherwise everything displays 'MonoBehaviour' only.
                // TODO: Move to separate method
                if (m_NativeObject.type.IsSubclassOf(m_Snapshot.coreTypes.nativeMonoBehaviour) || m_NativeObject.type.IsSubclassOf(m_Snapshot.coreTypes.nativeScriptableObject))
                {
                    if (m_Snapshot.FindNativeMonoScriptType(m_NativeObject.packed.nativeObjectsArrayIndex).valueOut(out var tpl))
                    {
                        if (!string.IsNullOrEmpty(tpl.monoScriptName))
                            displayName = tpl.monoScriptName;
                    }
                }
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CppButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_Owner.window.OnGoto(new GotoCommand(m_NativeObject));
                    }

                    {if (m_NativeObject.gcHandle.valueOut(out var gcHandle)) {
                        if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(gcHandle));
                        }
                    }}

                    {if (m_NativeObject.managedObject.valueOut(out var managedObject)) {
                        if (HeEditorGUI.CsButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_Owner.window.OnGoto(new GotoCommand(managedObject));
                        }
                    }}
                }

                base.OnGUI(position, column);
            }
        }
    }
}
