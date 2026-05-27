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
    private void BuildCommentsUIToolkit()
    {
        if (commentsRoot == null)
        {
            return;
        }

        commentsRoot.Clear();
        commentsRoot.AddToClassList("mcb-comments");
        if (!ShouldShowSelectedAssetInteractionUi())
        {
            commentsRoot.style.display = DisplayStyle.None;
            return;
        }

        commentsRoot.style.display = DisplayStyle.Flex;

        var state = EnsureInteractionLoad(SelectedAsset.id);
        var titleRow = CreateRow();
        titleRow.AddToClassList("mcb-comments__title-row");
        var title = CreateLabel("Comments", 13, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-comments__title");
        titleRow.Add(title);
        if (state != null)
        {
            var count = CreateLabel(state.commentCount.ToString(), 12, FontStyle.Bold, new Color(0.82f, 0.82f, 0.82f));
            count.AddToClassList("mcb-comments__count");
            titleRow.Add(count);
        }
        commentsRoot.Add(titleRow);

        if (state != null && state.isLoading)
        {
            commentsRoot.Add(CreateMessageLabel("Loading comments...", new Color(0.70f, 0.78f, 0.86f)));
        }
        else if (state == null || state.comments.Count == 0)
        {
            commentsRoot.Add(CreateMessageLabel("No comments yet.", new Color(0.62f, 0.62f, 0.62f)));
        }
        else
        {
            foreach (var comment in state.comments.OrderBy(comment => comment.createdAt ?? string.Empty))
            {
                commentsRoot.Add(CreateCommentRowUIToolkit(comment));
            }
        }

        Button submit = null;
        var input = new TextField { multiline = true, value = commentDraft };
        input.AddToClassList("mcb-comment-input");
        input.RegisterValueChangedCallback(evt =>
        {
            commentDraft = evt.newValue ?? "";
            submit?.SetEnabled(!isPostingComment && !string.IsNullOrWhiteSpace(commentDraft));
        });
        input.SetEnabled(!isPostingComment);
        commentsRoot.Add(input);

        submit = CreateTextButton(isPostingComment ? "Posting..." : "Post Comment", PostSelectedAssetComment);
        submit.style.marginTop = 8f;
        submit.style.width = 130f;
        submit.SetEnabled(!isPostingComment && !string.IsNullOrWhiteSpace(commentDraft));
        commentsRoot.Add(submit);
    }

    private VisualElement CreateCommentRowUIToolkit(InteractionRecord comment)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-comment");

        var avatar = new VisualElement();
        avatar.AddToClassList("mcb-comment__avatar");

        var avatarImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        if (comment != null && comment.fromUserId > 0)
        {
            avatarImage.image = UserService.GetUserAvatar(comment.fromUserId);
        }
        avatarImage.AddToClassList("mcb-comment__avatar-image");
        avatar.Add(avatarImage);
        row.Add(avatar);

        var body = new VisualElement();
        body.AddToClassList("mcb-comment__body");
        string userName = comment?.fromUsername;
        if (comment != null && comment.fromUserId > 0)
        {
            var info = UserService.GetUserInfo(comment.fromUserId);
            if (info != null && !string.IsNullOrWhiteSpace(info.username))
            {
                userName = info.username;
            }
        }

        var header = CreateRow();
        header.AddToClassList("mcb-comment__header");

        var author = CreateLabel(string.IsNullOrWhiteSpace(userName) ? "Unknown user" : userName, 12, FontStyle.Bold, Color.white);
        author.AddToClassList("mcb-comment__author");
        header.Add(author);

        bool isOwnComment = comment != null && comment.fromUserId > 0 && comment.fromUserId == GetCurrentUserId();
        if (isOwnComment)
        {
            var actions = CreateRow();
            actions.AddToClassList("mcb-comment__actions");

            var editButton = CreateIconOnlyButton(MCBInteractionIconKind.Edit, "Edit comment", () => BeginEditComment(comment));
            editButton.SetEnabled(!isUpdatingComment && deletingCommentId == 0);
            actions.Add(editButton);

            var deleteButton = CreateIconOnlyButton(MCBInteractionIconKind.Delete, "Delete comment", () => DeleteSelectedAssetComment(comment));
            deleteButton.SetEnabled(!isUpdatingComment && deletingCommentId != comment.id);
            actions.Add(deleteButton);

            header.Add(actions);
        }

        body.Add(header);

        if (comment != null && editingCommentId == comment.id) 
        {
            Button saveButton = null;
            var editInput = new TextField { multiline = true, value = editingCommentDraft };
            editInput.AddToClassList("mcb-comment-edit-input");
            editInput.RegisterValueChangedCallback(evt =>
            {
                editingCommentDraft = evt.newValue ?? "";
                saveButton?.SetEnabled(!isUpdatingComment && !string.IsNullOrWhiteSpace(editingCommentDraft));
            });
            editInput.SetEnabled(!isUpdatingComment);
            body.Add(editInput);

            var editActions = CreateRow();
            editActions.AddToClassList("mcb-comment__edit-actions");
            saveButton = CreateTextButton(isUpdatingComment ? "Saving..." : "Save", () => UpdateSelectedAssetComment(comment.id));
            saveButton.SetEnabled(!isUpdatingComment && !string.IsNullOrWhiteSpace(editingCommentDraft));
            editActions.Add(saveButton);

            var cancelButton = CreateTextButton("Cancel", CancelEditComment);
            cancelButton.SetEnabled(!isUpdatingComment);
            editActions.Add(cancelButton);
            body.Add(editActions);
        }
        else
        {
            var content = CreateLabel(comment != null && !string.IsNullOrWhiteSpace(comment.content) ? comment.content : "(empty comment)", 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
            content.AddToClassList("mcb-comment__content");
            body.Add(content);
        }

        row.Add(body);
        return row;
    }

    private bool ShouldShowSelectedAssetInteractionUi()
    {
        return editor.isAuthenticated &&
               editor.HasServerAccess &&
               !MCBPackageVersionService.RequiresMajorUpdate &&
               !isEditingSelectedAssetMedia &&
               SelectedAsset != null;
    }

    private AssetInteractionState EnsureInteractionLoad(int assetId)
    {
        var state = GetInteractionState(assetId, true);
        if (state == null || state.loadAttempted || state.isLoading || string.IsNullOrWhiteSpace(editor.authToken))
        {
            return state;
        }

        state.loadAttempted = true;
        state.isLoading = true;
        state.error = null;
        int currentUserId = GetCurrentUserId();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.LoadAssetInteractionsCoroutine(
                assetId,
                editor.authToken,
                currentUserId,
                (response, error) =>
                {
                    state.isLoading = false;
                    state.error = error;
                    if (response != null)
                    {
                        if (!HasActiveLikeSync(state))
                        {
                            state.likeCount = response.likeCount;
                            state.likedByCurrentUser = response.likedByCurrentUser;
                            state.desiredLikedByCurrentUser = response.likedByCurrentUser;
                            state.currentUserLikeId = response.currentUserLikeId ?? 0;
                        }
                        else if (response.currentUserLikeId.HasValue && response.currentUserLikeId.Value > 0)
                        {
                            state.currentUserLikeId = response.currentUserLikeId.Value;
                        }

                        state.commentCount = response.commentCount;
                        state.comments = response.interactions
                            .Where(item => item != null && string.Equals(item.type, InteractionTypes.COMMENT, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));

        return state;
    }

    private AssetInteractionState GetInteractionState(int assetId, bool create)
    {
        if (assetId <= 0)
        {
            return null;
        }

        if (!interactionStates.TryGetValue(assetId, out var state) && create)
        {
            state = new AssetInteractionState();
            interactionStates[assetId] = state;
        }

        return state;
    }

    private void ToggleSelectedAssetLike()
    {
        if (SelectedAsset == null)
        {
            return;
        }

        int assetId = SelectedAsset.id;
        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null || state.isLoading)
        {
            return;
        }

        bool nextLiked = !state.likedByCurrentUser;
        ApplyOptimisticLikeState(state, nextLiked);
        state.desiredLikedByCurrentUser = nextLiked;
        state.pendingLikeSync = true;
        state.error = null;
        UpdateSelectedAssetLikeVisual(assetId, state);
        UpdateLikeCountLabels(assetId, state.likeCount);
        editor.Repaint();

        if (!state.isLikeSyncRunning)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(SyncLikeQueueCoroutine(assetId, state));
        }
    }

    private IEnumerator SyncLikeQueueCoroutine(int assetId, AssetInteractionState state)
    {
        if (state == null || state.isLikeSyncRunning)
        {
            yield break;
        }

        state.isLikeSyncRunning = true;
        while (state.pendingLikeSync)
        {
            state.pendingLikeSync = false;
            bool targetLiked = state.desiredLikedByCurrentUser;

            if (targetLiked)
            {
                if (state.currentUserLikeId <= 0)
                {
                    InteractionRecord createdInteraction = null;
                    string error = null;
                    var payload = new CreateInteractionRequest
                    {
                        toAsset = assetId,
                        type = InteractionTypes.LIKE
                    };

                    yield return InteractionService.CreateInteractionCoroutine(
                        payload,
                        editor.authToken,
                        (interaction, createError) =>
                        {
                            createdInteraction = interaction;
                            error = createError;
                        });

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                        ReconcileLikeStateWithServer(state);
                        break;
                    }

                    state.currentUserLikeId = createdInteraction != null ? createdInteraction.id : 0;
                }
            }
            else if (state.currentUserLikeId > 0)
            {
                int interactionId = state.currentUserLikeId;
                string error = null;
                yield return InteractionService.DeleteInteractionCoroutine(
                    interactionId,
                    editor.authToken,
                    deleteError => error = deleteError);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    state.error = error;
                    ReconcileLikeStateWithServer(state);
                    break;
                }

                if (state.currentUserLikeId == interactionId)
                {
                    state.currentUserLikeId = 0;
                }
            }

            if (state.desiredLikedByCurrentUser != (state.currentUserLikeId > 0))
            {
                state.pendingLikeSync = true;
            }
        }

        state.isLikeSyncRunning = false;
        UpdateSelectedAssetLikeVisual(assetId, state);
        UpdateLikeCountLabels(assetId, state.likeCount);
        if (!string.IsNullOrWhiteSpace(state.error))
        {
            editor.RefreshUiToolkitSections();
        }
        else
        {
            editor.Repaint();
        }
    }

    private void UpdateSelectedAssetLikeVisual(int assetId, AssetInteractionState state)
    {
        if (SelectedAsset == null ||
            SelectedAsset.id != assetId ||
            selectedAssetLikeButton == null ||
            selectedAssetLikeCountLabel == null)
        {
            return;
        }

        bool liked = state != null && state.likedByCurrentUser;
        selectedAssetLikeCountLabel.text = state != null ? state.likeCount.ToString() : "0";
        selectedAssetLikeButton.tooltip = liked ? "Unlike asset" : "Like asset";
        selectedAssetLikeButton.EnableInClassList("mcb-selected-banner__like-button--liked", liked);
        selectedAssetLikeButton.SetEnabled(state != null && !state.isLoading);
    }

    private void UpdateLikeCountLabels(int assetId, int likeCount)
    {
        if (likeCountLabels.TryGetValue(assetId, out var label) && label != null)
        {
            label.text = likeCount.ToString();
        }
    }

    private static bool HasActiveLikeSync(AssetInteractionState state)
    {
        return state != null && (state.pendingLikeSync || state.isLikeSyncRunning);
    }

    private static void ApplyOptimisticLikeState(AssetInteractionState state, bool liked)
    {
        if (state == null || state.likedByCurrentUser == liked)
        {
            return;
        }

        state.likeCount = Mathf.Max(0, state.likeCount + (liked ? 1 : -1));
        state.likedByCurrentUser = liked;
    }

    private static void ReconcileLikeStateWithServer(AssetInteractionState state)
    {
        if (state == null)
        {
            return;
        }

        bool serverLiked = state.currentUserLikeId > 0;
        ApplyOptimisticLikeState(state, serverLiked);
        state.desiredLikedByCurrentUser = serverLiked;
        state.pendingLikeSync = false;
    }

    private void PostSelectedAssetComment()
    {
        if (SelectedAsset == null || isPostingComment || string.IsNullOrWhiteSpace(commentDraft))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        isPostingComment = true;
        state.error = null;
        string commentText = commentDraft.Trim();
        editor.RefreshUiToolkitSections();

        var payload = new CreateInteractionRequest
        {
            toAsset = SelectedAsset.id,
            type = InteractionTypes.COMMENT,
            content = commentText
        };

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.CreateInteractionCoroutine(
                payload,
                editor.authToken,
                (interaction, error) =>
                {
                    isPostingComment = false;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else
                    {
                        commentDraft = "";
                        if (interaction != null)
                        {
                            state.comments.Add(interaction);
                        }
                        state.commentCount += 1;
                    }
                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private void BeginEditComment(InteractionRecord comment)
    {
        if (comment == null || comment.fromUserId != GetCurrentUserId())
        {
            return;
        }

        editingCommentId = comment.id;
        editingCommentDraft = comment.content ?? "";
        editor.RefreshUiToolkitSections();
    }

    private void CancelEditComment()
    {
        editingCommentId = 0;
        editingCommentDraft = "";
        editor.RefreshUiToolkitSections();
    }

    private void UpdateSelectedAssetComment(int commentId)
    {
        if (SelectedAsset == null ||
            commentId <= 0 ||
            editingCommentId != commentId ||
            isUpdatingComment ||
            string.IsNullOrWhiteSpace(editingCommentDraft))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        string nextContent = editingCommentDraft.Trim();
        isUpdatingComment = true;
        state.error = null;
        editor.RefreshUiToolkitSections();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.UpdateInteractionCoroutine(
                commentId,
                nextContent,
                editor.authToken,
                (interaction, error) =>
                {
                    isUpdatingComment = false;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else if (interaction != null)
                    {
                        int index = state.comments.FindIndex(comment => comment != null && comment.id == interaction.id);
                        if (index >= 0)
                        {
                            state.comments[index] = interaction;
                        }

                        editingCommentId = 0;
                        editingCommentDraft = "";
                    }

                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private void DeleteSelectedAssetComment(InteractionRecord comment)
    {
        if (SelectedAsset == null || comment == null || comment.id <= 0 || comment.fromUserId != GetCurrentUserId())
        {
            return;
        }

        if (!EditorUtility.DisplayDialog("Delete Comment", "Delete this comment permanently?", "Delete", "Cancel"))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        deletingCommentId = comment.id;
        state.error = null;
        editor.RefreshUiToolkitSections();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.DeleteInteractionCoroutine(
                comment.id,
                editor.authToken,
                error =>
                {
                    deletingCommentId = 0;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else
                    {
                        state.comments.RemoveAll(item => item != null && item.id == comment.id);
                        state.commentCount = Mathf.Max(0, state.commentCount - 1);
                        if (editingCommentId == comment.id)
                        {
                            editingCommentId = 0;
                            editingCommentDraft = "";
                        }
                    }

                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private int GetCurrentUserId()
    {
        var auth = AuthenticationService.GetAuth();
        if (auth == null || string.IsNullOrWhiteSpace(auth.user))
        {
            return 0;
        }

        return int.TryParse(auth.user, out var userId) ? userId : 0;
    }
}
#endif
