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

    public struct ParameterUsage
    {
        public int currentSyncedBits;
        public int totalUsedAfterBuild;
        public int usedByAvatar;
        public int usedBySliders;
        public bool compressionEnabled;
        public bool compressionIsExternal;
        public string compressionPath;
    }

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
            AddApplyDuringUploadFeature(slidersObj, sliderEntry.name);
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

        var state = System.Activator.CreateInstance(_stateType);
        var blendshapeAction = CreateBlendshapeAction(sliderEntry.name, 100f);
        if (blendshapeAction != null)
        {
            var actionsField = _stateType.GetField("actions");
            if (actionsField?.GetValue(state) is System.Collections.IList actionsList)
            {
                actionsList.Add(blendshapeAction);
            }
        }

        _toggleType.GetField("state")?.SetValue(toggleFeature, state);
        _vrcFuryType.GetField("content").SetValue(vrcf, toggleFeature);
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

    public ParameterUsage GetAvatarParameterUsage(GameObject avatarRoot, int selectedCustomBaseSlidersCount)
    {
        var usage = AvatarParametersService.Instance.GetAvatarParameterUsage(avatarRoot, selectedCustomBaseSlidersCount);
        return new ParameterUsage
        {
            currentSyncedBits = usage.currentSyncedBits,
            totalUsedAfterBuild = usage.totalUsedAfterBuild,
            usedByAvatar = usage.usedByAvatar,
            usedBySliders = usage.usedBySliders,
            compressionEnabled = usage.compressionEnabled,
            compressionIsExternal = usage.compressionIsExternal,
            compressionPath = usage.compressionPath
        };
    }

    public ParameterUsage GetAvatarParameterUsage(GameObject avatarRoot)
    {
        return GetAvatarParameterUsage(avatarRoot, 0);
    }

    public void SetCompression(GameObject avatarRoot, bool enabled)
    {
        if (avatarRoot == null) return;
        
        // 1. Ensure Types are loaded
        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        if (_unlimitedType == null) _unlimitedType = FindType("VF.Model.Feature.UnlimitedParameters");

        if (_vrcFuryType == null || _unlimitedType == null)
        {
            Debug.LogError("[MCB] VRCFury types not found. Cannot toggle compression.");
            return;
        }

        // 2. Find or create the "mcb sliders" GameObject
        Transform slidersTransform = avatarRoot.transform.Find(SLIDERS_GAMEOBJECT_NAME);
        GameObject slidersObj;
        if (slidersTransform == null)
        {
            if (!enabled) return;
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

        // 3. Find existing Unlimited Parameters component on the sliders object
        var existingVrcfComponents = slidersObj.GetComponents(_vrcFuryType);
        var compressionComp = existingVrcfComponents.FirstOrDefault(c => 
            _vrcFuryType.GetField("content").GetValue(c)?.GetType() == _unlimitedType);

        if (enabled)
        {
            if (compressionComp == null)
            {
                // Add the Unlimited Parameters feature
                var vrcf = Undo.AddComponent(slidersObj, _vrcFuryType);
                var feature = System.Activator.CreateInstance(_unlimitedType);
                _vrcFuryType.GetField("content").SetValue(vrcf, feature);
                Debug.Log("[MCB] VRCFury Parameter Compression enabled.");
            }
        }
        else
        {
            if (compressionComp != null && compressionComp is Component comp)
            {
                Undo.DestroyObjectImmediate(comp);
                Debug.Log("[MCB] VRCFury Parameter Compression disabled.");
            }
        }
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
