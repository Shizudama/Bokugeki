#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.PhysBone;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

/// <summary>
/// Puppetize v4 — VRChat操り人形ギミック 統合セットアップツール
///
/// 生成されるHierarchy:
///   Avatar Root
///   └ PuppetWorldAnchor       ← VRC Parent Constraint
///     └ PuppetGrabHandle      ← VRC PhysBone (掴み判定)
///       └ PuppetGrabPoint     ← 空オブジェクト (Y=0.05, 回転最小化)
///         └ [PuppetObject]    ← パペット全体
///           └ [PuppetRoot]    ← ボーンのルート (四肢PhysBone)
///
/// 使い方:
///   1. Tools > Puppetize Setup
///   2. Avatar Descriptor / Puppet Object / Puppet Root を設定
///   3. Auto Detect → Apply Limb PhysBones (四肢のみ)
///   4. Setup World Drop System (GrabHandle + Constraint + Animator + Menu)
/// </summary>
public class PuppetizeSetup : EditorWindow
{
    private const string ASSET_FOLDER = "Assets/Bokugeki/Items/Puppetize/Generated";
    private const string PB_PARAM_PREFIX = "Puppet";
    private const string PARAM_IS_GRABBED = "Puppet_IsGrabbed";
    private const string PARAM_TOGGLE = "Puppetize_Toggle";
    private const string PARAM_RECALL = "Puppet_Recall";
    private const string PARAM_WORLD_FIX = "Puppetize_WorldFix";
    private const string PARAM_DANCE = "Puppetize_Dance";
    private const string PARAM_THROW = "Puppetize_Throw";
    private const string GRAB_HANDLE_NAME = "PuppetGrabHandle";
    private const string GRAB_POINT_NAME = "PuppetGrabPoint";
    private const string WORLD_ANCHOR_NAME = "PuppetWorldAnchor";

    // ─── 複数体対応ヘルパー ───
    private string Suf(int i) => puppetCount <= 1 ? "" : $"_{i+1}";
    private GameObject puppetObject => puppetObjects[_pi];
    private Transform puppetRoot => puppetRoots[_pi];
    private List<BoneChain> detectedChains => _allChains[_pi] ?? (_allChains[_pi] = new List<BoneChain>());
    private bool chainsDetected { get => _allChainsDetected[_pi]; set => _allChainsDetected[_pi] = value; }
    // パラメータ名（per-puppet）
    private string PToggle(int i) => "Puppetize_Toggle" + Suf(i);
    private string PWorldFix(int i) => "Puppetize_WorldFix" + Suf(i);
    private string PIsGrabbed(int i) => "Puppet" + Suf(i) + "_IsGrabbed";
    private string PRecall(int i) => "Puppet" + Suf(i) + "_Recall";
    private string PDance(int i) => "Puppetize_Dance" + Suf(i);
    private string PThrow(int i) => "Puppetize_Throw" + Suf(i);
    private string PPbPrefix(int i) => "Puppet" + Suf(i);
    private string WAnchor(int i) => WORLD_ANCHOR_NAME + Suf(i);
    private string GHandle(int i) => GRAB_HANDLE_NAME + Suf(i);
    private string GPoint(int i) => GRAB_POINT_NAME + Suf(i);

    [System.Serializable]
    public class BoneChain
    {
        public string label;
        public Transform root;
        public bool enabled = true;
        public bool isNeck = false;
        public string[] detectKeywords;
    }

    // 参照
    private VRCAvatarDescriptor avatarDescriptor;
    private const int MAX_PUPPETS = 3;
    private int puppetCount = 1;
    private GameObject[] puppetObjects = new GameObject[MAX_PUPPETS];
    private Transform[] puppetRoots = new Transform[MAX_PUPPETS];
    private int _pi = 0; // 現在の操作対象パペット

    // 四肢 PhysBone
    private float pull = 0.15f, spring = 0.4f, stiffness = 0.08f;
    private float gravity = -0.6f, gravityFalloff = 0f;
    private float grabMovement = 0.6f, radius = 0.05f, neckStiffness = 0.2f;
    private bool allowGrabbing = true, allowPosing = true, allowCollision = true;

    // 動作モード
    private bool keepPoseMode = true;   // 動かした位置をキープ（元に戻らない）
    private bool skipLegs = true;        // 足にロック用PhysBoneを付ける（直立不動）
    private bool lockArms = true;        // 腕にロック用PhysBoneを付ける（手首のみ可動）
    private bool lockPuppetAnimator = true;  // パペットのAnimatorを無効化
    private bool lockPuppetRotation = true;  // 人形の回転をワールドに固定
    private float limbMaxAngle = 45f;    // 四肢の関節制限（度）

    // GrabHandle（MVPで動作確認済みの値）
    private float grabHandleRadius = 0.2f;
    private float grabHandleGrabMove = 1f;
    private float grabHandleMaxStretch = 1000f;

    private enum Preset { Custom, Marionette, Ragdoll, Stiff }
    private Preset currentPreset = Preset.Marionette;
    private List<BoneChain>[] _allChains = new List<BoneChain>[MAX_PUPPETS];
    private bool[] _allChainsDetected = new bool[MAX_PUPPETS];
    private Vector2 scrollPos;
    private bool showWorldDrop = true;

    private static readonly string[][] ChainDefinitions = new string[][] {
        new[] { "Neck/Head", "true", "neck" },
        new[] { "Left Arm", "false", "l_upperarm", "left_upper_arm", "l_upper_arm", "upperarm_l", "upperarm.l", "left_shoulder", "l_arm", "leftupperarm" },
        new[] { "Right Arm", "false", "r_upperarm", "right_upper_arm", "r_upper_arm", "upperarm_r", "upperarm.r", "right_shoulder", "r_arm", "rightupperarm" },
        new[] { "Left Leg", "false", "l_upperleg", "left_upper_leg", "l_upper_leg", "upperleg_l", "upperleg.l", "left_thigh", "l_leg", "leftupperleg" },
        new[] { "Right Leg", "false", "r_upperleg", "right_upper_leg", "r_upper_leg", "upperleg_r", "upperleg.r", "right_thigh", "r_leg", "rightupperleg" },
        new[] { "Left Hand", "false", "l_hand", "left_hand", "hand_l", "hand.l", "lefthand" },
        new[] { "Right Hand", "false", "r_hand", "right_hand", "hand_r", "hand.r", "righthand" },
    };

    [MenuItem("Tools/Bokugeki/Puppetize Setup")]
    public static void ShowWindow()
    {
        var w = GetWindow<PuppetizeSetup>("Puppetize Setup");
        w.minSize = new Vector2(440, 700);
    }

    private void OnGUI()
    {
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        EditorGUILayout.Space(8);
        GUILayout.Label("Puppetize v4 — 統合セットアップ", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "1. 3つの参照を設定\n" +
            "2. Auto Detect → Apply Limb PhysBones (四肢のみ)\n" +
            "3. Setup World Drop System (掴み + ワールド固定 + メニュー)",
            MessageType.Info);
        EditorGUILayout.Space(4);

        avatarDescriptor = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
            "Avatar Descriptor", avatarDescriptor, typeof(VRCAvatarDescriptor), true);
        puppetCount = EditorGUILayout.IntSlider("パペット数", puppetCount, 1, MAX_PUPPETS);
        for (int pi = 0; pi < puppetCount; pi++)
        {
            _pi = pi;
            string lb = puppetCount <= 1 ? "" : $" #{pi+1}";
            EditorGUI.BeginChangeCheck();
            puppetObjects[pi] = (GameObject)EditorGUILayout.ObjectField($"Puppet Object{lb}", puppetObjects[pi], typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck() && puppetObjects[pi] != null && avatarDescriptor == null)
                avatarDescriptor = puppetObjects[pi].GetComponentInParent<VRCAvatarDescriptor>();
            EditorGUI.BeginChangeCheck();
            puppetRoots[pi] = (Transform)EditorGUILayout.ObjectField($"  Root{lb} (Hips)", puppetRoots[pi], typeof(Transform), true);
            if (EditorGUI.EndChangeCheck() && puppetRoots[pi] != null)
            {
                chainsDetected = false; detectedChains.Clear();
                if (avatarDescriptor == null) avatarDescriptor = puppetRoots[pi].GetComponentInParent<VRCAvatarDescriptor>();
                if (puppetObjects[pi] == null)
                {
                    Transform cursor = puppetRoots[pi]; Transform avatarTf = avatarDescriptor != null ? avatarDescriptor.transform : null;
                    while (cursor.parent != null) { if (cursor.parent == avatarTf || cursor.parent.name.Contains("GrabPoint")) { puppetObjects[pi] = cursor.gameObject; break; } cursor = cursor.parent; }
                    if (puppetObjects[pi] == null && puppetRoots[pi].parent != null && puppetRoots[pi].parent.parent != null) puppetObjects[pi] = puppetRoots[pi].parent.parent.gameObject;
                }
            }
        }
        _pi = 0;
        for (int pi = 0; pi < puppetCount; pi++)
            if (puppetObjects[pi] != null && puppetRoots[pi] != null && !puppetRoots[pi].IsChildOf(puppetObjects[pi].transform))
                EditorGUILayout.HelpBox($"Root と Object の親子関係が正しくありません (#{pi+1})", MessageType.Error);

