#if UNITY_EDITOR
using VRC.SDKBase;
#endif
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

// Represents a blendshape with a default value in creator mode.
[Serializable]
public class CreatorCorrectiveBlendshapeEntry
{
    public CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape;
    public string toFix;
    public CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape;
    public string fixedBy;
}

[Serializable]
public class CreatorBlendshapeEntry
{
    public string name;
    public string defaultValue;
    public bool isSlider;
    public bool isSliderDefault;
    public List<CreatorCorrectiveBlendshapeEntry> correctiveBlendshapes = new List<CreatorCorrectiveBlendshapeEntry>();
}

[Serializable]
public class BlendShapeFactorLinkEntry
{
    public bool enabled = true;
    public string targetRendererPath;
    public CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape;
    public string toFix;
    public CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape;
    public string fixedBy;
    public string factorParameterName;
}

// This component is a pure data container for an avatar that has been modified
// by the custom base workflow. It holds only the state that needs to be saved with the scene/prefab.
[AddComponentMenu("Orbiters/My Custom Base (MCB)")]
public class MyCustomBase : MonoBehaviour
#if UNITY_EDITOR
    , IEditorOnly
#endif
{
    // --- USER CONFIGURATION ---
    [Tooltip("If checked, you must manually assign the FBX file below. If unchecked, the tool will try to find it automatically.")]
    public bool specifyCustomBaseFbx = false;

    [Tooltip("The base FBX file for this avatar. Used to find compatible custom base versions.")]
    public List<GameObject> baseFbxFiles = new List<GameObject>();

    // --- APPLIED STATE ---
    [Tooltip("The version information of the custom base modification that is currently applied to this avatar's FBX.")]
    [HideInInspector] public CustomBaseVersion appliedCustomBaseVersion = null;

    [Tooltip("Stores the current values of the custom blendshape sliders.")]
    [HideInInspector] public List<float> blendShapeValues = new List<float>();

    [Tooltip("Stores user-customized blendshape values that override defaults (keys: blendshape names).")]
    [HideInInspector] [SerializeField] public List<string> customBlendshapeOverrideNames = new List<string>();
    
    [Tooltip("Stores user-customized blendshape values that override defaults (values: custom values).")]
    [HideInInspector] [SerializeField] public List<float> customBlendshapeOverrideValues = new List<float>();

    [Tooltip("When enabled, version switching preserves blendshape weights by name across mesh changes.")]
    [HideInInspector] [SerializeField] public bool preserveBlendshapeValuesOnVersionSwitch = true;

    [HideInInspector] [SerializeField] public bool preserveBlendshapeValuesOnVersionSwitchInitialized = false;

    [Tooltip("Enable/disable dynamic normals recalculation for blendshapes.")]
    [HideInInspector] public bool useDynamicNormals = true;

    [Tooltip("Use an A-Pose instead of T-Pose for dynamic normal calculations to prevent thigh clipping.")]
    [HideInInspector] public bool useAPoseForDynamicNormals = true;

    [Tooltip("The name of the sliders sub-menu in the VRChat expressions menu.")]
    [HideInInspector] public string slidersMenuName = "MCB sliders";

    [Tooltip("Whether the sliders GameObject is active or inactive (overriding version defaults).")]
    [HideInInspector] [SerializeField] public bool useCustomSlidersState = false;

    [Tooltip("The user-customized active state of the sliders GameObject.")]
    [HideInInspector] [SerializeField] public bool customSlidersState = true;

    [Tooltip("Whether slider selection overrides the version defaults.")]
    [HideInInspector] [SerializeField] public bool useCustomSliderSelection = false;

    [Tooltip("Stores user-customized slider selection by blendshape name.")]
    [HideInInspector] [SerializeField] public List<string> customSliderSelectionNames = new List<string>();

    [Tooltip("Persistent blendshape factor links that are applied to VRCFury built controllers during preprocess.")]
    [HideInInspector] [SerializeField] public List<BlendShapeFactorLinkEntry> blendShapeFactorLinks = new List<BlendShapeFactorLinkEntry>();

    [Tooltip("Serialized cache of applied version blendshape definitions used for build-time corrective links.")]
    [HideInInspector] [SerializeField] public List<CreatorBlendshapeEntry> appliedVersionBlendshapeLinksCache = new List<CreatorBlendshapeEntry>();

    // --- CREATOR MODE PERSISTENT DATA ---
    [HideInInspector] public bool isCreatorMode = false;
    [HideInInspector] public GameObject customFbxForCreator;
    [HideInInspector] public Avatar customBaseAvatarForCreatorProp;
    [HideInInspector] public GameObject avatarLogicPrefab;
    [HideInInspector] public bool includeCustomVeinsForCreator = false;
    [HideInInspector] public Texture2D customVeinsNormalMap;
    [HideInInspector] public bool includeDynamicNormalsBodyForCreator = false;
    [HideInInspector] public bool includeDynamicNormalsFlexingForCreator = false;
    [HideInInspector] public List<CreatorBlendshapeEntry> customBlendshapesForCreator = new List<CreatorBlendshapeEntry>();
}
