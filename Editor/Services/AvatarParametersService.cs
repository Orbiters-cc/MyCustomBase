#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

public class AvatarParametersService
{
    private static AvatarParametersService _instance;
    public static AvatarParametersService Instance => _instance ??= new AvatarParametersService();


    private System.Type _vrcFuryType;
    private System.Type _toggleType;
    private System.Type _unlimitedType;
    private System.Type _fullControllerType;
    private FieldInfo _networkSyncedField;

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

    public ParameterUsage GetAvatarParameterUsage(GameObject avatarRoot, int selectedCustomBaseSlidersCount)
    {
        var usage = new ParameterUsage();
        if (avatarRoot == null) return usage;

        var descriptor = avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
        if (descriptor == null) return usage;

        usage.currentSyncedBits = GetSyncedBitsFromExpressionParameters(descriptor);

        if (_vrcFuryType == null) _vrcFuryType = FindType("VF.Model.VRCFury");
        if (_toggleType == null) _toggleType = FindType("VF.Model.Feature.Toggle");
        if (_unlimitedType == null) _unlimitedType = FindType("VF.Model.Feature.UnlimitedParameters");
        if (_fullControllerType == null) _fullControllerType = FindType("VF.Model.Feature.FullController");

        if (_vrcFuryType == null)
        {
            usage.usedByAvatar = usage.currentSyncedBits;
            usage.totalUsedAfterBuild = usage.currentSyncedBits;
            usage.usedBySliders = 0;
            return usage;
        }

        var vrcfComponents = avatarRoot.GetComponentsInChildren(_vrcFuryType, true);
        var unlimitedInfo = FindUnlimitedParameters(vrcfComponents);
        usage.compressionEnabled = unlimitedInfo.enabled;
        usage.compressionPath = unlimitedInfo.path;
        usage.compressionIsExternal = unlimitedInfo.isExternal;

        var baseSyncedParamTypes = BuildSyncedParamTypeMap(descriptor.expressionParameters);
        var fullControllerStats = GetFullControllerStats(
            vrcfComponents,
            baseSyncedParamTypes,
            usage.compressionEnabled,
            unlimitedInfo.includeBools,
            unlimitedInfo.includePuppets
        );
        (int compressibleNumbers, int compressibleBools) avatarMenuCompressionStats = usage.compressionEnabled
            ? GetMenuCompressionStats(
                new[] { descriptor.expressionsMenu },
                baseSyncedParamTypes,
                unlimitedInfo.includeBools,
                unlimitedInfo.includePuppets
            )
            : (compressibleNumbers: 0, compressibleBools: 0);

        var toggleStats = GetToggleStats(vrcfComponents, usage.compressionEnabled, unlimitedInfo.includeBools);

        int safeSelected = Mathf.Max(0, selectedCustomBaseSlidersCount);
        int rawBitsWithoutMCB = (toggleStats.rawBitsTotal - toggleStats.mcbSliderRawBits) + fullControllerStats.addedSyncedBits;
        int numbersWithoutMCB =
            (toggleStats.compressibleNumbersTotal - toggleStats.mcbSliderCount)
            + fullControllerStats.compressibleNumbers
            + avatarMenuCompressionStats.compressibleNumbers;
        int boolsWithoutMCB =
            toggleStats.compressibleBoolsTotal
            + fullControllerStats.compressibleBools
            + avatarMenuCompressionStats.compressibleBools;

        int rawBitsWithSelection = rawBitsWithoutMCB + (safeSelected * 8);
        int numbersWithSelection = numbersWithoutMCB + safeSelected;
        int boolsWithSelection = boolsWithoutMCB;

        int savingsWithoutSliders = usage.compressionEnabled
            ? CalculateCompressionSavings(numbersWithoutMCB, boolsWithoutMCB)
            : 0;
        int savingsWithSliders = usage.compressionEnabled
            ? CalculateCompressionSavings(numbersWithSelection, boolsWithSelection)
            : 0;

        int totalWithoutSliders = usage.currentSyncedBits + rawBitsWithoutMCB - savingsWithoutSliders;
        int totalWithSliders = usage.currentSyncedBits + rawBitsWithSelection - savingsWithSliders;

        usage.usedByAvatar = Mathf.Max(0, totalWithoutSliders);
        usage.totalUsedAfterBuild = Mathf.Max(0, totalWithSliders);
        usage.usedBySliders = Mathf.Max(0, usage.totalUsedAfterBuild - usage.usedByAvatar);
        return usage;
    }

