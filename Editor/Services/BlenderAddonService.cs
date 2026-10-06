#if UNITY_EDITOR
using System.IO;
using Newtonsoft.Json;
using UnityEditor;

/// <summary>
/// The Blender extensions "Modify with Blender" installs before preparing the project: the MCB extension,
/// and XMuscle Orbit Helper in Blenders that have the X-Muscle System (which is never installed by MCB).
/// Each version here is the one copy on the Unity side; its GitHub release must publish "&lt;id&gt;-&lt;version&gt;.zip".
/// </summary>
public static class BlenderAddonService
{
    public const string BlenderAddonVersion = "0.2.0";
    public const string XMuscleToolkitVersion = "0.10.0";
    private const string BlenderAddonUrlPrefsKey = "MCB_BlenderAddonDownloadUrl";
    private const string ExtensionRepository = "user_default";
    private const string BlenderAddonId = "mcb_blender";
    private const string BlenderAddonReleaseUrlTemplate = "https://github.com/Orbiters-cc/MCB-Blender-Add-on/releases/download/v{0}/mcb_blender-{0}.zip";
    private const string XMuscleToolkitId = "xmuscle_orbit_helper";
    private const string XMuscleToolkitReleaseUrlTemplate = "https://github.com/Orbiters-cc/XMuscle-orbit-helper/releases/download/v{0}/xmuscle_orbit_helper-{0}.zip";
    private const string XMusclesExtensionId = "xmusclesystem";

    // Runs in Blender (background) before the launch config: installs or updates each extension with
    // `blender --command extension install-file --repo <repository> --enable <zip>` when the installed
    // version differs, enables it, then lets the MCB extension prepare the project.
    // Keep Python hash comments out of this literal: when UNITY_EDITOR is undefined, C# still
    // parses line-leading '#' as directives inside the excluded region (including string contents).
    private const string BootstrapScript = @"import addon_utils
import importlib
import json
import os
import subprocess
import sys
import tempfile
import traceback
import urllib.request

import bpy


def _log(message):
    print('[MCB Blender Launch] ' + str(message))


def _installed_module(extension_id):
    for module in addon_utils.modules(refresh=False):
        if module.__name__.startswith('bl_ext.') and module.__name__.rsplit('.', 1)[-1] == extension_id:
            return module
    return None


def _version_of(module):
    return '.'.join(str(part) for part in addon_utils.module_bl_info(module).get('version', ()))


def _download(url):
    if not url:
        raise RuntimeError('No download URL is configured.')
    target = os.path.join(tempfile.gettempdir(), os.path.basename(url.split('?')[0]) or 'extension.zip')
    _log('Downloading ' + url)
    urllib.request.urlretrieve(url, target)
    return target


def _unload(module_name):
    addon_utils.disable(module_name)
    for key in list(sys.modules.keys()):
        if key == module_name or key.startswith(module_name + '.'):
            del sys.modules[key]


def _install(extension, repository):
    zip_path = _download(extension.get('downloadUrl') or '')
    _log('Installing ' + zip_path + ' into the ' + repository + ' extension repository')
    completed = subprocess.run(
        [bpy.app.binary_path, '--command', 'extension', 'install-file', '--repo', repository, '--enable', zip_path],
        capture_output=True,
        text=True,
    )
    for line in (completed.stdout + completed.stderr).splitlines():
        _log('  ' + line)
    if completed.returncode != 0:
        raise RuntimeError('extension install-file failed with exit code ' + str(completed.returncode))
    importlib.invalidate_caches()
    addon_utils.modules(refresh=True)


def _ensure(extension, repository):
    extension_id = extension['id']
    expected = extension.get('version') or ''
    module = _installed_module(extension_id)
    installed = False
    if module is not None and _version_of(module) == expected:
        module_name = module.__name__
    else:
        if module is not None:
            _log(extension_id + ' ' + _version_of(module) + ' is installed, ' + expected + ' is needed')
            _unload(module.__name__)
        _install(extension, repository)
        module_name = 'bl_ext.' + repository + '.' + extension_id
        installed = True
    enabled = module_name in bpy.context.preferences.addons
    if not enabled or not addon_utils.check(module_name)[1]:
        if addon_utils.enable(module_name, default_set=True) is None:
            raise RuntimeError('Could not enable ' + module_name)
    return module_name, not enabled and not installed


def main():
    with open(CONFIG_PATH, 'r', encoding='utf-8') as handle:
        config = json.load(handle)
    extensions = config.get('extensions') or {}
    repository = extensions.get('repository') or 'user_default'
    mcb_module, save_preferences = _ensure(extensions['mcb'], repository)
    for extension in extensions.get('optional') or []:
        required_extension = extension.get('whenInstalled') or ''
        if required_extension and _installed_module(required_extension) is None:
            continue
        try:
            _module, changed = _ensure(extension, repository)
            save_preferences = save_preferences or changed
        except Exception:
            traceback.print_exc()
            _log('Continuing without ' + extension.get('id', ''))
    if save_preferences:
        bpy.ops.wm.save_userpref()
    launcher = importlib.import_module(mcb_module + '.launch')
    result = launcher.run_launch_config(CONFIG_PATH)
    _log(json.dumps(result, sort_keys=True))


try:
    main()
except Exception:
    traceback.print_exc()
    raise
";

    public static object CreateLaunchPayload()
    {
        return new
        {
            repository = ExtensionRepository,
            mcb = new
            {
                id = BlenderAddonId,
                version = BlenderAddonVersion,
                downloadUrl = GetAddonDownloadUrl()
            },
            optional = new[]
            {
                new
                {
                    id = XMuscleToolkitId,
                    version = XMuscleToolkitVersion,
                    downloadUrl = string.Format(XMuscleToolkitReleaseUrlTemplate, XMuscleToolkitVersion),
                    whenInstalled = XMusclesExtensionId
                }
            }
        };
    }

    public static void WriteBootstrapScript(string scriptPath, string launchConfigPath)
    {
        File.WriteAllText(scriptPath, "CONFIG_PATH = " + JsonConvert.ToString(launchConfigPath) + "\n" + BootstrapScript);
    }

    private static string GetAddonDownloadUrl()
    {
        string fallback = string.Format(BlenderAddonReleaseUrlTemplate, BlenderAddonVersion);
        return EditorPrefs.GetString(BlenderAddonUrlPrefsKey, fallback);
    }
}
#endif
