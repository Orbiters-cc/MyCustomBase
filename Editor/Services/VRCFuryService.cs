#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

public class VRCFuryService
{
    private static VRCFuryService _instance;
    public static VRCFuryService Instance => _instance ??= new VRCFuryService();

    public const string SLIDERS_GAMEOBJECT_NAME = "mcb sliders";

    // Type Cache
    private System.Type _vrcFuryType;
    private System.Type _setIconType;
    private System.Type _guidTextureType;
    private System.Type _toggleType;
    private System.Type _unlimitedType;
    private System.Type _applyDuringUploadType;
    private System.Type _stateType;
    private System.Type _blendShapeActionType;

    public static string GetSliderGlobalParamName(string sliderName)
    {
        if (string.IsNullOrWhiteSpace(sliderName))
        {
            return "UP_SLIDER_PARAM";
        }

        var chars = sliderName
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        string sanitized = new string(chars);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "SLIDER";
        }

        return "UP_SLIDER_" + sanitized;
    }

    public void ApplySliders(GameObject avatarRoot, string menuPath, List<CustomBlendshapeEntry> selectedSliders)
    {
        if (avatarRoot == null)
        {
            Debug.LogError("[MCB] Avatar root is null. Cannot apply sliders.");
            return;
        }

        // 1. Find or create the "mcb sliders" GameObject
        Transform slidersTransform = avatarRoot.transform.Find(SLIDERS_GAMEOBJECT_NAME);
        GameObject slidersObj;
        if (slidersTransform == null)
        {
            slidersObj = new GameObject(SLIDERS_GAMEOBJECT_NAME);
            slidersObj.transform.SetParent(avatarRoot.transform, false);
            Undo.RegisterCreatedObjectUndo(slidersObj, "Create MCB Sliders GameObject");
            
            // Set initial state from My Custom Base component
            var customBase = avatarRoot.GetComponentInChildren<MyCustomBase>(true);
            if (customBase != null)
            {
                bool desiredState = customBase.useCustomSlidersState ? customBase.customSlidersState : true;
                slidersObj.SetActive(desiredState);
            }
        }
        else
        {
            slidersObj = slidersTransform.gameObject;
        }

        // 2. Smart Sync: Get existing VRCFury components
        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        if (_toggleType == null) _toggleType = FindType("VF.Model.Feature.Toggle");
        if (_applyDuringUploadType == null) _applyDuringUploadType = FindType("VF.Model.Feature.ApplyDuringUpload");
        if (_stateType == null) _stateType = FindType("VF.Model.State");
        if (_blendShapeActionType == null) _blendShapeActionType = FindType("VF.Model.StateAction.BlendShapeAction");
        if (_vrcFuryType == null) {
            Debug.LogError("[MCB] VRCFury not found. Cannot sync sliders.");
            return;
        }
        if (_toggleType == null || _applyDuringUploadType == null || _stateType == null || _blendShapeActionType == null)
        {
            Debug.LogError("[MCB] Could not resolve required VRCFury types for sliders.");
            return;
        }

        var existingVrcfComponents = slidersObj.GetComponents(_vrcFuryType).ToList();
        var componentsToRemove = new List<Component>();

        foreach (var comp in existingVrcfComponents)
        {
            var content = _vrcFuryType.GetField("content").GetValue(comp);
            if (content == null) { componentsToRemove.Add(comp as Component); continue; }

            string contentTypeName = content.GetType().FullName;
            
            // Recreate MCB-generated slider features every sync to guarantee consistent settings.
            if (contentTypeName == "VF.Model.Feature.Toggle")
            {
                componentsToRemove.Add(comp as Component);
            }
            else if (contentTypeName == "VF.Model.Feature.ApplyDuringUpload")
            {
                componentsToRemove.Add(comp as Component);
            }
            // Check if it's the Menu Icon
            else if (contentTypeName == "VF.Model.Feature.SetIcon")
            {
                var iconPath = content.GetType().GetField("path")?.GetValue(content) as string;
                if (iconPath == menuPath) { /* Keep */ }
                else componentsToRemove.Add(comp as Component);
            }
            // Check if it's Unlimited Parameters
            else if (contentTypeName == "VF.Model.Feature.UnlimitedParameters")
            {
                /* Keep - controlled by the checkbox */
            }
            else componentsToRemove.Add(comp as Component);
        }

        // 4. Execute Changes
        foreach (var comp in componentsToRemove) Undo.DestroyObjectImmediate(comp);

        foreach (var sliderEntry in selectedSliders)
        {
            AddSliderToggleFeature(slidersObj, avatarRoot, menuPath, sliderEntry);
            foreach (string blendshapeName in GetSliderBlendshapeActionNames(avatarRoot, sliderEntry.name))
            {
                AddApplyDuringUploadFeature(slidersObj, blendshapeName);
            }
        }

        // 5. Add/Update Override Menu Icon if missing
        if (!string.IsNullOrEmpty(menuPath))
        {
            bool hasIcon = existingVrcfComponents
                .Except(componentsToRemove)
                .Any(c => _vrcFuryType.GetField("content").GetValue(c)?.GetType().FullName == "VF.Model.Feature.SetIcon");

            if (!hasIcon)
            {
                Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/vrcSliderIcon.png");
                if (icon != null) AddOverrideMenuIcon(slidersObj, menuPath, icon);
            }
        }
    }

    private void AddSliderToggleFeature(GameObject obj, GameObject avatarRoot, string menuPath, CustomBlendshapeEntry sliderEntry)
    {
        if (_vrcFuryType == null || _toggleType == null || _stateType == null || _blendShapeActionType == null) return;

        var vrcf = Undo.AddComponent(obj, _vrcFuryType);
        var toggleFeature = System.Activator.CreateInstance(_toggleType);

        string fullPath = string.IsNullOrEmpty(menuPath) ? sliderEntry.name : $"{menuPath}/{sliderEntry.name}";
        _toggleType.GetField("name")?.SetValue(toggleFeature, fullPath);
        _toggleType.GetField("slider")?.SetValue(toggleFeature, true);
        _toggleType.GetField("defaultSliderValue")?.SetValue(toggleFeature, GetCurrentSliderValue(avatarRoot, sliderEntry.name));
        _toggleType.GetField("useGlobalParam")?.SetValue(toggleFeature, true);
        _toggleType.GetField("globalParam")?.SetValue(toggleFeature, GetSliderGlobalParamName(sliderEntry.name));
        // Orbiters' default icon for a slider: a white gauge.
        if (_guidTextureType == null) _guidTextureType = FindType("VF.Model.GuidTexture2d");
        var iconField = _toggleType.GetField("icon");
        if (_guidTextureType != null && iconField != null)
        {
            var guidTex = System.Activator.CreateInstance(_guidTextureType);
            _guidTextureType.GetField("objRef", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy)
                ?.SetValue(guidTex, Orbiters.Toolkit.Editor.VRChat.OrbitersMenuIcons.Slider);
            iconField.SetValue(toggleFeature, guidTex);
            _toggleType.GetField("enableIcon")?.SetValue(toggleFeature, true);
        }

        var state = System.Activator.CreateInstance(_stateType);
        var actionsField = _stateType.GetField("actions");
        if (actionsField?.GetValue(state) is System.Collections.IList actionsList)
        {
            foreach (string blendshapeName in GetSliderBlendshapeActionNames(avatarRoot, sliderEntry.name))
            {
                var blendshapeAction = CreateBlendshapeAction(blendshapeName, 100f);
                if (blendshapeAction == null) continue;
                actionsList.Add(blendshapeAction);
            }
        }

        _toggleType.GetField("state")?.SetValue(toggleFeature, state);
        _vrcFuryType.GetField("content").SetValue(vrcf, toggleFeature);
    }

    private static List<string> GetSliderBlendshapeActionNames(GameObject avatarRoot, string sliderName)
    {
        var customBase = avatarRoot != null ? avatarRoot.GetComponentInChildren<MyCustomBase>(true) : null;
        var names = MCBReFitIntegration.GetBlendShapeNamesWithTransferredReFit(customBase, sliderName);
        return names.Count > 0 ? names : new List<string> { sliderName };
    }

    private void AddApplyDuringUploadFeature(GameObject obj, string blendshapeName)
    {
        if (_vrcFuryType == null || _applyDuringUploadType == null || _stateType == null) return;

        var vrcf = Undo.AddComponent(obj, _vrcFuryType);
        var uploadFeature = System.Activator.CreateInstance(_applyDuringUploadType);

        var state = System.Activator.CreateInstance(_stateType);
        var blendshapeAction = CreateBlendshapeAction(blendshapeName, 0f);
        if (blendshapeAction != null)
        {
            var actionsField = _stateType.GetField("actions");
            if (actionsField?.GetValue(state) is System.Collections.IList actionsList)
            {
                actionsList.Add(blendshapeAction);
            }
        }

        _applyDuringUploadType.GetField("action")?.SetValue(uploadFeature, state);
        _vrcFuryType.GetField("content").SetValue(vrcf, uploadFeature);
    }

    private object CreateBlendshapeAction(string blendshapeName, float blendshapeValue)
    {
        if (_blendShapeActionType == null) return null;

        var action = System.Activator.CreateInstance(_blendShapeActionType);
        _blendShapeActionType.GetField("blendShape")?.SetValue(action, blendshapeName);
        _blendShapeActionType.GetField("blendShapeValue")?.SetValue(action, blendshapeValue);
        _blendShapeActionType.GetField("renderer")?.SetValue(action, null);
        _blendShapeActionType.GetField("allRenderers")?.SetValue(action, true);
        return action;
    }

    private static float GetCurrentSliderValue(GameObject avatarRoot, string blendshapeName)
    {
        if (avatarRoot == null || string.IsNullOrEmpty(blendshapeName))
        {
            return 0f;
        }

        foreach (var smr in MeshFinder.GetAllSkinnedMeshRenderers(avatarRoot.transform.root))
        {
            if (smr == null || smr.sharedMesh == null) continue;

            int blendshapeIndex = smr.sharedMesh.GetBlendShapeIndex(blendshapeName);
            if (blendshapeIndex >= 0)
            {
                return Mathf.Clamp01(smr.GetBlendShapeWeight(blendshapeIndex) / 100f);
            }
        }

        return 0f;
    }

    private void AddOverrideMenuIcon(GameObject obj, string menuPath, Texture2D icon)
    {
        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        if (_setIconType == null) _setIconType = FindType("VF.Model.Feature.SetIcon");
        if (_guidTextureType == null) _guidTextureType = FindType("VF.Model.GuidTexture2d");

        if (_vrcFuryType == null || _setIconType == null || _guidTextureType == null) return;

        var vrcf = Undo.AddComponent(obj, _vrcFuryType);
        var feature = System.Activator.CreateInstance(_setIconType);
        _setIconType.GetField("path").SetValue(feature, menuPath);

        var guidTex = System.Activator.CreateInstance(_guidTextureType);
        _guidTextureType.GetField("objRef", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy)
            .SetValue(guidTex, icon);
        _setIconType.GetField("icon").SetValue(feature, guidTex);

        _vrcFuryType.GetField("content").SetValue(vrcf, feature);
    }

    /// <summary>
    /// A VRCFury Full Controller on <paramref name="host"/> that merges <paramref name="controller"/> into the FX layer. Its
    /// unsynced parameters stay global (not renamed), so contacts elsewhere on the avatar can drive them by name.
    /// </summary>
    public bool AddFullController(GameObject host, RuntimeAnimatorController controller)
    {
        if (AddFullController(host, new FullControllerSpec { Controller = controller, AllNonsyncedAreGlobal = true }) != null) return true;
        Debug.LogWarning("[MCB] VRCFury was not found: the XMuscles controller is generated but not merged into the avatar.");
        return false;
    }

    /// <summary>What a Full Controller merges: an FX controller, an optional menu (under <see cref="MenuPrefix"/>) and
    /// parameters, parameter names kept as they are, and animation paths rewritten for objects that moved.</summary>
    public sealed class FullControllerSpec
    {
        public RuntimeAnimatorController Controller;
        public ScriptableObject Menu, Parameters;
        public string MenuPrefix = "";
        public bool AllNonsyncedAreGlobal;
        public List<string> GlobalParams = new List<string>();
        public List<KeyValuePair<string, string>> PathRewrites = new List<KeyValuePair<string, string>>();
    }

    /// <summary>A VRCFury Full Controller on <paramref name="host"/>, with Undo; null when VRCFury is missing.</summary>
    public Component AddFullController(GameObject host, FullControllerSpec spec)
    {
        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        var featureType = FindType("VF.Model.Feature.FullController");
        if (_vrcFuryType == null || featureType == null) return null;
        var feature = System.Activator.CreateInstance(featureType, true);
        void Add(string list, string entryName, string guidName, string field, Object asset, string prefix = null)
        {
            var entryType = FindType("VF.Model.Feature.FullController+" + entryName);
            var guidType = FindType("VF.Model.Guid" + guidName);
            var entry = System.Activator.CreateInstance(entryType, true);
            var guid = System.Activator.CreateInstance(guidType, true);
            guidType.GetField("objRef", PublicInstance)?.SetValue(guid, asset);
            guidType.GetField("id", PublicInstance)?.SetValue(guid, string.Empty);
            entryType.GetField(field).SetValue(entry, guid);
            if (prefix != null) entryType.GetField("prefix").SetValue(entry, prefix);
            ((System.Collections.IList)featureType.GetField(list).GetValue(feature)).Add(entry);
        }
        if (spec.Controller != null) Add("controllers", "ControllerEntry", "Controller", "controller", spec.Controller);
        if (spec.Menu != null) Add("menus", "MenuEntry", "Menu", "menu", spec.Menu, spec.MenuPrefix ?? "");
        if (spec.Parameters != null) Add("prms", "ParamsEntry", "Params", "parameters", spec.Parameters);
        featureType.GetField("allNonsyncedAreGlobal").SetValue(feature, spec.AllNonsyncedAreGlobal);
        var globals = (List<string>)featureType.GetField("globalParams").GetValue(feature);
        globals.AddRange(spec.GlobalParams.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
        var rewriteType = FindType("VF.Model.Feature.FullController+BindingRewrite");
        var rewrites = (System.Collections.IList)featureType.GetField("rewriteBindings").GetValue(feature);
        foreach (var pair in spec.PathRewrites)
        {
            var rewrite = System.Activator.CreateInstance(rewriteType, true);
            rewriteType.GetField("from").SetValue(rewrite, pair.Key);
            rewriteType.GetField("to").SetValue(rewrite, pair.Value);
            rewrites.Add(rewrite);
        }
        return AddFeature(host, feature);
    }

    /// <summary>
    /// A VRCFury Armature Link from <paramref name="host"/> to the avatar object at <paramref name="targetPath"/> (from the
    /// avatar root), with Undo; null when VRCFury is missing. Not recursive and not aligned: the host keeps its pose, so
    /// place it on the target first.
    /// </summary>
    public Component AddArmatureLink(GameObject host, string targetPath)
    {
        var featureType = FindType("VF.Model.Feature.ArmatureLink");
        var linkType = FindType("VF.Model.Feature.ArmatureLink+LinkTo");
        if (featureType == null || linkType == null) return null;
        var feature = System.Activator.CreateInstance(featureType, true);
        featureType.GetField("propBone").SetValue(feature, host);
        var links = (System.Collections.IList)featureType.GetField("linkTo").GetValue(feature);
        links.Clear();
        var link = System.Activator.CreateInstance(linkType, true);
        linkType.GetField("useBone").SetValue(link, false);
        linkType.GetField("useObj").SetValue(link, false);
        linkType.GetField("offset").SetValue(link, targetPath);
        links.Add(link);
        foreach (string field in new[] { "recursive", "alignPosition", "alignRotation", "alignScale" }) featureType.GetField(field).SetValue(feature, false);
        return AddFeature(host, feature);
    }

    private const System.Reflection.BindingFlags PublicInstance =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;

    private Component AddFeature(GameObject host, object feature)
    {
        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        if (_vrcFuryType == null) return null;
        var component = Undo.AddComponent(host, _vrcFuryType);
        _vrcFuryType.GetField("content").SetValue(component, feature);
        EditorUtility.SetDirty(component);
        return component;
    }

    private System.Type FindType(string fullName)
    {
        // Check if already in cache
        if (fullName == "VF.Model.VRCFury" && _vrcFuryType != null) return _vrcFuryType;
        if (fullName == "VF.Model.Feature.SetIcon" && _setIconType != null) return _setIconType;
        if (fullName == "VF.Model.GuidTexture2d" && _guidTextureType != null) return _guidTextureType;
        if (fullName == "VF.Model.Feature.Toggle" && _toggleType != null) return _toggleType;
        if (fullName == "VF.Model.Feature.UnlimitedParameters" && _unlimitedType != null) return _unlimitedType;
        if (fullName == "VF.Model.Feature.ApplyDuringUpload" && _applyDuringUploadType != null) return _applyDuringUploadType;
        if (fullName == "VF.Model.State" && _stateType != null) return _stateType;
        if (fullName == "VF.Model.StateAction.BlendShapeAction" && _blendShapeActionType != null) return _blendShapeActionType;

        string[] assemblyNames = { "VRCFury-Runtime", "VRCFury-Editor", "VRCFury" };
        foreach (var assemblyName in assemblyNames)
        {
            var type = System.Type.GetType($"{fullName}, {assemblyName}");
            if (type != null) return type;
        }

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName);
            if (type != null) return type;
            if (assembly.GetName().Name.Contains("VRCFury"))
            {
                type = assembly.GetTypes().FirstOrDefault(t => t.FullName == fullName);
                if (type != null) return type;
            }
        }
        return null;
    }

}
#endif