    private int GetSyncedBitsFromExpressionParameters(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor)
    {
        if (descriptor == null || !descriptor.customExpressions || descriptor.expressionParameters == null)
        {
            return 0;
        }

        if (_networkSyncedField == null)
        {
            _networkSyncedField = typeof(VRCExpressionParameters.Parameter)
                .GetField("networkSynced", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        int total = 0;
        var parameters = descriptor.expressionParameters.parameters;
        if (parameters == null) return 0;

        foreach (var param in parameters)
        {
            if (param == null) continue;

            bool isSynced = _networkSyncedField == null || (bool)_networkSyncedField.GetValue(param);
            if (!isSynced) continue;

            total += VRCExpressionParameters.TypeCost(param.valueType);
        }

        return total;
    }

    private Dictionary<string, VRCExpressionParameters.ValueType> BuildSyncedParamTypeMap(VRCExpressionParameters expressionParameters)
    {
        var map = new Dictionary<string, VRCExpressionParameters.ValueType>();
        if (expressionParameters == null || expressionParameters.parameters == null) return map;

        if (_networkSyncedField == null)
        {
            _networkSyncedField = typeof(VRCExpressionParameters.Parameter)
                .GetField("networkSynced", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        foreach (var param in expressionParameters.parameters)
        {
            if (param == null || string.IsNullOrEmpty(param.name)) continue;

            bool isSynced = _networkSyncedField == null || (bool)_networkSyncedField.GetValue(param);
            if (!isSynced) continue;

            if (!map.ContainsKey(param.name))
            {
                map[param.name] = param.valueType;
            }
        }

        return map;
    }

    private (int addedSyncedBits, int compressibleNumbers, int compressibleBools) GetFullControllerStats(
        Component[] vrcfComponents,
        Dictionary<string, VRCExpressionParameters.ValueType> mergedSyncedParamTypes,
        bool compressionEnabled,
        bool includeBools,
        bool includePuppets
    )
    {
        if (_vrcFuryType == null || _fullControllerType == null)
        {
            return (0, 0, 0);
        }

        int addedSyncedBits = 0;
        var menus = new List<VRCExpressionsMenu>();

        foreach (var component in vrcfComponents)
        {
            if (component == null) continue;

            var content = _vrcFuryType.GetField("content")?.GetValue(component);
            if (content == null || !_fullControllerType.IsInstanceOfType(content)) continue;

            var prmsEntries = _fullControllerType.GetField("prms")?.GetValue(content) as IEnumerable;
            if (prmsEntries != null)
            {
                foreach (var entry in prmsEntries)
                {
                    if (entry == null) continue;
                    var wrapper = entry.GetType().GetField("parameters")?.GetValue(entry);
                    var paramsAsset = GetObjRefFromGuidWrapper(wrapper) as VRCExpressionParameters;
                    if (paramsAsset == null || paramsAsset.parameters == null) continue;

                    foreach (var param in paramsAsset.parameters)
                    {
                        if (param == null || string.IsNullOrEmpty(param.name)) continue;

                        bool isSynced = _networkSyncedField == null || (bool)_networkSyncedField.GetValue(param);
                        if (!isSynced) continue;

                        if (mergedSyncedParamTypes.ContainsKey(param.name)) continue;
                        mergedSyncedParamTypes[param.name] = param.valueType;
                        addedSyncedBits += VRCExpressionParameters.TypeCost(param.valueType);
                    }
                }
            }

            var menuEntries = _fullControllerType.GetField("menus")?.GetValue(content) as IEnumerable;
            if (menuEntries != null)
            {
                foreach (var entry in menuEntries)
                {
                    if (entry == null) continue;
                    var wrapper = entry.GetType().GetField("menu")?.GetValue(entry);
                    var menuAsset = GetObjRefFromGuidWrapper(wrapper) as VRCExpressionsMenu;
                    if (menuAsset != null) menus.Add(menuAsset);
                }
            }
        }

        if (!compressionEnabled || menus.Count == 0)
        {
            return (addedSyncedBits, 0, 0);
        }

        var compressionStats = GetMenuCompressionStats(menus, mergedSyncedParamTypes, includeBools, includePuppets);
        return (addedSyncedBits, compressionStats.compressibleNumbers, compressionStats.compressibleBools);
    }

    private (int compressibleNumbers, int compressibleBools) GetMenuCompressionStats(
        IEnumerable<VRCExpressionsMenu> menus,
        Dictionary<string, VRCExpressionParameters.ValueType> mergedSyncedParamTypes,
        bool includeBools,
        bool includePuppets
    )
    {
        if (menus == null || mergedSyncedParamTypes == null)
        {
            return (0, 0);
        }

        var candidateNames = new HashSet<string>();
        foreach (var menu in menus)
        {
            CollectCompressibleParamNamesFromMenu(menu, includePuppets, candidateNames);
        }

        int compressibleNumbers = 0;
        int compressibleBools = 0;
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!mergedSyncedParamTypes.TryGetValue(name, out var type)) continue;

            if (type == VRCExpressionParameters.ValueType.Int || type == VRCExpressionParameters.ValueType.Float)
            {
                compressibleNumbers++;
            }
            else if (includeBools && type == VRCExpressionParameters.ValueType.Bool)
            {
                compressibleBools++;
            }
        }

        return (compressibleNumbers, compressibleBools);
    }

    private void CollectCompressibleParamNamesFromMenu(VRCExpressionsMenu root, bool includePuppets, HashSet<string> output)
    {
        if (root == null || output == null) return;

        var stack = new Stack<VRCExpressionsMenu>();
        var seen = new HashSet<VRCExpressionsMenu>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var menu = stack.Pop();
            if (menu == null || seen.Contains(menu)) continue;
            seen.Add(menu);

            if (menu.controls == null) continue;
            foreach (var control in menu.controls)
            {
                if (control == null) continue;

                if (control.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet)
                {
                    TryAddSubParam(control, 0, output);
                }
                else if (control.type == VRCExpressionsMenu.Control.ControlType.Toggle ||
                         control.type == VRCExpressionsMenu.Control.ControlType.Button)
                {
                    TryAddParam(control.parameter, output);
                }
                else if (includePuppets && control.type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet)
                {
                    TryAddSubParam(control, 0, output);
                    TryAddSubParam(control, 1, output);
                }
                else if (includePuppets && control.type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet)
                {
                    TryAddSubParam(control, 0, output);
                    TryAddSubParam(control, 1, output);
                    TryAddSubParam(control, 2, output);
                    TryAddSubParam(control, 3, output);
                }
                else if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    stack.Push(control.subMenu);
                }
            }
        }
    }

