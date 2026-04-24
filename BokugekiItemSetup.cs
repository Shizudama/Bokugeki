#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.Dynamics;

/// <summary>
/// Bokugeki Items Setup
/// アイテムを複数個複製して、それぞれに以下の機能を持たせる:
///   ・掴める (VRCPhysBone)
///   ・表示On/Off (Enable)
///   ・ワールド固定 (World Lock, VRCParentConstraint.FreezeToWorld)
///   ・カスタムアニメ (任意)
/// メニュー: Bokugeki > ページ > アイテム名 (SubMenu) > Enable / Lock / Anim
/// すべてのパラメータは saved=false。
/// </summary>
public class BokugekiItemsSetup : EditorWindow
{
    const int ItemsPerPage = 4;
    const string RootMenuName = "Bokugeki";
    const string ParamPrefix = "Bokugeki";

    [Serializable]
    public class CustomAnim
    {
        public string label = "Anim";
        public AnimationClip onClip;
        public AnimationClip offClip;   // null なら空クリップを自動生成
    }

    [Serializable]
    public class ItemEntry
    {
        public GameObject target;
        public int count = 1;              // 1..10
        public bool enableToggle = true;
        public bool worldLock = true;

        // 複数のカスタムアニメ(任意数。onClip が設定されている要素だけが有効化される)
        public List<CustomAnim> customAnims = new List<CustomAnim>();

        // ── 旧フィールド (後方互換のため残す。Apply 時に customAnims に統合) ──
        public string customAnimLabel = "Anim";
        public AnimationClip customAnimOn;
        public AnimationClip customAnimOff;
    }

    // --- Window fields ---
    VRCAvatarDescriptor avatar;
    List<ItemEntry> items = new List<ItemEntry>();
    string outputFolder = "Assets/Bokugeki/Generated";
    bool enableGrab = true;
    float grabRadius = 0.35f;            // デフォルトを広めに(0.2→0.35m)
    bool autoExpandRadius = true;        // メッシュのサイズに応じて自動拡張
    Vector3 grabPointOffset = new Vector3(0f, 0.05f, 0f);
    bool autoCenterGrabPoint = false;
    bool lockTilt = true;
    bool networkSync = true;
    bool useModularAvatar = true;   // MA が入っていれば優先利用
    Vector2 scroll;

    [MenuItem("Tools/Bokugeki/Bokugeki Items Setup")]
    public static void Open() => GetWindow<BokugekiItemsSetup>("Bokugeki Items Setup");

