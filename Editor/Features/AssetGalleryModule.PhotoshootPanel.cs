#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

public partial class AssetGalleryModule
{
    private void BeginEditSelectedAssetMedia(AvatarDiscoveredAsset asset)
    {
        if (!CanEditSelectedAssetMedia(asset))
        {
            return;
        }

        SelectedAsset = asset;
        isEditingSelectedAssetMedia = true;
        isSavingSelectedAssetMedia = false;
        selectedAssetMediaEditError = null;
        ClearTextureField(ref editThumbnail);
        ClearTextureField(ref editBanner);
        ResetPhotoshootState(destroyPreviewTexture: true);
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private void CancelSelectedAssetMediaEdit()
    {
        ResetSelectedAssetMediaEditState(destroyPreviewTexture: true);
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private void ResetSelectedAssetMediaEditState(bool destroyPreviewTexture)
    {
        isEditingSelectedAssetMedia = false;
        isSavingSelectedAssetMedia = false;
        selectedAssetMediaEditError = null;
        ClearTextureField(ref editThumbnail);
        ClearTextureField(ref editBanner);
        if (destroyPreviewTexture)
        {
            ResetPhotoshootState(destroyPreviewTexture: true);
        }
    }

    private void BuildSelectedAssetMediaEditUIToolkit(VisualElement root)
    {
        BuildPhotoshootSectionUIToolkit(root, includeBackButton: true);

        if (!string.IsNullOrWhiteSpace(selectedAssetMediaEditError))
        {
            root.Add(CreateMessageLabel(selectedAssetMediaEditError, new Color(1f, 0.55f, 0.35f)));
        }

        var saveButton = CreateTextButton(isSavingSelectedAssetMedia ? "Saving..." : "Save", () =>
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(UpdateSelectedAssetMediaCoroutine());
            editor.RefreshUiToolkitSections();
        });
        saveButton.AddToClassList("mcb-button--primary");
        saveButton.AddToClassList("mcb-photoshoot-save-button");
        saveButton.style.marginTop = 12f;
        saveButton.style.height = 32f;
        saveButton.SetEnabled(!isSavingSelectedAssetMedia &&
                              !isGeneratingPhotoshootImage &&
                              CanEditSelectedAssetMedia(SelectedAsset) &&
                              HasPendingSelectedAssetMediaEdit());
        root.Add(saveButton);
    }

    private void BuildPhotoshootSectionUIToolkit(VisualElement root, bool includeBackButton = false)
    {
        EnsurePhotoshootCatalog();
        ClearPhotoshootUiReferences();

        var panel = new VisualElement();
        panel.AddToClassList("mcb-photoshoot-panel");
        panel.EnableInClassList("mcb-photoshoot-panel--flush-top", includeBackButton);
        root.Add(panel);

        if (CanGeneratePhotoshoot() && !HasPhotoshootLivePreviewTextures())
        {
            UpdatePhotoshootLivePreviews(refreshUi: false);
        }

        BuildPhotoshootStageUIToolkit(panel, includeBackButton);
        BuildPhotoshootPickerRow(panel, "Light", BuildPhotoshootLightOptionsUIToolkit);
        BuildPhotoshootPickerRow(panel, "Pose", BuildPhotoshootPoseOptionsUIToolkit);
        BuildPhotoshootPickerRow(panel, "Background", BuildPhotoshootBackgroundOptionsUIToolkit);
        BuildPhotoshootExpressionPickerUIToolkit(panel);

        if (!string.IsNullOrWhiteSpace(photoshootError))
        {
            panel.Add(CreateMessageLabel(photoshootError, new Color(1f, 0.55f, 0.35f)));
        }
        else if (!string.IsNullOrWhiteSpace(photoshootStatus))
        {
            var status = CreateMessageLabel(photoshootStatus, new Color(0.70f, 0.78f, 0.86f));
            status.AddToClassList("mcb-photoshoot-status");
            panel.Add(status);
        }
    }

    private void BuildPhotoshootStageUIToolkit(VisualElement root, bool includeBackButton)
    {
        var stage = new VisualElement();
        stage.AddToClassList("mcb-photoshoot-stage");
        stage.EnableInClassList("mcb-photoshoot-stage--with-back", includeBackButton);
        stage.RegisterCallback<GeometryChangedEvent>(_ => UpdatePhotoshootStageAspect(stage));
        root.Add(stage);

        Texture stageTexture = GetPhotoshootDisplayTexture(PhotoshootGenerationService.ShotKind.Banner);
        photoshootStagePreviewImage = new Image { image = stageTexture, scaleMode = ScaleMode.ScaleToFit };
        photoshootStagePreviewImage.AddToClassList("mcb-photoshoot-stage-bg");
        stage.Add(photoshootStagePreviewImage);

        var content = new VisualElement();
        content.AddToClassList("mcb-photoshoot-stage-content");
        stage.Add(content);

        if (includeBackButton)
        {
            var backButton = CreateTextButton("< Back to asset", CancelSelectedAssetMediaEdit);
            backButton.AddToClassList("mcb-photoshoot-back-button");
            backButton.SetEnabled(!isSavingSelectedAssetMedia && !isGeneratingPhotoshootImage);
            stage.Add(backButton);
        }

        var previewRow = new VisualElement();
        previewRow.AddToClassList("mcb-photoshoot-live-row");
        content.Add(previewRow);

        BuildPhotoshootLiveCard(previewRow, PhotoshootGenerationService.ShotKind.Thumbnail, "Thumbnail");

        var centerSpacer = new VisualElement();
        centerSpacer.AddToClassList("mcb-photoshoot-live-spacer");
        previewRow.Add(centerSpacer);

        BuildPhotoshootLiveCard(previewRow, PhotoshootGenerationService.ShotKind.Banner, "Banner");
        BuildPhotoshootRotationControl(stage);
        BuildPhotoshootAdjustmentControls(stage);
    }

    private static void UpdatePhotoshootStageAspect(VisualElement stage)
    {
        if (stage == null)
        {
            return;
        }

        float width = stage.resolvedStyle.width;
        if (width <= 0f || float.IsNaN(width))
        {
            return;
        }

        stage.style.height = Mathf.Max(280f, width * 9f / 16f);
    }

    private void BuildPhotoshootLiveCard(VisualElement root, PhotoshootGenerationService.ShotKind shotKind, string titleText)
    {
        bool isFixed = IsPhotoshootShotFixed(shotKind);
        var card = new VisualElement();
        card.AddToClassList("mcb-photoshoot-live-card");
        card.EnableInClassList("mcb-photoshoot-live-card--banner", shotKind == PhotoshootGenerationService.ShotKind.Banner);
        root.Add(card);

        var header = new VisualElement();
        header.AddToClassList("mcb-photoshoot-live-header");
        card.Add(header);

        var title = CreateLabel(titleText, 15, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-photoshoot-live-title");
        header.Add(title);

        var actions = new VisualElement();
        actions.AddToClassList("mcb-photoshoot-live-actions");
        card.Add(actions);

        var setButton = CreateTextButton(isFixed ? "Retry" : "Set", () =>
        {
            if (IsPhotoshootShotFixed(shotKind))
            {
                RetryPhotoshootShot(shotKind);
            }
            else
            {
                StartPhotoshootImageGeneration(shotKind);
            }
        });
        setButton.AddToClassList("mcb-photoshoot-set-button");
        setButton.SetEnabled(!IsPhotoshootMediaInputBlocked() && CanGeneratePhotoshoot());
        actions.Add(setButton);

        var browseButton = CreateTextButton("Browse", () => BrowsePhotoshootImage(shotKind));
        browseButton.AddToClassList("mcb-photoshoot-browse-button");
        browseButton.SetEnabled(!IsPhotoshootMediaInputBlocked());
        actions.Add(browseButton);

        if (shotKind == PhotoshootGenerationService.ShotKind.Thumbnail)
        {
            var frame = new VisualElement();
            frame.AddToClassList("mcb-photoshoot-live-preview");
            card.Add(frame);

            Texture texture = GetPhotoshootDisplayTexture(shotKind);
            photoshootThumbnailPreviewImage = new Image { image = texture, scaleMode = ScaleMode.ScaleAndCrop };
            photoshootThumbnailPreviewImage.AddToClassList("mcb-photoshoot-live-image");
            frame.Add(photoshootThumbnailPreviewImage);
        }
    }

    private void BuildPhotoshootAdjustmentControls(VisualElement stage)
    {
        var controls = new VisualElement();
        controls.AddToClassList("mcb-photoshoot-adjustments");
        stage.Add(controls);

        var placementGroup = new VisualElement();
        placementGroup.AddToClassList("mcb-photoshoot-placement-group");
        controls.Add(placementGroup);
        placementGroup.Add(BuildPhotoshootPlacementGrid());
        placementGroup.Add(CreateLabel("Placement", 12, FontStyle.Bold, Color.white));

        var zoomGroup = new VisualElement();
        zoomGroup.AddToClassList("mcb-photoshoot-zoom-group");
        controls.Add(zoomGroup);

        photoshootZoom = Mathf.Clamp(photoshootZoom, PhotoshootMinZoom, PhotoshootMaxZoom);
        var slider = new Slider(PhotoshootMinZoom, PhotoshootMaxZoom, SliderDirection.Vertical) { value = photoshootZoom };
        slider.AddToClassList("mcb-photoshoot-vertical-zoom");
        slider.RegisterValueChangedCallback(evt =>
        {
            photoshootZoom = Mathf.Clamp(evt.newValue, PhotoshootMinZoom, PhotoshootMaxZoom);
            UpdatePhotoshootLivePreviews(refreshUi: false);
        });
        zoomGroup.Add(slider);
        zoomGroup.Add(CreateLabel("Zoom", 12, FontStyle.Bold, Color.white));
    }

    private void BuildPhotoshootRotationControl(VisualElement stage)
    {
        var group = new VisualElement();
        group.AddToClassList("mcb-photoshoot-rotation-group");
        stage.Add(group);

        photoshootRotationDegrees = Mathf.Clamp(photoshootRotationDegrees, PhotoshootMinRotationDegrees, PhotoshootMaxRotationDegrees);
        var slider = new Slider(PhotoshootMinRotationDegrees, PhotoshootMaxRotationDegrees) { value = photoshootRotationDegrees };
        slider.AddToClassList("mcb-photoshoot-rotation-slider");
        slider.RegisterValueChangedCallback(evt =>
        {
            photoshootRotationDegrees = Mathf.Clamp(evt.newValue, PhotoshootMinRotationDegrees, PhotoshootMaxRotationDegrees);
            UpdatePhotoshootLivePreviews(refreshUi: false);
        });
        group.Add(slider);
        group.Add(CreateLabel("Rotation", 12, FontStyle.Bold, Color.white));
    }

    private VisualElement BuildPhotoshootPlacementGrid()
    {
        const float gridSize = 72f;
        const float dotSize = 14f;

        var grid = new VisualElement();
        grid.AddToClassList("mcb-photoshoot-placement-grid");

        for (int i = 1; i < 3; i++)
        {
            var verticalLine = new VisualElement();
            verticalLine.AddToClassList("mcb-photoshoot-placement-line");
            verticalLine.AddToClassList("mcb-photoshoot-placement-line--vertical");
            verticalLine.style.left = gridSize * i / 3f;
            grid.Add(verticalLine);

            var horizontalLine = new VisualElement();
            horizontalLine.AddToClassList("mcb-photoshoot-placement-line");
            horizontalLine.AddToClassList("mcb-photoshoot-placement-line--horizontal");
            horizontalLine.style.top = gridSize * i / 3f;
            grid.Add(horizontalLine);
        }

        var dot = new VisualElement();
        dot.AddToClassList("mcb-photoshoot-placement-dot");
        grid.Add(dot);
        PositionPhotoshootPlacementDot(dot, gridSize, dotSize);

        bool dragging = false;
        Action<Vector2> updatePlacement = localPosition =>
        {
            float x01 = Mathf.Clamp01(localPosition.x / gridSize);
            float y01 = Mathf.Clamp01(localPosition.y / gridSize);
            photoshootPlacement = new Vector2((x01 - 0.5f) * 2f, (0.5f - y01) * 2f);
            PositionPhotoshootPlacementDot(dot, gridSize, dotSize);
            UpdatePhotoshootLivePreviews(refreshUi: false);
        };

        grid.RegisterCallback<MouseDownEvent>(evt =>
        {
            dragging = true;
            grid.CaptureMouse();
            updatePlacement(evt.localMousePosition);
            evt.StopPropagation();
        });
        grid.RegisterCallback<MouseMoveEvent>(evt =>
        {
            if (!dragging)
            {
                return;
            }

            updatePlacement(evt.localMousePosition);
            evt.StopPropagation();
        });
        grid.RegisterCallback<MouseUpEvent>(evt =>
        {
            dragging = false;
            if (grid.HasMouseCapture())
            {
                grid.ReleaseMouse();
            }
            evt.StopPropagation();
        });
        grid.RegisterCallback<MouseCaptureOutEvent>(_ => dragging = false);

        return grid;
    }

    private void PositionPhotoshootPlacementDot(VisualElement dot, float gridSize, float dotSize)
    {
        float x = (dotSize * -0.5f) + ((Mathf.Clamp(photoshootPlacement.x, -1f, 1f) + 1f) * 0.5f * gridSize);
        float y = (dotSize * -0.5f) + ((1f - ((Mathf.Clamp(photoshootPlacement.y, -1f, 1f) + 1f) * 0.5f)) * gridSize);
        dot.style.left = x;
        dot.style.top = y;
    }

    private void BuildPhotoshootPickerRow(VisualElement root, string labelText, Action<VisualElement> buildOptions)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-photoshoot-picker-row");
        root.Add(row);

        var label = CreateLabel(labelText, 13, FontStyle.Bold, Color.white);
        label.AddToClassList("mcb-photoshoot-picker-label");
        row.Add(label);

        var options = new VisualElement();
        options.AddToClassList("mcb-photoshoot-picker-options");
        row.Add(options);
        buildOptions(options);
    }

    private void BuildPhotoshootLightOptionsUIToolkit(VisualElement root)
    {
        for (int i = 0; i < photoshootCatalog.lightPresets.Count; i++)
        {
            int index = i;
            var option = photoshootCatalog.lightPresets[i];
            var button = new Button(() =>
            {
                if (photoshootLightPresetIndex == index)
                {
                    return;
                }

                photoshootLightPresetIndex = index;
                UpdatePhotoshootSelectionVisuals();
                UpdatePhotoshootLivePreviews(refreshUi: false);
            });
            button.AddToClassList("mcb-photoshoot-swatch");
            button.AddToClassList("mcb-photoshoot-swatch--light");
            button.EnableInClassList("mcb-photoshoot-swatch--selected", photoshootLightPresetIndex == index);
            button.tooltip = option.displayName;
            button.Add(new Image { image = GetPhotoshootLightIcon(option), scaleMode = ScaleMode.ScaleToFit });
            photoshootLightOptionButtons.Add(button);
            root.Add(button);
        }
    }

    private void BuildPhotoshootPoseOptionsUIToolkit(VisualElement root)
    {
        for (int i = 0; i < photoshootCatalog.bodyPoses.Count; i++)
        {
            int index = i;
            var option = photoshootCatalog.bodyPoses[i];
            var button = new Button(() =>
            {
                if (photoshootBodyPoseIndex == index)
                {
                    return;
                }

                photoshootBodyPoseIndex = index;
                UpdatePhotoshootSelectionVisuals();
                UpdatePhotoshootLivePreviews(refreshUi: false);
            });
            button.AddToClassList("mcb-photoshoot-swatch");
            button.AddToClassList("mcb-photoshoot-swatch--pose");
            button.EnableInClassList("mcb-photoshoot-swatch--selected", photoshootBodyPoseIndex == index);
            button.tooltip = option.displayName;
            button.Add(new Image { image = GetPhotoshootPoseIcon(option), scaleMode = ScaleMode.ScaleToFit });
            photoshootPoseOptionButtons.Add(button);
            root.Add(button);
        }
    }

    private void BuildPhotoshootBackgroundOptionsUIToolkit(VisualElement root)
    {
        for (int i = 0; i < photoshootCatalog.backgrounds.Count; i++)
        {
            int index = i;
            var option = photoshootCatalog.backgrounds[i];
            var button = new Button(() =>
            {
                if (photoshootBackgroundIndex == index)
                {
                    return;
                }

                photoshootBackgroundIndex = index;
                UpdatePhotoshootSelectionVisuals();
                UpdatePhotoshootLivePreviews(refreshUi: false);
            });
            button.AddToClassList("mcb-photoshoot-swatch");
            button.AddToClassList("mcb-photoshoot-swatch--background");
            button.EnableInClassList("mcb-photoshoot-swatch--selected", photoshootBackgroundIndex == index);
            button.tooltip = option.displayName;
            if (option.texture != null)
            {
                button.Add(new Image { image = option.texture, scaleMode = ScaleMode.ScaleAndCrop });
            }
            else
            {
                var fallback = new VisualElement();
                fallback.AddToClassList("mcb-photoshoot-background-fallback");
                button.Add(fallback);
            }
            photoshootBackgroundOptionButtons.Add(button);
            root.Add(button);
        }
    }

    private void BuildPhotoshootExpressionPickerUIToolkit(VisualElement root)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-photoshoot-picker-row");
        row.AddToClassList("mcb-photoshoot-expression-row");
        root.Add(row);

        var label = CreateLabel("Expression", 13, FontStyle.Bold, Color.white);
        label.AddToClassList("mcb-photoshoot-picker-label");
        row.Add(label);

        var content = new VisualElement();
        content.AddToClassList("mcb-photoshoot-expression-content");
        row.Add(content);

        if (photoshootCatalog.faceBlendshapes.Count == 0)
        {
            content.Add(CreateMessageLabel("No matching face blendshapes found.", new Color(0.72f, 0.72f, 0.72f)));
            return;
        }

        var shapeChips = new VisualElement();
        shapeChips.AddToClassList("mcb-avatar-chip-group");
        shapeChips.AddToClassList("mcb-photoshoot-face-chips");
        content.Add(shapeChips);

        foreach (var option in photoshootCatalog.faceBlendshapes)
        {
            string blendshapeName = option.name;
            string chipLabel = option.rendererCount > 1
                ? $"{blendshapeName} ({option.rendererCount})"
                : blendshapeName;
            var chip = CreatePhotoshootChip(
                chipLabel,
                photoshootSelectedFaceBlendshapes.Contains(blendshapeName),
                () =>
                {
                    if (!photoshootSelectedFaceBlendshapes.Add(blendshapeName))
                    {
                        photoshootSelectedFaceBlendshapes.Remove(blendshapeName);
                    }

                    UpdatePhotoshootSelectionVisuals();
                    UpdatePhotoshootLivePreviews(refreshUi: false, forceFaceBlendshapeApply: true);
                    SchedulePhotoshootLivePreviewRefresh(forceFaceBlendshapeApply: true);
                });
            photoshootExpressionChipButtons[blendshapeName] = chip;
            shapeChips.Add(chip);
        }
    }

