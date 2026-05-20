#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class PhotoshootGenerationService
{
    public enum ShotKind
    {
        Thumbnail,
        Banner
    }

    public sealed class BodyPoseOption
    {
        public string displayName;
        public string assetPath;
        public AnimationClip clip;
    }

    public sealed class BackgroundOption
    {
        public string displayName;
        public string assetPath;
        public Texture2D texture;
    }

    public sealed class FaceBlendshapeOption
    {
        public string name;
        public int rendererCount;
    }

    public sealed class LightPresetOption
    {
        public string displayName;
        public Color ambientColor;
        public Color keyColor;
        public float keyIntensity;
        public Vector3 keyRotation;
        public Color fillColor;
        public float fillIntensity;
        public Vector3 fillPosition;
        public Color rimColor;
        public float rimIntensity;
        public Vector3 rimRotation;
    }

    public sealed class Catalog
    {
        public List<BodyPoseOption> bodyPoses = new List<BodyPoseOption>();
        public List<BackgroundOption> backgrounds = new List<BackgroundOption>();
        public List<FaceBlendshapeOption> faceBlendshapes = new List<FaceBlendshapeOption>();
        public List<LightPresetOption> lightPresets = new List<LightPresetOption>();
    }

    public sealed class RenderRequest
    {
        public GameObject avatarRoot;
        public AnimationClip bodyPose;
        public Texture2D background;
        public LightPresetOption lightPreset;
        public IEnumerable<string> selectedFaceBlendshapeNames;
        public bool forceFaceBlendshapeApply;
        public ShotKind shotKind;
        public float zoom;
        public Vector2 placement;
        public float avatarYawDegrees;
        public int width;
        public int height;
    }

    public sealed class LivePreviewSession : IDisposable
    {
        private const string SceneName = "MCB Photoshoot";

        private sealed class PreviewRenderTarget
        {
            public RenderTexture sceneRenderTexture;
            public RenderTexture renderTexture;
            public RenderTexture nextRenderTexture;
            public int width;
            public int height;
        }

        private Scene scene;
        private Scene previousActiveScene;
        private GameObject avatarCopy;
        private GameObject lastAvatarRoot;
        private GameObject backgroundObject;
        private GameObject keyLightObject;
        private GameObject fillLightObject;
        private GameObject rimLightObject;
        private AnimationClip lastBodyPose;
        private readonly PreviewRenderTarget thumbnailTarget = new PreviewRenderTarget();
        private readonly PreviewRenderTarget bannerTarget = new PreviewRenderTarget();
        private Camera camera;
        private Material backgroundMaterial;
        private Material bannerEffectMaterial;
        private string lastFaceBlendshapeKey;
        private ShotKind lastPreviewShotKind = ShotKind.Thumbnail;

        public Texture PreviewTexture => GetPreviewTexture(lastPreviewShotKind);
        public string ActiveSceneName => scene.IsValid() ? scene.name : null;
        public bool IsOpen => scene.IsValid();

        public Texture GetPreviewTexture(ShotKind shotKind)
        {
            return GetRenderTarget(shotKind).renderTexture;
        }

        public void UpdatePreview(RenderRequest request)
        {
            ValidateRequest(request);
            EnsureScene();
            PreviewRenderTarget target = GetRenderTarget(request.shotKind);
            EnsureRenderTexture(target, request.width, request.height, request.shotKind);
            EnsureCamera();
            bool avatarChanged = EnsureAvatarCopy(request.avatarRoot, request.bodyPose);

            string faceBlendshapeKey = CreateFaceBlendshapeKey(request.selectedFaceBlendshapeNames);
            bool faceBlendshapesChanged =
                request.forceFaceBlendshapeApply ||
                avatarChanged ||
                !string.Equals(lastFaceBlendshapeKey, faceBlendshapeKey, StringComparison.Ordinal);
            if (faceBlendshapesChanged)
            {
                ResetAndApplyFaceBlendshapes(avatarCopy, request.selectedFaceBlendshapeNames);
                lastFaceBlendshapeKey = faceBlendshapeKey;
            }
            Bounds bounds = CenterAvatarOnStage(avatarCopy, LiveSceneStageOrigin);
            ApplyAvatarRotation(avatarCopy, request.avatarYawDegrees);
            bounds = CenterAvatarOnStage(avatarCopy, LiveSceneStageOrigin);

            ConfigureCamera(camera, bounds, request.shotKind, request.width, request.height, request.zoom, request.placement);
            RebuildBackground(camera, bounds, request.background);
            ApplyLiveLightPreset(request.lightPreset ?? CreateLightPresets()[0]);

            camera.targetTexture = target.sceneRenderTexture;
            if (faceBlendshapesChanged)
            {
                camera.Render();
            }
            camera.Render();
            ApplyShotPostProcess(request.shotKind, target);
            MarkRenderTargetUpdated(target);
            lastPreviewShotKind = request.shotKind;
        }

        public Texture2D Capture(RenderRequest request)
        {
            ValidateRequest(request);
            UpdatePreview(request);

            RenderTexture previousActiveTexture = RenderTexture.active;
            try
            {
                RenderTexture.active = GetRenderTarget(request.shotKind).renderTexture;
                var texture = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false, false)
                {
                    name = "MCB Photoshoot Capture"
                };
                texture.ReadPixels(new Rect(0, 0, request.width, request.height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previousActiveTexture;
            }
        }

        public void Dispose()
        {
            if (camera != null)
            {
                camera.targetTexture = null;
            }

            ReleaseRenderTarget(thumbnailTarget);
            ReleaseRenderTarget(bannerTarget);

            if (backgroundMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(backgroundMaterial);
                backgroundMaterial = null;
            }

            if (bannerEffectMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(bannerEffectMaterial);
                bannerEffectMaterial = null;
            }

            if (scene.IsValid())
            {
                EditorSceneManager.CloseScene(scene, true);
                scene = default(Scene);
            }

            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }

            avatarCopy = null;
            lastAvatarRoot = null;
            backgroundObject = null;
            keyLightObject = null;
            fillLightObject = null;
            rimLightObject = null;
            camera = null;
            lastFaceBlendshapeKey = null;
        }

        private void EnsureScene()
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                return;
            }

            previousActiveScene = SceneManager.GetActiveScene();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            scene.name = SceneName;
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
        }

        private PreviewRenderTarget GetRenderTarget(ShotKind shotKind)
        {
            return shotKind == ShotKind.Banner ? bannerTarget : thumbnailTarget;
        }

        private void EnsureRenderTexture(PreviewRenderTarget target, int width, int height, ShotKind shotKind)
        {
            if (target.sceneRenderTexture != null &&
                target.renderTexture != null &&
                target.nextRenderTexture != null &&
                target.width == width &&
                target.height == height &&
                target.sceneRenderTexture.IsCreated() &&
                target.renderTexture.IsCreated() &&
                target.nextRenderTexture.IsCreated())
            {
                return;
            }

            if (camera != null)
            {
                camera.targetTexture = null;
            }

            ReleaseRenderTarget(target);

            target.width = width;
            target.height = height;
            string namePrefix = shotKind == ShotKind.Banner ? "Banner" : "Thumbnail";
            target.sceneRenderTexture = CreateRenderTexture(width, height, 24, $"MCB Photoshoot {namePrefix} Scene Render", antiAlias: true);
            target.renderTexture = CreateRenderTexture(width, height, 0, $"MCB Photoshoot {namePrefix} Live Preview", antiAlias: false);
            target.nextRenderTexture = CreateRenderTexture(width, height, 0, $"MCB Photoshoot {namePrefix} Live Preview Next", antiAlias: false);
            ClearRenderTexture(target.sceneRenderTexture, PreviewClearColor);
            ClearRenderTexture(target.renderTexture, PreviewClearColor);
            ClearRenderTexture(target.nextRenderTexture, PreviewClearColor);
        }

        private void EnsureCamera()
        {
            if (camera != null)
            {
                return;
            }

            var cameraGo = CreateVisibleSceneObject("Photoshoot Camera", scene);
            camera = cameraGo.AddComponent<Camera>();
            camera.hideFlags = HideFlags.DontSave;
        }

        private bool EnsureAvatarCopy(GameObject avatarRoot, AnimationClip bodyPose)
        {
            if (avatarCopy != null && lastAvatarRoot == avatarRoot && lastBodyPose == bodyPose)
            {
                return false;
            }

            if (avatarCopy != null)
            {
                UnityEngine.Object.DestroyImmediate(avatarCopy);
                avatarCopy = null;
            }

            avatarCopy = UnityEngine.Object.Instantiate(avatarRoot, scene) as GameObject;
            if (avatarCopy == null)
            {
                throw new InvalidOperationException("Could not duplicate avatar into the photoshoot scene.");
            }

            avatarCopy.name = avatarRoot.name + " Photoshoot";
            avatarCopy.hideFlags = HideFlags.DontSave;
            avatarCopy.transform.position = Vector3.zero;
            avatarCopy.transform.rotation = Quaternion.identity;
            DisableAudioListeners(avatarCopy);
            SampleBodyPose(avatarCopy, bodyPose);

            lastAvatarRoot = avatarRoot;
            lastBodyPose = bodyPose;
            lastFaceBlendshapeKey = null;
            return true;
        }

        private void RebuildBackground(Camera previewCamera, Bounds bounds, Texture2D texture)
        {
            if (backgroundObject != null && backgroundMaterial != null)
            {
                ConfigureBackground(backgroundObject, backgroundMaterial, previewCamera, bounds, texture);
                return;
            }

            if (backgroundMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(backgroundMaterial);
                backgroundMaterial = null;
            }
            if (backgroundObject != null)
            {
                UnityEngine.Object.DestroyImmediate(backgroundObject);
                backgroundObject = null;
            }

            backgroundMaterial = CreateBackground(previewCamera, bounds, texture, scene, out backgroundObject, visible: true);
        }

        private void ApplyLiveLightPreset(LightPresetOption preset)
        {
            var keyLight = EnsureLiveLight(ref keyLightObject, "Key Light", LightType.Directional);
            keyLightObject.transform.position = LiveSceneStageOrigin;
            keyLightObject.transform.rotation = Quaternion.Euler(preset.keyRotation);
            keyLight.color = preset.keyColor;
            keyLight.intensity = preset.keyIntensity;
            keyLight.shadows = LightShadows.None;

            var fillLight = EnsureLiveLight(ref fillLightObject, "Fill Light", LightType.Point);
            fillLightObject.transform.position = LiveSceneStageOrigin + preset.fillPosition;
            fillLight.color = preset.fillColor;
            fillLight.intensity = preset.fillIntensity;
            fillLight.range = 5f;
            fillLight.shadows = LightShadows.None;

            var rimLight = EnsureLiveLight(ref rimLightObject, "Rim Light", LightType.Directional);
            rimLightObject.transform.position = LiveSceneStageOrigin;
            rimLightObject.transform.rotation = Quaternion.Euler(preset.rimRotation);
            rimLight.color = preset.rimColor;
            rimLight.intensity = preset.rimIntensity;
            rimLight.shadows = LightShadows.None;
        }

        private Light EnsureLiveLight(ref GameObject lightObject, string name, LightType lightType)
        {
            if (lightObject == null)
            {
                lightObject = CreateVisibleSceneObject(name, scene);
                var newLight = lightObject.AddComponent<Light>();
                newLight.type = lightType;
                return newLight;
            }

            var light = lightObject.GetComponent<Light>();
            if (light == null)
            {
                light = lightObject.AddComponent<Light>();
            }
            light.type = lightType;
            return light;
        }

        private void ApplyShotPostProcess(ShotKind shotKind, PreviewRenderTarget target)
        {
            if (target.sceneRenderTexture == null || target.renderTexture == null)
            {
                return;
            }

            RenderTexture output = target.nextRenderTexture != null ? target.nextRenderTexture : target.renderTexture;
            if (shotKind == ShotKind.Banner && EnsureBannerEffectMaterial())
            {
                bannerEffectMaterial.SetColor("_OverlayColor", BannerEffectOverlayColor);
                bannerEffectMaterial.SetFloat("_MaxBlurTexels", BannerEffectMaxBlurTexels);
                Graphics.Blit(target.sceneRenderTexture, output, bannerEffectMaterial);
                SwapPreviewRenderTexture(target);
                return;
            }

            Graphics.Blit(target.sceneRenderTexture, output);
            SwapPreviewRenderTexture(target);
        }

        private static void SwapPreviewRenderTexture(PreviewRenderTarget target)
        {
            if (target.nextRenderTexture == null)
            {
                return;
            }

            RenderTexture previous = target.renderTexture;
            target.renderTexture = target.nextRenderTexture;
            target.nextRenderTexture = previous;
        }

        private static void MarkRenderTargetUpdated(PreviewRenderTarget target)
        {
            if (target == null)
            {
                return;
            }

            IncrementTextureUpdateCount(target.sceneRenderTexture);
            IncrementTextureUpdateCount(target.renderTexture);
            IncrementTextureUpdateCount(target.nextRenderTexture);
        }

        private bool EnsureBannerEffectMaterial()
        {
            if (bannerEffectMaterial != null)
            {
                return true;
            }

            Shader shader = Shader.Find(BannerEffectShaderName);
            if (shader == null)
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(BannerEffectShaderPath);
            }
            if (shader == null)
            {
                return false;
            }

            bannerEffectMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return true;
        }

        private static RenderTexture CreateRenderTexture(int width, int height, int depth, string name, bool antiAlias)
        {
            var texture = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32)
            {
                antiAliasing = antiAlias ? 4 : 1,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
                name = name,
                useMipMap = false,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();
            return texture;
        }

        private static void ClearRenderTexture(RenderTexture texture, Color color)
        {
            if (texture == null)
            {
                return;
            }

            RenderTexture previousActiveTexture = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                GL.Clear(true, true, color);
            }
            finally
            {
                RenderTexture.active = previousActiveTexture;
            }
        }

        private static void ReleaseRenderTexture(ref RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            if (RenderTexture.active == texture)
            {
                RenderTexture.active = null;
            }
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }

        private static void ReleaseRenderTarget(PreviewRenderTarget target)
        {
            ReleaseRenderTexture(ref target.sceneRenderTexture);
            ReleaseRenderTexture(ref target.renderTexture);
            ReleaseRenderTexture(ref target.nextRenderTexture);
            target.width = 0;
            target.height = 0;
        }
    }

    private const string BodyPoseFolder = "Assets/animation";
    private const string BackgroundFolder = "Assets/mcb test";
    private const string BannerEffectShaderName = "Hidden/MCB/PhotoshootBannerEffect";
    private const string BannerEffectShaderPath = "Packages/orbiters.mcb/Editor/Shaders/MCBPhotoshootBannerEffect.shader";
    private const float BannerEffectMaxBlurTexels = 48f;
    private const float ReferenceCameraFieldOfView = 31.8f;
    private const float DefaultCameraZoom = 1.35f;
    private const float MinCameraZoom = 0.65f;
    private const float MaxCameraZoom = 7.5f;
    private const float PlacementFrameStrength = 0.80f;
    private static readonly System.Reflection.MethodInfo TextureIncrementUpdateCountMethod =
        typeof(Texture).GetMethod(
            "IncrementUpdateCount",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
    private static readonly Vector3 LiveSceneStageOrigin = new Vector3(10000f, 0f, 10000f);
    private static readonly Color PreviewClearColor = new Color(0x30 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
    private static readonly Color BannerEffectOverlayColor = new Color(0x30 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
    private static readonly string[] FaceKeywords = { "smile", "happy", "sad", "wink", "grin", "angry" };

    public static Catalog BuildCatalog(GameObject avatarRoot)
    {
        var catalog = new Catalog();
        catalog.bodyPoses = FindBodyPoses();
        catalog.backgrounds = FindBackgrounds();
        catalog.faceBlendshapes = FindFaceBlendshapes(avatarRoot);
        catalog.lightPresets = CreateLightPresets();
        return catalog;
    }

    private static void ValidateRequest(RenderRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.avatarRoot == null)
        {
            throw new InvalidOperationException("No avatar root is available for photoshoot generation.");
        }
        if (request.width <= 0 || request.height <= 0)
        {
            throw new InvalidOperationException("Photoshoot render size must be greater than zero.");
        }
    }

    private static string CreateFaceBlendshapeKey(IEnumerable<string> selectedFaceBlendshapeNames)
    {
        if (selectedFaceBlendshapeNames == null)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            selectedFaceBlendshapeNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static List<BodyPoseOption> FindBodyPoses()
    {
        var options = new List<BodyPoseOption>
        {
            new BodyPoseOption { displayName = "Default Pose" }
        };

        if (!AssetDatabase.IsValidFolder(BodyPoseFolder))
        {
            return options;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { BodyPoseFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                continue;
            }

            options.Add(new BodyPoseOption
            {
                displayName = ObjectNames.NicifyVariableName(Path.GetFileNameWithoutExtension(path).Replace("_pose", "")),
                assetPath = path,
                clip = clip
            });
        }

        return options
            .OrderBy(option => option.clip == null ? 0 : 1)
            .ThenBy(option => option.displayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<BackgroundOption> FindBackgrounds()
    {
        var options = new List<BackgroundOption>
        {
            new BackgroundOption { displayName = "Studio Grey" }
        };

        if (!AssetDatabase.IsValidFolder(BackgroundFolder))
        {
            return options;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { BackgroundFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                continue;
            }

            options.Add(new BackgroundOption
            {
                displayName = ObjectNames.NicifyVariableName(Path.GetFileNameWithoutExtension(path)),
                assetPath = path,
                texture = texture
            });
        }

        return options
            .OrderBy(option => option.texture == null ? 0 : 1)
            .ThenBy(option => option.displayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<FaceBlendshapeOption> FindFaceBlendshapes(GameObject avatarRoot)
    {
        var countsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (avatarRoot == null)
        {
            return new List<FaceBlendshapeOption>();
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null)
            {
                continue;
            }

            var seenOnRenderer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendshapeName = mesh.GetBlendShapeName(i);
                if (!IsFaceBlendshapeCandidate(blendshapeName) || !seenOnRenderer.Add(blendshapeName))
                {
                    continue;
                }

                int count;
                countsByName.TryGetValue(blendshapeName, out count);
                countsByName[blendshapeName] = count + 1;
            }
        }

        return countsByName
            .Select(pair => new FaceBlendshapeOption { name = pair.Key, rendererCount = pair.Value })
            .OrderBy(option => option.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsFaceBlendshapeCandidate(string blendshapeName)
    {
        if (string.IsNullOrWhiteSpace(blendshapeName))
        {
            return false;
        }

        return FaceKeywords.Any(keyword => blendshapeName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static List<LightPresetOption> CreateLightPresets()
    {
        return new List<LightPresetOption>
        {
            new LightPresetOption
            {
                displayName = "Studio Soft",
                ambientColor = new Color(0.34f, 0.34f, 0.36f),
                keyColor = Color.white,
                keyIntensity = 1.15f,
                keyRotation = new Vector3(42f, -34f, 0f),
                fillColor = new Color(0.78f, 0.86f, 1f),
                fillIntensity = 0.55f,
                fillPosition = new Vector3(-1.9f, 1.35f, 2.2f),
                rimColor = new Color(0.82f, 0.92f, 1f),
                rimIntensity = 0.55f,
                rimRotation = new Vector3(145f, 28f, 0f)
            },
            new LightPresetOption
            {
                displayName = "Product Bright",
                ambientColor = new Color(0.45f, 0.45f, 0.45f),
                keyColor = Color.white,
                keyIntensity = 1.35f,
                keyRotation = new Vector3(38f, -12f, 0f),
                fillColor = Color.white,
                fillIntensity = 0.75f,
                fillPosition = new Vector3(-1.6f, 1.1f, 2.0f),
                rimColor = new Color(0.76f, 0.84f, 1f),
                rimIntensity = 0.35f,
                rimRotation = new Vector3(150f, 42f, 0f)
            },
            new LightPresetOption
            {
                displayName = "Dramatic Rim",
                ambientColor = new Color(0.18f, 0.18f, 0.20f),
                keyColor = new Color(1f, 0.92f, 0.82f),
                keyIntensity = 0.95f,
                keyRotation = new Vector3(48f, -48f, 0f),
                fillColor = new Color(0.58f, 0.66f, 1f),
                fillIntensity = 0.25f,
                fillPosition = new Vector3(-2.2f, 1.0f, 2.4f),
                rimColor = new Color(0.62f, 0.86f, 1f),
                rimIntensity = 1.15f,
                rimRotation = new Vector3(140f, 34f, 0f)
            },
            new LightPresetOption
            {
                displayName = "Warm Sunset",
                ambientColor = new Color(0.30f, 0.24f, 0.22f),
                keyColor = new Color(1f, 0.70f, 0.46f),
                keyIntensity = 1.2f,
                keyRotation = new Vector3(34f, -62f, 0f),
                fillColor = new Color(0.62f, 0.74f, 1f),
                fillIntensity = 0.35f,
                fillPosition = new Vector3(-1.9f, 1.25f, 2.2f),
                rimColor = new Color(1f, 0.82f, 0.58f),
                rimIntensity = 0.8f,
                rimRotation = new Vector3(136f, 48f, 0f)
            },
            new LightPresetOption
            {
                displayName = "Cool Outdoor",
                ambientColor = new Color(0.30f, 0.34f, 0.40f),
                keyColor = new Color(0.86f, 0.94f, 1f),
                keyIntensity = 1.05f,
                keyRotation = new Vector3(52f, -26f, 0f),
                fillColor = new Color(0.72f, 0.82f, 1f),
                fillIntensity = 0.5f,
                fillPosition = new Vector3(-1.7f, 1.2f, 2.3f),
                rimColor = new Color(0.78f, 0.94f, 1f),
                rimIntensity = 0.65f,
                rimRotation = new Vector3(150f, 24f, 0f)
            }
        };
    }

    private static GameObject CreateVisibleSceneObject(string name, Scene scene)
    {
        var gameObject = new GameObject(name)
        {
            hideFlags = HideFlags.DontSave
        };
        SceneManager.MoveGameObjectToScene(gameObject, scene);
        return gameObject;
    }

    private static void DisableAudioListeners(GameObject root)
    {
        foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
        {
            listener.enabled = false;
        }
    }

    private static void SampleBodyPose(GameObject avatarRoot, AnimationClip clip)
    {
        if (avatarRoot == null || clip == null)
        {
            return;
        }

        clip.SampleAnimation(avatarRoot, 0f);
    }

    private static void ResetAndApplyFaceBlendshapes(GameObject avatarRoot, IEnumerable<string> selectedFaceBlendshapeNames)
    {
        var selected = new HashSet<string>(selectedFaceBlendshapeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null)
            {
                continue;
            }

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendshapeName = mesh.GetBlendShapeName(i);
                if (!IsFaceBlendshapeCandidate(blendshapeName))
                {
                    continue;
                }

                renderer.SetBlendShapeWeight(i, selected.Contains(blendshapeName) ? 100f : 0f);
            }

            renderer.updateWhenOffscreen = true;
        }
    }

    private static void IncrementTextureUpdateCount(Texture texture)
    {
        if (texture == null || TextureIncrementUpdateCountMethod == null)
        {
            return;
        }

        try
        {
            TextureIncrementUpdateCountMethod.Invoke(texture, null);
        }
        catch
        {
            // Older Unity versions may expose the method but reject invocation for render textures.
        }
    }

    private static Bounds CenterAvatarOnStage(GameObject avatarRoot, Vector3 stageOrigin)
    {
        Bounds bounds = CalculateVisibleBounds(avatarRoot);
        Vector3 offset = new Vector3(stageOrigin.x - bounds.center.x, stageOrigin.y - bounds.min.y, stageOrigin.z - bounds.center.z);
        avatarRoot.transform.position += offset;
        return CalculateVisibleBounds(avatarRoot);
    }

    private static void ApplyAvatarRotation(GameObject avatarRoot, float yawDegrees)
    {
        if (avatarRoot == null)
        {
            return;
        }

        avatarRoot.transform.rotation = Quaternion.Euler(0f, Mathf.Clamp(yawDegrees, -90f, 90f), 0f);
    }

    private static Bounds CalculateVisibleBounds(GameObject avatarRoot)
    {
        bool hasBounds = false;
        var bounds = new Bounds(Vector3.zero, Vector3.one);
        foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds || bounds.size.sqrMagnitude <= 0.0001f)
        {
            throw new InvalidOperationException("The avatar has no visible renderers to frame.");
        }

        return bounds;
    }

    private static void ConfigureCamera(Camera camera, Bounds bounds, ShotKind shotKind, int width, int height, float zoom, Vector2 placement)
    {
        zoom = Mathf.Clamp(zoom > 0f ? zoom : DefaultCameraZoom, MinCameraZoom, MaxCameraZoom);
        placement = new Vector2(Mathf.Clamp(placement.x, -1f, 1f), Mathf.Clamp(placement.y, -1f, 1f));
        float aspect = Mathf.Max(0.1f, width / (float)Mathf.Max(1, height));
        float verticalSpan = shotKind == ShotKind.Thumbnail
            ? Mathf.Max(bounds.size.y * 0.58f, bounds.size.x * 0.82f)
            : bounds.size.y * 0.82f;
        float horizontalSpan = shotKind == ShotKind.Thumbnail
            ? Mathf.Max(bounds.size.x * 0.78f, verticalSpan * aspect * 0.52f)
            : Mathf.Max(bounds.size.x * 1.02f, verticalSpan * aspect * 0.82f);
        verticalSpan /= zoom;
        horizontalSpan /= zoom;

        Vector3 target = bounds.center + Vector3.up * (shotKind == ShotKind.Thumbnail ? bounds.size.y * 0.22f : bounds.size.y * 0.16f);
        float verticalFovRadians = ReferenceCameraFieldOfView * Mathf.Deg2Rad;
        float horizontalFovRadians = 2f * Mathf.Atan(Mathf.Tan(verticalFovRadians * 0.5f) * aspect);
        float distanceForHeight = verticalSpan / (2f * Mathf.Tan(verticalFovRadians * 0.5f));
        float distanceForWidth = horizontalSpan / (2f * Mathf.Tan(horizontalFovRadians * 0.5f));
        float distance = Mathf.Max(distanceForHeight, distanceForWidth) + Mathf.Max(0.2f, bounds.extents.z);

        camera.transform.position = target + Vector3.forward * distance;
        Vector3 cameraForward = (target - camera.transform.position).normalized;
        Vector3 cameraRight = Vector3.Cross(Vector3.up, cameraForward).normalized;
        Vector3 framedTarget = target
            - cameraRight * (horizontalSpan * PlacementFrameStrength * placement.x)
            - Vector3.up * (verticalSpan * PlacementFrameStrength * placement.y);
        camera.transform.rotation = Quaternion.LookRotation(framedTarget - camera.transform.position, Vector3.up);
        camera.fieldOfView = ReferenceCameraFieldOfView;
        camera.aspect = aspect;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = Mathf.Max(50f, distance * 6f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = PreviewClearColor;
        camera.allowHDR = true;
        camera.allowMSAA = true;
    }

    private static Material CreateBackground(
        Camera camera,
        Bounds bounds,
        Texture2D texture,
        Scene scene,
        out GameObject background,
        bool visible)
    {
        background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Photoshoot Background";
        background.hideFlags = visible ? HideFlags.DontSave : HideFlags.HideAndDontSave;
        SceneManager.MoveGameObjectToScene(background, scene);
        var collider = background.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }

        Shader shader = FindBackgroundShader(texture);
        var material = new Material(shader)
        {
            hideFlags = visible ? HideFlags.DontSave : HideFlags.HideAndDontSave
        };
        if (material.HasProperty("_Cull"))
        {
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        }
        material.doubleSidedGI = true;
        ConfigureBackground(background, material, camera, bounds, texture);

        var renderer = background.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return material;
    }

    private static void ConfigureBackground(
        GameObject background,
        Material material,
        Camera camera,
        Bounds bounds,
        Texture2D texture)
    {
        float distanceToTarget = Mathf.Max(0.1f, Vector3.Dot(bounds.center - camera.transform.position, camera.transform.forward));
        float backgroundDepth = Mathf.Max(bounds.size.z + 1.5f, distanceToTarget * 0.75f);
        Vector3 backgroundPosition = camera.transform.position + camera.transform.forward * (distanceToTarget + backgroundDepth);
        float cameraToBackground = Vector3.Distance(camera.transform.position, backgroundPosition);
        float viewHeight = 2f * cameraToBackground * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
        float viewWidth = viewHeight * camera.aspect;

        background.transform.position = backgroundPosition;
        background.transform.rotation = Quaternion.LookRotation(backgroundPosition - camera.transform.position, camera.transform.up);
        background.transform.localScale = new Vector3(viewWidth * 1.12f, viewHeight * 1.12f, 1f);

        Shader shader = FindBackgroundShader(texture);
        if (shader != null && material.shader != shader)
        {
            material.shader = shader;
        }
        if (material.HasProperty("_Cull"))
        {
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        }

        if (material.HasProperty("_MainTex"))
        {
            material.mainTexture = texture != null ? texture : Texture2D.whiteTexture;
        }
        Color color = texture != null ? Color.white : PreviewClearColor;
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

    private static Shader FindBackgroundShader(Texture2D texture)
    {
        if (texture == null)
        {
            return Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
        }

        return Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
    }

}
#endif