    // =============================================================
    //  UI
    // =============================================================
    void OnGUI()
    {
        EditorGUILayout.LabelField("Bokugeki Items Setup", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "各アイテムに以下を設定できます:\n" +
            "・Count: 複製数 (1で単体、2以上で '<Name> 2', '<Name> 3' ... を自動生成)\n" +
            "・Enable: 表示On/Off\n" +
            "・World Lock: ワールド固定\n" +
            "・Custom Anim: 任意のアニメクリップで追加トグル\n" +
            $"・メニュー: '{RootMenuName}' 配下にページ({ItemsPerPage}件毎)→アイテム別SubMenuで整理",
            MessageType.Info);

        avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
            "Avatar", avatar, typeof(VRCAvatarDescriptor), true);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Items", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Use Selection"))
            {
                items = Selection.gameObjects
                    .Where(g => g != null)
                    .Select(g => new ItemEntry { target = g })
                    .ToList();
            }
            if (GUILayout.Button("Clear")) items.Clear();
        }

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(260));
        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i] ?? new ItemEntry();
            items[i] = entry;

            EditorGUILayout.BeginVertical(GUI.skin.box);

            bool removed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                entry.target = (GameObject)EditorGUILayout.ObjectField(entry.target, typeof(GameObject), true);
                if (GUILayout.Button("-", GUILayout.Width(24))) { items.RemoveAt(i); i--; removed = true; }
            }

            if (!removed)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    entry.count = EditorGUILayout.IntSlider(
                        new GUIContent("Count (複製数)", "1=単体。2以上なら '<Name> 2', '<Name> 3' ... を自動生成して複製配置"),
                        entry.count, 1, 10);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        entry.enableToggle = EditorGUILayout.ToggleLeft("Enable", entry.enableToggle, GUILayout.Width(90));
                        entry.worldLock = EditorGUILayout.ToggleLeft("World Lock", entry.worldLock, GUILayout.Width(120));
                    }

                    EditorGUILayout.LabelField("Custom Animations (複数設定可)", EditorStyles.miniLabel);
                    if (entry.customAnims == null) entry.customAnims = new List<CustomAnim>();

                    // 旧単一 fields からの自動移行(互換)
                    if (entry.customAnimOn != null && entry.customAnims.Count == 0)
                    {
                        entry.customAnims.Add(new CustomAnim
                        {
                            label = string.IsNullOrEmpty(entry.customAnimLabel) ? "Anim" : entry.customAnimLabel,
                            onClip = entry.customAnimOn,
                            offClip = entry.customAnimOff
                        });
                        entry.customAnimOn = null;
                        entry.customAnimOff = null;
                    }

                    for (int ai = 0; ai < entry.customAnims.Count; ai++)
                    {
                        var anim = entry.customAnims[ai];
                        if (anim == null) { entry.customAnims[ai] = anim = new CustomAnim(); }

                        EditorGUILayout.BeginVertical("HelpBox");
                        bool animRemoved = false;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField($"Anim {ai + 1}", EditorStyles.miniBoldLabel, GUILayout.Width(80));
                            if (GUILayout.Button("×", GUILayout.Width(24)))
                            {
                                entry.customAnims.RemoveAt(ai); ai--; animRemoved = true;
                            }
                        }
                        if (!animRemoved)
                        {
                            anim.label = EditorGUILayout.TextField("  Menu Label", anim.label);
                            anim.onClip = (AnimationClip)EditorGUILayout.ObjectField("  On Clip (必須)", anim.onClip, typeof(AnimationClip), false);
                            anim.offClip = (AnimationClip)EditorGUILayout.ObjectField("  Off Clip (任意)", anim.offClip, typeof(AnimationClip), false);
                        }
                        EditorGUILayout.EndVertical();
                    }

                    if (GUILayout.Button("+ Add Custom Anim", GUILayout.Height(20)))
                        entry.customAnims.Add(new CustomAnim());
                }
            }

            EditorGUILayout.EndVertical();
        }
        if (GUILayout.Button("+ Add slot")) items.Add(new ItemEntry());
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        enableGrab = EditorGUILayout.Toggle("Grabbable (PhysBone)", enableGrab);
        using (new EditorGUI.DisabledScope(!enableGrab))
        {
            grabRadius = EditorGUILayout.Slider(
                new GUIContent("Grab Radius (m)",
                    "掴み判定の球体/カプセルの半径(ベース値)。大きいほど掴みやすい。"),
                grabRadius, 0.01f, 2.0f);
            autoExpandRadius = EditorGUILayout.Toggle(
                new GUIContent("Auto-expand Radius",
                    "メッシュ Bounds の最大軸長に応じて radius を自動拡張。大きいアイテムでも確実に掴める。"),
                autoExpandRadius);
            autoCenterGrabPoint = EditorGUILayout.Toggle(
                new GUIContent("Auto-orient Grab Point",
                    "GrabPointの「向き」だけメッシュ中心方向に自動調整。長さは小さく保つため掴んだ時の回転が最小化される。"),
                autoCenterGrabPoint);
            using (new EditorGUI.DisabledScope(autoCenterGrabPoint))
                grabPointOffset = EditorGUILayout.Vector3Field(
                    new GUIContent("Grab Point Offset (local m)",
                        "PBRootローカル座標でのGrabPoint位置。小さいほど掴んだ時の回転が少ない。推奨: (0, 0.05, 0)"),
                    grabPointOffset);

            lockTilt = EditorGUILayout.Toggle(
                new GUIContent("傾き防止(水平回転のみ許可)",
                    "VRCRotationConstraint(FreezeToWorld)を付与し、X・Z軸の回転をロック。Y軸(水平回転)だけ自由にする。"),
                lockTilt);
        }
        networkSync = EditorGUILayout.Toggle("Network Synced", networkSync);

        // ModularAvatar integration
        bool maAvailable = MAIsAvailable();
        using (new EditorGUI.DisabledScope(!maAvailable))
        {
            useModularAvatar = EditorGUILayout.Toggle(
                new GUIContent("Use ModularAvatar (自動統合)",
                    maAvailable
                        ? "アバター直下に MA 統合オブジェクトを作成し、\nFX/Params/Menu をアバター本体を書き換えずに統合します。"
                        : "ModularAvatar がプロジェクトにインストールされていません。"),
                useModularAvatar);
        }
        if (!maAvailable)
            EditorGUILayout.HelpBox("ModularAvatar 未インストール(フォールバックでアバター本体に直接書き込みます)", MessageType.None);

        EditorGUILayout.Space(6);
        bool ready = avatar != null && items.Any(x => x != null && x.target != null);
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Apply", GUILayout.Height(32)))
            {
                try { Apply(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Bokugeki Items Setup", "Error: " + e.Message, "OK");
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Unwrap: 選択したアイテムから _PBRoot / _GrabPoint のラッパー階層を除去し、\n" +
                "元の親の下に戻します。複製されたコピー ('<Name> 2' など) も削除します。\n" +
                "FX Layer / Parameters / Menu は削除しません。",
                MessageType.None);
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
            if (GUILayout.Button("Unwrap (元の階層に戻す・複製も削除)", GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("Unwrap",
                    "選択したアイテムのラッパー構造を削除し、元の親配下に戻します。\n" +
                    "また、複製されたコピー ('<Name> 2'〜) も合わせて削除します。\n\n" +
                    "※ FX Layer / Parameters / Menu は削除されません。",
                    "Unwrap する", "キャンセル"))
                {
                    try { Unwrap(); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        EditorUtility.DisplayDialog("Bokugeki Items Setup", "Error: " + e.Message, "OK");
                    }
                }
            }
            GUI.backgroundColor = prevColor;
        }
    }

    // =============================================================
    //  Apply
    // =============================================================
    void Apply()
    {
        var validEntries = items.Where(x => x != null && x.target != null)
                                .GroupBy(x => x.target).Select(g => g.First())
                                .ToList();
        foreach (var e in validEntries)
        {
            if (e.target == avatar.gameObject)
                throw new Exception("アバターそのものをアイテムに指定することはできません。");
            if (!e.target.transform.IsChildOf(avatar.transform))
                throw new Exception($"'{e.target.name}' はアバターの子ではありません。");
            e.count = Mathf.Clamp(e.count, 1, 10);
        }

        // プレハブ Unpack 確認(必要な場合のみ)
        if (enableGrab)
        {
            var prefabRoots = new HashSet<GameObject>();
            foreach (var e in validEntries)
            {
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(e.target);
                if (root != null) prefabRoots.Add(root);
            }
            if (prefabRoots.Count > 0)
            {
                string names = string.Join(", ", prefabRoots.Select(p => $"'{p.name}'"));
                bool ok = EditorUtility.DisplayDialog("Prefab Unpack が必要",
                    $"アイテムが以下のプレハブの内部にあります:\n{names}\n\n" +
                    "ラップ・複製を行うにはプレハブ構造を変更する必要があります。\n" +
                    "続行するにはプレハブを Unpack します。",
                    "Unpack して続行", "中止");
                if (!ok) return;
                foreach (var pr in prefabRoots)
                {
                    PrefabUtility.UnpackPrefabInstance(pr, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    Debug.Log($"[Bokugeki] Unpacked prefab: {pr.name}");
                }
            }
        }

        EnsureFolder(outputFolder);

        var fx = EnsureFXController();
        var exParams = EnsureExpressionParameters();

        bool usingMA = useModularAvatar && MAIsAvailable();

        // MA モードでは rootMenu 経由の入れ子を作らず、bokuMenu を新規独立Menuとして直接扱う。
        // 非MAモードではアバター本体メニュー配下に "Bokugeki" サブメニューを作る。
        VRCExpressionsMenu rootMenu = null;
        VRCExpressionsMenu bokuMenu;
        if (usingMA)
        {
            bokuMenu = EnsureExpressionsMenu();  // MA専用の独立Menuが返る
            // bokuMenu 自体の controls はクリアして毎回きれいな状態にする
            bokuMenu.controls.Clear();
            EditorUtility.SetDirty(bokuMenu);
        }
        else
        {
            rootMenu = EnsureExpressionsMenu();
            bokuMenu = EnsureSubMenu(rootMenu, RootMenuName,
                $"{avatar.gameObject.name}_Menu_{RootMenuName}.asset");
        }

        Undo.RegisterCompleteObjectUndo(avatar, "Bokugeki Items Setup");
        Undo.RegisterCompleteObjectUndo(fx, "Bokugeki Items Setup");
        Undo.RegisterCompleteObjectUndo(exParams, "Bokugeki Items Setup");
        if (rootMenu != null) Undo.RegisterCompleteObjectUndo(rootMenu, "Bokugeki Items Setup");
        Undo.RegisterCompleteObjectUndo(bokuMenu, "Bokugeki Items Setup");

        // 全コピーを列挙 (validEntries をフラット化。各 ItemEntry の各コピー目が1エントリ)
        // index: 0=オリジナル、1以降=コピー
        var allInstances = new List<(ItemEntry entry, GameObject go, int copyIndex)>();
        foreach (var entry in validEntries)
        {
            var copies = GetOrCreateCopies(entry.target, entry.count);
            for (int i = 0; i < copies.Count; i++)
                allInstances.Add((entry, copies[i], i));
        }

        // ページ分割 + セットアップ
        var pageCache = new Dictionary<int, VRCExpressionsMenu>();
        for (int i = 0; i < allInstances.Count; i++)
        {
            int pageIdx = i / ItemsPerPage;
            if (!pageCache.TryGetValue(pageIdx, out var page))
            {
                string pageName = $"{RootMenuName} {pageIdx + 1}";
                page = EnsureSubMenu(bokuMenu, pageName,
                    $"{avatar.gameObject.name}_Menu_{RootMenuName}_{pageIdx + 1}.asset");
                Undo.RegisterCompleteObjectUndo(page, "Bokugeki Items Setup");
                pageCache[pageIdx] = page;
            }
            SetupInstance(allInstances[i].entry, allInstances[i].go, allInstances[i].copyIndex, fx, exParams, page);
        }

        EditorUtility.SetDirty(fx);
        EditorUtility.SetDirty(exParams);
        if (rootMenu != null) EditorUtility.SetDirty(rootMenu);
        EditorUtility.SetDirty(bokuMenu);
        foreach (var page in pageCache.Values) EditorUtility.SetDirty(page);
        EditorUtility.SetDirty(avatar);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // ModularAvatar 統合 (有効かつインストール済みの場合)
        bool maApplied = false;
        if (usingMA)
        {
            SetupModularAvatar(fx, exParams, bokuMenu);
            maApplied = true;
        }

        EditorUtility.DisplayDialog("Bokugeki Items Setup",
            $"{validEntries.Count} エントリ / 計 {allInstances.Count} インスタンスを\n" +
            $"{pageCache.Count} ページに分けてセットアップしました。\n\n" +
            (maApplied
                ? $"ModularAvatar 統合: '{BOKUGEKI_MA_ROOT_NAME}' 配下にMAコンポーネントを作成。\n" +
                  "アバター本体の FX/Params/Menu は変更していません。"
                : "ModularAvatar 統合: 無効 or 未インストール (アバター本体に直接書き込み)"),
            "OK");
    }

    /// <summary>
    /// original から Count 個のインスタンスを用意して返す。
    /// [0] = original 本人、[1..] = '<Name> 2', '<Name> 3' ...
    /// 既に同名のコピーが存在すれば再利用、無ければ Instantiate で生成。
    /// </summary>
    List<GameObject> GetOrCreateCopies(GameObject original, int count)
    {
        var result = new List<GameObject> { original };

        // 複製配置のscope = original のラップ前の親。
        // 4階層ラップ: item ← _GrabPoint ← _PBRoot ← _Anchor → scope = _Anchor の親
        // 3階層レガシー: item ← _GrabPoint ← _PBRoot → scope = _PBRoot の親
        Transform scope = original.transform.parent;
        if (scope != null && scope.name == $"{original.name}_GrabPoint"
            && scope.parent != null && scope.parent.name == $"{original.name}_PBRoot")
        {
            if (scope.parent.parent != null && scope.parent.parent.name == $"{original.name}_Anchor")
                scope = scope.parent.parent.parent;   // 4階層
            else
                scope = scope.parent.parent;          // 3階層レガシー
        }

        for (int i = 2; i <= count; i++)
        {
            string copyName = $"{original.name} {i}";

            // 既存の同名オブジェクトを探す(scope配下の全子孫)
            GameObject found = FindDescendantByName(scope, copyName);
            if (found == null)
            {
                // オリジナルを Instantiate して scope 直下に配置
                var copy = (GameObject)UnityEngine.Object.Instantiate(original, scope);
                copy.name = copyName;
                copy.transform.position = original.transform.position;
                copy.transform.rotation = original.transform.rotation;
                copy.transform.localScale = original.transform.localScale;
                Undo.RegisterCreatedObjectUndo(copy, "Bokugeki Copy Item");
                found = copy;
                Debug.Log($"[Bokugeki] 複製作成: '{copyName}' を '{(scope ? scope.name : "<root>")}' 直下に生成", found);
            }
            result.Add(found);
        }
        return result;
    }

    static GameObject FindDescendantByName(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root)
        {
            if (t.name == name) return t.gameObject;
            var r = FindDescendantByName(t, name);
            if (r != null) return r;
        }
        return null;
    }

    // =============================================================
    //  Per-instance setup
    // =============================================================
    /// <summary>
    /// 1 インスタンス分のセットアップ。
    /// instance はそのアイテム本体 (オリジナル または コピー)。
    /// copyIndex: 0=オリジナル、1以降がコピー。パラメータ命名に使う。
    /// </summary>
    void SetupInstance(ItemEntry entry, GameObject instance, int copyIndex,
        AnimatorController fx, VRCExpressionParameters exParams, VRCExpressionsMenu pageMenu)
    {
        string rawName = instance.name;
        string safeBase = Sanitize(entry.target.name);
        string safeInstance = copyIndex == 0 ? safeBase : $"{safeBase}_{copyIndex + 1}";
        string displayName = rawName;

        // 1. ラッパー(Anchor→PBRoot→GrabPoint→item)を用意し、PhysBone を付与
        GameObject pbRoot = enableGrab ? EnsureWrapperAndPhysBone(instance) : instance;
        if (pbRoot == null)
        {
            Debug.LogWarning($"[Bokugeki] '{rawName}' のラップに失敗。スキップ。", instance);
            return;
        }

        // Anchor は PBRoot の親。enableGrab=false のときは instance 自体を anchor 扱い (旧挙動互換)。
        GameObject anchor = enableGrab && pbRoot.transform.parent != null
            ? pbRoot.transform.parent.gameObject
            : pbRoot;

        string pbRootPath = AnimationUtility.CalculateTransformPath(pbRoot.transform, avatar.transform);
        string anchorPath = AnimationUtility.CalculateTransformPath(anchor.transform, avatar.transform);

        // 2. アイテム専用 SubMenu を作成(ページメニュー配下)
        var itemMenu = EnsureSubMenu(pageMenu, displayName,
            $"{avatar.gameObject.name}_Menu_Item_{safeInstance}.asset");
        Undo.RegisterCompleteObjectUndo(itemMenu, "Bokugeki Items Setup");
        // 再実行時の重複を避けるためクリア
        itemMenu.controls.Clear();

        // === 3. Enable (表示On/Off) ===
        // Anchor を On/Off することで 4階層丸ごと表示切替 (item 直 Off だと
        // PBRoot 上の PhysBone が活きて掴み判定だけ残るのを避ける)。
        if (entry.enableToggle)
        {
            var onClip = BuildActiveClip(anchorPath, true,
                $"{ParamPrefix}_{safeInstance}_Enable_On");
            var offClip = BuildActiveClip(anchorPath, false,
                $"{ParamPrefix}_{safeInstance}_Enable_Off");

            string pName = $"{ParamPrefix}_{safeInstance}_Enable";
            AddToggleLayer(fx, pName, pName, offClip, onClip, false);
            AddExpressionParam(exParams, pName, false);
            AddMenuToggle(itemMenu, "Enable", pName);
        }

        // === 4. World Lock (ワールド固定) ===
        // Anchor 上の VRCParentConstraint.FreezeToWorld を切替 (true=ワールド固定)。
        if (entry.worldLock && enableGrab)
        {
            var offClip = BuildFreezeToWorldClip(anchorPath, false,
                $"{ParamPrefix}_{safeInstance}_Lock_Off");
            var onClip = BuildFreezeToWorldClip(anchorPath, true,
                $"{ParamPrefix}_{safeInstance}_Lock_On");

            string pName = $"{ParamPrefix}_{safeInstance}_Lock";
            AddToggleLayer(fx, pName, pName, offClip, onClip, false);
            AddExpressionParam(exParams, pName, false);
            AddMenuToggle(itemMenu, "World Lock", pName);
        }

        // === 5. Custom Anims (ユーザー指定クリップ。複数設定可) ===
        // 後方互換: 旧単一 fields が残っていれば customAnims に統合
        if (entry.customAnims == null) entry.customAnims = new List<CustomAnim>();
        if (entry.customAnimOn != null
            && !entry.customAnims.Any(a => a != null && a.onClip == entry.customAnimOn))
        {
            entry.customAnims.Add(new CustomAnim
            {
                label = string.IsNullOrEmpty(entry.customAnimLabel) ? "Anim" : entry.customAnimLabel,
                onClip = entry.customAnimOn,
                offClip = entry.customAnimOff,
            });
        }

        // ラベル重複を避けるため出現番号を追記する
        var labelCount = new Dictionary<string, int>();
        int animIdx = 0;
        foreach (var anim in entry.customAnims)
        {
            if (anim == null || anim.onClip == null) continue;

            animIdx++;
            string rawLabel = string.IsNullOrEmpty(anim.label) ? "Anim" : anim.label;
            string label = rawLabel;
            if (labelCount.ContainsKey(rawLabel))
            {
                labelCount[rawLabel]++;
                label = $"{rawLabel} {labelCount[rawLabel]}";
            }
            else
            {
                labelCount[rawLabel] = 1;
            }

            string labelSafe = Sanitize(rawLabel);
            string pName = $"{ParamPrefix}_{safeInstance}_Anim_{labelSafe}_{animIdx}";

            var onClip = anim.onClip;
            var offClip = anim.offClip != null
                ? anim.offClip
                : BuildEmptyClip($"{ParamPrefix}_{safeInstance}_Anim_{labelSafe}_{animIdx}_Off");

            AddToggleLayer(fx, pName, pName, offClip, onClip, false);
            AddExpressionParam(exParams, pName, false);
            AddMenuToggle(itemMenu, label, pName);
        }

        // サブメニューが空になる場合(全機能off)だけ警告
        if (itemMenu.controls.Count == 0)
            Debug.LogWarning($"[Bokugeki] '{displayName}' に有効な機能がありません(Enable/Lock/Animすべて無効)。", instance);
    }


    // =============================================================
    //  Unwrap (複製も削除)
    // =============================================================
    class UnwrapPlan
    {
        public GameObject item;
        public GameObject grabPoint;
        public GameObject pbRoot;
        public GameObject anchor;           // 4階層ラップなら最外 wrapper、3階層レガシーなら null
        public Transform restoreParent;
        public int restoreSibling;
        public Vector3 itemWorldPos;
        public Quaternion itemWorldRot;
        public bool itemMoved;

        /// <summary>最外 wrapper (anchor が居れば anchor、無ければ pbRoot)。破棄対象。</summary>
        public GameObject TopWrapper => anchor != null ? anchor : pbRoot;
    }

    void Unwrap()
    {
        var originalTargets = items.Where(x => x != null && x.target != null)
                                   .Select(x => x.target).Distinct().ToList();
        if (originalTargets.Count == 0)
        {
            EditorUtility.DisplayDialog("Bokugeki Items Setup",
                "Items リストに有効なオブジェクトがありません。", "OK");
            return;
        }

        // 方針: 「復元対象 (originals)」と「削除対象 (copies)」を最初に明確に分けて集める。
        var originalPlans = new List<UnwrapPlan>();
        var copyPlans = new List<UnwrapPlan>();
        var emptyPbRoots = new List<GameObject>();
        int skipped = 0;

        foreach (var original in originalTargets)
        {
            var origPlan = ResolvePlanFrom(original);
            if (origPlan != null) originalPlans.Add(origPlan);
            else skipped++;

            // scope = original がラップされていればその3階層上の親、未ラップなら単純に親
            // scope = original がラップされていればその最外親、未ラップなら単純に親。
            // 4階層: item ← _GrabPoint ← _PBRoot ← _Anchor → scope = _Anchor の親
            // 3階層: item ← _GrabPoint ← _PBRoot → scope = _PBRoot の親
            Transform scope = original.transform.parent;
            if (scope != null
                && scope.name == $"{original.name}_GrabPoint"
                && scope.parent != null
                && scope.parent.name == $"{original.name}_PBRoot")
            {
                if (scope.parent.parent != null
                    && scope.parent.parent.name == $"{original.name}_Anchor")
                    scope = scope.parent.parent.parent;   // 4階層
                else
                    scope = scope.parent.parent;          // 3階層レガシー
            }

            if (scope != null)
            {
                for (int i = 2; i <= 30; i++)
                {
                    string copyName = $"{original.name} {i}";
                    var copyGo = FindDescendantByName(scope, copyName);
                    if (copyGo == null) break;
                    var cp = ResolvePlanFrom(copyGo);
                    if (cp != null) copyPlans.Add(cp);

                    // 抜け殻 (Anchor または PBRoot のみ残っている空ラッパー) を拾う
                    var copyAnchorName = $"{copyName}_Anchor";
                    var copyAnchorGo = FindDescendantByName(scope, copyAnchorName);
                    if (copyAnchorGo != null)
                    {
                        // anchor 配下に item が一切無ければ抜け殻として扱う
                        bool empty = true;
                        foreach (Transform c in copyAnchorGo.transform)
                        {
                            if (!c.name.EndsWith("_PBRoot")) { empty = false; break; }
                            foreach (Transform gc in c) { if (!gc.name.EndsWith("_GrabPoint") || gc.childCount > 0) { empty = false; break; } }
                            if (!empty) break;
                        }
                        if (empty) emptyPbRoots.Add(copyAnchorGo);
                    }
                    var copyPbRootName = $"{copyName}_PBRoot";
                    var copyPbRootGo = FindDescendantByName(scope, copyPbRootName);
                    if (copyPbRootGo != null && copyPbRootGo.transform.childCount == 0)
                        emptyPbRoots.Add(copyPbRootGo);
                }
            }
        }

        originalPlans = originalPlans.GroupBy(p => p.pbRoot).Select(g => g.First()).ToList();
        copyPlans = copyPlans.GroupBy(p => p.pbRoot).Select(g => g.First()).ToList();

        var originalSet = new HashSet<GameObject>(originalTargets);
        copyPlans.RemoveAll(p => p.item != null && originalSet.Contains(p.item));

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Bokugeki Unwrap");
        int group = Undo.GetCurrentGroup();

        // Phase A: オリジナル救出
        int movedCount = 0;
        foreach (var p in originalPlans)
        {
            if (TryMoveItemOut(p)) { p.itemMoved = true; movedCount++; }
            else
                Debug.LogError($"[Bokugeki] オリジナル '{p.item?.name}' の救出に失敗。PBRoot は削除せず残します。", p.item);
        }

        // Phase A2: RotationConstraint 削除 (救出成功分)
        foreach (var p in originalPlans)
        {
            if (!p.itemMoved || p.item == null) continue;
            var rc = p.item.GetComponent<VRCRotationConstraint>();
            if (rc != null) { try { Undo.DestroyObjectImmediate(rc); } catch { } }
        }

        // Phase A3: 救出成功分のみ TopWrapper (Anchor または 旧 PBRoot) 削除
        int destroyedOriginal = 0, residual = 0;
        foreach (var p in originalPlans)
        {
            var top = p.TopWrapper;
            if (top == null) continue;
            if (!p.itemMoved)
            {
                try { top.name = $"{top.name}__UNWRAP_FAILED"; } catch { }
                residual++;
                continue;
            }
            if (p.item != null && p.item.transform.IsChildOf(top.transform))
            {
                try { top.name = $"{top.name}__UNWRAP_FAILED"; } catch { }
                residual++;
                continue;
            }
            try { Undo.DestroyObjectImmediate(top); destroyedOriginal++; }
            catch { residual++; }
        }

        // Phase A4: sibling/world transform 最終調整
        foreach (var p in originalPlans)
        {
            if (!p.itemMoved || p.item == null) continue;
            var itemT = p.item.transform;
            if (p.restoreParent != null && p.restoreSibling >= 0)
            {
                try
                {
                    int idx = Mathf.Clamp(p.restoreSibling, 0, p.restoreParent.childCount - 1);
                    if (idx >= 0) itemT.SetSiblingIndex(idx);
                }
                catch { }
            }
            try { itemT.position = p.itemWorldPos; itemT.rotation = p.itemWorldRot; } catch { }
        }

        // Phase B: 複製コピーを TopWrapper ごと破棄
        int destroyedCopies = 0;
        foreach (var p in copyPlans)
        {
            var top = p.TopWrapper;
            if (top == null) continue;
            if (p.item != null && originalSet.Contains(p.item))
            {
                Debug.LogWarning($"[Bokugeki] コピー判定に originals が混入していたため削除スキップ: '{p.item.name}'");
                continue;
            }
            try
            {
                Undo.DestroyObjectImmediate(top);
                destroyedCopies++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bokugeki] コピー '{top.name}' の削除失敗: {e.Message}");
            }
        }

        if (emptyPbRoots.Count > 0) CleanEmptyPbRoots(emptyPbRoots);

        Undo.CollapseUndoOperations(group);
        items.RemoveAll(x => x == null || x.target == null);

        EditorUtility.DisplayDialog("Bokugeki Items Setup",
            $"Unwrap 結果\n\n" +
            $"  オリジナル救出: {movedCount}/{originalPlans.Count} 件\n" +
            $"  オリジナルPBRoot削除: {destroyedOriginal} 件\n" +
            $"  複製コピー削除: {destroyedCopies}/{copyPlans.Count} 件\n" +
            $"  PBRoot残存(救出失敗/手動要削除): {residual} 件\n" +
            $"  抜け殻削除: {emptyPbRoots.Count} 件\n" +
            $"  スキップ: {skipped} 件",
            "OK");
    }

    /// <summary>
    /// 単一 GameObject をラップ構造の中のどの役割かで判定して UnwrapPlan を生成。
    /// 4階層ラップ (Anchor→PBRoot→GrabPoint→item) と 3階層レガシー (PBRoot→GrabPoint→item)
    /// の両方に対応。未ラップ状態なら null。
    /// </summary>
    UnwrapPlan ResolvePlanFrom(GameObject entry)
    {
        if (entry == null) return null;

        GameObject item = null, grabPoint = null, pbRoot = null, anchor = null;
        string n = entry.name;

        if (n.EndsWith("_Anchor"))
        {
            anchor = entry;
            string baseName = n.Substring(0, n.Length - "_Anchor".Length);
            pbRoot = FindChildByName(anchor.transform, $"{baseName}_PBRoot");
            if (pbRoot == null && anchor.transform.childCount > 0)
                pbRoot = anchor.transform.GetChild(0).gameObject;
            if (pbRoot != null)
            {
                grabPoint = FindChildByName(pbRoot.transform, $"{baseName}_GrabPoint");
                if (grabPoint == null && pbRoot.transform.childCount > 0)
                    grabPoint = pbRoot.transform.GetChild(0).gameObject;
                if (grabPoint != null && grabPoint.transform.childCount > 0)
                    item = grabPoint.transform.GetChild(0).gameObject;
            }
        }
        else if (n.EndsWith("_PBRoot"))
        {
            pbRoot = entry;
            string baseName = n.Substring(0, n.Length - "_PBRoot".Length);
            grabPoint = FindChildByName(pbRoot.transform, $"{baseName}_GrabPoint");
            if (grabPoint == null && pbRoot.transform.childCount > 0)
                grabPoint = pbRoot.transform.GetChild(0).gameObject;
            if (grabPoint != null && grabPoint.transform.childCount > 0)
                item = grabPoint.transform.GetChild(0).gameObject;
            // Anchor 親があれば拾う
            if (pbRoot.transform.parent != null && pbRoot.transform.parent.name.EndsWith("_Anchor"))
                anchor = pbRoot.transform.parent.gameObject;
        }
        else if (n.EndsWith("_GrabPoint"))
        {
            grabPoint = entry;
            if (grabPoint.transform.parent != null && grabPoint.transform.parent.name.EndsWith("_PBRoot"))
            {
                pbRoot = grabPoint.transform.parent.gameObject;
                if (pbRoot.transform.parent != null && pbRoot.transform.parent.name.EndsWith("_Anchor"))
                    anchor = pbRoot.transform.parent.gameObject;
            }
            if (grabPoint.transform.childCount > 0)
                item = grabPoint.transform.GetChild(0).gameObject;
        }
        else
        {
            var parent = entry.transform.parent;
            // 4階層パターン: entry(item) ← GrabPoint ← PBRoot ← Anchor
            if (parent != null
                && parent.name == $"{n}_GrabPoint"
                && parent.parent != null
                && parent.parent.name == $"{n}_PBRoot"
                && parent.parent.parent != null
                && parent.parent.parent.name == $"{n}_Anchor")
            {
                item = entry;
                grabPoint = parent.gameObject;
                pbRoot = parent.parent.gameObject;
                anchor = parent.parent.parent.gameObject;
            }
            // 3階層レガシーパターン: entry(item) ← GrabPoint ← PBRoot
            else if (parent != null
                && parent.name == $"{n}_GrabPoint"
                && parent.parent != null
                && parent.parent.name == $"{n}_PBRoot")
            {
                item = entry;
                grabPoint = parent.gameObject;
                pbRoot = parent.parent.gameObject;
            }
        }

        if (item == null || grabPoint == null || pbRoot == null) return null;

        // 最外 wrapper = anchor があれば anchor、無ければ pbRoot。そこを基準に sibling 復元。
        Transform top = anchor != null ? anchor.transform : pbRoot.transform;

        return new UnwrapPlan
        {
            item = item,
            grabPoint = grabPoint,
            pbRoot = pbRoot,
            anchor = anchor,
            restoreParent = top.parent,
            restoreSibling = top.GetSiblingIndex(),
            itemWorldPos = item.transform.position,
            itemWorldRot = item.transform.rotation,
            itemMoved = false,
        };
    }

    bool TryMoveItemOut(UnwrapPlan p)
    {
        if (p.item == null || p.pbRoot == null) return false;
        var itemT = p.item.transform;
        if (!itemT.IsChildOf(p.pbRoot.transform)) return true;

        try { Undo.SetTransformParent(itemT, p.restoreParent, "Bokugeki Unwrap Move"); if (!itemT.IsChildOf(p.pbRoot.transform)) return true; } catch { }
        try { itemT.SetParent(p.restoreParent, true); if (!itemT.IsChildOf(p.pbRoot.transform)) return true; } catch { }
        try
        {
            itemT.SetParent(null, true);
            if (!itemT.IsChildOf(p.pbRoot.transform))
            {
                if (p.restoreParent != null) { try { itemT.SetParent(p.restoreParent, true); } catch { } }
                return true;
            }
        }
        catch { }
        return false;
    }

    void CleanEmptyPbRoots(List<GameObject> emptyPbRoots)
    {
        foreach (var go in emptyPbRoots)
        {
            if (go == null) continue;
            if (go.transform.childCount > 0)
            {
                bool onlyGrabPoints = true;
                foreach (Transform c in go.transform)
                    if (!c.name.EndsWith("_GrabPoint") || c.childCount > 0) { onlyGrabPoints = false; break; }
                if (!onlyGrabPoints) continue;
            }
            try { Undo.DestroyObjectImmediate(go); } catch { }
        }
    }

    static GameObject FindChildByName(Transform parent, string name)
    {
        foreach (Transform c in parent) if (c.name == name) return c.gameObject;
        return null;
    }

    // =============================================================
    //  PhysBone ラッパー (Puppetize v4 方式・4階層化)
    //  Anchor ── VRCParentConstraint (Source=Hips)
    //    └ PBRoot ── VRCPhysBone (Immobile=1, MaxStretch=1000)
    //        └ GrabPoint (localPos Y=0.05)
    //            └ item
    // =============================================================
    GameObject EnsureWrapperAndPhysBone(GameObject item)
    {
        string anchorName    = $"{item.name}_Anchor";
        string pbRootName    = $"{item.name}_PBRoot";
        string grabPointName = $"{item.name}_GrabPoint";
        Transform origParent = item.transform.parent;

        bool alreadyWrapped =
            origParent != null
            && origParent.name == grabPointName
            && origParent.parent != null
            && origParent.parent.name == pbRootName
            && origParent.parent.parent != null
            && origParent.parent.parent.name == anchorName;

        GameObject anchor, pbRoot, grabPoint;

        if (alreadyWrapped)
        {
            grabPoint = origParent.gameObject;
            pbRoot    = origParent.parent.gameObject;
            anchor    = origParent.parent.parent.gameObject;
        }
        else
        {
            // 旧3階層ラップがあれば一旦解く (安全のため)
            if (origParent != null
                && origParent.name == grabPointName
                && origParent.parent != null
                && origParent.parent.name == pbRootName)
            {
                // 旧 PBRoot の位置に item を戻して再ラップ
                Vector3 wp = item.transform.position;
                Quaternion wr = item.transform.rotation;
                var legacyPb = origParent.parent.gameObject;
                var legacyParent = legacyPb.transform.parent;
                item.transform.SetParent(legacyParent, true);
                item.transform.position = wp;
                item.transform.rotation = wr;
                Undo.DestroyObjectImmediate(legacyPb);
                origParent = item.transform.parent;
            }

            Vector3 origWorldPos = item.transform.position;
            Quaternion origWorldRot = item.transform.rotation;
            int origSiblingIndex = item.transform.GetSiblingIndex();

            // 1. Anchor
            anchor = new GameObject(anchorName);
            Undo.RegisterCreatedObjectUndo(anchor, "Bokugeki Wrap Item");
            anchor.transform.SetParent(origParent, false);
            anchor.transform.position = origWorldPos;
            anchor.transform.rotation = origWorldRot;
            anchor.transform.SetSiblingIndex(origSiblingIndex);

            // 2. PBRoot (Anchor 子、ローカル0)
            pbRoot = new GameObject(pbRootName);
            pbRoot.transform.SetParent(anchor.transform, false);
            pbRoot.transform.localPosition = Vector3.zero;
            pbRoot.transform.localRotation = Quaternion.identity;

            // 3. GrabPoint
            grabPoint = new GameObject(grabPointName);
            grabPoint.transform.SetParent(pbRoot.transform, false);

            // 4. item を GrabPoint 子に
            item.transform.SetParent(grabPoint.transform, true);
            item.transform.position = origWorldPos;
            item.transform.rotation = origWorldRot;

            if (item.transform.parent != grabPoint.transform
                || grabPoint.transform.parent != pbRoot.transform
                || pbRoot.transform.parent != anchor.transform)
            {
                Debug.LogError($"[Bokugeki] 4階層ラップに失敗: '{item.name}'", item);
                Undo.DestroyObjectImmediate(anchor);
                return null;
            }
        }

        // GrabPoint のローカル位置
        Vector3 computedOffset = ComputeGrabPointLocalOffset(item, pbRoot);
        Undo.RecordObject(grabPoint.transform, "Bokugeki Set GrabPoint Offset");
        grabPoint.transform.localPosition = computedOffset;

        // 既存PhysBoneはクリーン再構築 (PBRoot に載せる)
        foreach (var e in pbRoot.GetComponents<VRCPhysBone>()) Undo.DestroyObjectImmediate(e);
        foreach (var e in grabPoint.GetComponents<VRCPhysBone>()) Undo.DestroyObjectImmediate(e);
        foreach (var e in item.GetComponents<VRCPhysBone>()) Undo.DestroyObjectImmediate(e);
        foreach (var e in anchor.GetComponents<VRCPhysBone>()) Undo.DestroyObjectImmediate(e);

        var pb = Undo.AddComponent<VRCPhysBone>(pbRoot);
        pb.rootTransform = pbRoot.transform;
        pb.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
        pb.pull = 0f; pb.spring = 0f; pb.stiffness = 0f;
        pb.gravity = 0f; pb.gravityFalloff = 0f;
        pb.immobile = 1f;
        pb.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion;
        pb.maxStretch = 1000f;

        // Radius = スライダー値。autoExpandRadius 有効時はメッシュサイズに応じて拡張
        float effectiveRadius = Mathf.Max(grabRadius, 0.05f);
        if (autoExpandRadius)
        {
            float meshHalfExtent = ComputeMeshMaxHalfExtent(item, pbRoot);
            if (meshHalfExtent > effectiveRadius)
                effectiveRadius = meshHalfExtent;
        }
        pb.radius = effectiveRadius;

        pb.grabMovement = 1f;
        pb.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True;
        pb.allowPosing = VRCPhysBoneBase.AdvancedBool.True;
        pb.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
        pb.snapToHand = false;
        // Puppetize 同様、item をチェーンから除外 (PhysBone が item 以下のボーンに侵入しないように)
        pb.ignoreTransforms = new List<Transform> { item.transform };

        EditorUtility.SetDirty(pb);
        if (PrefabUtility.IsPartOfPrefabInstance(pb))
            PrefabUtility.RecordPrefabInstancePropertyModifications(pb);

        // Anchor に VRCParentConstraint (Source=Hips) を設定。
        // FreezeToWorld=false (= Hips に追従)。WorldLock 時は animator で true に切替。
        SafeConfigureAnchorConstraint(anchor);

        // 傾き防止
        if (lockTilt) ApplyRotationLock(item);
        else RemoveExistingRotationLock(item);

        return pbRoot;
    }

    /// <summary>
    /// Anchor に VRCParentConstraint を付与し、Source=Hips (Humanoidなら) で設定。
    /// Puppetize の SafeConfigureConstraint と同じ手法で、SerializedObject 経由で
    /// Sources 配列に安全に書き込む。
    /// </summary>
    void SafeConfigureAnchorConstraint(GameObject anchor)
    {
        if (anchor == null) return;

        // 既存 VRCParentConstraint があれば削除してから追加
        var existing = anchor.GetComponent<VRCParentConstraint>();
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        var pc = Undo.AddComponent<VRCParentConstraint>(anchor);

        // Source = Hips (Humanoidなら)、無ければアバタールート
        Transform src = avatar.transform;
        var anim = avatar.GetComponent<Animator>();
        if (anim != null && anim.avatar != null && anim.avatar.isHuman)
        {
            var h = anim.GetBoneTransform(HumanBodyBones.Hips);
            if (h != null) src = h;
        }

        var so = new SerializedObject(pc);

        // Sources 配列を検索
        SerializedProperty sourceArray = null;
        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            if (iter.isArray && iter.name.ToLower().Contains("source"))
            {
                sourceArray = so.FindProperty(iter.propertyPath);
                break;
            }
        }

        if (sourceArray != null)
        {
            if (sourceArray.arraySize == 0) sourceArray.InsertArrayElementAtIndex(0);
            var elem = sourceArray.GetArrayElementAtIndex(0);

            var elemIter = elem.Copy();
            int depth = elemIter.depth;
            bool sourceSet = false;
            while (elemIter.Next(true))
            {
                if (elemIter.depth <= depth) break;
                if (!sourceSet && elemIter.propertyType == SerializedPropertyType.ObjectReference)
                {
                    elemIter.objectReferenceValue = src;
                    sourceSet = true;
                }
                if (elemIter.propertyType == SerializedPropertyType.Float)
                {
                    elemIter.floatValue = 1f;
                }
            }
        }

        FindBoolPropByName(so, "IsActive", true);
        FindBoolPropByName(so, "Locked",   true);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(pc);
        if (PrefabUtility.IsPartOfPrefabInstance(pc))
            PrefabUtility.RecordPrefabInstancePropertyModifications(pc);
    }

    static void FindBoolPropByName(SerializedObject so, string name, bool value)
    {
        string lower = name.ToLower();
        var it = so.GetIterator();
        while (it.Next(true))
        {
            if (it.propertyType == SerializedPropertyType.Boolean && it.name.ToLower() == lower)
            {
                it.boolValue = value;
                return;
            }
        }
    }

    /// <summary>
    /// item 配下のすべての Renderer の World Bounds を合成し、
    /// 対角線の半分(=球として内包するための半径)を返す。
    /// autoExpandRadius 用。メッシュが無ければ 0。
    /// </summary>
    float ComputeMeshMaxHalfExtent(GameObject item, GameObject pbRoot)
    {
        var renderers = item.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0) return 0f;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        return b.extents.magnitude;
    }

    Vector3 ComputeGrabPointLocalOffset(GameObject item, GameObject pbRoot)
    {
        const float MinLength = 0.05f;
        if (autoCenterGrabPoint)
        {
            var renderer = item.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                Vector3 meshCenterWorld = renderer.bounds.center;
                Vector3 localDir = pbRoot.transform.InverseTransformPoint(meshCenterWorld);
                if (localDir.sqrMagnitude < 1e-6f) return new Vector3(0f, MinLength, 0f);
                return localDir.normalized * MinLength;
            }
        }
        Vector3 offset = grabPointOffset;
        if (offset.sqrMagnitude < 1e-6f) offset = new Vector3(0f, MinLength, 0f);
        return offset;
    }

    // =============================================================
    //  Rotation Constraint (傾き防止)
    // =============================================================
    void ApplyRotationLock(GameObject target)
    {
        var existing = target.GetComponent<VRCRotationConstraint>();
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        var rc = Undo.AddComponent<VRCRotationConstraint>(target);
        var so = new SerializedObject(rc);
        SetConstraintBool(so, "FreezeToWorld", true);

        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            if (iter.propertyType != SerializedPropertyType.Boolean) continue;
            string n = iter.name.ToLower();
            if (!n.Contains("affectsrotation")) continue;
            if (n.Contains("x")) iter.boolValue = true;
            else if (n.Contains("y")) iter.boolValue = false;
            else if (n.Contains("z")) iter.boolValue = true;
        }
        SetConstraintBool(so, "IsActive", true);
        SetConstraintBool(so, "Locked", true);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(rc);
        if (PrefabUtility.IsPartOfPrefabInstance(rc))
            PrefabUtility.RecordPrefabInstancePropertyModifications(rc);
    }

    void RemoveExistingRotationLock(GameObject target)
    {
        var existing = target.GetComponent<VRCRotationConstraint>();
        if (existing != null) Undo.DestroyObjectImmediate(existing);
    }

    void SetConstraintBool(SerializedObject so, string name, bool value)
    {
        var p = so.FindProperty(name);
        if (p != null && p.propertyType == SerializedPropertyType.Boolean)
        { p.boolValue = value; return; }
        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            if (iter.propertyType == SerializedPropertyType.Boolean
                && iter.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { iter.boolValue = value; return; }
        }
    }

    // =============================================================
    //  Animation Clips
    // =============================================================
    AnimationClip BuildActiveClip(string path, bool active, string filename)
    {
        var clip = new AnimationClip { name = filename };
        var binding = new EditorCurveBinding
        {
            path = path, type = typeof(GameObject), propertyName = "m_IsActive"
        };
        AnimationUtility.SetEditorCurve(clip, binding,
            AnimationCurve.Constant(0f, 1f / 60f, active ? 1f : 0f));
        DisableLoop(clip);
        SaveOrReplace(clip, $"{outputFolder}/{filename}.anim");
        return clip;
    }

    AnimationClip BuildFreezeToWorldClip(string path, bool freeze, string filename)
    {
        var clip = new AnimationClip { name = filename };
        var binding = new EditorCurveBinding
        {
            path = path, type = typeof(VRCParentConstraint), propertyName = "FreezeToWorld"
        };
        AnimationUtility.SetEditorCurve(clip, binding,
            AnimationCurve.Constant(0f, 1f / 60f, freeze ? 1f : 0f));
        DisableLoop(clip);
        SaveOrReplace(clip, $"{outputFolder}/{filename}.anim");
        return clip;
    }

    AnimationClip BuildEmptyClip(string filename)
    {
        var clip = new AnimationClip { name = filename };
        DisableLoop(clip);
        SaveOrReplace(clip, $"{outputFolder}/{filename}.anim");
        return clip;
    }

    void SaveOrReplace(AnimationClip clip, string assetPath)
    {
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
        if (existing != null) EditorUtility.CopySerialized(clip, existing);
        else AssetDatabase.CreateAsset(clip, assetPath);
    }

    static void DisableLoop(AnimationClip clip)
    {
        var s = AnimationUtility.GetAnimationClipSettings(clip);
        s.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, s);
    }

    // =============================================================
    //  FX / Parameters / Menu 確保
    //  useModularAvatar が true の場合は、アバター本体への代入はスキップし、
    //  新規独立アセットを生成して MA コンポーネントが参照する形にする。
    // =============================================================
    AnimatorController EnsureFXController()
    {
        if (useModularAvatar && MAIsAvailable())
        {
            // MA モード: アバター本体の FX は触らず、Bokugeki 専用 Controller を新規生成
            string p = $"{outputFolder}/{avatar.gameObject.name}_Bokugeki_FX.controller";
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(p)
                     ?? AnimatorController.CreateAnimatorControllerAtPath(p);
            return ac;
        }

        var layers = avatar.baseAnimationLayers;
        int fxIdx = -1;
        for (int i = 0; i < layers.Length; i++)
            if (layers[i].type == VRCAvatarDescriptor.AnimLayerType.FX) { fxIdx = i; break; }
        if (fxIdx < 0) throw new Exception("FXレイヤーが見つかりません。");

        var layer = layers[fxIdx];
        var acRes = layer.animatorController as AnimatorController;
        if (acRes == null || layer.isDefault)
        {
            string p = $"{outputFolder}/{avatar.gameObject.name}_FX.controller";
            acRes = AssetDatabase.LoadAssetAtPath<AnimatorController>(p)
                 ?? AnimatorController.CreateAnimatorControllerAtPath(p);
            layer.animatorController = acRes;
            layer.isDefault = false;
            layers[fxIdx] = layer;
            avatar.baseAnimationLayers = layers;
            avatar.customizeAnimationLayers = true;
        }
        return acRes;
    }

    VRCExpressionParameters EnsureExpressionParameters()
    {
        if (useModularAvatar && MAIsAvailable())
        {
            // MA モード: Bokugeki 専用 ExParams を新規生成(アバター本体には代入しない)
            string path = $"{outputFolder}/{avatar.gameObject.name}_Bokugeki_ExParams.asset";
            var p = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(path);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                p.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
                AssetDatabase.CreateAsset(p, path);
            }
            return p;
        }

        var pExist = avatar.expressionParameters;
        if (pExist == null)
        {
            string path = $"{outputFolder}/{avatar.gameObject.name}_ExParams.asset";
            pExist = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(path);
            if (pExist == null)
            {
                pExist = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                pExist.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
                AssetDatabase.CreateAsset(pExist, path);
            }
            avatar.expressionParameters = pExist;
            avatar.customExpressions = true;
        }
        return pExist;
    }

    VRCExpressionsMenu EnsureExpressionsMenu()
    {
        if (useModularAvatar && MAIsAvailable())
        {
            // MA モード: Bokugeki 専用 Menu ルートを新規生成(アバター本体には代入しない)
            string path = $"{outputFolder}/{avatar.gameObject.name}_Bokugeki_Menu.asset";
            var m = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
            if (m == null)
            {
                m = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                m.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        var mExist = avatar.expressionsMenu;
        if (mExist == null)
        {
            string path = $"{outputFolder}/{avatar.gameObject.name}_Menu.asset";
            mExist = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
            if (mExist == null)
            {
                mExist = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                mExist.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(mExist, path);
            }
            avatar.expressionsMenu = mExist;
            avatar.customExpressions = true;
        }
        return mExist;
    }

    VRCExpressionsMenu EnsureSubMenu(VRCExpressionsMenu parent, string name, string assetFileName)
    {
        var existing = parent.controls.FirstOrDefault(c =>
            c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.name == name);
        if (existing?.subMenu != null) return existing.subMenu;

        string path = $"{outputFolder}/{assetFileName}";
        var sub = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
        if (sub == null)
        {
            sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            sub.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(sub, path);
        }
        if (existing != null) existing.subMenu = sub;
        else
        {
            if (parent.controls.Count >= 8)
                Debug.LogWarning($"[Bokugeki] Menu '{parent.name}' は既に 8 件です。'{name}' は追加されましたが表示されない可能性があります。");
            parent.controls.Add(new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = sub
            });
        }
        return sub;
    }

    // =============================================================
    //  FX Layer / Parameter / Menu 追加
    // =============================================================
    void AddToggleLayer(AnimatorController ac, string layerName, string paramName,
        AnimationClip offClip, AnimationClip onClip, bool defaultOn)
    {
        if (!ac.parameters.Any(p => p.name == paramName))
        {
            ac.AddParameter(new AnimatorControllerParameter
            {
                name = paramName,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = defaultOn
            });
        }

        var layers = ac.layers;
        for (int i = layers.Length - 1; i >= 0; i--)
            if (layers[i].name == layerName) ac.RemoveLayer(i);

        var sm = new AnimatorStateMachine
        {
            name = layerName,
            hideFlags = HideFlags.HideInHierarchy
        };
        AssetDatabase.AddObjectToAsset(sm, ac);

        var offState = sm.AddState("Off");
        offState.motion = offClip; offState.writeDefaultValues = false;

        var onState = sm.AddState("On");
        onState.motion = onClip; onState.writeDefaultValues = false;

        sm.defaultState = defaultOn ? onState : offState;

        var toOn = offState.AddTransition(onState);
        toOn.hasExitTime = false; toOn.duration = 0;
        toOn.AddCondition(AnimatorConditionMode.If, 0, paramName);

        var toOff = onState.AddTransition(offState);
        toOff.hasExitTime = false; toOff.duration = 0;
        toOff.AddCondition(AnimatorConditionMode.IfNot, 0, paramName);

        ac.AddLayer(new AnimatorControllerLayer
        {
            name = layerName,
            defaultWeight = 1f,
            stateMachine = sm
        });
    }

    void AddExpressionParam(VRCExpressionParameters exParams, string paramName, bool defaultValue)
    {
        var list = exParams.parameters?.ToList() ?? new List<VRCExpressionParameters.Parameter>();
        var existing = list.FirstOrDefault(p => p.name == paramName);
        if (existing == null)
        {
            list.Add(new VRCExpressionParameters.Parameter
            {
                name = paramName,
                valueType = VRCExpressionParameters.ValueType.Bool,
                saved = false,
                defaultValue = defaultValue ? 1f : 0f,
                networkSynced = networkSync
            });
            exParams.parameters = list.ToArray();
        }
        else
        {
            existing.valueType = VRCExpressionParameters.ValueType.Bool;
            existing.saved = false;
            existing.defaultValue = defaultValue ? 1f : 0f;
            existing.networkSynced = networkSync;
        }
    }

    void AddMenuToggle(VRCExpressionsMenu menu, string name, string paramName)
    {
        var existing = menu.controls.FirstOrDefault(c =>
            c.name == name && c.type == VRCExpressionsMenu.Control.ControlType.Toggle);
        if (existing != null)
        {
            existing.parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName };
            existing.value = 1f; return;
        }
        if (menu.controls.Count >= 8)
            Debug.LogWarning($"[Bokugeki] Menu '{menu.name}' は既に 8 件です。'{name}' は追加されましたが表示されない可能性があります。");
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = name,
            type = VRCExpressionsMenu.Control.ControlType.Toggle,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName },
            value = 1f
        });
    }

    // =============================================================
    //  Utils
    // =============================================================
    static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{cur}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    // =============================================================
    //  ModularAvatar 統合 (リフレクションベース: MA 未インストール環境でもコンパイル可能)
    // =============================================================
    const string MA_NAMESPACE = "nadena.dev.modular_avatar.core";
    const string BOKUGEKI_MA_ROOT_NAME = "Bokugeki_MA";

    /// <summary>ModularAvatar がプロジェクトにインストールされているか</summary>
    static bool MAIsAvailable()
    {
        return FindMAType("ModularAvatarMergeAnimator") != null
            && FindMAType("ModularAvatarMenuInstaller") != null
            && FindMAType("ModularAvatarParameters") != null;
    }

    static Type FindMAType(string shortName)
    {
        string full = $"{MA_NAMESPACE}.{shortName}";
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(full, false);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>
    /// アバター直下に Bokugeki_MA オブジェクトを作成(既にあれば再利用)し、
    /// MergeAnimator / MenuInstaller / Parameters コンポーネントをセットアップ。
    /// </summary>
    void SetupModularAvatar(AnimatorController fx, VRCExpressionParameters exParams, VRCExpressionsMenu bokuMenu)
    {
        if (!MAIsAvailable())
        {
            Debug.LogWarning("[Bokugeki] ModularAvatar が見つかりません。MA 統合はスキップされます。");
            return;
        }

        // ルート探索: アバター直下に Bokugeki_MA
        var existing = avatar.transform.Find(BOKUGEKI_MA_ROOT_NAME);
        GameObject maRoot;
        if (existing != null)
        {
            maRoot = existing.gameObject;
        }
        else
        {
            maRoot = new GameObject(BOKUGEKI_MA_ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(maRoot, "Bokugeki MA Setup");
            maRoot.transform.SetParent(avatar.transform, false);
            maRoot.transform.localPosition = Vector3.zero;
            maRoot.transform.localRotation = Quaternion.identity;
            maRoot.transform.localScale = Vector3.one;
        }

        // MergeAnimator: FX controller をアバターの FX にマージ
        SetupMAMergeAnimator(maRoot, fx);

        // MenuInstaller: bokuMenu をアバターのメインメニューに追加
        SetupMAMenuInstaller(maRoot, bokuMenu);

        // Parameters: ExParams 内のパラメータを自動登録
        SetupMAParameters(maRoot, exParams);

        EditorUtility.SetDirty(maRoot);
        Debug.Log($"[Bokugeki] ModularAvatar 統合完了: '{BOKUGEKI_MA_ROOT_NAME}' 配下にコンポーネント設定。", maRoot);
    }

    void SetupMAMergeAnimator(GameObject maRoot, AnimatorController fx)
    {
        var maMergeType = FindMAType("ModularAvatarMergeAnimator");
        if (maMergeType == null) return;

        var comp = maRoot.GetComponent(maMergeType);
        if (comp == null)
            comp = Undo.AddComponent(maRoot, maMergeType);

        var so = new SerializedObject(comp);

        // animator (AnimatorController 参照) を fx に設定
        // MA のバージョン差を吸収するため複数の候補プロパティ名を試す
        TrySetObjectRef(so, new[] { "animator", "m_Animator" }, fx);

        // layerType = FX (enum。FXは通常値4だがバージョン差を考慮してstring名でも探す)
        TrySetEnumByName(so, new[] { "layerType", "m_LayerType" }, "FX");

        // deleteAttachedAnimator = true(既存のAnimatorコンポーネントと競合しないよう)
        TrySetBool(so, new[] { "deleteAttachedAnimator", "m_DeleteAttachedAnimator" }, true);

        // pathMode = Absolute
        //   BuildActiveClip は avatar.transform を基準にした絶対パス(例: "BokugekiItems/Chirashi_PBRoot")
        //   でアニメーションクリップを生成している。
        //   Relative だと MA が maRoot 基準でパス解決してしまい、
        //   "Bokugeki_MA/BokugekiItems/..." を探しに行って対象が見つからず
        //   アニメーションが無効化される。Absolute にすることで
        //   アバタールート基準でパスが解決される。
        TrySetEnumByName(so, new[] { "pathMode", "m_PathMode" }, "Absolute");

        // matchAvatarWriteDefaults = true
        TrySetBool(so, new[] { "matchAvatarWriteDefaults", "m_MatchAvatarWriteDefaults" }, true);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
    }

    void SetupMAMenuInstaller(GameObject maRoot, VRCExpressionsMenu bokuMenu)
    {
        var maMenuType = FindMAType("ModularAvatarMenuInstaller");
        if (maMenuType == null) return;

        var comp = maRoot.GetComponent(maMenuType);
        if (comp == null)
            comp = Undo.AddComponent(maRoot, maMenuType);

        var so = new SerializedObject(comp);

        // menuToAppend を bokuMenu に設定
        TrySetObjectRef(so, new[] { "menuToAppend", "m_MenuToAppend" }, bokuMenu);

        // installTargetMenu = null (= アバターのルートメニューに追加される)
        TrySetObjectRef(so, new[] { "installTargetMenu", "m_InstallTargetMenu" }, null);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
    }

    void SetupMAParameters(GameObject maRoot, VRCExpressionParameters exParams)
    {
        var maParamsType = FindMAType("ModularAvatarParameters");
        if (maParamsType == null) return;

        var comp = maRoot.GetComponent(maParamsType);
        if (comp == null)
            comp = Undo.AddComponent(maRoot, maParamsType);

        var so = new SerializedObject(comp);

        // parameters (List<ParameterConfig>) を exParams 内容で上書き
        var paramsProp = so.FindProperty("parameters");
        if (paramsProp == null) paramsProp = so.FindProperty("m_Parameters");
        if (paramsProp != null && paramsProp.isArray)
        {
            paramsProp.arraySize = exParams.parameters.Length;
            for (int i = 0; i < exParams.parameters.Length; i++)
            {
                var src = exParams.parameters[i];
                var elem = paramsProp.GetArrayElementAtIndex(i);

                // nameOrPrefix
                var nameP = elem.FindPropertyRelative("nameOrPrefix");
                if (nameP == null) nameP = elem.FindPropertyRelative("name");
                if (nameP != null) nameP.stringValue = src.name;

                // syncType: 0=NotSynced, 1=Int, 2=Float, 3=Bool
                // MA の ParameterSyncType enum: None=0, Int=1, Float=2, Bool=3
                int syncVal = 3; // default Bool
                switch (src.valueType)
                {
                    case VRCExpressionParameters.ValueType.Int: syncVal = 1; break;
                    case VRCExpressionParameters.ValueType.Float: syncVal = 2; break;
                    case VRCExpressionParameters.ValueType.Bool: syncVal = 3; break;
                }
                var syncP = elem.FindPropertyRelative("syncType");
                if (syncP == null) syncP = elem.FindPropertyRelative("m_SyncType");
                if (syncP != null) syncP.enumValueIndex = syncVal;

                // defaultValue
                var defP = elem.FindPropertyRelative("defaultValue");
                if (defP == null) defP = elem.FindPropertyRelative("m_DefaultValue");
                if (defP != null) defP.floatValue = src.defaultValue;

                // saved = false (全パラメータで saved しない仕様)
                var savedP = elem.FindPropertyRelative("saved");
                if (savedP == null) savedP = elem.FindPropertyRelative("m_Saved");
                if (savedP != null) savedP.boolValue = false;

                // localOnly = !networkSynced
                var localP = elem.FindPropertyRelative("localOnly");
                if (localP == null) localP = elem.FindPropertyRelative("m_LocalOnly");
                if (localP != null) localP.boolValue = !src.networkSynced;

                // internalParameter = false (公開パラメータ)
                var intP = elem.FindPropertyRelative("internalParameter");
                if (intP == null) intP = elem.FindPropertyRelative("m_InternalParameter");
                if (intP != null) intP.boolValue = false;

                // isPrefix = false (完全一致名)
                var preP = elem.FindPropertyRelative("isPrefix");
                if (preP == null) preP = elem.FindPropertyRelative("m_IsPrefix");
                if (preP != null) preP.boolValue = false;
            }
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
    }

    // --- SerializedProperty helpers (プロパティ名の揺れを吸収) ---
    static bool TrySetObjectRef(SerializedObject so, string[] candidates, UnityEngine.Object value)
    {
        foreach (var n in candidates)
        {
            var p = so.FindProperty(n);
            if (p != null && p.propertyType == SerializedPropertyType.ObjectReference)
            { p.objectReferenceValue = value; return true; }
        }
        return false;
    }

    static bool TrySetBool(SerializedObject so, string[] candidates, bool value)
    {
        foreach (var n in candidates)
        {
            var p = so.FindProperty(n);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            { p.boolValue = value; return true; }
        }
        return false;
    }

    /// <summary>enum プロパティを「名前」で設定(enum 値の並び順に依存しない)</summary>
    static bool TrySetEnumByName(SerializedObject so, string[] candidates, string enumValueName)
    {
        foreach (var n in candidates)
        {
            var p = so.FindProperty(n);
            if (p == null) continue;
            if (p.propertyType != SerializedPropertyType.Enum) continue;
            int idx = Array.IndexOf(p.enumNames, enumValueName);
            if (idx < 0)
            {
                // display name でも探す
                idx = Array.IndexOf(p.enumDisplayNames, enumValueName);
            }
            if (idx >= 0) { p.enumValueIndex = idx; return true; }
        }
        return false;
    }
}
#endif