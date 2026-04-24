// =============================================================================
// PersonaSetup.cs
//
// VRChat avatar "Persona" (face-swap) gimmick setup tool.
// Place this file under an Editor/ folder in your Unity project.
//
// Menu: Tools > Bokugeki > PersonaSetup
//
// Requirements:
//   - VRChat SDK3 (Avatars)
//   - Persona.shader (Shader "Custom/Persona")
//   - (Optional) Modular Avatar v1.x — detected automatically at runtime via
//     reflection; no asmdef / define symbol configuration required.
//
// Features:
//   - Duplicates the source face mesh as OuterFace_Mask (keeps original
//     materials, only swaps the shader to Custom/Persona).
//   - Automatically computes pivot (Neck world position) for the peel
//     rotation.
//   - Assigns a pure black Unlit material to a user-provided Inner Face
//     GameObject (and its descendants' renderers).
//   - Swaps the entire body to a pure black material the moment peel starts.
//   - Hides OuterFace_Mask while idle; reveals it at peel start; destroys
//     it at peel completion.
//   - Integrates with Modular Avatar (MergeAnimator / Parameters /
//     MenuInstaller) when available, otherwise writes to the avatar's FX /
//     ExParams / ExMenu directly.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Bokugeki.PersonaTool
{
    public class PersonaSetup : EditorWindow
    {
        // ---------------------------------------------------------------------
        // User inputs
        // ---------------------------------------------------------------------
        private VRCAvatarDescriptor avatar;
        private SkinnedMeshRenderer sourceFaceMesh;
        private GameObject innerFace;            // required - revealed after peel
        private Shader personaShader;

        // Body Material Swap - replaces body with pure black at peel start
        private bool enableBodyMaterialSwap = true;
        private SkinnedMeshRenderer bodyMesh;    // defaults to sourceFaceMesh if null

        // Animation
        private float transformDuration = 2.0f;
        private float resetDuration = 0.3f;

        // Pivot (material)
        private bool autoCalculatePivot = true;
        private float pivotWorldY = 1.4f;
        private float pivotWorldZ = 0.0f;
        [Range(0.1f, 3f)] private float rotationStrength = 0.5f;
        [Range(0f, 1f)] private float brightness = 0.5f;
        [Range(0f, 0.9f)] private float fadeStart = 0.6f;

        // Output
        private string outputBasePath = "Assets/Bokugeki/Items/Persona/Generated";
        private string paramName = "Persona";
        private string menuLabel = "Persona";

        private bool useModularAvatar = true;

        private Vector2 scroll;

        // Black color used for all auto-generated materials
        private static readonly Color BlackColor = new Color(0f, 0f, 0f, 1f);

        // ---------------------------------------------------------------------
        // Menu
        // ---------------------------------------------------------------------
        [MenuItem("Tools/Bokugeki/PersonaSetup")]
        public static void ShowWindow()
        {
            var w = GetWindow<PersonaSetup>("Persona Setup");
            w.minSize = new Vector2(420, 520);
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Persona Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "アバターの顔を剥がして下から別の顔を出すギミックを自動構築します。\n" +
                "Source Face Mesh は元の顔の SkinnedMeshRenderer、Inner Face は剥離後に現れる顔メッシュの GameObject を指定してください。",
                MessageType.Info);
            EditorGUILayout.Space();

            DrawTargetsSection();
            EditorGUILayout.Space();

            DrawAnimationSection();
            EditorGUILayout.Space();

            DrawPivotSection();
            EditorGUILayout.Space();

            DrawMaterialSection();
            EditorGUILayout.Space();

            DrawBodySwapSection();
            EditorGUILayout.Space();

            DrawIntegrationSection();
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!ValidateInputs(out _)))
            {
                if (GUILayout.Button("Setup Persona", GUILayout.Height(36)))
                {
                    RunSetup();
                }
            }

            if (!ValidateInputs(out var msg))
            {
                EditorGUILayout.HelpBox(msg, MessageType.Warning);
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------------
        // UI sections
        // ---------------------------------------------------------------------
        private void DrawTargetsSection()
        {
            EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);
            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", avatar, typeof(VRCAvatarDescriptor), true);
            sourceFaceMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Source Face Mesh", sourceFaceMesh, typeof(SkinnedMeshRenderer), true);
            personaShader = (Shader)EditorGUILayout.ObjectField(
                "Persona Shader", personaShader, typeof(Shader), false);
            if (personaShader == null)
            {
                var s = Shader.Find("Custom/Persona");
                if (s != null && GUILayout.Button("Auto-find \"Custom/Persona\" shader"))
                    personaShader = s;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Inner Face", EditorStyles.miniBoldLabel);
            innerFace = (GameObject)EditorGUILayout.ObjectField(
                "Inner Face GO", innerFace, typeof(GameObject), true);
            EditorGUILayout.HelpBox(
                "アバター階層内の、剥離後に現れる顔メッシュの GameObject を指定してください。" +
                "配下の全 Renderer のマテリアルが自動生成された真っ黒マテリアルに差し替えられます。",
                MessageType.None);
        }

        private void DrawAnimationSection()
        {
            EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
            transformDuration = Mathf.Max(0.1f, EditorGUILayout.FloatField("Transform Duration (s)", transformDuration));
            resetDuration = Mathf.Max(0f, EditorGUILayout.FloatField("Reset Blend (s)", resetDuration));
        }

        private void DrawPivotSection()
        {
            EditorGUILayout.LabelField("Pivot (world space)", EditorStyles.boldLabel);
            autoCalculatePivot = EditorGUILayout.Toggle("Auto-calculate from Neck", autoCalculatePivot);
            using (new EditorGUI.DisabledScope(autoCalculatePivot))
            {
                pivotWorldY = EditorGUILayout.FloatField("Pivot World Y", pivotWorldY);
                pivotWorldZ = EditorGUILayout.FloatField("Pivot World Z", pivotWorldZ);
            }
            rotationStrength = EditorGUILayout.Slider("Rotation Strength", rotationStrength, 0.1f, 3f);
        }

        private void DrawMaterialSection()
        {
            EditorGUILayout.LabelField("Material (Persona shader)", EditorStyles.boldLabel);
            brightness = EditorGUILayout.Slider("MToon Brightness", brightness, 0f, 1f);
            fadeStart = EditorGUILayout.Slider("Fade Start", fadeStart, 0f, 0.9f);
        }

        private void DrawBodySwapSection()
        {
            EditorGUILayout.LabelField("Body Material Swap", EditorStyles.boldLabel);
            enableBodyMaterialSwap = EditorGUILayout.Toggle("Enable", enableBodyMaterialSwap);

            if (!enableBodyMaterialSwap) return;

            bodyMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Body Mesh (blank = Source)", bodyMesh, typeof(SkinnedMeshRenderer), true);

            var targetBody = bodyMesh != null ? bodyMesh : sourceFaceMesh;
            if (targetBody != null)
            {
                int slotCount = targetBody.sharedMaterials != null ? targetBody.sharedMaterials.Length : 0;
                EditorGUILayout.HelpBox(
                    $"剥離開始時(Transform 突入の瞬間)に、Body の {slotCount} 個のスロット全てが真っ黒マテリアルで塗り潰されます。" +
                    "白目含め全スロットが同一の真っ黒マテリアルになります。",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Body Mesh または Source Face Mesh を指定してください。",
                    MessageType.Warning);
            }
        }

        private void DrawIntegrationSection()
        {
            EditorGUILayout.LabelField("Integration", EditorStyles.boldLabel);
            outputBasePath = EditorGUILayout.TextField("Output Folder", outputBasePath);
            paramName = EditorGUILayout.TextField("Parameter Name", paramName);
            menuLabel = EditorGUILayout.TextField("Menu Label", menuLabel);

            bool maAvailable = MABridge.IsAvailable;
            using (new EditorGUI.DisabledScope(!maAvailable))
            {
                useModularAvatar = EditorGUILayout.Toggle("Use Modular Avatar", useModularAvatar && maAvailable);
            }
            if (!maAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Modular Avatar が検出されませんでした。アバターの FX / ExParams / ExMenu に直接書き込みます。",
                    MessageType.None);
            }
        }

        // ---------------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------------
        private bool ValidateInputs(out string msg)
        {
            if (avatar == null) { msg = "Avatar が未指定です。"; return false; }
            if (sourceFaceMesh == null) { msg = "Source Face Mesh が未指定です。"; return false; }
            if (personaShader == null) { msg = "Persona Shader が未指定です。"; return false; }
            if (!sourceFaceMesh.transform.IsChildOf(avatar.transform))
            {
                msg = "Source Face Mesh はこのアバターの子である必要があります。"; return false;
            }
            if (innerFace != null && !innerFace.transform.IsChildOf(avatar.transform))
            {
                msg = "Inner Face はこのアバターの子である必要があります。"; return false;
            }
            msg = null; return true;
        }

        // ---------------------------------------------------------------------
        // Main setup pipeline
        // ---------------------------------------------------------------------
        private void RunSetup()
        {
            if (!ValidateInputs(out var err))
            {
                EditorUtility.DisplayDialog("Persona Setup", err, "OK");
                return;
            }

            try
            {
                AssetDatabase.StartAssetEditing();

                var outputPath = $"{outputBasePath}/{SanitizeName(avatar.name)}";
                EnsureFolder(outputPath);

                // 1. Find bones
                var headBone = FindHeadBone();
                var neckBone = FindNeckBone(headBone);
                if (headBone == null)
                {
                    EditorUtility.DisplayDialog("Persona Setup", "Head ボーンが見つかりませんでした。", "OK");
                    return;
                }

                // 2. Create / replace root
                const string rootName = "Persona_Setup";
                var existing = avatar.transform.Find(rootName);
                if (existing != null)
                {
                    if (!EditorUtility.DisplayDialog("Persona Setup",
                        $"'{rootName}' が既に存在します。上書きしますか?", "上書き", "キャンセル"))
                        return;
                    Undo.DestroyObjectImmediate(existing.gameObject);
                }

                var rootGO = new GameObject(rootName);
                Undo.RegisterCreatedObjectUndo(rootGO, "Create Persona Setup");
                rootGO.transform.SetParent(avatar.transform, false);

                // 3. Build OuterFace_Mask
                var maskGO = BuildOuterFaceMask(rootGO.transform, outputPath);
                var maskSMR = maskGO.GetComponent<SkinnedMeshRenderer>();

                // 4. Pivot auto-calc (world space, using Neck bone position)
                if (autoCalculatePivot)
                {
                    var pivotBone = neckBone != null ? neckBone : headBone;
                    var world = pivotBone.position;
                    pivotWorldY = world.y;
                    pivotWorldZ = world.z;
                }

                // 5. Apply material parameters
                //    Shader uses world-space pivot/clip:
                //      _PivotWorldY / _PivotWorldZ : rotation axis (neck world)
                //      _ClipWorldY               : everything below this Y is clipped
                foreach (var m in maskSMR.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.HasProperty("_PivotWorldY")) m.SetFloat("_PivotWorldY", pivotWorldY);
                    if (m.HasProperty("_PivotWorldZ")) m.SetFloat("_PivotWorldZ", pivotWorldZ);
                    if (m.HasProperty("_ClipWorldY"))  m.SetFloat("_ClipWorldY",  pivotWorldY - 0.05f);
                    if (m.HasProperty("_RotationStrength")) m.SetFloat("_RotationStrength", rotationStrength);
                    if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", brightness);
                    if (m.HasProperty("_FadeStart")) m.SetFloat("_FadeStart", fadeStart);
                    if (m.HasProperty("_PeelProgress")) m.SetFloat("_PeelProgress", 0f);
                    EditorUtility.SetDirty(m);
                }

                // 6. Inner face: apply black material, ensure deactivated initially
                GameObject innerFaceGO = innerFace;
                if (innerFaceGO != null)
                {
                    ApplyInnerFaceMaterial(innerFaceGO, outputPath);

                    Undo.RecordObject(innerFaceGO, "Persona inner face deactivate");
                    innerFaceGO.SetActive(false);
                    EditorUtility.SetDirty(innerFaceGO);
                }

                // 7. Create animation clips
                var clipIdle = BuildIdleClip(maskGO, innerFaceGO, outputPath);
                var clipTransform = BuildTransformClip(maskGO, innerFaceGO, outputPath);

                // 7b. Body material swap — all slots go black on peel start
                if (enableBodyMaterialSwap)
                {
                    var targetBody = bodyMesh != null ? bodyMesh : sourceFaceMesh;
                    if (targetBody != null)
                    {
                        var personaBodyMats = BuildBodyPersonaMaterials(targetBody, outputPath);
                        if (personaBodyMats != null)
                        {
                            AddBodyMaterialSwapToClips(clipIdle, clipTransform, targetBody, personaBodyMats);
                        }
                    }
                }

                // 8. Create AnimatorController
                var controller = BuildController(clipIdle, clipTransform, outputPath);

                // 9. Integrate (MA or direct)
                if (useModularAvatar && MABridge.IsAvailable)
                    IntegrateWithModularAvatar(rootGO, controller, outputPath);
                else
                    IntegrateDirectly(controller, outputPath);

                AssetDatabase.SaveAssets();
                Selection.activeGameObject = rootGO;
                EditorGUIUtility.PingObject(rootGO);
                EditorUtility.DisplayDialog("Persona Setup",
                    "セットアップが完了しました。", "OK");
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Persona Setup",
                    $"セットアップ中にエラーが発生しました:\n{ex.Message}", "OK");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        // ---------------------------------------------------------------------
        // Step 3: OuterFace_Mask
        //   Duplicate the source renderer and re-parent into Persona_Setup.
        //   For each material slot, clone the original material and swap the
        //   shader to Custom/Persona. This preserves textures, colors, and
        //   other shader-specific properties.
        // ---------------------------------------------------------------------
        private GameObject BuildOuterFaceMask(Transform parent, string outputPath)
        {
            var maskGO = Instantiate(sourceFaceMesh.gameObject);
            maskGO.name = "OuterFace_Mask";
            maskGO.transform.SetParent(parent, false);
            maskGO.transform.localPosition = Vector3.zero;
            maskGO.transform.localRotation = Quaternion.identity;
            maskGO.transform.localScale = Vector3.one;

            // Keep only Transform + SkinnedMeshRenderer on the duplicate
            var keep = new System.Type[]
            {
                typeof(Transform), typeof(SkinnedMeshRenderer)
            };
            foreach (var c in maskGO.GetComponents<Component>().Reverse())
            {
                if (c == null) continue;
                if (!keep.Contains(c.GetType()))
                    DestroyImmediate(c, true);
            }

            var smr = maskGO.GetComponent<SkinnedMeshRenderer>();

            var originalMats = sourceFaceMesh.sharedMaterials;
            var newMats = new Material[originalMats.Length];
            for (int i = 0; i < originalMats.Length; i++)
            {
                var src = originalMats[i];
                Material mat;
                if (src != null)
                {
                    mat = new Material(src);
                    mat.shader = personaShader;
                }
                else
                {
                    mat = new Material(personaShader);
                }
                mat.name = $"Persona_Mask_{i}";

                var matPath = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_Mask_{i}.mat");
                AssetDatabase.CreateAsset(mat, matPath);
                newMats[i] = mat;
            }
            smr.sharedMaterials = newMats;

            // Hide by default. Idle clip also sets this, so the scene view
            // matches the runtime state before Persona is triggered.
            maskGO.SetActive(false);

            return maskGO;
        }

        // ---------------------------------------------------------------------
        // Step 6a: Inner face material (pure black Unlit)
        //   Choose Unlit/Color for solid black, with graceful fallback.
        // ---------------------------------------------------------------------
        private Material BuildInnerFaceMaterial(string outputPath)
        {
            Shader shader = Shader.Find("Unlit/Color")
                         ?? Shader.Find("Unlit/Texture")
                         ?? Shader.Find("Standard");

            var mat = new Material(shader);
            mat.name = "InnerFace_Mat";

            if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     BlackColor);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", BlackColor);
            if (mat.HasProperty("_MainColor")) mat.SetColor("_MainColor", BlackColor);

            var matPath = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/InnerFace_Mat.mat");
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }

        // ---------------------------------------------------------------------
        // Step 6b: Apply black material to every renderer under innerFace
        // ---------------------------------------------------------------------
        private void ApplyInnerFaceMaterial(GameObject innerFaceGO, string outputPath)
        {
            var mat = BuildInnerFaceMaterial(outputPath);
            var renderers = innerFaceGO.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning("[PersonaSetup] Inner Face GameObject has no Renderer. Material not applied.");
                return;
            }
            foreach (var r in renderers)
            {
                Undo.RecordObject(r, "Persona inner face material");
                var slots = r.sharedMaterials;
                var newMats = new Material[slots.Length];
                for (int i = 0; i < slots.Length; i++) newMats[i] = mat;
                r.sharedMaterials = newMats;
                // Unlit black face plate - shadows off
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                EditorUtility.SetDirty(r);
            }
        }

        // ---------------------------------------------------------------------
        // Step 7b: Body material swap (build + clip injection)
        //   Create one black material and assign to every slot (including
        //   eye/sclera slots). Animation swaps slot → black at t=0.
        // ---------------------------------------------------------------------
        private Material[] BuildBodyPersonaMaterials(SkinnedMeshRenderer body, string outputPath)
        {
            var originals = body.sharedMaterials;
            var variants = new Material[originals.Length];

            Shader shader = Shader.Find("Unlit/Color")
                         ?? Shader.Find("Unlit/Texture")
                         ?? Shader.Find("Standard");

            var sharedMat = new Material(shader);
            sharedMat.name = "Persona_Body";
            if (sharedMat.HasProperty("_Color"))     sharedMat.SetColor("_Color",     BlackColor);
            if (sharedMat.HasProperty("_BaseColor")) sharedMat.SetColor("_BaseColor", BlackColor);
            if (sharedMat.HasProperty("_MainColor")) sharedMat.SetColor("_MainColor", BlackColor);

            var matPath = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_Body.mat");
            AssetDatabase.CreateAsset(sharedMat, matPath);

            for (int i = 0; i < originals.Length; i++)
            {
                variants[i] = originals[i] != null ? sharedMat : null;
            }
            return variants;
        }

        private void AddBodyMaterialSwapToClips(
            AnimationClip idle, AnimationClip transform,
            SkinnedMeshRenderer body, Material[] personaMats)
        {
            string bodyPath = AnimationPath(body.transform);
            var originals = body.sharedMaterials;

            for (int i = 0; i < originals.Length; i++)
            {
                if (originals[i] == null || personaMats[i] == null) continue;

                var binding = new EditorCurveBinding
                {
                    path = bodyPath,
                    type = typeof(SkinnedMeshRenderer),
                    propertyName = $"m_Materials.Array.data[{i}]"
                };

                // Idle: hold original
                AnimationUtility.SetObjectReferenceCurve(idle, binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = originals[i] }
                });

                // Transform: black material at t=0 (step curve)
                AnimationUtility.SetObjectReferenceCurve(transform, binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = personaMats[i] }
                });
            }
        }

        // ---------------------------------------------------------------------
        // Animation clips
        // ---------------------------------------------------------------------
        private AnimationClip BuildIdleClip(GameObject maskGO, GameObject innerFaceGO, string outputPath)
        {
            var clip = new AnimationClip { name = "Persona_Idle" };
            clip.frameRate = 60f;

            var maskPath = AnimationPath(maskGO.transform);

            // Hide mask in idle - body's face shows through normally
            SetBoolCurve(clip, maskPath, typeof(GameObject), "m_IsActive", false, false);
            SetFloatCurve(clip, maskPath, typeof(SkinnedMeshRenderer),
                "material._PeelProgress", 0f, 0f);

            if (innerFaceGO != null)
            {
                var innerPath = AnimationPath(innerFaceGO.transform);
                SetBoolCurve(clip, innerPath, typeof(GameObject), "m_IsActive", false, false);
            }

            var p = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_Idle.anim");
            AssetDatabase.CreateAsset(clip, p);
            return clip;
        }

        private AnimationClip BuildTransformClip(GameObject maskGO, GameObject innerFaceGO, string outputPath)
        {
            var clip = new AnimationClip { name = "Persona_Transform" };
            clip.frameRate = 60f;

            var maskPath = AnimationPath(maskGO.transform);

            // Peel progress: 0 -> 1 linearly over transformDuration
            SetFloatCurve(clip, maskPath, typeof(SkinnedMeshRenderer),
                "material._PeelProgress", 0f, 1f, transformDuration);

            // Mask active: true at t=0 (appear), false at end (cull after fadeout)
            {
                var binding = new EditorCurveBinding
                {
                    path = maskPath,
                    type = typeof(GameObject),
                    propertyName = "m_IsActive"
                };
                var curve = new AnimationCurve();
                curve.AddKey(new Keyframe(0f, 1f));
                curve.AddKey(new Keyframe(transformDuration, 0f));
                ApplyConstantTangents(curve);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            // Inner face: appear at t=0 and stay active
            if (innerFaceGO != null)
            {
                var innerPath = AnimationPath(innerFaceGO.transform);
                var binding = new EditorCurveBinding
                {
                    path = innerPath,
                    type = typeof(GameObject),
                    propertyName = "m_IsActive"
                };
                var curve = new AnimationCurve();
                curve.AddKey(new Keyframe(0f, 1f));
                ApplyConstantTangents(curve);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            var p = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_Transform.anim");
            AssetDatabase.CreateAsset(clip, p);
            return clip;
        }

        // ---------------------------------------------------------------------
        // AnimatorController
        // ---------------------------------------------------------------------
        private AnimatorController BuildController(AnimationClip idle, AnimationClip transform, string outputPath)
        {
            var path = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_FX.controller");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            controller.AddParameter(paramName, AnimatorControllerParameterType.Bool);

            var layers = controller.layers;
            layers[0].name = "Persona";
            layers[0].defaultWeight = 1f;
            controller.layers = layers;

            var sm = controller.layers[0].stateMachine;
            sm.entryPosition = new Vector3(-200, 0, 0);
            sm.anyStatePosition = new Vector3(-200, 100, 0);
            sm.exitPosition = new Vector3(800, 0, 0);

            var idleState = sm.AddState("Idle", new Vector3(200, 0, 0));
            idleState.motion = idle;
            idleState.writeDefaultValues = false;

            var transformState = sm.AddState("Transform", new Vector3(500, 0, 0));
            transformState.motion = transform;
            transformState.writeDefaultValues = false;

            sm.defaultState = idleState;

            // Idle -> Transform
            var t1 = idleState.AddTransition(transformState);
            t1.hasExitTime = false;
            t1.duration = 0f;
            t1.canTransitionToSelf = false;
            t1.AddCondition(AnimatorConditionMode.If, 0f, paramName);

            // Transform -> Idle (smooth reset)
            var t2 = transformState.AddTransition(idleState);
            t2.hasExitTime = false;
            t2.duration = resetDuration;
            t2.canTransitionToSelf = false;
            t2.AddCondition(AnimatorConditionMode.IfNot, 0f, paramName);

            EditorUtility.SetDirty(controller);
            return controller;
        }

        // ---------------------------------------------------------------------
        // Modular Avatar integration (via reflection)
        // ---------------------------------------------------------------------
        private void IntegrateWithModularAvatar(GameObject rootGO, AnimatorController controller, string outputPath)
        {
            if (!MABridge.IsAvailable)
            {
                Debug.LogWarning("[PersonaSetup] Modular Avatar types not found; falling back to direct integration.");
                IntegrateDirectly(controller, outputPath);
                return;
            }

            // MergeAnimator
            var merge = rootGO.AddComponent(MABridge.T_MergeAnimator);
            MABridge.ReflectSet(merge, "animator", controller);
            MABridge.ReflectSet(merge, "layerType", VRCAvatarDescriptor.AnimLayerType.FX);
            MABridge.ReflectSet(merge, "deleteAttachedAnimator", true);
            if (MABridge.T_MergeAnimatorPathMode != null)
            {
                var abs = System.Enum.Parse(MABridge.T_MergeAnimatorPathMode, "Absolute");
                MABridge.ReflectSet(merge, "pathMode", abs);
            }
            MABridge.ReflectSet(merge, "matchAvatarWriteDefaults", false);

            // Parameters
            var maParams = rootGO.AddComponent(MABridge.T_Parameters);
            var listType = typeof(List<>).MakeGenericType(MABridge.T_ParameterConfig);
            var paramList = (System.Collections.IList)System.Activator.CreateInstance(listType);
            var cfg = System.Activator.CreateInstance(MABridge.T_ParameterConfig);
            MABridge.ReflectSet(cfg, "nameOrPrefix", paramName);
            if (MABridge.T_ParameterSyncType != null)
            {
                var boolSync = System.Enum.Parse(MABridge.T_ParameterSyncType, "Bool");
                MABridge.ReflectSet(cfg, "syncType", boolSync);
            }
            MABridge.ReflectSet(cfg, "defaultValue", 0f);
            MABridge.ReflectSet(cfg, "saved", true);
            MABridge.ReflectSet(cfg, "localOnly", false);
            MABridge.ReflectSet(cfg, "internalParameter", false);
            MABridge.ReflectSet(cfg, "isPrefix", false);
            paramList.Add(cfg);
            MABridge.ReflectSet(maParams, "parameters", paramList);

            // Menu (asset + installer)
            var menuAsset = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menuAsset.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control
                {
                    name = menuLabel,
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName },
                    value = 1f,
                }
            };
            var menuAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputPath}/Persona_Menu.asset");
            AssetDatabase.CreateAsset(menuAsset, menuAssetPath);

            // Installer goes on the same root as MergeAnimator/Parameters.
            // Installer target is determined by its properties, not GameObject
            // hierarchy, so separating them into a child GameObject would be
            // purely cosmetic.
            var installer = rootGO.AddComponent(MABridge.T_MenuInstaller);
            MABridge.ReflectSet(installer, "menuToAppend", menuAsset);
        }

        // ---------------------------------------------------------------------
        // Direct integration (no MA)
        // ---------------------------------------------------------------------
        private void IntegrateDirectly(AnimatorController personaController, string outputPath)
        {
            EnsurePlayableLayer(VRCAvatarDescriptor.AnimLayerType.FX, outputPath, out var fxController);

            if (!fxController.parameters.Any(p => p.name == paramName))
                fxController.AddParameter(paramName, AnimatorControllerParameterType.Bool);

            if (!fxController.layers.Any(l => l.name == "Persona"))
            {
                var src = personaController.layers[0];
                var srcSM = src.stateMachine;
                var newSM = DuplicateStateMachine(srcSM, fxController);
                var newLayer = new AnimatorControllerLayer
                {
                    name = "Persona",
                    defaultWeight = 1f,
                    stateMachine = newSM,
                };
                fxController.AddLayer(newLayer);
            }
            EditorUtility.SetDirty(fxController);

            EnsureExpressionParameters(outputPath);
            var exParams = avatar.expressionParameters;
            if (!exParams.parameters.Any(p => p.name == paramName))
            {
                var list = new List<VRCExpressionParameters.Parameter>(exParams.parameters)
                {
                    new VRCExpressionParameters.Parameter
                    {
                        name = paramName,
                        valueType = VRCExpressionParameters.ValueType.Bool,
                        defaultValue = 0f,
                        saved = true,
                        networkSynced = true,
                    }
                };
                exParams.parameters = list.ToArray();
                EditorUtility.SetDirty(exParams);
            }

            EnsureExpressionMenu(outputPath);
            var menu = avatar.expressionsMenu;
            if (!menu.controls.Any(c => c.name == menuLabel))
            {
                menu.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = menuLabel,
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName },
                    value = 1f,
                });
                EditorUtility.SetDirty(menu);
            }
        }

        // ---------------------------------------------------------------------
        // Direct-mode helpers
        // ---------------------------------------------------------------------
        private void EnsurePlayableLayer(
            VRCAvatarDescriptor.AnimLayerType type, string outputPath, out AnimatorController controller)
        {
            var layers = avatar.baseAnimationLayers;
            int idx = -1;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].type == type) { idx = i; break; }

            if (idx < 0)
                throw new System.Exception($"Playable layer {type} not found on the avatar.");

            var layer = layers[idx];
            var existing = layer.animatorController as AnimatorController;

            if (existing == null || layer.isDefault)
            {
                var newPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{outputPath}/{SanitizeName(avatar.name)}_{type}.controller");
                var newCtrl = AnimatorController.CreateAnimatorControllerAtPath(newPath);
                layer.animatorController = newCtrl;
                layer.isDefault = false;
                layer.isEnabled = true;
                layers[idx] = layer;
                avatar.baseAnimationLayers = layers;
                avatar.customizeAnimationLayers = true;
                EditorUtility.SetDirty(avatar);
                controller = newCtrl;
            }
            else
            {
                controller = existing;
            }
        }

        private void EnsureExpressionParameters(string outputPath)
        {
            if (avatar.expressionParameters != null && avatar.customExpressions) return;

            if (avatar.expressionParameters == null)
            {
                var p = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                p.parameters = new VRCExpressionParameters.Parameter[0];
                var path = AssetDatabase.GenerateUniqueAssetPath(
                    $"{outputPath}/{SanitizeName(avatar.name)}_ExParams.asset");
                AssetDatabase.CreateAsset(p, path);
                avatar.expressionParameters = p;
            }
            avatar.customExpressions = true;
            EditorUtility.SetDirty(avatar);
        }

        private void EnsureExpressionMenu(string outputPath)
        {
            if (avatar.expressionsMenu != null && avatar.customExpressions) return;

            if (avatar.expressionsMenu == null)
            {
                var m = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                m.controls = new List<VRCExpressionsMenu.Control>();
                var path = AssetDatabase.GenerateUniqueAssetPath(
                    $"{outputPath}/{SanitizeName(avatar.name)}_ExMenu.asset");
                AssetDatabase.CreateAsset(m, path);
                avatar.expressionsMenu = m;
            }
            avatar.customExpressions = true;
            EditorUtility.SetDirty(avatar);
        }

        private AnimatorStateMachine DuplicateStateMachine(
            AnimatorStateMachine src, AnimatorController destController)
        {
            var dst = new AnimatorStateMachine
            {
                name = src.name,
                hideFlags = HideFlags.HideInHierarchy,
                entryPosition = src.entryPosition,
                anyStatePosition = src.anyStatePosition,
                exitPosition = src.exitPosition,
            };
            AssetDatabase.AddObjectToAsset(dst, destController);

            var stateMap = new Dictionary<AnimatorState, AnimatorState>();
            foreach (var cs in src.states)
            {
                var ns = dst.AddState(cs.state.name, cs.position);
                ns.motion = cs.state.motion;
                ns.writeDefaultValues = cs.state.writeDefaultValues;
                ns.speed = cs.state.speed;
                ns.cycleOffset = cs.state.cycleOffset;
                ns.mirror = cs.state.mirror;
                ns.timeParameter = cs.state.timeParameter;
                ns.timeParameterActive = cs.state.timeParameterActive;
                foreach (var beh in cs.state.behaviours)
                {
                    var clone = Object.Instantiate(beh);
                    clone.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(clone, destController);
                    var behList = new List<StateMachineBehaviour>(ns.behaviours) { clone };
                    ns.behaviours = behList.ToArray();
                }
                stateMap[cs.state] = ns;
                if (src.defaultState == cs.state) dst.defaultState = ns;
            }

            foreach (var cs in src.states)
            {
                var ns = stateMap[cs.state];
                foreach (var t in cs.state.transitions)
                {
                    if (t.destinationState == null) continue;
                    var nt = ns.AddTransition(stateMap[t.destinationState]);
                    nt.hasExitTime = t.hasExitTime;
                    nt.exitTime = t.exitTime;
                    nt.duration = t.duration;
                    nt.offset = t.offset;
                    nt.canTransitionToSelf = t.canTransitionToSelf;
                    foreach (var c in t.conditions)
                        nt.AddCondition(c.mode, c.threshold, c.parameter);
                }
            }

            return dst;
        }

        // ---------------------------------------------------------------------
        // Utility
        // ---------------------------------------------------------------------
        private Transform FindHeadBone()
        {
            var anim = avatar.GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                var b = anim.GetBoneTransform(HumanBodyBones.Head);
                if (b != null) return b;
            }
            return FindByNameRecursive(avatar.transform,
                new[] { "Head", "head", "J_Bip_C_Head" });
        }

        private Transform FindNeckBone(Transform headBone)
        {
            var anim = avatar.GetComponent<Animator>();
            if (anim != null && anim.isHuman)
            {
                var b = anim.GetBoneTransform(HumanBodyBones.Neck);
                if (b != null) return b;
            }
            if (headBone != null && headBone.parent != null &&
                headBone.parent.name.ToLowerInvariant().Contains("neck"))
                return headBone.parent;
            return null;
        }

        private static Transform FindByNameRecursive(Transform root, string[] names)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var n in names)
                    if (t.name == n) return t;
            }
            return null;
        }

        private string AnimationPath(Transform t)
        {
            return AnimationUtility.CalculateTransformPath(t, avatar.transform);
        }

        private static void SetFloatCurve(AnimationClip clip, string path, System.Type type,
            string prop, float start, float end, float duration = 0f)
        {
            var binding = new EditorCurveBinding { path = path, type = type, propertyName = prop };
            var curve = new AnimationCurve();
            curve.AddKey(new Keyframe(0f, start));
            curve.AddKey(new Keyframe(Mathf.Max(1f / 60f, duration), end));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void SetBoolCurve(AnimationClip clip, string path, System.Type type,
            string prop, bool start, bool end)
        {
            var binding = new EditorCurveBinding { path = path, type = type, propertyName = prop };
            var curve = new AnimationCurve();
            curve.AddKey(new Keyframe(0f, start ? 1f : 0f));
            curve.AddKey(new Keyframe(1f / 60f, end ? 1f : 0f));
            ApplyConstantTangents(curve);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void ApplyConstantTangents(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            var accum = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{accum}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(accum, parts[i]);
                accum = next;
            }
        }

        private static string SanitizeName(string n)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                n = n.Replace(c, '_');
            return n;
        }
    }

    // =========================================================================
    // MABridge — reflection-based bridge to Modular Avatar.
    //
    // MA's own "MA_VRCSDK3_AVATARS" define is only active inside MA's assembly,
    // so we can't use #if to detect MA from user code. Instead we resolve the
    // required types at runtime from loaded assemblies. This lets the tool
    // detect and use MA without any asmdef/define configuration.
    // =========================================================================
    internal static class MABridge
    {
        private const string NS = "nadena.dev.modular_avatar.core.";
        private static bool _initialized;

        public static System.Type T_MergeAnimator;
        public static System.Type T_Parameters;
        public static System.Type T_MenuItem;
        public static System.Type T_MenuInstaller;
        public static System.Type T_ParameterConfig;
        public static System.Type T_ParameterSyncType;
        public static System.Type T_MergeAnimatorPathMode;

        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return T_MergeAnimator != null
                    && T_Parameters != null
                    && T_MenuInstaller != null
                    && T_ParameterConfig != null;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            T_MergeAnimator = FindType(NS + "ModularAvatarMergeAnimator");
            T_Parameters = FindType(NS + "ModularAvatarParameters");
            T_MenuItem = FindType(NS + "ModularAvatarMenuItem");
            T_MenuInstaller = FindType(NS + "ModularAvatarMenuInstaller");
            T_ParameterConfig = FindType(NS + "ParameterConfig");
            T_ParameterSyncType = FindType(NS + "ParameterSyncType");
            T_MergeAnimatorPathMode = FindType(NS + "MergeAnimatorPathMode");
        }

        private static System.Type FindType(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType(fullName); }
                catch { /* ignore */ }
                if (t != null) return t;
            }
            return null;
        }

        public static void ReflectSet(object target, string name, object value)
        {
            if (target == null) return;
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;

            var t = target.GetType();
            while (t != null)
            {
                var f = t.GetField(name, F);
                if (f != null) { f.SetValue(target, value); return; }
                var p = t.GetProperty(name, F);
                if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
                t = t.BaseType;
            }
            UnityEngine.Debug.LogWarning(
                $"[PersonaSetup/MABridge] Could not set '{name}' on {target.GetType().FullName}. " +
                "MA API may have changed.");
        }
    }
}