    private Button CreatePhotoshootChip(string text, bool selected, Action onClick)
    {
        var chip = new Button(onClick);
        chip.AddToClassList("mcb-avatar-chip");
        chip.AddToClassList("mcb-photoshoot-chip");
        chip.EnableInClassList("mcb-avatar-chip--selected", selected);

        var marker = new VisualElement();
        marker.AddToClassList("mcb-avatar-chip__marker");
        chip.Add(marker);

        var chipLabel = CreateLabel(text, 12, FontStyle.Normal, Color.white);
        chipLabel.AddToClassList("mcb-avatar-chip__label");
        chip.Add(chipLabel);
        return chip;
    }

    private Texture2D GetPhotoshootLightIcon(PhotoshootGenerationService.LightPresetOption option)
    {
        string key = option.displayName ?? "light";
        Texture2D texture;
        if (photoshootLightIconCache.TryGetValue(key, out texture) && texture != null)
        {
            return texture;
        }

        texture = CreatePhotoshootLightIconTexture(option, 64);
        photoshootLightIconCache[key] = texture;
        return texture;
    }

    private Texture2D GetPhotoshootPoseIcon(PhotoshootGenerationService.BodyPoseOption option)
    {
        string key = !string.IsNullOrWhiteSpace(option.assetPath) ? option.assetPath : "__default_pose";
        Texture2D texture;
        if (photoshootPoseIconCache.TryGetValue(key, out texture) && texture != null)
        {
            return texture;
        }

        texture = CreatePhotoshootPoseIconTexture(GetCurrentPhotoshootAvatarRoot(), option.clip, 64);
        photoshootPoseIconCache[key] = texture;
        return texture;
    }

