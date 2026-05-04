#if UNITY_EDITOR
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;

public static class BlenderAddonService
{
    private const string BlenderAddonUrlPrefsKey = "MCB_BlenderAddonDownloadUrl";
    private const string BlenderAddonModuleName = "mcb_blender";
    private const string BlenderAddonVersion = "0.1.0";
    private const string BlenderAddonReleaseUrlTemplate = "https://github.com/Orbiters-cc/mcb-blender-addon/releases/download/v{0}/mcb_blender-{0}.zip";

    public static object CreateLaunchPayload()
    {
        return new
        {
            moduleName = BlenderAddonModuleName,
            version = BlenderAddonVersion,
            downloadUrl = GetAddonDownloadUrl(),
            localZipPath = GetBundledAddonZipPath()
        };
    }

    public static void WriteBootstrapScript(string scriptPath, string launchConfigPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("import importlib");
        builder.AppendLine("import json");
        builder.AppendLine("import os");
        builder.AppendLine("import sys");
        builder.AppendLine("import tempfile");
        builder.AppendLine("import traceback");
        builder.AppendLine("import urllib.request");
        builder.AppendLine("import bpy");
        builder.AppendLine("CONFIG_PATH = " + JsonConvert.ToString(launchConfigPath));
        builder.AppendLine("");
        builder.AppendLine("def _log(message):");
        builder.AppendLine("    print('[MCB Blender Launch] ' + str(message))");
        builder.AppendLine("");
        builder.AppendLine("def _addon_matches(module_name, expected_version):");
        builder.AppendLine("    try:");
        builder.AppendLine("        try:");
        builder.AppendLine("            bpy.ops.preferences.addon_enable(module=module_name)");
        builder.AppendLine("        except Exception:");
        builder.AppendLine("            pass");
        builder.AppendLine("        module = importlib.import_module(module_name)");
        builder.AppendLine("        version = '.'.join(str(part) for part in module.bl_info.get('version', ()))");
        builder.AppendLine("        if expected_version and version != expected_version:");
        builder.AppendLine("            _log('Installed addon version ' + version + ' does not match ' + expected_version)");
        builder.AppendLine("            return False");
        builder.AppendLine("        bpy.ops.preferences.addon_enable(module=module_name)");
        builder.AppendLine("        return True");
        builder.AppendLine("    except Exception:");
        builder.AppendLine("        return False");
        builder.AppendLine("");
        builder.AppendLine("def _download(url):");
        builder.AppendLine("    if not url:");
        builder.AppendLine("        raise RuntimeError('No MCB Blender addon download URL is configured.')");
        builder.AppendLine("    target = os.path.join(tempfile.gettempdir(), os.path.basename(url.split('?')[0]) or 'mcb_blender.zip')");
        builder.AppendLine("    _log('Downloading addon from ' + url)");
        builder.AppendLine("    urllib.request.urlretrieve(url, target)");
        builder.AppendLine("    return target");
        builder.AppendLine("");
        builder.AppendLine("def _reset_module_cache(module_name):");
        builder.AppendLine("    for key in list(sys.modules.keys()):");
        builder.AppendLine("        if key == module_name or key.startswith(module_name + '.'):");
        builder.AppendLine("            del sys.modules[key]");
        builder.AppendLine("    importlib.invalidate_caches()");
        builder.AppendLine("");
        builder.AppendLine("def _ensure_addon(config):");
        builder.AppendLine("    addon = config.get('addon') or {}");
        builder.AppendLine("    module_name = addon.get('moduleName') or 'mcb_blender'");
        builder.AppendLine("    expected_version = addon.get('version') or ''");
        builder.AppendLine("    if _addon_matches(module_name, expected_version):");
        builder.AppendLine("        return");
        builder.AppendLine("    zip_path = addon.get('localZipPath') or ''");
        builder.AppendLine("    if not zip_path or not os.path.exists(zip_path):");
        builder.AppendLine("        zip_path = _download(addon.get('downloadUrl') or '')");
        builder.AppendLine("    _log('Installing addon from ' + zip_path)");
        builder.AppendLine("    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)");
        builder.AppendLine("    _reset_module_cache(module_name)");
        builder.AppendLine("    bpy.ops.preferences.addon_enable(module=module_name)");
        builder.AppendLine("    try:");
        builder.AppendLine("        bpy.ops.wm.save_userpref()");
        builder.AppendLine("    except Exception as exc:");
        builder.AppendLine("        _log('Could not save Blender preferences: ' + str(exc))");
        builder.AppendLine("");
        builder.AppendLine("def main():");
        builder.AppendLine("    with open(CONFIG_PATH, 'r', encoding='utf-8') as handle:");
        builder.AppendLine("        config = json.load(handle)");
        builder.AppendLine("    _ensure_addon(config)");
        builder.AppendLine("    launcher = importlib.import_module('mcb_blender.launch')");
        builder.AppendLine("    launcher = importlib.reload(launcher)");
        builder.AppendLine("    result = launcher.run_launch_config(CONFIG_PATH)");
        builder.AppendLine("    _log(json.dumps(result, sort_keys=True))");
        builder.AppendLine("");
        builder.AppendLine("try:");
        builder.AppendLine("    main()");
        builder.AppendLine("except Exception:");
        builder.AppendLine("    traceback.print_exc()");
        builder.AppendLine("    raise");

        File.WriteAllText(scriptPath, builder.ToString());
    }

    private static string GetBundledAddonZipPath()
    {
        string path = Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER_FULL_PATH, "Editor", "Blender", "mcb_blender.zip");
        return File.Exists(path) ? Path.GetFullPath(path) : "";
    }

    private static string GetAddonDownloadUrl()
    {
        string fallback = string.Format(BlenderAddonReleaseUrlTemplate, BlenderAddonVersion);
        return EditorPrefs.GetString(BlenderAddonUrlPrefsKey, fallback);
    }
}
#endif