        EditorGUILayout.Space(8);

        // ══ SECTION 1: 四肢 PhysBone ══
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        GUILayout.Label("Section 1: 四肢 PhysBone", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        currentPreset = (Preset)EditorGUILayout.EnumPopup("Preset", currentPreset);
        if (EditorGUI.EndChangeCheck()) ApplyPreset(currentPreset);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Limb PhysBone", EditorStyles.boldLabel);
        using (new EditorGUI.IndentLevelScope())
        {
            pull = EditorGUILayout.Slider("Pull", pull, 0f, 1f);
            spring = EditorGUILayout.Slider("Spring", spring, 0f, 1f);
            stiffness = EditorGUILayout.Slider("Stiffness", stiffness, 0f, 1f);
            gravity = EditorGUILayout.Slider("Gravity", gravity, -1f, 0f);
            gravityFalloff = EditorGUILayout.Slider("Gravity Falloff", gravityFalloff, 0f, 1f);
            grabMovement = EditorGUILayout.Slider("Grab Movement", grabMovement, 0f, 1f);
            radius = EditorGUILayout.Slider("Radius", radius, 0f, 0.2f);
            EditorGUILayout.Space(2);
            neckStiffness = EditorGUILayout.Slider("Neck Stiffness", neckStiffness, 0f, 1f);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Interaction", EditorStyles.boldLabel);
        using (new EditorGUI.IndentLevelScope())
        {
            allowGrabbing = EditorGUILayout.Toggle("Allow Grabbing", allowGrabbing);
            allowPosing = EditorGUILayout.Toggle("Allow Posing", allowPosing);
            allowCollision = EditorGUILayout.Toggle("Allow Collision", allowCollision);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
        using (new EditorGUI.IndentLevelScope())
        {
            skipLegs = EditorGUILayout.Toggle("足を直立不動にする", skipLegs);
            lockArms = EditorGUILayout.Toggle("腕を固定（手首のみ可動）", lockArms);
            lockPuppetAnimator = EditorGUILayout.Toggle("パペットのAnimatorを無効化", lockPuppetAnimator);
            keepPoseMode = EditorGUILayout.Toggle("動かした位置をキープ (KeepPose)", keepPoseMode);
            lockPuppetRotation = EditorGUILayout.Toggle("人形の傾き防止（水平回転は自由）", lockPuppetRotation);
            limbMaxAngle = EditorGUILayout.Slider("関節の最大曲げ角度", limbMaxAngle, 0f, 180f);
        }
        EditorGUILayout.HelpBox(
            "足を直立不動: 足にロック用PhysBoneを付与（Animator上書き）\n" +
            "腕を固定: 上腕を固定し、手首にだけPhysBoneを付与\n" +
            "Animator無効化: パペットのAnimatorを切り、アバターと連動させない\n" +
            "KeepPose: 掴んで離した位置に手足が留まります\n" +
            "傾き防止: X,Z回転をロック、Y回転（水平）は自由",
            MessageType.None);

        EditorGUILayout.Space(8);
        bool anyRoot = false; for (int pi=0;pi<puppetCount;pi++) if(puppetRoots[pi]!=null) anyRoot=true;
        GUI.enabled = anyRoot;
        if (GUILayout.Button("Auto Detect Chains", GUILayout.Height(28)))
        { for(int pi=0;pi<puppetCount;pi++){_pi=pi; if(puppetRoot!=null) AutoDetectChains();} _pi=0; }
        GUI.enabled = true;

        if (chainsDetected)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Detected Chains", EditorStyles.boldLabel);
            if (detectedChains.All(c => c.root == null))
                EditorGUILayout.HelpBox("自動検出できませんでした。手動で設定してください。", MessageType.Warning);

            for (int i = 0; i < detectedChains.Count; i++)
            {
                var ch = detectedChains[i];
                EditorGUILayout.BeginHorizontal();
                ch.enabled = EditorGUILayout.Toggle(ch.enabled, GUILayout.Width(16));
                EditorGUILayout.LabelField(ch.label, GUILayout.Width(90));
                ch.root = (Transform)EditorGUILayout.ObjectField(ch.root, typeof(Transform), true);
                ch.isNeck = EditorGUILayout.ToggleLeft("Neck", ch.isNeck, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Add Chain"))
                detectedChains.Add(new BoneChain { label = "Custom " + detectedChains.Count, enabled = true });

            EditorGUILayout.Space(8);
            bool hasAny = detectedChains.Any(c => c.enabled && c.root != null);
            GUI.enabled = hasAny;
            GUI.backgroundColor = new Color(0.3f, 0.9f, 0.5f);
            if (GUILayout.Button("Apply Limb PhysBones (四肢のみ)", GUILayout.Height(32)))
            { for(int pi=0;pi<puppetCount;pi++){_pi=pi; if(puppetRoot!=null&&chainsDetected) ApplyLimbPhysBones();} _pi=0; }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            EditorGUILayout.Space(4);
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("Remove All PhysBones"))
                if (EditorUtility.DisplayDialog("確認", "削除しますか？", "削除", "キャンセル"))
                { for(int pi=0;pi<puppetCount;pi++){_pi=pi; RemoveAllPhysBones();} _pi=0; }
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.Space(12);

        // ══ SECTION 2: World Drop ══
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        showWorldDrop = EditorGUILayout.Foldout(showWorldDrop, "Section 2: World Drop + 掴みシステム", true, EditorStyles.foldoutHeader);
        if (showWorldDrop)
        {
            EditorGUILayout.HelpBox(
                "以下を一括自動生成します:\n\n" +
                "  PuppetGrabHandle   (PhysBone 掴み判定)\n" +
                "  PuppetGrabPoint    (Y=0.05 微小オフセット)\n" +
                "  PuppetWorldAnchor  (Parent Constraint)\n" +
                "  FX Animator レイヤー x2 / クリップ x4\n" +
                "  Expression Parameters / Menu", MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("GrabHandle Settings", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                grabHandleRadius = EditorGUILayout.Slider("Radius (掴み判定の大きさ)", grabHandleRadius, 0.01f, 0.5f);
                grabHandleMaxStretch = EditorGUILayout.Slider("Max Stretch (移動範囲)", grabHandleMaxStretch, 1f, 2000f);
                grabHandleGrabMove = EditorGUILayout.Slider("Grab Movement (追従度)", grabHandleGrabMove, 0f, 1f);
            }
            EditorGUILayout.HelpBox(
                "GrabPoint Y=0.05 + MaxStretch=1000: 回転最小で50mの移動範囲\n" +
                "※ Questでは グリップボタン(中指で握る) で掴みます\n" +
                "※ トリガー(人差し指) では掴めません",
                MessageType.None);

            EditorGUILayout.Space(4);
            bool canSetup = avatarDescriptor != null && puppetObject != null && puppetRoot != null;
            if (!canSetup) EditorGUILayout.HelpBox("3つの参照をすべて設定してください。", MessageType.Warning);

            GUI.enabled = canSetup;
            GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
            if (GUILayout.Button("Setup World Drop System", GUILayout.Height(36))) SetupWorldDropSystem();
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);
            GUI.backgroundColor = new Color(1f, 0.7f, 0.5f);
            if (GUILayout.Button("Remove World Drop System (全パペット)"))
                if (EditorUtility.DisplayDialog("確認", "削除しますか？", "削除", "キャンセル")) RemoveWorldDropSystem();
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
        }

        EditorGUILayout.Space(12);
        EditorGUILayout.EndScrollView();
    }

    // ═══ プリセット ═══
    private void ApplyPreset(Preset p)
    {
        switch (p)
        {
            case Preset.Marionette:
                pull=0.15f; spring=0.4f; stiffness=0.08f; gravity=-0.6f; gravityFalloff=0f;
                grabMovement=0.6f; radius=0.05f; neckStiffness=0.2f;
                grabHandleRadius=0.2f; grabHandleGrabMove=1f;
                grabHandleMaxStretch=1000f; break;
                pull=0.05f; spring=0.2f; stiffness=0.02f; gravity=-0.8f; gravityFalloff=0f;
                grabMovement=0.7f; radius=0.06f; neckStiffness=0.05f;
                grabHandleRadius=0.2f; grabHandleGrabMove=1f;
                grabHandleMaxStretch=1000f; break;
            case Preset.Stiff:
                pull=0.3f; spring=0.6f; stiffness=0.3f; gravity=-0.4f; gravityFalloff=0f;
                grabMovement=0.5f; radius=0.04f; neckStiffness=0.35f;
                grabHandleRadius=0.2f; grabHandleGrabMove=1f;
                grabHandleMaxStretch=1000f; break;
        }
    }

    // ═══ チェーン検出 ═══
    private void AutoDetectChains()
    {
        detectedChains.Clear();
        if (puppetRoot == null) { chainsDetected = true; return; }
        var all = puppetRoot.GetComponentsInChildren<Transform>(true);
        foreach (var def in ChainDefinitions)
        {
            string label = def[0]; bool isNeck = def[1] == "true";

            // ★ モードに応じて検出対象を決める
            bool isLeg  = label == "Left Leg"  || label == "Right Leg";
            bool isArm  = label == "Left Arm"  || label == "Right Arm";
            bool isHand = label == "Left Hand" || label == "Right Hand";

            if (skipLegs && isLeg) continue;   // 足は検出しない
            if (lockArms && isArm) continue;   // 腕は検出しない
            if (!lockArms && isHand) continue; // 腕モード無効時は手も不要

            var kw = def.Skip(2).ToArray();
            Transform found = null;
            foreach (var t in all) { string n = t.name.ToLower().Replace(" ","_"); if (kw.Any(k => n.Contains(k))) { found=t; break; } }

            detectedChains.Add(new BoneChain { label=label, root=found, enabled=found!=null, isNeck=isNeck, detectKeywords=kw });
        }
        chainsDetected = true;
        Debug.Log($"[Puppetize] {detectedChains.Count(c=>c.root!=null)}/{detectedChains.Count} チェーン検出 (skipLegs={skipLegs}, lockArms={lockArms})。");
    }

    private void EnsureMinChainSlots()
    {
        while (detectedChains.Count < ChainDefinitions.Length)
        { int i=detectedChains.Count; detectedChains.Add(new BoneChain { label=ChainDefinitions[i][0], isNeck=ChainDefinitions[i][1]=="true" }); }
    }

    // ═══ 四肢 PhysBone ═══
    private void ApplyLimbPhysBones()
    {
        if (puppetRoot == null) return;
        Undo.SetCurrentGroupName("Puppetize Limb PhysBones");
        int ug = Undo.GetCurrentGroup();
        RemoveAllPhysBones();
        int count = 0;

        // KeepPose モード時は全ての「元に戻す力」をゼロに
        float applyPull      = keepPoseMode ? 0f : pull;
        float applySpring    = keepPoseMode ? 0f : spring;
        float applyStiffness = keepPoseMode ? 0f : stiffness;
        float applyNeckStiff = keepPoseMode ? 0f : neckStiffness;
        float applyGravity   = keepPoseMode ? 0f : gravity;

        foreach (var ch in detectedChains)
        {
            if (!ch.enabled || ch.root == null) continue;

            // ★ 安全策: もし手動でArm/Legが追加されていてモードONなら無視
            bool isLeg = ch.label == "Left Leg" || ch.label == "Right Leg";
            bool isArm = ch.label == "Left Arm" || ch.label == "Right Arm";
            bool isHand = ch.label == "Left Hand" || ch.label == "Right Hand";
            if (skipLegs && isLeg) continue;
            if (lockArms && isArm) continue;
            if (!lockArms && isHand) continue;

            var pb = Undo.AddComponent<VRCPhysBone>(ch.root.gameObject);
            pb.rootTransform = ch.root;
            pb.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;

            // 通常の PhysBone（Neck / Hand のみ到達）
            pb.pull = applyPull;
            pb.spring = applySpring;
            pb.stiffness = ch.isNeck ? applyNeckStiff : applyStiffness;
            pb.gravity = applyGravity;
            pb.gravityFalloff = gravityFalloff;
            pb.maxStretch = 0f;
            pb.grabMovement = grabMovement;
            pb.radius = radius;
            pb.allowGrabbing = allowGrabbing ? VRCPhysBoneBase.AdvancedBool.True : VRCPhysBoneBase.AdvancedBool.False;
            pb.allowPosing = allowPosing ? VRCPhysBoneBase.AdvancedBool.True : VRCPhysBoneBase.AdvancedBool.False;
            pb.allowCollision = allowCollision ? VRCPhysBoneBase.AdvancedBool.True : VRCPhysBoneBase.AdvancedBool.False;

            if (limbMaxAngle < 180f)
            {
                pb.limitType = VRCPhysBoneBase.LimitType.Angle;
                pb.maxAngleX = limbMaxAngle;
            }

            count++;
        }
        Undo.CollapseUndoOperations(ug);

        string modeInfo = keepPoseMode ? " (KeepPose)" : "";
        Debug.Log($"[Puppetize] {count} 個の PhysBone 設定完了{modeInfo}。対象: 首{(lockArms ? " + 両手首" : " + 両腕")}。");
        EditorUtility.DisplayDialog("Puppetize",
            $"{count} 個の PhysBone を設定しました。\n" +
            (keepPoseMode ? "【KeepPose】動かした位置をキープします。\n" : "") +
            (skipLegs ? "【足】PhysBone無し（親に追従）\n" : "") +
            (lockArms ? "【腕】PhysBone無し / 手首のみ可動\n" : "") +
            "次に Section 2「Setup World Drop System」を実行してください。", "OK");
    }

    private void RemoveAllPhysBones()
    {
        // ★ puppetObject 全体を検索（puppetRoot 配下だけだと PhysPappetModel に元から
        //   付いていた PhysBone を見落とす可能性があるため）
        var searchRoot = puppetObject != null ? puppetObject.transform : puppetRoot;
        if (searchRoot == null) return;

        // PhysBone 本体を削除
        var ex = searchRoot.GetComponentsInChildren<VRCPhysBone>(true);
        foreach (var pb in ex) Undo.DestroyObjectImmediate(pb);
        if (ex.Length > 0) Debug.Log($"[Puppetize] パペット内の {ex.Length} 個の PhysBone をすべて削除。");

        // ★ PhysBone Collider も削除（Spineなどに残っているとGrabHandle PhysBoneと干渉する）
        var cols = searchRoot.GetComponentsInChildren<VRCPhysBoneCollider>(true);
        foreach (var c in cols) Undo.DestroyObjectImmediate(c);
        if (cols.Length > 0) Debug.Log($"[Puppetize] パペット内の {cols.Length} 個の PhysBone Collider をすべて削除。");
    }

    // ═══ World Drop System ═══
    private void SetupWorldDropSystem()
    {
        if (avatarDescriptor == null) return;
        Undo.SetCurrentGroupName("Puppetize World Drop");
        int ug = Undo.GetCurrentGroup();
        EnsureAssetFolder();

        var fxPath = $"{ASSET_FOLDER}/Puppetize_FX.controller";
        var fx = AnimatorController.CreateAnimatorControllerAtPath(fxPath);
        bool anyFailed = false;

        for (int pi = 0; pi < puppetCount; pi++)
        {
            _pi = pi;
            if (puppetObject == null || puppetRoot == null) continue;
            string suf = Suf(pi);
            string lbl = puppetCount <= 1 ? "" : $" #{pi+1}";

            // Avatar Descriptor 自動削除
            var descs = puppetObject.GetComponentsInChildren<VRCAvatarDescriptor>(true);
            foreach (var d in descs) { if (d.gameObject != avatarDescriptor.gameObject) Undo.DestroyObjectImmediate(d); }

            CleanExistingWorldDrop();
            var grabHandle = CreateGrabStructure();
            var gp = grabHandle.transform.Find(GPoint(pi));
            if (gp == null || puppetObject.transform.parent != gp)
            {
                Debug.LogWarning($"[Puppetize] パペット{lbl}の自動配置に失敗。手動で{GPoint(pi)}に配置後、再実行してください。");
                anyFailed = true; continue;
            }
            Debug.Log($"[Puppetize] パペット{lbl} OK: {RelPath(avatarDescriptor.transform, puppetObject.transform)}");
            if (lockPuppetAnimator) DisablePuppetAnimators();
            SetupGrabHandlePhysBone(grabHandle);
            if (lockPuppetRotation) SetupPuppetRotationLock();

            var wa = grabHandle.transform.parent.gameObject;
            var cFollow = CreateStateClip($"Puppetize_Following{suf}", wa, grabHandle, false, 1f);
            var cFixed  = CreateStateClip($"Puppetize_WorldFixed{suf}", wa, grabHandle, true, 1f);
            var cReset  = CreateStateClip($"Puppetize_Reset{suf}", wa, grabHandle, false, 0f);
            var cOn  = CreateToggleClip($"Puppetize_ON{suf}", true);
            var cOff = CreateToggleClip($"Puppetize_OFF{suf}", false);

            SetupWorldDropLayer(fx, cFollow, cFixed, cReset, pi);
            SetupToggleLayer(fx, cOn, cOff, pi);

            // エモートアニメーション
            var cDance = CreateDanceClip($"Puppetize_Dance{suf}");
            var cThrow = CreateThrowClip($"Puppetize_Throw{suf}");
            SetupEmoteLayer(fx, cDance, cThrow, pi);
        }
        _pi = 0;
        EditorUtility.SetDirty(fx);

        if (anyFailed)
            EditorUtility.DisplayDialog("Puppetize", "一部パペットの自動配置に失敗。手動配置後に再実行してください。", "OK");

        SetupExpressionParameters();
        if (HasModularAvatar())
        {
            var a0 = avatarDescriptor.transform.Find(WAnchor(0));
            if (a0 != null) SetupWithModularAvatar(a0.gameObject, fx);
        }
        else { AssignFXToAvatar(fx); SetupExpressionMenu(); }

        Undo.CollapseUndoOperations(ug);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Puppetize", $"セットアップ完了! ({puppetCount}体, {puppetCount*4}bit)", "OK");
    }

    /// <summary>既存の WorldAnchor を完全に削除し、パペットをアバター直下に戻す</summary>
    private void CleanExistingWorldDrop()
    {
        var avatarRoot = avatarDescriptor.transform;
        if (puppetObject != null)
        {
            var rc = puppetObject.GetComponent<VRCRotationConstraint>(); if (rc) Undo.DestroyObjectImmediate(rc);
        }
        var anchor = avatarRoot.Find(WAnchor(_pi));
        if (anchor != null)
        {
            if (puppetObject != null && puppetObject.transform.IsChildOf(anchor))
            { var p=puppetObject.transform.position; var r=puppetObject.transform.rotation; puppetObject.transform.SetParent(avatarRoot,true); puppetObject.transform.position=p; puppetObject.transform.rotation=r; }
            Undo.DestroyObjectImmediate(anchor.gameObject);
        }
        // 残骸も削除
        foreach (var n in new[]{GHandle(_pi), GPoint(_pi)}) { var t=avatarRoot.Find(n); if(t) Undo.DestroyObjectImmediate(t.gameObject); }
    }

    private GameObject CreateGrabStructure()
    {
        var avatarRoot = avatarDescriptor.transform;
        var wPos = puppetObject.transform.position;
        var wRot = puppetObject.transform.rotation;

        // ★ 全オブジェクトを先に作り、最後にUndoを1回だけ登録
        var anchorGO = new GameObject(WAnchor(_pi));
        anchorGO.transform.SetParent(avatarRoot, false);
        anchorGO.transform.position = wPos;
        anchorGO.transform.rotation = wRot;

        var handleGO = new GameObject(GHandle(_pi));
        handleGO.transform.SetParent(anchorGO.transform, false);
        handleGO.transform.localPosition = Vector3.zero;

        // GrabPoint（★ Y=0.05 微小オフセット + MaxStretch=1000 で全方向50m移動可能）
        // 回転は0.05m分しか発生せず、Rotation Constraint FreezeToWorld で相殺
        var pointGO = new GameObject(GPoint(_pi));
        pointGO.transform.SetParent(handleGO.transform, false);
        pointGO.transform.localPosition = new Vector3(0f, 0.05f, 0f);

        // パペットを GrabPoint の子に移動
        puppetObject.transform.SetParent(pointGO.transform, true);
        puppetObject.transform.position = wPos;
        puppetObject.transform.rotation = wRot;

        // ★ Undo登録はHierarchy完成後に1回だけ
        Undo.RegisterCreatedObjectUndo(anchorGO, "Create Puppetize Hierarchy");

        Debug.Log($"[Puppetize] GrabHandle 構造を作成。パペット: {RelPath(avatarRoot, puppetObject.transform)}");

        // Constraint
        var constraint = Undo.AddComponent<VRCParentConstraint>(anchorGO);
        SafeConfigureConstraint(constraint, avatarRoot);

        return handleGO;
    }

    private void SetupGrabHandlePhysBone(GameObject handleGO)
    {
        var pb = Undo.AddComponent<VRCPhysBone>(handleGO);
        pb.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;

        // ★ MVPで動作確認済みの設定をそのまま使用
        pb.pull = 0f;
        pb.spring = 0f;
        pb.stiffness = 0f;
        pb.gravity = 0f;
        pb.gravityFalloff = 0f;
        pb.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion;
        pb.immobile = 1f;

        pb.maxStretch = grabHandleMaxStretch;
        pb.radius = grabHandleRadius;
        pb.grabMovement = grabHandleGrabMove;

        pb.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True;
        pb.allowPosing = VRCPhysBoneBase.AdvancedBool.True;
        pb.allowCollision = VRCPhysBoneBase.AdvancedBool.False;

        pb.parameter = PPbPrefix(_pi);

        // ★ 重要: パペット全体を ignoreTransforms に追加して
        // PhysBoneチェーンが人形のボーン階層に侵入するのを防ぐ
        // これにより、GrabHandle → GrabPoint の2ボーンだけがチェーンに含まれ、
        // 人形本体は単なる剛体としてGrabPointに追従するだけになる
        if (puppetObject != null)
        {
            pb.ignoreTransforms = new List<Transform> { puppetObject.transform };
            Debug.Log($"[Puppetize] ignoreTransforms に {puppetObject.name} を追加（チェーンからパペット全体を除外）");
        }

        Debug.Log($"[Puppetize] GrabHandle PhysBone 設定完了 (Immobile=1, R={grabHandleRadius}, MaxStretch={grabHandleMaxStretch})");
    }

    // ─── パペット内のAnimatorを無効化 ───
    private void DisablePuppetAnimators()
    {
        var animators = puppetObject.GetComponentsInChildren<Animator>(true);
        int disabledCount = 0;
        foreach (var anim in animators)
        {
            // VRC Avatar Descriptorと同じGameObjectのAnimatorは触らない
            if (anim.gameObject == avatarDescriptor.gameObject) continue;

            Undo.RecordObject(anim, "Disable Puppet Animator");
            anim.enabled = false;
            disabledCount++;
        }
        if (disabledCount > 0)
            Debug.Log($"[Puppetize] パペット内の {disabledCount} 個のAnimatorを無効化しました。");
    }

    // ─── 人形の回転をワールド固定 ───
    private void SetupPuppetRotationLock()
    {
        // 既存のRotationConstraintがあれば削除
        var existing = puppetObject.GetComponent<VRCRotationConstraint>();
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        var rc = Undo.AddComponent<VRCRotationConstraint>(puppetObject);
        var so = new SerializedObject(rc);

        // ★ 全プロパティをダンプして正しい名前を特定
        Debug.Log("[Puppetize] === Rotation Constraint プロパティ一覧 ===");
        var debugIter = so.GetIterator();
        while (debugIter.Next(true))
        {
            if (debugIter.name.ToLower().Contains("freeze") ||
                debugIter.name.ToLower().Contains("affect") ||
                debugIter.name.ToLower().Contains("rotation") ||
                debugIter.name.ToLower().Contains("active") ||
                debugIter.name.ToLower().Contains("locked") ||
                debugIter.name.ToLower().Contains("world"))
                Debug.Log($"  {debugIter.propertyPath} ({debugIter.propertyType}) name={debugIter.name}");
        }

        // ★ FreezeToWorld = true（Source不要でワールド回転を基準にする）
        SetConstraintBool(so, "FreezeToWorld", true);

        // ★ 回転軸の制御: X,Z をロック / Y を自由（水平回転のみ許可）
        // "AffectsRotationX/Y/Z" の場合: true=制約する=ロック
        // "FreezeRotationX/Y/Z" の場合: true=除外する=自由
        var iter2 = so.GetIterator();
        while (iter2.Next(true))
        {
            if (iter2.propertyType != SerializedPropertyType.Boolean) continue;
            string n = iter2.name.ToLower();

            // "AffectsRotation" パターン (true = constraint affects = locked)
            if (n.Contains("affectsrotation"))
            {
                if (n.Contains("x"))      { iter2.boolValue = true;  Debug.Log($"[Puppetize] {iter2.propertyPath} = true (X軸ロック)"); }
                else if (n.Contains("y")) { iter2.boolValue = false; Debug.Log($"[Puppetize] {iter2.propertyPath} = false (Y軸フリー)"); }
                else if (n.Contains("z")) { iter2.boolValue = true;  Debug.Log($"[Puppetize] {iter2.propertyPath} = true (Z軸ロック)"); }
            }
        }

        SetConstraintBool(so, "IsActive", true);
        SetConstraintBool(so, "Locked", true);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(rc);
        Debug.Log("[Puppetize] Rotation Constraint: FreezeToWorld + X,Zロック / Y自由");
    }

    /// <summary>Constraint 用 Bool プロパティ設定（プロパティ走査で確実に見つける）</summary>
    private void SetConstraintBool(SerializedObject so, string name, bool value)
    {
        // 直接アクセスを試行
        var p = so.FindProperty(name);
        if (p != null && p.propertyType == SerializedPropertyType.Boolean)
        { p.boolValue = value; return; }

        // 走査で探す
        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            if (iter.propertyType == SerializedPropertyType.Boolean
                && iter.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
            { iter.boolValue = value; Debug.Log($"[Puppetize] Constraint: {iter.propertyPath} = {value}"); return; }
        }
        Debug.LogWarning($"[Puppetize] プロパティ '{name}' が見つかりません");
    }

    private void SafeConfigureConstraint(VRCParentConstraint pc, Transform avatarRoot)
    {
        var anim = avatarRoot.GetComponent<Animator>();
        Transform src = avatarRoot;
        if (anim != null && anim.avatar != null) { var h = anim.GetBoneTransform(HumanBodyBones.Hips); if (h) src = h; }

        var so = new SerializedObject(pc);
        bool ok = false;

        // ★ 全プロパティをログに出力（デバッグ用）
        Debug.Log("[Puppetize] === Parent Constraint プロパティ一覧 ===");
        var debugIter = so.GetIterator();
        while (debugIter.Next(true))
        {
            Debug.Log($"  {debugIter.propertyPath} ({debugIter.propertyType}) name={debugIter.name}");
        }
        Debug.Log("[Puppetize] === プロパティ一覧終了 ===");

        // ★ Sources 配列を検索（NextVisible ではなく Next で非表示プロパティも走査）
        SerializedProperty sourceArray = null;
        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            // 配列かつ名前に source を含むプロパティを探す
            if (iter.isArray && iter.name.ToLower().Contains("source"))
            {
                sourceArray = so.FindProperty(iter.propertyPath);
                Debug.Log($"[Puppetize] Sources 配列発見: '{iter.propertyPath}' (size={iter.arraySize})");
                break;
            }
        }

        if (sourceArray != null)
        {
            if (sourceArray.arraySize == 0) sourceArray.InsertArrayElementAtIndex(0);
            var elem = sourceArray.GetArrayElementAtIndex(0);

            // 要素内を走査（Next で非表示含む）
            var elemIter = elem.Copy();
            int depth = elemIter.depth;
            while (elemIter.Next(true))
            {
                if (elemIter.depth <= depth) break;

                // ObjectReference で最初に見つかったものを Source とする
                if (!ok && elemIter.propertyType == SerializedPropertyType.ObjectReference)
                {
                    elemIter.objectReferenceValue = src;
                    ok = true;
                    Debug.Log($"[Puppetize] Source 設定: '{elemIter.propertyPath}' = {src.name}");
                }
                if (elemIter.propertyType == SerializedPropertyType.Float)
                {
                    elemIter.floatValue = 1f;
                    Debug.Log($"[Puppetize] Weight 設定: '{elemIter.propertyPath}' = 1.0");
                }
            }
        }

        // IsActive, Locked を設定
        SetBoolProp(so, "IsActive", true);
        SetBoolProp(so, "Locked", true);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(pc);

        if (ok)
            Debug.Log($"[Puppetize] Parent Constraint 自動設定成功: Source = {src.name}");
        else
            Debug.LogError($"[Puppetize] Parent Constraint Source 自動設定失敗！\n" +
                           $"  手動で Inspector の Sources に「{src.name}」を追加してください。\n" +
                           "  上記のプロパティ一覧をConsoleで確認し、開発者に報告してください。");
    }

    // ─── SerializedObject ヘルパー（プロパティ名を複数パターン試行）───
    private void SetBoolProp(SerializedObject so, string name, bool value)
    {
        string lower = name.ToLower();
        var iter = so.GetIterator();
        while (iter.Next(true))
        {
            if (iter.propertyType == SerializedPropertyType.Boolean && iter.name.ToLower() == lower)
            {
                iter.boolValue = value;
                return;
            }
        }
    }

    // ═══ アニメーション ═══
    private AnimationClip CreateStateClip(string name, GameObject anchor, GameObject grabHandle, bool freezeToWorld, float pbEnabled)
    {
        var clip = new AnimationClip { name = name };
        string anchorPath = RelPath(avatarDescriptor.transform, anchor.transform);
        float ftw = freezeToWorld ? 1f : 0f;

        // ★ 2キーフレーム（0秒と0.5秒）で0.5秒の長さを確保
        // Resetting ステートで PhysBone OFF→ON のリセット時間が必要なため
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = anchorPath, type = typeof(VRCParentConstraint), propertyName = "FreezeToWorld" },
            new AnimationCurve(new Keyframe(0f, ftw), new Keyframe(0.5f, ftw)));

        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = RelPath(avatarDescriptor.transform, grabHandle.transform), type = typeof(VRCPhysBone), propertyName = "m_Enabled" },
            new AnimationCurve(new Keyframe(0f, pbEnabled), new Keyframe(0.5f, pbEnabled)));

        var p = $"{ASSET_FOLDER}/{name}.anim";
        SaveClip(clip, p);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
    }

    private AnimationClip CreateToggleClip(string name, bool active)
    {
        var clip = new AnimationClip { name = name };
        float v = active ? 1f : 0f;

        // ★ puppetObject の GameObject 自体をオンオフ
        string path = RelPath(avatarDescriptor.transform, puppetObject.transform);
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = path, type = typeof(GameObject), propertyName = "m_IsActive" },
            new AnimationCurve(new Keyframe(0f, v)));
        Debug.Log($"[Puppetize] {name}: パス={path}, active={active}");

        var p = $"{ASSET_FOLDER}/{name}.anim"; SaveClip(clip, p);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
    }

    // ═══ エモートアニメーション ═══

    private AnimationClip CreateDanceClip(string clipName)
    {
        var clip = new AnimationClip { name = clipName };
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        float dur = 1.0f;
        string puppetPath = RelPath(avatarDescriptor.transform, puppetObject.transform);

        // 体全体の上下バウンス
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localPosition.y" },
            new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.15f, 0.03f), new Keyframe(0.25f, -0.01f),
                new Keyframe(0.5f, 0f), new Keyframe(0.65f, 0.03f), new Keyframe(0.75f, -0.01f), new Keyframe(dur, 0f)));
        // 左右揺れ
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localEulerAnglesRaw.z" },
            new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.25f, 8f), new Keyframe(0.5f, 0f), new Keyframe(0.75f, -8f), new Keyframe(dur, 0f)));
        // 水平回転
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localEulerAnglesRaw.y" },
            new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.25f, 15f), new Keyframe(0.5f, 0f), new Keyframe(0.75f, -15f), new Keyframe(dur, 0f)));

        // ボーンアニメーション
        if (puppetRoot != null)
        {
            string hipsPath = RelPath(avatarDescriptor.transform, puppetRoot);
            AnimationUtility.SetEditorCurve(clip,
                new EditorCurveBinding { path = hipsPath, type = typeof(Transform), propertyName = "localPosition.y" },
                new AnimationCurve(
                    new Keyframe(0f, 0f), new Keyframe(0.125f, 0.02f), new Keyframe(0.25f, -0.01f),
                    new Keyframe(0.375f, 0.02f), new Keyframe(0.5f, 0f), new Keyframe(0.625f, 0.02f),
                    new Keyframe(0.75f, -0.01f), new Keyframe(0.875f, 0.02f), new Keyframe(dur, 0f)));

            foreach (var bone in puppetRoot.GetComponentsInChildren<Transform>(true))
            {
                string bName = bone.name.ToLower();
                string bonePath = RelPath(avatarDescriptor.transform, bone);
                if (bName.Contains("l_upperarm") || bName.Contains("left_upper_arm") || bName.Contains("upperarm_l"))
                    AnimationUtility.SetEditorCurve(clip, new EditorCurveBinding { path = bonePath, type = typeof(Transform), propertyName = "localEulerAnglesRaw.x" },
                        new AnimationCurve(new Keyframe(0f,0f), new Keyframe(0.25f,30f), new Keyframe(0.5f,0f), new Keyframe(0.75f,-20f), new Keyframe(dur,0f)));
                if (bName.Contains("r_upperarm") || bName.Contains("right_upper_arm") || bName.Contains("upperarm_r"))
                    AnimationUtility.SetEditorCurve(clip, new EditorCurveBinding { path = bonePath, type = typeof(Transform), propertyName = "localEulerAnglesRaw.x" },
                        new AnimationCurve(new Keyframe(0f,0f), new Keyframe(0.25f,-20f), new Keyframe(0.5f,0f), new Keyframe(0.75f,30f), new Keyframe(dur,0f)));
            }
        }
        var p = $"{ASSET_FOLDER}/{clipName}.anim"; SaveClip(clip, p);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
    }

    private AnimationClip CreateThrowClip(string clipName)
    {
        var clip = new AnimationClip { name = clipName };
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        float dur = 3.0f;
        string puppetPath = RelPath(avatarDescriptor.transform, puppetObject.transform);

        // 前方20m
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localPosition.z" },
            new AnimationCurve(
                new Keyframe(0f,0f){outTangent=12f}, new Keyframe(0.5f,5f), new Keyframe(1f,10f),
                new Keyframe(1.5f,14f), new Keyframe(2f,17f), new Keyframe(2.5f,19f), new Keyframe(dur,20f){inTangent=1f}));
        // 放物線
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localPosition.y" },
            new AnimationCurve(
                new Keyframe(0f,0f), new Keyframe(0.3f,2.5f), new Keyframe(0.8f,4f),
                new Keyframe(1.3f,3.5f), new Keyframe(1.8f,2f), new Keyframe(2.3f,0.5f), new Keyframe(dur,-0.5f)));
        // 8回転
        float totalRot = 360f * 8f; int steps = 16;
        var rotKeys = new Keyframe[steps+1];
        for (int i=0;i<=steps;i++) rotKeys[i] = new Keyframe((dur/steps)*i, (totalRot/steps)*i);
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = puppetPath, type = typeof(Transform), propertyName = "localEulerAnglesRaw.x" },
            new AnimationCurve(rotKeys));

        // 体育座りポーズ
        if (puppetRoot != null)
        {
            foreach (var bone in puppetRoot.GetComponentsInChildren<Transform>(true))
            {
                string b = bone.name.ToLower();
                string bp = RelPath(avatarDescriptor.transform, bone);
                if (b.Contains("spine") && !b.Contains("upper")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 45f);
                if (b.Contains("chest") && !b.Contains("upper")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 30f);
                if (b.Contains("neck")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 40f);
                if (b.Contains("l_upperleg")||b.Contains("left_upper_leg")||b.Contains("upperleg_l")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, -100f);
                if (b.Contains("r_upperleg")||b.Contains("right_upper_leg")||b.Contains("upperleg_r")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, -100f);
                if (b.Contains("l_lowerleg")||b.Contains("left_lower_leg")||b.Contains("lowerleg_l")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 120f);
                if (b.Contains("r_lowerleg")||b.Contains("right_lower_leg")||b.Contains("lowerleg_r")) SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 120f);
                if (b.Contains("l_upperarm")||b.Contains("left_upper_arm")||b.Contains("upperarm_l")) { SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 60f); SetPose(clip, bp, "localEulerAnglesRaw.z", dur, -30f); }
                if (b.Contains("r_upperarm")||b.Contains("right_upper_arm")||b.Contains("upperarm_r")) { SetPose(clip, bp, "localEulerAnglesRaw.x", dur, 60f); SetPose(clip, bp, "localEulerAnglesRaw.z", dur, 30f); }
                if (b.Contains("l_lowerarm")||b.Contains("left_lower_arm")||b.Contains("lowerarm_l")) SetPose(clip, bp, "localEulerAnglesRaw.y", dur, -90f);
                if (b.Contains("r_lowerarm")||b.Contains("right_lower_arm")||b.Contains("lowerarm_r")) SetPose(clip, bp, "localEulerAnglesRaw.y", dur, 90f);
            }
        }
        var path = $"{ASSET_FOLDER}/{clipName}.anim"; SaveClip(clip, path);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    }

    private void SetPose(AnimationClip clip, string bonePath, string prop, float dur, float angle)
    {
        AnimationUtility.SetEditorCurve(clip,
            new EditorCurveBinding { path = bonePath, type = typeof(Transform), propertyName = prop },
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.1f, angle), new Keyframe(dur, angle)));
    }

    private void SetupEmoteLayer(AnimatorController c, AnimationClip dance, AnimationClip throwClip, int pi)
    {
        if (!c) return; string n = "Puppetize_Emote" + Suf(pi); RemoveLayer(c, n);
        AddP(c, PDance(pi), AnimatorControllerParameterType.Bool);
        AddP(c, PThrow(pi), AnimatorControllerParameterType.Bool);
        var l = MkLayer(c, n);

        var idleClip = new AnimationClip { name = $"Puppetize_Idle{Suf(pi)}" };
        AssetDatabase.AddObjectToAsset(idleClip, AssetDatabase.GetAssetPath(c));

        var sIdle  = l.stateMachine.AddState("Idle",  new Vector3(250,0,0));   sIdle.motion=idleClip;  sIdle.writeDefaultValues=false;
        var sDance = l.stateMachine.AddState("Dance", new Vector3(250,80,0));  sDance.motion=dance;    sDance.writeDefaultValues=false;
        var sThrow = l.stateMachine.AddState("Throw", new Vector3(250,160,0)); sThrow.motion=throwClip; sThrow.writeDefaultValues=false;
        l.stateMachine.defaultState = sIdle;

        // Throw 終了時に自動リセット
        var driver = sIdle.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
        driver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name=PThrow(pi), value=0f, type=VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set });

        var t1=sIdle.AddTransition(sDance); t1.AddCondition(AnimatorConditionMode.If,0,PDance(pi)); t1.duration=0.1f; t1.hasExitTime=false; t1.hasFixedDuration=true;
        var t2=sDance.AddTransition(sIdle); t2.AddCondition(AnimatorConditionMode.IfNot,0,PDance(pi)); t2.duration=0.2f; t2.hasExitTime=false; t2.hasFixedDuration=true;
        var t3=sIdle.AddTransition(sThrow); t3.AddCondition(AnimatorConditionMode.If,0,PThrow(pi)); t3.duration=0f; t3.hasExitTime=false; t3.hasFixedDuration=true;
        var t4=sThrow.AddTransition(sIdle); t4.hasExitTime=true; t4.exitTime=1f; t4.duration=0.1f; t4.hasFixedDuration=true;

        c.AddLayer(l); EditorUtility.SetDirty(c);
    }

    // ═══ FX Animator ═══
    private bool HasModularAvatar()
    {
        return System.Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator, nadena.dev.modular-avatar.core") != null;
    }

    /// <summary>Puppetize専用のFXコントローラーを作成（MA・従来共通）</summary>
    // CreatePuppetizeFXController は SetupWorldDropSystem 内でインライン化済み

    /// <summary>従来方式: FXコントローラーをアバターのFXスロットに直接割り当て</summary>
    private void AssignFXToAvatar(AnimatorController fx)
    {
        var ls = avatarDescriptor.baseAnimationLayers;
        for (int i = 0; i < ls.Length; i++)
        {
            if (ls[i].type != VRCAvatarDescriptor.AnimLayerType.FX) continue;
            ls[i].animatorController = fx;
            ls[i].isDefault = false;
            avatarDescriptor.baseAnimationLayers = ls;
            EditorUtility.SetDirty(avatarDescriptor);
            return;
        }
        Debug.LogError("[Puppetize] FX レイヤーなし。");
    }

    /// <summary>MA方式: PuppetWorldAnchorにMAコンポーネントを追加（SerializedObject方式）</summary>
    private void SetupWithModularAvatar(GameObject anchorGO, AnimatorController fx)
    {
        // ═══ MAMergeAnimator ═══
        var maMergeType = System.Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator, nadena.dev.modular-avatar.core");
        if (maMergeType != null && fx != null)
        {
            var existing = anchorGO.GetComponent(maMergeType);
            if (existing != null) Undo.DestroyObjectImmediate(existing);

            var merge = Undo.AddComponent(anchorGO, maMergeType);
            var so = new SerializedObject(merge);

            // animator = Puppetize_FX.controller
            SetSO(so, "animator", fx);
            // layerType = FX (5)
            SetSOEnum(so, "layerType", 5);
            // pathMode = Absolute (1)
            SetSOEnum(so, "pathMode", 1);
            // matchAvatarWriteDefaults = true
            SetSO(so, "matchAvatarWriteDefaults", true);
            // deleteAttachedAnimator = true（不要なAnimatorを自動削除）
            SetSO(so, "deleteAttachedAnimator", true);

            so.ApplyModifiedProperties();
            Debug.Log($"[Puppetize] MAMergeAnimator: animator={fx.name}, layerType=FX, pathMode=Absolute");
        }

        // ═══ MAParameters ═══
        var maParamType = System.Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarParameters, nadena.dev.modular-avatar.core");
        if (maParamType != null)
        {
            var existingP = anchorGO.GetComponent(maParamType);
            if (existingP != null) Undo.DestroyObjectImmediate(existingP);

            var maParams = Undo.AddComponent(anchorGO, maParamType);
            var so = new SerializedObject(maParams);
            var paramsProp = so.FindProperty("parameters");
            if (paramsProp != null && paramsProp.isArray)
            {
                paramsProp.ClearArray();
                // syncType: NotSynced=0, Int=1, Float=2, Bool=3
                for (int pi = 0; pi < puppetCount; pi++)
                {
                    AddMAParam(paramsProp, PToggle(pi),    3, true,  0f);
                    AddMAParam(paramsProp, PWorldFix(pi), 3, false, 0f);
                    AddMAParam(paramsProp, PIsGrabbed(pi),3, false, 0f);
                    AddMAParam(paramsProp, PRecall(pi),    3, false, 0f);
                    AddMAParam(paramsProp, PDance(pi),    3, false, 0f);
                    AddMAParam(paramsProp, PThrow(pi),    3, false, 0f);
                }
            }
            so.ApplyModifiedProperties();
            Debug.Log("[Puppetize] MAParameters: 4パラメータ登録完了");
        }

        // ═══ MAMenuInstaller ═══
        var maMenuType = System.Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller, nadena.dev.modular-avatar.core");
        if (maMenuType != null)
        {
            var existingM = anchorGO.GetComponent(maMenuType);
            if (existingM != null) Undo.DestroyObjectImmediate(existingM);

            // サブメニュー作成（操作ボタン群）
            var subPath = $"{ASSET_FOLDER}/Puppetize_SubMenu.asset";
            VRCExpressionsMenu sub;
            if (File.Exists(subPath)) { sub = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(subPath); sub.controls.Clear(); }
            else { sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); sub.controls = new List<VRCExpressionsMenu.Control>(); AssetDatabase.CreateAsset(sub, subPath); }

            for (int pi = 0; pi < puppetCount; pi++)
            {
                sub.controls.Add(new VRCExpressionsMenu.Control { name = (puppetCount<=1?"ON/OFF":$"#{pi+1} ON/OFF"), type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = PToggle(pi) } });
                sub.controls.Add(new VRCExpressionsMenu.Control { name = (puppetCount<=1?"ワールド固定":$"#{pi+1} 固定"), type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = PWorldFix(pi) } });
                sub.controls.Add(new VRCExpressionsMenu.Control { name = (puppetCount<=1?"回収":$"#{pi+1} 回収"), type = VRCExpressionsMenu.Control.ControlType.Button, parameter = new VRCExpressionsMenu.Control.Parameter { name = PRecall(pi) } });
                sub.controls.Add(new VRCExpressionsMenu.Control { name = (puppetCount<=1?"念仏踊り":$"#{pi+1} 踊り"), type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = PDance(pi) } });
                sub.controls.Add(new VRCExpressionsMenu.Control { name = (puppetCount<=1?"投げる":$"#{pi+1} 投げ"), type = VRCExpressionsMenu.Control.ControlType.Button, parameter = new VRCExpressionsMenu.Control.Parameter { name = PThrow(pi) } });
            }
            EditorUtility.SetDirty(sub);

            // メインメニュー作成（「Puppetize」エントリ → サブメニューを開く）
            var mainPath = $"{ASSET_FOLDER}/Puppetize_MainMenu.asset";
            VRCExpressionsMenu main;
            if (File.Exists(mainPath)) { main = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(mainPath); main.controls.Clear(); }
            else { main = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); main.controls = new List<VRCExpressionsMenu.Control>(); AssetDatabase.CreateAsset(main, mainPath); }

            main.controls.Add(new VRCExpressionsMenu.Control {
                name = "Puppetize",
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = sub
            });
            EditorUtility.SetDirty(main);

            // MAMenuInstaller に MainMenu を設定
            var maMenu = Undo.AddComponent(anchorGO, maMenuType);
            var so = new SerializedObject(maMenu);
            SetSO(so, "menuToAppend", main);
            SetSO(so, "installTargetAvatar", (Object)avatarDescriptor);
            so.ApplyModifiedProperties();

            Debug.Log("[Puppetize] MAMenuInstaller: MainMenu → SubMenu 構成で設定完了");
        }
    }

    // ─── MA用 SerializedObject ヘルパー ───
    private void SetSO(SerializedObject so, string propName, Object value)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.ObjectReference)
            p.objectReferenceValue = value;
    }
    private void SetSO(SerializedObject so, string propName, bool value)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            p.boolValue = value;
    }
    private void SetSOEnum(SerializedObject so, string propName, int value)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.Enum)
            p.enumValueIndex = value;
    }
    private void AddMAParam(SerializedProperty array, string name, int syncType, bool saved, float defaultVal)
    {
        int idx = array.arraySize;
        array.InsertArrayElementAtIndex(idx);
        var elem = array.GetArrayElementAtIndex(idx);

        var nameP = elem.FindPropertyRelative("nameOrPrefix");
        if (nameP != null) nameP.stringValue = name;

        var syncP = elem.FindPropertyRelative("syncType");
        if (syncP != null) syncP.enumValueIndex = syncType;

        var savedP = elem.FindPropertyRelative("saved");
        if (savedP != null) savedP.boolValue = saved;

        var defP = elem.FindPropertyRelative("defaultValue");
        if (defP != null) defP.floatValue = defaultVal;

        // internalParameter = false（公開パラメータ）
        var intP = elem.FindPropertyRelative("internalParameter");
        if (intP != null) intP.boolValue = false;

        // isPrefix = false（完全一致名）
        var preP = elem.FindPropertyRelative("isPrefix");
        if (preP != null) preP.boolValue = false;
    }

    private void SetupWorldDropLayer(AnimatorController c, AnimationClip follow, AnimationClip fix, AnimationClip reset, int pi)
    {
        if (!c) return; string n = "Puppetize_WorldDrop" + Suf(pi); RemoveLayer(c, n);
        AddP(c, PWorldFix(pi), AnimatorControllerParameterType.Bool);
        AddP(c, PRecall(pi), AnimatorControllerParameterType.Bool);
        AddP(c, PIsGrabbed(pi), AnimatorControllerParameterType.Bool);
        var l = MkLayer(c, n);
        var s1 = l.stateMachine.AddState("Following",  new Vector3(250, 0, 0));   s1.motion=follow; s1.writeDefaultValues=false;
        var s2 = l.stateMachine.AddState("WorldFixed", new Vector3(250, 100, 0));  s2.motion=fix;    s2.writeDefaultValues=false;
        var s3 = l.stateMachine.AddState("Resetting",  new Vector3(500, 50, 0));   s3.motion=reset;  s3.writeDefaultValues=false;
        l.stateMachine.defaultState = s1;

        // ★ Resetting に入った時に Recall と WorldFix を自動リセット
        var resetDriver = s3.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
        resetDriver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter {
            name = PRecall(pi), value = 0f,
            type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set
        });
        resetDriver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter {
            name = PWorldFix(pi), value = 0f,
            type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set
        });

        // Following → WorldFixed: ワールド固定ボタンON
        var t1 = s1.AddTransition(s2);
        t1.AddCondition(AnimatorConditionMode.If, 0, PWorldFix(pi));
        t1.duration = 0f; t1.hasExitTime = false; t1.hasFixedDuration = true;

        // WorldFixed → Following: ワールド固定ボタンOFF
        var t2 = s2.AddTransition(s1);
        t2.AddCondition(AnimatorConditionMode.IfNot, 0, PWorldFix(pi));
        t2.duration = 0f; t2.hasExitTime = false; t2.hasFixedDuration = true;

        // ★ WorldFixed → Resetting: 回収ボタン（ワールド固定中に回収）
        var t3 = s2.AddTransition(s3);
        t3.AddCondition(AnimatorConditionMode.If, 0, PRecall(pi));
        t3.duration = 0f; t3.hasExitTime = false; t3.hasFixedDuration = true;

        // ★ Following → Resetting: 回収ボタン（掴んで遠くに持っていった後に回収）
        var t5 = s1.AddTransition(s3);
        t5.AddCondition(AnimatorConditionMode.If, 0, PRecall(pi));
        t5.duration = 0f; t5.hasExitTime = false; t5.hasFixedDuration = true;

        // Resetting → Following: 0.5秒後に自動遷移（PhysBoneリセット時間を確保）
        var t4 = s3.AddTransition(s1);
        t4.hasExitTime = true; t4.exitTime = 1f;
        t4.duration = 0f; t4.hasFixedDuration = true;

        c.AddLayer(l); EditorUtility.SetDirty(c);
    }

    private void SetupToggleLayer(AnimatorController c, AnimationClip on, AnimationClip off, int pi)
    {
        if (!c) return; string n = "Puppetize_Toggle" + Suf(pi); RemoveLayer(c, n);
        AddP(c, PToggle(pi), AnimatorControllerParameterType.Bool);
        var l = MkLayer(c, n);
        var s1 = l.stateMachine.AddState("OFF", new Vector3(250,0,0)); s1.motion=off; s1.writeDefaultValues=false;
        var s2 = l.stateMachine.AddState("ON", new Vector3(250,80,0)); s2.motion=on; s2.writeDefaultValues=false;
        l.stateMachine.defaultState = s1;
        var t1=s1.AddTransition(s2); t1.AddCondition(AnimatorConditionMode.If,0,PToggle(pi)); t1.duration=0f; t1.hasExitTime=false; t1.hasFixedDuration=true;
        var t2=s2.AddTransition(s1); t2.AddCondition(AnimatorConditionMode.IfNot,0,PToggle(pi)); t2.duration=0f; t2.hasExitTime=false; t2.hasFixedDuration=true;
        c.AddLayer(l); EditorUtility.SetDirty(c);
    }

    private AnimatorControllerLayer MkLayer(AnimatorController c, string n)
    {
        var l = new AnimatorControllerLayer { name=n, defaultWeight=1f, stateMachine=new AnimatorStateMachine{name=n} };
        l.stateMachine.hideFlags = HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(l.stateMachine, AssetDatabase.GetAssetPath(c)); return l;
    }

    // ═══ Expression ═══
    private void SetupExpressionParameters()
    {
        var path = $"{ASSET_FOLDER}/Puppetize_ExParams.asset";
        VRCExpressionParameters ep;
        if (File.Exists(path)) { ep = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(path); ep.parameters = new VRCExpressionParameters.Parameter[0]; }
        else { ep = ScriptableObject.CreateInstance<VRCExpressionParameters>(); ep.parameters = new VRCExpressionParameters.Parameter[0]; AssetDatabase.CreateAsset(ep, path); }
        for (int pi = 0; pi < puppetCount; pi++)
        {
            AEP(ep, PToggle(pi), VRCExpressionParameters.ValueType.Bool, true);
            AEP(ep, PWorldFix(pi), VRCExpressionParameters.ValueType.Bool, false);
            AEP(ep, PIsGrabbed(pi), VRCExpressionParameters.ValueType.Bool, false);
            AEP(ep, PRecall(pi), VRCExpressionParameters.ValueType.Bool, false);
            AEP(ep, PDance(pi), VRCExpressionParameters.ValueType.Bool, false);
            AEP(ep, PThrow(pi), VRCExpressionParameters.ValueType.Bool, false);
        }
        EditorUtility.SetDirty(ep);
        if (avatarDescriptor.expressionParameters == null) { avatarDescriptor.expressionParameters = ep; EditorUtility.SetDirty(avatarDescriptor); }
        else { var aep = avatarDescriptor.expressionParameters; for(int pi=0;pi<puppetCount;pi++) { AEP(aep,PToggle(pi),VRCExpressionParameters.ValueType.Bool,true); AEP(aep,PWorldFix(pi),VRCExpressionParameters.ValueType.Bool,false); AEP(aep,PIsGrabbed(pi),VRCExpressionParameters.ValueType.Bool,false); AEP(aep,PRecall(pi),VRCExpressionParameters.ValueType.Bool,false); AEP(aep,PDance(pi),VRCExpressionParameters.ValueType.Bool,false); AEP(aep,PThrow(pi),VRCExpressionParameters.ValueType.Bool,false); } EditorUtility.SetDirty(aep); }
    }
    private void AEP(VRCExpressionParameters ep, string n, VRCExpressionParameters.ValueType t, bool s)
    { if (ep.parameters.Any(p=>p.name==n)) return; var l=ep.parameters.ToList();
      l.Add(new VRCExpressionParameters.Parameter{name=n,valueType=t,defaultValue=0f,saved=s,networkSynced=true}); ep.parameters=l.ToArray(); }

    private void SetupExpressionMenu()
    {
        var rm = avatarDescriptor.expressionsMenu;
        if (rm == null)
        { rm = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); rm.controls=new List<VRCExpressionsMenu.Control>();
          AssetDatabase.CreateAsset(rm, $"{ASSET_FOLDER}/Puppetize_RootMenu.asset"); avatarDescriptor.expressionsMenu=rm; EditorUtility.SetDirty(avatarDescriptor); }

        // メインメニュー（Puppetize エントリ → サブメニュー）
        var mainPath = $"{ASSET_FOLDER}/Puppetize_MainMenu.asset"; VRCExpressionsMenu main;
        if (File.Exists(mainPath)) { main=AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(mainPath); main.controls.Clear(); }
        else { main=ScriptableObject.CreateInstance<VRCExpressionsMenu>(); main.controls=new List<VRCExpressionsMenu.Control>(); AssetDatabase.CreateAsset(main,mainPath); }

        for (int pi = 0; pi < puppetCount; pi++)
        {
            string suf = Suf(pi); string lbl = puppetCount <= 1 ? "" : $" #{pi+1}";
            var sp = $"{ASSET_FOLDER}/Puppetize_SubMenu{suf}.asset"; VRCExpressionsMenu sub;
            if (File.Exists(sp)) { sub=AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(sp); sub.controls.Clear(); }
            else { sub=ScriptableObject.CreateInstance<VRCExpressionsMenu>(); sub.controls=new List<VRCExpressionsMenu.Control>(); AssetDatabase.CreateAsset(sub,sp); }
            sub.controls.Add(new VRCExpressionsMenu.Control{name="ON/OFF",type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter{name=PToggle(pi)}});
            sub.controls.Add(new VRCExpressionsMenu.Control{name="ワールド固定",type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter{name=PWorldFix(pi)}});
            sub.controls.Add(new VRCExpressionsMenu.Control{name="回収",type=VRCExpressionsMenu.Control.ControlType.Button,parameter=new VRCExpressionsMenu.Control.Parameter{name=PRecall(pi)}});
            sub.controls.Add(new VRCExpressionsMenu.Control{name="念仏踊り",type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter{name=PDance(pi)}});
            sub.controls.Add(new VRCExpressionsMenu.Control{name="投げる",type=VRCExpressionsMenu.Control.ControlType.Button,parameter=new VRCExpressionsMenu.Control.Parameter{name=PThrow(pi)}});
            EditorUtility.SetDirty(sub);

            if (puppetCount <= 1)
                main.controls.Add(new VRCExpressionsMenu.Control{name="Puppetize",type=VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=sub});
            else
                main.controls.Add(new VRCExpressionsMenu.Control{name=$"Puppet{lbl}",type=VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=sub});
        }
        EditorUtility.SetDirty(main);
        if (!rm.controls.Any(c=>c.name=="Puppetize" || c.name.StartsWith("Puppet")))
        { if (rm.controls.Count<8) { rm.controls.Add(new VRCExpressionsMenu.Control{name="Puppetize",type=VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=main}); EditorUtility.SetDirty(rm); } }
    }

    // ═══ 削除 ═══
    private void RemoveWorldDropSystem()
    {
        if (!avatarDescriptor) return;

        // Rotation Constraint も削除
        if (puppetObject != null)
        {
            var rc = puppetObject.GetComponent<VRCRotationConstraint>();
            if (rc != null) Undo.DestroyObjectImmediate(rc);
        }

        var anchor = avatarDescriptor.transform.Find(WAnchor(_pi));
        if (!anchor) return;
        if (puppetObject != null && puppetObject.transform.IsChildOf(anchor))
        {
            var p = puppetObject.transform.position;
            var r = puppetObject.transform.rotation;
            puppetObject.transform.SetParent(avatarDescriptor.transform, true);
            puppetObject.transform.position = p;
            puppetObject.transform.rotation = r;
            Debug.Log("[Puppetize] パペットをアバター直下に復帰。");
        }
        Undo.DestroyObjectImmediate(anchor.gameObject);
        Debug.Log("[Puppetize] World Drop 削除。");
    }

    // ═══ Util ═══
    private void EnsureAssetFolder()
    { if (!AssetDatabase.IsValidFolder("Assets/Bokugeki")) AssetDatabase.CreateFolder("Assets","Bokugeki");
      if (!AssetDatabase.IsValidFolder("Assets/Bokugeki/Items")) AssetDatabase.CreateFolder("Assets/Bokugeki","Items");
      if (!AssetDatabase.IsValidFolder("Assets/Bokugeki/Items/Puppetize")) AssetDatabase.CreateFolder("Assets/Bokugeki/Items","Puppetize");
      if (!AssetDatabase.IsValidFolder(ASSET_FOLDER)) AssetDatabase.CreateFolder("Assets/Bokugeki/Items/Puppetize","Generated"); }
    private string RelPath(Transform root, Transform target)
    { var parts=new List<string>(); var c=target; while(c!=null&&c!=root){parts.Insert(0,c.name);c=c.parent;} return string.Join("/",parts); }
    private void SaveClip(AnimationClip clip, string path)
    { var e=AssetDatabase.LoadAssetAtPath<Object>(path); if(e) EditorUtility.CopySerialized(clip,e); else AssetDatabase.CreateAsset(clip,path); }
    private void AddP(AnimatorController c, string n, AnimatorControllerParameterType t) { if (c.parameters.Any(p=>p.name==n)) return; c.AddParameter(n,t); }
    private void RemoveLayer(AnimatorController c, string n)
    { var ls=c.layers.ToList(); int i=ls.FindIndex(l=>l.name==n); if(i>=0){if(ls[i].stateMachine) AssetDatabase.RemoveObjectFromAsset(ls[i].stateMachine); ls.RemoveAt(i); c.layers=ls.ToArray();} }
}
#endif