    private static Texture2D CreatePhotoshootLightIconTexture(PhotoshootGenerationService.LightPresetOption option, int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            name = "MCB Photoshoot Light Preview",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        Vector3 keyDirection = (Quaternion.Euler(option.keyRotation) * Vector3.forward).normalized;
        Vector3 rimDirection = (Quaternion.Euler(option.rimRotation) * Vector3.forward).normalized;
        Vector3 fillDirection = (option.fillPosition.sqrMagnitude > 0.001f ? option.fillPosition.normalized : new Vector3(-0.5f, 0.4f, 1f)).normalized;
        Color clear = new Color(0f, 0f, 0f, 0f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size) * 2f - 1f;
                float v = ((y + 0.5f) / size) * 2f - 1f;
                float r2 = u * u + v * v;
                if (r2 > 1f)
                {
                    texture.SetPixel(x, y, clear);
                    continue;
                }

                Vector3 normal = new Vector3(u, v, Mathf.Sqrt(Mathf.Max(0f, 1f - r2))).normalized;
                float key = Mathf.Max(0f, Vector3.Dot(normal, -keyDirection)) * option.keyIntensity;
                float fill = Mathf.Max(0f, Vector3.Dot(normal, fillDirection)) * option.fillIntensity;
                float rim = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(normal, -rimDirection)), 2.5f) * option.rimIntensity;
                Color color = option.ambientColor * 0.72f + option.keyColor * key + option.fillColor * fill + option.rimColor * rim;
                float edge = Mathf.SmoothStep(0.72f, 1f, Mathf.Sqrt(r2));
                color = Color.Lerp(color, color * 0.55f, edge);
                color.a = Mathf.SmoothStep(1.0f, 0.88f, Mathf.Sqrt(r2));
                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply(false, true);
        return texture;
    }

    private static Texture2D CreatePhotoshootPoseIconTexture(GameObject avatarRoot, AnimationClip clip, int size)
    {
        var texture = CreateTransparentPhotoshootIconTexture(size, "MCB Photoshoot Pose Preview");
        bool drawn = false;
        GameObject avatarCopy = null;
        try
        {
            if (avatarRoot != null)
            {
                avatarCopy = UnityEngine.Object.Instantiate(avatarRoot);
                avatarCopy.hideFlags = HideFlags.HideAndDontSave;
                avatarCopy.SetActive(true);
                if (clip != null)
                {
                    clip.SampleAnimation(avatarCopy, 0f);
                }

                var animator = avatarCopy.GetComponentInChildren<Animator>();
                if (animator != null && animator.isHuman)
                {
                    drawn = DrawHumanoidPoseIcon(texture, animator, size);
                }
            }
        }
        finally
        {
            if (avatarCopy != null)
            {
                UnityEngine.Object.DestroyImmediate(avatarCopy);
            }
        }

        if (!drawn)
        {
            DrawFallbackPoseIcon(texture, size);
        }

        texture.Apply(false, true);
        return texture;
    }

    private static Texture2D CreateTransparentPhotoshootIconTexture(int size, string name)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        Color clear = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                texture.SetPixel(x, y, clear);
            }
        }
        return texture;
    }

    private static bool DrawHumanoidPoseIcon(Texture2D texture, Animator animator, int size)
    {
        var bones = new Dictionary<HumanBodyBones, Vector3>();
        HumanBodyBones[] trackedBones =
        {
            HumanBodyBones.Head,
            HumanBodyBones.Neck,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Chest,
            HumanBodyBones.Spine,
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot
        };

        foreach (var bone in trackedBones)
        {
            Transform transform = animator.GetBoneTransform(bone);
            if (transform != null)
            {
                bones[bone] = transform.position;
            }
        }

        if (!bones.ContainsKey(HumanBodyBones.Head) || !bones.ContainsKey(HumanBodyBones.Hips))
        {
            return false;
        }

        List<Vector3> points = bones.Values.ToList();
        float minX = points.Min(point => point.x);
        float maxX = points.Max(point => point.x);
        float minY = points.Min(point => point.y);
        float maxY = points.Max(point => point.y);
        if (maxX - minX < 0.001f || maxY - minY < 0.001f)
        {
            return false;
        }

        const float padding = 9f;
        Func<Vector3, Vector2> project = point =>
        {
            float x = Mathf.Lerp(padding, size - padding, Mathf.InverseLerp(minX, maxX, point.x));
            float y = Mathf.Lerp(padding, size - padding, Mathf.InverseLerp(minY, maxY, point.y));
            return new Vector2(x, y);
        };

        Color color = Color.white;
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Hips, HumanBodyBones.Spine, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Spine, HumanBodyBones.Chest, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Chest, HumanBodyBones.UpperChest, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.UpperChest, HumanBodyBones.Neck, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Neck, HumanBodyBones.Head, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Chest, HumanBodyBones.LeftUpperArm, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Chest, HumanBodyBones.RightUpperArm, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.Hips, HumanBodyBones.RightUpperLeg, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, color, 4);
        TryDrawPoseLine(texture, bones, project, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, color, 4);

        DrawPoseCircle(texture, project(bones[HumanBodyBones.Head]), 5, color);
        return true;
    }

    private static void TryDrawPoseLine(
        Texture2D texture,
        Dictionary<HumanBodyBones, Vector3> bones,
        Func<Vector3, Vector2> project,
        HumanBodyBones from,
        HumanBodyBones to,
        Color color,
        int thickness)
    {
        Vector3 fromPosition;
        Vector3 toPosition;
        if (!bones.TryGetValue(from, out fromPosition) || !bones.TryGetValue(to, out toPosition))
        {
            return;
        }

        DrawPoseLine(texture, project(fromPosition), project(toPosition), color, thickness);
    }

    private static void DrawFallbackPoseIcon(Texture2D texture, int size)
    {
        Color color = Color.white;
        Vector2 head = new Vector2(size * 0.50f, size * 0.78f);
        Vector2 chest = new Vector2(size * 0.50f, size * 0.58f);
        Vector2 hips = new Vector2(size * 0.50f, size * 0.38f);
        DrawPoseCircle(texture, head, 5, color);
        DrawPoseLine(texture, head + Vector2.down * 5f, chest, color, 4);
        DrawPoseLine(texture, chest, hips, color, 4);
        DrawPoseLine(texture, chest, new Vector2(size * 0.30f, size * 0.50f), color, 4);
        DrawPoseLine(texture, chest, new Vector2(size * 0.70f, size * 0.66f), color, 4);
        DrawPoseLine(texture, hips, new Vector2(size * 0.36f, size * 0.14f), color, 4);
        DrawPoseLine(texture, hips, new Vector2(size * 0.66f, size * 0.18f), color, 4);
    }

    private static void DrawPoseLine(Texture2D texture, Vector2 from, Vector2 to, Color color, int thickness)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 point = Vector2.Lerp(from, to, i / (float)steps);
            DrawPoseCircle(texture, point, thickness * 0.5f, color);
        }
    }

    private static void DrawPoseCircle(Texture2D texture, Vector2 center, float radius, Color color)
    {
        int minX = Mathf.FloorToInt(center.x - radius);
        int maxX = Mathf.CeilToInt(center.x + radius);
        int minY = Mathf.FloorToInt(center.y - radius);
        int maxY = Mathf.CeilToInt(center.y + radius);
        float radiusSqr = radius * radius;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (x < 0 || y < 0 || x >= texture.width || y >= texture.height)
                {
                    continue;
                }

                Vector2 delta = new Vector2(x, y) - center;
                if (delta.sqrMagnitude <= radiusSqr)
                {
                    texture.SetPixel(x, y, color);
                }
            }
        }
    }

    private void EnsurePhotoshootCatalog()
    {
        if (photoshootCatalog != null)
        {
            ClampPhotoshootSelections();
            PruneSelectedPhotoshootBlendshapes();
            return;
        }

        photoshootCatalog = PhotoshootGenerationService.BuildCatalog(GetCurrentPhotoshootAvatarRoot());
        ClampPhotoshootSelections();
        PruneSelectedPhotoshootBlendshapes();
    }

    private void ClampPhotoshootSelections()
    {
        photoshootBodyPoseIndex = ClampIndex(photoshootBodyPoseIndex, photoshootCatalog?.bodyPoses?.Count ?? 0);
        photoshootBackgroundIndex = ClampIndex(photoshootBackgroundIndex, photoshootCatalog?.backgrounds?.Count ?? 0);
        photoshootLightPresetIndex = ClampIndex(photoshootLightPresetIndex, photoshootCatalog?.lightPresets?.Count ?? 0);
    }

    private static int ClampIndex(int value, int count)
    {
        return count <= 0 ? 0 : Mathf.Clamp(value, 0, count - 1);
    }

    private void PruneSelectedPhotoshootBlendshapes()
    {
        if (photoshootCatalog?.faceBlendshapes == null || photoshootSelectedFaceBlendshapes.Count == 0)
        {
            return;
        }

        var available = new HashSet<string>(
            photoshootCatalog.faceBlendshapes.Select(option => option.name),
            StringComparer.OrdinalIgnoreCase);
        photoshootSelectedFaceBlendshapes.RemoveWhere(name => !available.Contains(name));
    }

    private GameObject GetCurrentPhotoshootAvatarRoot()
    {
        return editor?.customBaseTarget != null
            ? editor.customBaseTarget.transform.root.gameObject
            : null;
    }

    private bool CanGeneratePhotoshoot()
    {
        return GetCurrentPhotoshootAvatarRoot() != null &&
               !isSubmittingCustomBase &&
               !isSavingSelectedAssetMedia;
    }

    private bool IsPhotoshootPanelActive()
    {
        return isCreatingCustomBase || isEditingSelectedAssetMedia;
    }

    private bool HasPhotoshootLivePreviewTextures()
    {
        return photoshootPreviewSession != null &&
               photoshootPreviewSession.GetPreviewTexture(PhotoshootGenerationService.ShotKind.Thumbnail) != null &&
               photoshootPreviewSession.GetPreviewTexture(PhotoshootGenerationService.ShotKind.Banner) != null;
    }

    private void ClearPhotoshootUiReferences()
    {
        photoshootStagePreviewImage = null;
        photoshootThumbnailPreviewImage = null;
        photoshootLightOptionButtons.Clear();
        photoshootPoseOptionButtons.Clear();
        photoshootBackgroundOptionButtons.Clear();
        photoshootExpressionChipButtons.Clear();
    }

    private void UpdatePhotoshootSelectionVisuals()
    {
        for (int i = 0; i < photoshootLightOptionButtons.Count; i++)
        {
            photoshootLightOptionButtons[i]?.EnableInClassList("mcb-photoshoot-swatch--selected", i == photoshootLightPresetIndex);
        }

        for (int i = 0; i < photoshootPoseOptionButtons.Count; i++)
        {
            photoshootPoseOptionButtons[i]?.EnableInClassList("mcb-photoshoot-swatch--selected", i == photoshootBodyPoseIndex);
        }

        for (int i = 0; i < photoshootBackgroundOptionButtons.Count; i++)
        {
            photoshootBackgroundOptionButtons[i]?.EnableInClassList("mcb-photoshoot-swatch--selected", i == photoshootBackgroundIndex);
        }

        foreach (var pair in photoshootExpressionChipButtons)
        {
            pair.Value?.EnableInClassList("mcb-avatar-chip--selected", photoshootSelectedFaceBlendshapes.Contains(pair.Key));
        }
    }

    private bool IsPhotoshootMediaInputBlocked()
    {
        return isSubmittingCustomBase || isSavingSelectedAssetMedia || isGeneratingPhotoshootImage;
    }

    private bool HasPendingSelectedAssetMediaEdit()
    {
        return editThumbnail != null || editBanner != null;
    }

    private void UpdatePhotoshootLivePreviews(bool refreshUi, bool forceFaceBlendshapeApply = false)
    {
        RenderPhotoshootLivePreview(PhotoshootGenerationService.ShotKind.Thumbnail, forceFaceBlendshapeApply);
        RenderPhotoshootLivePreview(PhotoshootGenerationService.ShotKind.Banner, forceFaceBlendshapeApply);
        RefreshPhotoshootLivePreviewImages();

        if (refreshUi)
        {
            editor.RefreshUiToolkitSections();
        }

        RepaintPhotoshootPreview();
    }

    private void UpdatePhotoshootLivePreview(PhotoshootGenerationService.ShotKind shotKind, bool refreshUi, bool forceFaceBlendshapeApply = false)
    {
        RenderPhotoshootLivePreview(shotKind, forceFaceBlendshapeApply);
        RefreshPhotoshootLivePreviewImages();

        if (refreshUi)
        {
            editor.RefreshUiToolkitSections();
        }

        RepaintPhotoshootPreview();
    }

    private void RenderPhotoshootLivePreview(PhotoshootGenerationService.ShotKind shotKind, bool forceFaceBlendshapeApply = false)
    {
        if (!IsPhotoshootPanelActive() || !CanGeneratePhotoshoot())
        {
            return;
        }

        try
        {
            photoshootError = null;
            var request = BuildPhotoshootRequest(shotKind, preview: true, forceFaceBlendshapeApply: forceFaceBlendshapeApply);
            if (photoshootPreviewSession == null)
            {
                photoshootPreviewSession = new PhotoshootGenerationService.LivePreviewSession();
            }

            photoshootPreviewSession.UpdatePreview(request);
            photoshootStatus = $"Live scene: {photoshootPreviewSession.ActiveSceneName}";
        }
        catch (Exception ex)
        {
            photoshootError = ex.Message;
            photoshootStatus = null;
        }
    }

    private void RefreshPhotoshootLivePreviewImages()
    {
        if (photoshootStagePreviewImage != null)
        {
            photoshootStagePreviewImage.image = GetPhotoshootDisplayTexture(PhotoshootGenerationService.ShotKind.Banner);
            photoshootStagePreviewImage.MarkDirtyRepaint();
        }

        if (photoshootThumbnailPreviewImage != null)
        {
            photoshootThumbnailPreviewImage.image = GetPhotoshootDisplayTexture(PhotoshootGenerationService.ShotKind.Thumbnail);
            photoshootThumbnailPreviewImage.MarkDirtyRepaint();
        }
    }

    private void RepaintPhotoshootPreview()
    {
        photoshootStagePreviewImage?.MarkDirtyRepaint();
        photoshootThumbnailPreviewImage?.MarkDirtyRepaint();
        galleryRoot?.MarkDirtyRepaint();
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
        editor.Repaint();
    }

    private void SchedulePhotoshootLivePreviewRefresh(bool forceFaceBlendshapeApply = false)
    {
        int refreshTicket = ++photoshootLivePreviewRefreshTicket;
        EditorApplication.delayCall += () =>
        {
            if (refreshTicket != photoshootLivePreviewRefreshTicket || !IsPhotoshootPanelActive() || !CanGeneratePhotoshoot())
            {
                return;
            }

            UpdatePhotoshootLivePreviews(refreshUi: false, forceFaceBlendshapeApply: forceFaceBlendshapeApply);
        };
    }

    private Texture GetPhotoshootPreviewTexture(PhotoshootGenerationService.ShotKind shotKind)
    {
        return photoshootPreviewSession?.GetPreviewTexture(shotKind);
    }

    private Texture GetPhotoshootDisplayTexture(PhotoshootGenerationService.ShotKind shotKind)
    {
        Texture fixedTexture = GetPhotoshootAssignedTexture(shotKind);
        return fixedTexture != null ? fixedTexture : GetPhotoshootPreviewTexture(shotKind);
    }

    private Texture2D GetPhotoshootAssignedTexture(PhotoshootGenerationService.ShotKind shotKind)
    {
        if (shotKind == PhotoshootGenerationService.ShotKind.Thumbnail)
        {
            return isEditingSelectedAssetMedia ? editThumbnail : createThumbnail;
        }

        return isEditingSelectedAssetMedia ? editBanner : createBanner;
    }

    private bool IsPhotoshootShotFixed(PhotoshootGenerationService.ShotKind shotKind)
    {
        return GetPhotoshootAssignedTexture(shotKind) != null;
    }

    private void SetPhotoshootShotTexture(PhotoshootGenerationService.ShotKind shotKind, Texture2D texture)
    {
        if (shotKind == PhotoshootGenerationService.ShotKind.Thumbnail)
        {
            if (isEditingSelectedAssetMedia)
            {
                SetEditThumbnail(texture);
            }
            else
            {
                SetCreateThumbnail(texture);
            }
        }
        else if (isEditingSelectedAssetMedia)
        {
            SetEditBanner(texture);
        }
        else
        {
            SetCreateBanner(texture);
        }
    }

    private void RetryPhotoshootShot(PhotoshootGenerationService.ShotKind shotKind)
    {
        SetPhotoshootShotTexture(shotKind, null);
        UpdatePhotoshootLivePreview(shotKind, refreshUi: true);
    }

    private void BrowsePhotoshootImage(PhotoshootGenerationService.ShotKind shotKind)
    {
        string path = EditorUtility.OpenFilePanelWithFilters(
            $"Choose {GetPhotoshootShotDisplayName(shotKind)}",
            Application.dataPath,
            new[] { "Image files", "png,jpg,jpeg", "All files", "*" });
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Texture2D texture = LoadPhotoshootImageFromPath(path);
            if (texture == null)
            {
                throw new InvalidOperationException("Selected image could not be loaded.");
            }

            SetPhotoshootShotTexture(shotKind, texture);
            photoshootError = null;
            photoshootStatus = $"{GetPhotoshootShotDisplayName(shotKind)} selected";
        }
        catch (Exception ex)
        {
            photoshootError = ex.Message;
            photoshootStatus = null;
        }

        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private static Texture2D LoadPhotoshootImageFromPath(string path)
    {
        string normalizedPath = path.Replace('\\', '/');
        string normalizedAssetsPath = Application.dataPath.Replace('\\', '/');
        if (normalizedPath.StartsWith(normalizedAssetsPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            string assetPath = "Assets" + normalizedPath.Substring(normalizedAssetsPath.Length);
            var assetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (assetTexture != null)
            {
                return assetTexture;
            }
        }

        byte[] bytes = File.ReadAllBytes(path);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
        {
            name = Path.GetFileNameWithoutExtension(path),
            hideFlags = HideFlags.DontSave
        };
        if (!ImageConversion.LoadImage(texture, bytes, false))
        {
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        return texture;
    }

    private void StartPhotoshootImageGeneration(PhotoshootGenerationService.ShotKind shotKind)
    {
        if (isGeneratingPhotoshootImage || !CanGeneratePhotoshoot())
        {
            return;
        }

        CapturePhotoshootImages(new[] { shotKind });
    }

    private void CapturePhotoshootImages(PhotoshootGenerationService.ShotKind[] shotKinds)
    {
        if (shotKinds == null || shotKinds.Length == 0)
        {
            return;
        }

        isGeneratingPhotoshootImage = true;
        photoshootError = null;
        try
        {
            foreach (var shotKind in shotKinds)
            {
                if (!CanGeneratePhotoshoot())
                {
                    return;
                }

                string displayName = GetPhotoshootShotDisplayName(shotKind);
                try
                {
                    string captureSummary = GenerateAndAssignPhotoshootImage(shotKind);
                    photoshootStatus = $"{displayName} captured ({captureSummary})";
                }
                catch (Exception ex)
                {
                    photoshootError = ex.Message;
                    photoshootStatus = null;
                    break;
                }
            }
        }
        finally
        {
            isGeneratingPhotoshootImage = false;
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        }
    }

    private string GenerateAndAssignPhotoshootImage(PhotoshootGenerationService.ShotKind shotKind)
    {
        var request = BuildPhotoshootRequest(shotKind, preview: false);
        if (photoshootPreviewSession == null)
        {
            photoshootPreviewSession = new PhotoshootGenerationService.LivePreviewSession();
        }

        var texture = photoshootPreviewSession.Capture(request);
        if (texture == null)
        {
            throw new InvalidOperationException("Photoshoot image was not captured.");
        }

        texture.name = $"MCB Photoshoot {GetPhotoshootShotDisplayName(shotKind)}";
        if (shotKind == PhotoshootGenerationService.ShotKind.Thumbnail)
        {
            if (isEditingSelectedAssetMedia)
            {
                SetEditThumbnail(texture);
            }
            else
            {
                SetCreateThumbnail(texture);
            }
        }
        else
        {
            if (isEditingSelectedAssetMedia)
            {
                SetEditBanner(texture);
            }
            else
            {
                SetCreateBanner(texture);
            }
        }

        return $"{request.width}x{request.height}";
    }

    private PhotoshootGenerationService.RenderRequest BuildPhotoshootRequest(
        PhotoshootGenerationService.ShotKind shotKind,
        bool preview,
        bool forceFaceBlendshapeApply = false)
    {
        EnsurePhotoshootCatalog();
        Vector2Int size = GetPhotoshootRenderSize(shotKind, preview);

        var bodyPose = photoshootCatalog.bodyPoses.Count > 0
            ? photoshootCatalog.bodyPoses[photoshootBodyPoseIndex].clip
            : null;
        var background = photoshootCatalog.backgrounds.Count > 0
            ? photoshootCatalog.backgrounds[photoshootBackgroundIndex].texture
            : null;
        var lightPreset = photoshootCatalog.lightPresets.Count > 0
            ? photoshootCatalog.lightPresets[photoshootLightPresetIndex]
            : null;

        return new PhotoshootGenerationService.RenderRequest
        {
            avatarRoot = GetCurrentPhotoshootAvatarRoot(),
            bodyPose = bodyPose,
            background = background,
            lightPreset = lightPreset,
            selectedFaceBlendshapeNames = photoshootSelectedFaceBlendshapes.ToArray(),
            forceFaceBlendshapeApply = forceFaceBlendshapeApply,
            shotKind = shotKind,
            zoom = photoshootZoom,
            placement = photoshootPlacement,
            avatarYawDegrees = photoshootRotationDegrees,
            width = size.x,
            height = size.y
        };
    }

    private static Vector2Int GetPhotoshootRenderSize(PhotoshootGenerationService.ShotKind shotKind, bool preview)
    {
        if (shotKind == PhotoshootGenerationService.ShotKind.Banner)
        {
            return preview ? new Vector2Int(768, 432) : new Vector2Int(1600, 900);
        }

        return new Vector2Int(512, 512);
    }

    private static string GetPhotoshootShotDisplayName(PhotoshootGenerationService.ShotKind shotKind)
    {
        return shotKind == PhotoshootGenerationService.ShotKind.Banner ? "Banner" : "Thumbnail";
    }

    private void SetCreateThumbnail(Texture2D texture)
    {
        ReplaceTextureField(ref createThumbnail, texture);
    }

    private void SetCreateBanner(Texture2D texture)
    {
        ReplaceTextureField(ref createBanner, texture);
    }

    private void SetEditThumbnail(Texture2D texture)
    {
        ReplaceTextureField(ref editThumbnail, texture);
    }

    private void SetEditBanner(Texture2D texture)
    {
        ReplaceTextureField(ref editBanner, texture);
    }

    private static void ReplaceTextureField(ref Texture2D field, Texture2D texture)
    {
        if (field == texture)
        {
            return;
        }

        DestroyTransientTexture(field);
        field = texture;
    }

    private static void ClearTextureField(ref Texture2D field)
    {
        DestroyTransientTexture(field);
        field = null;
    }

    private static void DestroyTransientTexture(Texture2D texture)
    {
        if (texture == null || !string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(texture)))
        {
            return;
        }

        UnityEngine.Object.DestroyImmediate(texture);
    }

    private void ReleasePhotoshootPreviewTexture()
    {
        ClosePhotoshootPreviewSession();
    }

    private void ClosePhotoshootPreviewSession()
    {
        if (photoshootPreviewSession == null)
        {
            return;
        }

        photoshootPreviewSession.Dispose();
        photoshootPreviewSession = null;
    }

    private void ReleasePhotoshootIconCache()
    {
        foreach (var texture in photoshootPoseIconCache.Values.Concat(photoshootLightIconCache.Values))
        {
            if (texture != null)
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        photoshootPoseIconCache.Clear();
        photoshootLightIconCache.Clear();
    }

    private void ResetPhotoshootState(bool destroyPreviewTexture)
    {
        photoshootStateVersion++;
        photoshootLivePreviewRefreshTicket++;
        photoshootCatalog = null;
        photoshootBodyPoseIndex = 0;
        photoshootBackgroundIndex = 0;
        photoshootLightPresetIndex = 0;
        photoshootZoom = 1.35f;
        photoshootPlacement = Vector2.zero;
        photoshootRotationDegrees = 0f;
        photoshootSelectedFaceBlendshapes.Clear();
        photoshootStatus = null;
        photoshootError = null;
        isGeneratingPhotoshootImage = false;
        ClearPhotoshootUiReferences();
        ReleasePhotoshootIconCache();
        if (destroyPreviewTexture)
        {
            ReleasePhotoshootPreviewTexture();
        }
    }

    private IEnumerator UpdateSelectedAssetMediaCoroutine()
    {
        if (isSavingSelectedAssetMedia ||
            !CanEditSelectedAssetMedia(SelectedAsset) ||
            !HasPendingSelectedAssetMediaEdit())
        {
            yield break;
        }

        int assetId = SelectedAsset.id;
        isSavingSelectedAssetMedia = true;
        selectedAssetMediaEditError = null;
        editor.Repaint();

        var form = new WWWForm();
        AddImageToForm(form, "thumbnail", editThumbnail);
        AddImageToForm(form, "banner", editBanner);

        string url = $"{MCBUtils.getApiUrl()}/assets/{assetId}/media?t={editor.authToken}";
        using (var request = UnityWebRequest.Post(url, form))
        {
            if (!string.IsNullOrEmpty(editor.authToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {editor.authToken}");
            }

            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.Upload);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Update asset media"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                selectedAssetMediaEditError = ExtractErrorMessage(request.downloadHandler?.text) ??
                                              $"Failed to update asset media: HTTP {request.responseCode} {request.error}";
            }
            else
            {
                try
                {
                    var response = JsonConvert.DeserializeObject<CreateCustomBaseAssetResponse>(request.downloadHandler.text);
                    if (response?.asset == null || response.asset.id != assetId)
                    {
                        throw new InvalidOperationException("The server did not return the updated asset media.");
                    }

                    ApplySelectedAssetMediaUpdate(response.asset);
                    ResetSelectedAssetMediaEditState(destroyPreviewTexture: true);
                    selectedAssetBannerFrame = null;
                    selectedAssetBannerImage = null;
                    selectedAssetBannerMessage = null;
                    selectedAssetBannerAssetId = 0;
                }
                catch (Exception ex)
                {
                    selectedAssetMediaEditError = $"Failed to parse updated asset media: {ex.Message}";
                }
            }
        }

        isSavingSelectedAssetMedia = false;
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }
}
#endif