    private static void TryAddSubParam(VRCExpressionsMenu.Control control, int index, HashSet<string> output)
    {
        if (control == null || control.subParameters == null) return;
        if (index < 0 || index >= control.subParameters.Length) return;
        TryAddParam(control.subParameters[index], output);
    }

    private static void TryAddParam(VRCExpressionsMenu.Control.Parameter parameter, HashSet<string> output)
    {
        var name = parameter?.name;
        if (string.IsNullOrEmpty(name)) return;
        output.Add(name);
    }

    private static Object GetObjRefFromGuidWrapper(object wrapper)
    {
        if (wrapper == null) return null;

        var type = wrapper.GetType();
        while (type != null)
        {
            var objRefField = type.GetField("objRef", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (objRefField != null)
            {
                return objRefField.GetValue(wrapper) as Object;
            }
            type = type.BaseType;
        }

        return null;
    }

    private (bool enabled, bool includeBools, bool includePuppets, string path, bool isExternal) FindUnlimitedParameters(Component[] vrcfComponents)
    {
        if (_vrcFuryType == null || _unlimitedType == null)
        {
            return (false, false, false, string.Empty, false);
        }

        foreach (var component in vrcfComponents)
        {
            if (component == null) continue;

            var content = _vrcFuryType.GetField("content")?.GetValue(component);
            if (content == null || !_unlimitedType.IsInstanceOfType(content)) continue;

            bool includeBools = (bool)(_unlimitedType.GetField("includeBools")?.GetValue(content) ?? false);
            bool includePuppets = (bool)(_unlimitedType.GetField("includePuppets")?.GetValue(content) ?? false);
            var go = (component as Component)?.gameObject;
            string path = GetGameObjectPath(go);
            bool external = go != null && go.name != VRCFuryService.SLIDERS_GAMEOBJECT_NAME;
            return (true, includeBools, includePuppets, path, external);
        }

        return (false, false, false, string.Empty, false);
    }

    private (int rawBitsTotal, int compressibleNumbersTotal, int compressibleBoolsTotal, int mcbSliderRawBits, int mcbSliderCount)
        GetToggleStats(Component[] vrcfComponents, bool compressionEnabled, bool includeBools)
    {
        if (_vrcFuryType == null || _toggleType == null)
        {
            return (0, 0, 0, 0, 0);
        }

        int rawBitsTotal = 0;
        int compressibleNumbersTotal = 0;
        int compressibleBoolsTotal = 0;
        int mcbSliderRawBits = 0;
        int mcbSliderCount = 0;

        foreach (var component in vrcfComponents)
        {
            if (component == null) continue;

            var content = _vrcFuryType.GetField("content")?.GetValue(component);
            if (content == null || !_toggleType.IsInstanceOfType(content)) continue;

            bool isSlider = (bool)(_toggleType.GetField("slider")?.GetValue(content) ?? false);
            bool isInt = (bool)(_toggleType.GetField("useInt")?.GetValue(content) ?? false);

            int cost = (isSlider || isInt) ? 8 : 1;
            rawBitsTotal += cost;

            if (compressionEnabled)
            {
                if (isSlider || isInt) compressibleNumbersTotal++;
                else if (includeBools) compressibleBoolsTotal++;
            }

            var go = component.gameObject;
            if (go != null && go.name == VRCFuryService.SLIDERS_GAMEOBJECT_NAME && isSlider)
            {
                mcbSliderCount++;
                mcbSliderRawBits += 8;
            }
        }

        return (rawBitsTotal, compressibleNumbersTotal, compressibleBoolsTotal, mcbSliderRawBits, mcbSliderCount);
    }

    private int CalculateCompressionSavings(int compressibleNumbersCount, int compressibleBoolsCount)
    {
        int numbers = Mathf.Max(0, compressibleNumbersCount);
        int bools = Mathf.Max(0, compressibleBoolsCount);

        int boolsOptimized = bools <= 8 ? 0 : bools;
        int bitsToAdd = 8 + (numbers > 0 ? 8 : 0) + (boolsOptimized > 0 ? 8 : 0);
        int bitsToRemove = (numbers * 8) + boolsOptimized;

        if (bitsToAdd >= bitsToRemove) return 0;
        return bitsToRemove - bitsToAdd;
    }

    private System.Type FindType(string fullName)
    {
        string[] assemblyNames = { "VRCFury-Runtime", "VRCFury-Editor", "VRCFury" };
        foreach (var assemblyName in assemblyNames)
        {
            var type = System.Type.GetType(fullName + ", " + assemblyName);
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

    private string GetGameObjectPath(GameObject obj)
    {
        if (obj == null) return string.Empty;
        string path = obj.name;
        while (obj.transform.parent != null)
        {
            obj = obj.transform.parent.gameObject;
            path = obj.name + "/" + path;
        }
        return path;
    }
}
#endif
