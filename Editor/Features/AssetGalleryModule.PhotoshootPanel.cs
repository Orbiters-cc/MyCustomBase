#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Orbiters.Toolkit.Editor.Photoshoot;

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
                              !photoshoot.IsGenerating &&
                              CanEditSelectedAssetMedia(SelectedAsset) &&
                              HasPendingSelectedAssetMediaEdit());
        root.Add(saveButton);
    }

    private void BuildPhotoshootSectionUIToolkit(VisualElement root, bool includeBackButton = false)
    {
        root.Add(new Orbiters.Toolkit.Editor.Photoshoot.PhotoshootPanel(photoshoot, new PhotoshootOptions
        {
            AvatarRoot = GetCurrentPhotoshootAvatarRoot,
            IncludeBanner = true,
            ThumbnailSize = new Vector2Int(512, 512),
            BannerSize = new Vector2Int(1600, 900),
            // The server blurs and fades MCB banners itself (the same effect for every upload, web or Unity).
            ServerAppliesBannerEffect = true,
            CanGenerate = () => !isSubmittingCustomBase && !isSavingSelectedAssetMedia,
            InputBlocked = () => isSubmittingCustomBase || isSavingSelectedAssetMedia,
            GetShot = GetPhotoshootAssignedTexture,
            SetShot = SetPhotoshootShotTexture,
            Back = includeBackButton ? CancelSelectedAssetMediaEdit : (Action)null,
            Changed = () =>
            {
                editor.RefreshUiToolkitSections();
                editor.Repaint();
            },
            Repaint = () =>
            {
                galleryRoot?.MarkDirtyRepaint();
                editor.Repaint();
            }
        }));
    }

    private GameObject GetCurrentPhotoshootAvatarRoot()
    {
        return editor?.customBaseTarget != null
            ? editor.customBaseTarget.transform.root.gameObject
            : null;
    }

    private bool HasPendingSelectedAssetMediaEdit()
    {
        return editThumbnail != null || editBanner != null;
    }

    private Texture2D GetPhotoshootAssignedTexture(PhotoshootService.ShotKind shotKind)
    {
        if (shotKind == PhotoshootService.ShotKind.Thumbnail)
        {
            return isEditingSelectedAssetMedia ? editThumbnail : createThumbnail;
        }

        return isEditingSelectedAssetMedia ? editBanner : createBanner;
    }

    private void SetPhotoshootShotTexture(PhotoshootService.ShotKind shotKind, Texture2D texture)
    {
        if (shotKind == PhotoshootService.ShotKind.Thumbnail)
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

    private void ResetPhotoshootState(bool destroyPreviewTexture)
    {
        photoshoot.Reset(destroyPreviewTexture);
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

        var form = new List<IMultipartFormSection>();
        AddImageToForm(form, "thumbnail", editThumbnail);
        AddImageToForm(form, "banner", editBanner);

        string url = $"{MCBUtils.getApiUrl()}/assets/{assetId}/media";
        using (var request = UnityWebRequest.Post(url, form))
        {
            MCBRequestHeaders.SetAuthorization(request, editor.authToken);

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
