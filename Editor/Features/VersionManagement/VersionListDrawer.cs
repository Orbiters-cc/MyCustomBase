
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public partial class VersionListDrawer
{
    private readonly MCBEditor editor;
    private readonly VersionActions actions;
    private readonly NetworkService networkService = new NetworkService();
    
    // UI State
    private static readonly Color AccentGreen = new Color32(0, 218, 109, 255);
    private static bool displayAllChangelogs = false;
    private static Dictionary<string, bool> individualChangelogStates = new Dictionary<string, bool>();
    private static bool isListCollapsed = true;
    private static string editingVersionKey;
    private static string editingVersionTitleDraft = "";
    private static string editingVersionChangelogDraft = "";
    private static bool isUpdatingVersionMetadata;
    private static string versionMetadataUpdateError;
    
    // Special reset version identifier
    public static readonly CustomBaseVersion RESET_VERSION = new CustomBaseVersion
    {
        version = "Base Default",
        scope = Scope.PUBLIC,
        changelog = "Reset to the original avatar configuration without any custom base modifications."
    };
    
    // Cached textures for collapse/expand icons
    private static Texture2D collapseIcon;
    private static Texture2D expandIcon;

    public VersionListDrawer(MCBEditor editor, VersionActions actions)
    {
        this.editor = editor;
        this.actions = actions;
        LoadIcons();
    }
    
    private void LoadIcons()
    {
        if (collapseIcon == null)
            collapseIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/collapse.png");
        if (expandIcon == null)
            expandIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/expand.png");
    }

    public void Draw()
    {
        DrawHeader();
        
        // Warn about unsupported custom base (behind feature flag)
        if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION))
        {
            if (editor.currentIsCustom && !editor.customWarningShown)
            {
                EditorGUILayout.HelpBox("The current custom version of your avatar base is not supported. If you update it to one of the custom base versions, you may lose some custom features or blendshapes of your custom base.", MessageType.Warning);
                editor.customWarningShown = true;
            }
        }
        
        var allVersions = editor.GetAllVersions();
        
        if (allVersions.Any() || editor.isFetching)
        {
            if (!editor.isFetching)
            {
                // Preload user info for all versions
                UserService.PreloadUserInfo(allVersions);
                
                if (allVersions.Count > 0)
                {
                    // Draw first version
                    DrawVersionListItem(allVersions[0], true, false);
                    
                    // Draw collapse/expand button after first item (only if there are more than 2 items total including reset)
                    if (allVersions.Count > 1)
                    {
                        DrawCollapseExpandButton(false, false);
                    }
                    
                    // Draw middle versions (only if not collapsed and there are middle items)
                    if (!isListCollapsed && allVersions.Count > 1)
                    {
                        for (var i = 1; i < allVersions.Count; i++)
                        {
                            DrawVersionListItem(allVersions[i], false, false);
                        }
                    }
                }
                
                // Draw user custom versions above reset row (behind feature flag)
                if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION))
                {
                    DrawUserCustomRows();
                }
        
                // Draw reset item as the last item
                DrawResetVersionItem(allVersions.Count == 0 && (!FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) || editor.userCustomVersions == null || editor.userCustomVersions.Count == 0));
            }
            else
            {
                EditorGUILayout.LabelField("Fetching version list...", EditorStyles.centeredGreyMiniLabel);
            }
        }
        else if (!editor.isFetching)
        {
            if (string.IsNullOrEmpty(editor.currentBaseFbxHash))
                EditorGUILayout.HelpBox("Assign or detect a Base FBX first.", MessageType.Info);
            else if (editor.fetchAttempted)
                EditorGUILayout.HelpBox("No compatible versions found for the detected Base FBX hash.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Click 'Check for Updates' to find compatible versions.", MessageType.Info);
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal();
        
        EditorGUILayout.LabelField($"{editor.GetSelectedAssetDisplayName()} Versions", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        
        EditorGUI.BeginChangeCheck();
        displayAllChangelogs = EditorGUILayout.Toggle("Display all changelogs", displayAllChangelogs);
        if (EditorGUI.EndChangeCheck())
        {
            // Clear individual states when switching modes
            individualChangelogStates.Clear();
        }
        
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(5);
    }
    
    private void DrawCollapseExpandButton(bool isFirst, bool isLast)
    {
        // Mark the start of the entire button area
        Rect buttonItemStartRect = GUILayoutUtility.GetRect(0, 0);
        
        // Start the button item layout
        EditorGUILayout.BeginVertical();
        
        // Add space above button
        GUILayout.Space(2);
        
        EditorGUILayout.BeginHorizontal();
        
        // Reserve space for timeline - this will span the entire button height
        Rect timelineRect = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f));
        
        // Right side content (the clickable area) - aligned to left
        EditorGUILayout.BeginVertical();
        
        EditorGUILayout.BeginHorizontal();
        
        Texture2D iconToUse = isListCollapsed ? expandIcon : collapseIcon;
        string labelText = isListCollapsed ? "expand all versions" : "collapse all versions";
        
        // Draw icon and text aligned to left, vertically centered
        if (iconToUse != null)
        {
            GUILayout.Label(iconToUse, GUILayout.Width(22), GUILayout.Height(22));
            GUILayout.Space(8);
        }
        
        // Center the label vertically with the icon
        EditorGUILayout.BeginVertical();
        GUILayout.FlexibleSpace();
        GUILayout.Label(labelText, EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndVertical();
        
        GUILayout.FlexibleSpace(); // Push everything to the left
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();
        
        // Add space below button
        GUILayout.Space(2);
        
        EditorGUILayout.EndVertical();
        
        // Get the rect for the entire button item after all layout is complete
        Rect buttonItemEndRect = GUILayoutUtility.GetRect(0, 0);
        Rect fullButtonItemRect = new Rect(buttonItemStartRect.x, buttonItemStartRect.y, 
                                          buttonItemStartRect.width, buttonItemEndRect.y - buttonItemStartRect.y);
        
        // Handle click on the entire button area
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && fullButtonItemRect.Contains(Event.current.mousePosition))
        {
            isListCollapsed = !isListCollapsed;
            Event.current.Use();
            editor.Repaint();
        }
        
        // Change cursor to pointer when hovering over the clickable area
        if (Event.current.type == EventType.Repaint && fullButtonItemRect.Contains(Event.current.mousePosition))
        {
            EditorGUIUtility.AddCursorRect(fullButtonItemRect, MouseCursor.Link);
        }
        
        // Draw the timeline graphics
        if (Event.current.type == EventType.Repaint)
        {
            Color timelineColor = AccentGreen;
            float centerX = timelineRect.center.x;
            float lineWidth = 2f;
            
            if (!isFirst)
            {
                Handles.color = timelineColor;
                if (isListCollapsed)
                {
                    // Draw dotted line when collapsed
                    Vector3 startPoint = new Vector3(centerX, fullButtonItemRect.yMin, 0);
                    Vector3 endPoint = new Vector3(centerX, fullButtonItemRect.yMax, 0);
                    DrawDottedLine(startPoint, endPoint, lineWidth);
                }
                else
                {
                    // Draw solid line when expanded
                    Handles.DrawAAPolyLine(lineWidth, new Vector3(centerX, fullButtonItemRect.yMin, 0), new Vector3(centerX, fullButtonItemRect.yMax, 0));
                }
            }
        }
    }
    
    private void DrawUserCustomRows()
    {
        if (editor.userCustomVersions == null || editor.userCustomVersions.Count == 0)
            return;

        var entries = editor.userCustomVersions;
        var allVersions = editor.GetAllVersions();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool isApplied = editor.currentIsCustom && !string.IsNullOrEmpty(editor.currentAppliedFbxHash) &&
                             string.Equals(editor.currentAppliedFbxHash, entry.appliedUserAviHash, StringComparison.OrdinalIgnoreCase);
            bool isSelected = editor.selectedCustomVersionForAction == entry;

            bool isFirst = (allVersions == null || allVersions.Count == 0) && i == 0; // first overall if no normal versions drawn
            bool isLast = false; // reset item follows after custom rows

            System.Action onSelected = () =>
            {
                if (isSelected)
                {
                    editor.selectedCustomVersionForAction = null;
                }
                else
                {
                    editor.selectedCustomVersionForAction = entry;
                    editor.selectedVersionForAction = null;
                }
            };

            DrawVersionItemInternal(
                ver: null,
                isFirst: isFirst,
                isLast: isLast,
                isSelected: isSelected,
                isApplied: isApplied,
                drawContent: () =>
                {
                    // Match normal item layout: left title, middle chips, right actions
                    // Title
                    EditorGUILayout.BeginVertical();
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label($"Custom {entry.detectionDate}", GUILayout.Width(140));
                    EditorGUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndVertical();

                    GUILayout.FlexibleSpace();

                    // Chips
                    EditorGUILayout.BeginVertical();
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.BeginHorizontal();
                    if (isApplied)
                    {
                        DrawScopeLabel("Installed", AccentGreen);
                        GUILayout.Space(5);
                        DrawScopeLabel("Current", AccentGreen);
                        GUILayout.Space(10);
                    }
                    DrawScopeLabel("Custom", Color.red);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndVertical();

                    // Right-side action icons (delete)
                    using (new EditorGUI.DisabledScope(editor.isDownloading || editor.isDeleting))
                    {
                        EditorGUILayout.BeginVertical();
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.BeginHorizontal();

                        var deleteIcon = EditorGUIUtility.IconContent("TreeEditor.Trash");
                        Rect deleteRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));

                        if (Event.current.type == EventType.Repaint)
                        {
                            GUI.DrawTexture(deleteRect, deleteIcon.image);
                            if (deleteRect.Contains(Event.current.mousePosition))
                            {
                                EditorGUIUtility.AddCursorRect(deleteRect, MouseCursor.Link);
                            }
                        }

                        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && deleteRect.Contains(Event.current.mousePosition))
                        {
                            bool confirm = EditorUtility.DisplayDialog(
                                "Delete Unknown Version",
                                "Are you sure you want to delete this unknown version. This might be another unsupported edit that you might want to backup. This action is irreversible.",
                                "Delete",
                                "Cancel");
                            if (confirm)
                            {
                                // Perform deletion
                                bool ok = UserCustomVersionService.Instance.Delete(entry);
                                if (ok)
                                {
                                    if (editor.selectedCustomVersionForAction == entry)
                                        editor.selectedCustomVersionForAction = null;
                                    // Reload entries from service cache
                                    editor.userCustomVersions = UserCustomVersionService.Instance.GetAll();
                                    editor.Repaint();
                                }
                            }
                            Event.current.Use();
                        }

                        EditorGUILayout.EndHorizontal();
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndVertical();
                    }
                },
                disabledReason: null,
                onSelected: onSelected,
                changelogKeyOverride: null,
                changelogTextOverride: null
            );
        }
    }

    private void DrawDottedLine(Vector3 start, Vector3 end, float lineWidth)
    {
        float distance = Vector3.Distance(start, end);
        float dashLength = 4f;
        float gapLength = 3f;
        float totalSegmentLength = dashLength + gapLength;
        int segments = Mathf.FloorToInt(distance / totalSegmentLength);
        
        Vector3 direction = (end - start).normalized;
        Vector3 currentPos = start;
        
        for (int i = 0; i < segments; i++)
        {
            Vector3 dashEnd = currentPos + direction * dashLength;
            Handles.DrawAAPolyLine(lineWidth, currentPos, dashEnd);
            currentPos = dashEnd + direction * gapLength;
        }
        
        // Draw remaining partial dash if any
        if (Vector3.Distance(currentPos, end) > 0.1f)
        {
            Handles.DrawAAPolyLine(lineWidth, currentPos, end);
        }
    }

    private void DrawVersionListItem(CustomBaseVersion ver, bool isFirst, bool isLast)
    {
        string versionFolderPath = MCBUtils.GetVersionDataPath(ver);
        bool hasLocalContent = !string.IsNullOrEmpty(versionFolderPath) && Directory.Exists(Path.GetFullPath(versionFolderPath));
        bool isSelected = ver.Equals(editor.selectedVersionForAction);
        bool isApplied = ver.Equals(editor.customBaseTarget.appliedCustomBaseVersion);

        DrawVersionItemInternal(ver, isFirst, isLast, isSelected, isApplied, () => {
            // Vertically center the version label and user info with the helpbox
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"{editor.GetSelectedAssetDisplayName()} {ver.version}", GUILayout.Width(180));
            
            // Draw user info if available and not unsubmitted
            if (ver.uploaderId > 0)
            {
                DrawUserInfo(ver.uploaderId);
            }
            EditorGUILayout.EndHorizontal();
            
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
            
            GUILayout.FlexibleSpace();
            
            // Vertically center the scope labels
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            
            if (ver.isUnsubmitted)
            {
                DrawScopeLabel("Unsubmitted", Color.gray);
                GUILayout.Space(5);
            }
            if (isApplied)
            {
                DrawScopeLabel("Installed", AccentGreen);
                GUILayout.Space(5);
            }
            DrawScopeLabel(ver.isImported ? "Imported" : ver.scope.ToString(), ver.isImported ? new Color(0.45f, 0.85f, 1f) : GetColorForScope(ver.scope));
            GUILayout.Space(10);
            
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
            
            // Action buttons as clickable icons
            using (new EditorGUI.DisabledScope(editor.isDownloading || editor.isDeleting))
            {
                DrawActionIcons(ver, hasLocalContent);
            }
        });
    }
    
    private void DrawUserInfo(int uploaderId)
    {
        var userInfo = UserService.GetUserInfo(uploaderId);
        var userAvatar = UserService.GetUserAvatar(uploaderId);

        GUILayout.Label("by", EditorStyles.miniLabel, GUILayout.Width(15));
        GUILayout.Space(3);

        Rect avatarRect = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18), GUILayout.Height(18));
        Color fallbackColor = new Color(0.23f, 0.23f, 0.23f);
        EditorUIUtils.DrawCircularAvatar(avatarRect, userAvatar, fallbackColor, new Color(0.46f, 0.46f, 0.46f), 1f);

        bool requestedThisFrame = false;

        if (userInfo == null)
        {
            UserService.RequestUserInfo(uploaderId, () => editor.Repaint());
            requestedThisFrame = true;
        }

        if (userAvatar == null)
        {
            if (userInfo != null && !string.IsNullOrEmpty(userInfo.username))
            {
                string initials = GetInitials(userInfo.username);
                var initialsStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
                GUI.Label(avatarRect, initials, initialsStyle);
            }

            if (!requestedThisFrame)
            {
                UserService.RequestUserInfo(uploaderId, () => editor.Repaint());
            }
        }

        GUILayout.Space(5);

        string displayName;
        if (userInfo != null && !string.IsNullOrEmpty(userInfo.username))
        {
            displayName = userInfo.username;
        }
        else if (userInfo != null)
        {
            displayName = $"User {uploaderId}";
        }
        else
        {
            displayName = "...";
        }

        GUILayout.Label(displayName, EditorStyles.miniLabel);
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        string[] parts = name.Split(new[] { ' ', '\t', '\n', '\r', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        string a = parts[0].Substring(0, 1);
        string b = parts[parts.Length - 1].Substring(0, 1);
        return (a + b).ToUpperInvariant();
    }

    private void DrawActionIcons(CustomBaseVersion ver, bool hasLocalContent)
    {
        EditorGUILayout.BeginVertical();
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        
        // Upload button (only show for unsubmitted versions)
        if (ver.isUnsubmitted)
        {
            var uploadIcon = EditorGUIUtility.IconContent("CloudConnect");
            Rect uploadRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
            
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(uploadRect, uploadIcon.image);
                if (uploadRect.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.AddCursorRect(uploadRect, MouseCursor.Link);
                }
            }
            
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && uploadRect.Contains(Event.current.mousePosition))
            {
                if (EditorUtility.DisplayDialog("Confirm Upload", $"Upload version {ver.version} to the server?\n\nThis action is irreversible.", "Upload", "Cancel"))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(editor.creatorModule.UploadUnsubmittedVersionCoroutine(ver));
                }
                Event.current.Use();
            }
        }
        
        // Changelog button (only show when not displaying all changelogs and changelog exists)
        if (!displayAllChangelogs && !string.IsNullOrEmpty(ver.changelog))
        {
            var changelogIcon = EditorGUIUtility.IconContent("UnityEditor.ConsoleWindow");
            Rect changelogRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
            
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(changelogRect, changelogIcon.image);
                if (changelogRect.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.AddCursorRect(changelogRect, MouseCursor.Link);
                }
            }
            
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && changelogRect.Contains(Event.current.mousePosition))
            {
                string changelogKey = $"{ver.version}_{ver.scope}";
                bool showingChangelog = individualChangelogStates.ContainsKey(changelogKey) && individualChangelogStates[changelogKey];
                individualChangelogStates[changelogKey] = !showingChangelog;
                Event.current.Use();
                editor.Repaint();
            }
        }
        
        if (hasLocalContent && editor.customBaseTarget != null && editor.customBaseTarget.isCreatorMode)
        {
            var exportIcon = EditorGUIUtility.IconContent("SaveAs");
            Rect exportRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));

            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(exportRect, exportIcon.image);
                if (exportRect.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.AddCursorRect(exportRect, MouseCursor.Link);
                }
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && exportRect.Contains(Event.current.mousePosition))
            {
                actions.ExportOfflineVersion(ver);
                Event.current.Use();
            }
        }

        // Download/Delete button
        bool showTrashIcon = hasLocalContent || ver.isUnsubmitted;
        var actionIcon = showTrashIcon ? EditorGUIUtility.IconContent("TreeEditor.Trash") : EditorGUIUtility.IconContent("Download-Available");
        Rect actionRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
        
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(actionRect, actionIcon.image);
            if (actionRect.Contains(Event.current.mousePosition))
            {
                EditorGUIUtility.AddCursorRect(actionRect, MouseCursor.Link);
            }
        }
        
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && actionRect.Contains(Event.current.mousePosition))
        {
            if (showTrashIcon)
            {
                string confirmMsg = ver.isUnsubmitted ? $"Delete unsubmitted version {ver.version}? This will remove it from the list." : $"Delete local files for version {ver.version}?";
                if (EditorUtility.DisplayDialog("Confirm Delete", confirmMsg, "Delete", "Cancel"))
                    actions.StartVersionDelete(ver);
            }
            else
            {
                actions.StartVersionDownload(ver, false);
            }
            Event.current.Use();
        }
        
        EditorGUILayout.EndHorizontal();
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndVertical();
    }
    
    private void DrawResetVersionItem(bool isFirst)
    {
        bool isSelected = RESET_VERSION.Equals(editor.selectedVersionForAction);
        var fileManagerService = new FileManagerService();
        bool canReset = fileManagerService.BackupExists(actions.GetCurrentFBXPath()) || editor.isCustomBase;
        bool isApplied = !editor.isCustomBase && !editor.currentIsCustom; // Reset is "applied" when we're not in custom base state and not in custom state

        DrawVersionItemInternal(RESET_VERSION, isFirst, true, isSelected, isApplied, () => {
            // Vertically center the reset label with the helpbox
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Base Default", GUILayout.Width(140));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
            
            GUILayout.FlexibleSpace();
            
            // Vertically center the current label
            if (isApplied)
            {
                EditorGUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                DrawScopeLabel("Current", AccentGreen);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                GUILayout.Space(10);
            }
            
            // Show changelog button if not displaying all changelogs
            if (!displayAllChangelogs && !string.IsNullOrEmpty(RESET_VERSION.changelog))
            {
                EditorGUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                
                var changelogIcon = EditorGUIUtility.IconContent("UnityEditor.ConsoleWindow");
                Rect changelogRect = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
                
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(changelogRect, changelogIcon.image);
                    if (changelogRect.Contains(Event.current.mousePosition))
                    {
                        EditorGUIUtility.AddCursorRect(changelogRect, MouseCursor.Link);
                    }
                }
                
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && changelogRect.Contains(Event.current.mousePosition))
                {
                    string changelogKey = "reset_base";
                    bool showingChangelog = individualChangelogStates.ContainsKey(changelogKey) && individualChangelogStates[changelogKey];
                    individualChangelogStates[changelogKey] = !showingChangelog;
                    Event.current.Use();
                    editor.Repaint();
                }
                
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
            }
            
        }, canReset ? null : "No backup available");
    }
    
    private void DrawVersionItemInternal(CustomBaseVersion ver, bool isFirst, bool isLast, bool isSelected, bool isApplied, System.Action drawContent, string disabledReason = null, System.Action onSelected = null, string changelogKeyOverride = null, string changelogTextOverride = null)
    {
        bool isDisabled = !string.IsNullOrEmpty(disabledReason);
        
        // Mark the start of the entire version item area
        Rect versionItemStartRect = GUILayoutUtility.GetRect(0, 0);
        
        // Start the whole version item layout
        EditorGUILayout.BeginVertical();
        
        // Add space above version box
        GUILayout.Space(2);
        
        EditorGUILayout.BeginHorizontal();
        
        // Reserve space for timeline - this will span the entire version item height
        Rect timelineRect = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f));
        
        // Right side content
        EditorGUILayout.BeginVertical();
        
        // Create custom GUIStyle for selected helpbox
        GUIStyle helpBoxStyle = new GUIStyle(EditorStyles.helpBox);
        if (isSelected && !isDisabled)
        {
            // Make selected version brighter
            Color originalBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(originalBg.r * 1.3f, originalBg.g * 1.3f, originalBg.b * 1.3f, originalBg.a);
        }
        else if (isDisabled)
        {
            // Make disabled versions grayed out
            GUI.backgroundColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        }
        
        using (new EditorGUI.DisabledScope(isDisabled))
        {
            EditorGUILayout.BeginVertical(helpBoxStyle);
            EditorGUILayout.BeginHorizontal();
            
            drawContent();
            
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        
        // Reset background color
        GUI.backgroundColor = Color.white;
        
        // Show changelog based on display mode (with optional overrides)
        bool shouldShowChangelog = false;
        string effectiveChangelog = changelogTextOverride ?? ver?.changelog;
        if (displayAllChangelogs)
        {
            shouldShowChangelog = !string.IsNullOrEmpty(effectiveChangelog);
        }
        else
        {
            string changelogKey = changelogKeyOverride ?? (ver == RESET_VERSION ? "reset_base" : $"{ver?.version}_{ver?.scope}");
            if (!string.IsNullOrEmpty(changelogKey))
            {
                shouldShowChangelog = individualChangelogStates.ContainsKey(changelogKey) && individualChangelogStates[changelogKey] && !string.IsNullOrEmpty(effectiveChangelog);
            }
        }

        if (shouldShowChangelog)
        {
            EditorGUILayout.LabelField(effectiveChangelog, EditorStyles.wordWrappedLabel);
        }
        
        if (isDisabled && !string.IsNullOrEmpty(disabledReason))
        {
            EditorGUILayout.LabelField(disabledReason, EditorStyles.miniLabel);
        }
        
        EditorGUILayout.EndVertical(); // End right side content
        EditorGUILayout.EndHorizontal(); // End main horizontal layout
        
        // Add space below version box
        GUILayout.Space(2);
        
        EditorGUILayout.EndVertical(); // End whole version item
        
        // Get the rect for the entire version item after all layout is complete
        Rect versionItemEndRect = GUILayoutUtility.GetRect(0, 0);
        Rect fullVersionItemRect = new Rect(versionItemStartRect.x, versionItemStartRect.y, 
                                           versionItemStartRect.width, versionItemEndRect.y - versionItemStartRect.y);
        
        // Handle dot interaction
        Vector2 dotCenter = new Vector2(timelineRect.center.x, fullVersionItemRect.center.y);
        float dotRadius = 4f;
        float clickableRadius = 8f;
        Rect dotClickArea = new Rect(dotCenter.x - clickableRadius, dotCenter.y - clickableRadius, 
                                    clickableRadius * 2, clickableRadius * 2);
        
        // Handle click on dot OR anywhere in the full version item area (only if not disabled)
        if (!isDisabled && Event.current.type == EventType.MouseDown && Event.current.button == 0)
        {
            bool clickedOnDot = dotClickArea.Contains(Event.current.mousePosition);
            bool clickedOnVersionArea = fullVersionItemRect.Contains(Event.current.mousePosition);
            
            if (clickedOnDot || clickedOnVersionArea)
            {
                if (onSelected != null)
                {
                    onSelected();
                }
                else
                {
                    if (isSelected)
                    {
                        editor.selectedVersionForAction = null;
                        editor.selectedCustomVersionForAction = null;
                    }
                    else
                    {
                        editor.selectedVersionForAction = ver;
                        editor.selectedCustomVersionForAction = null;
                    }
                }
                Event.current.Use();
                editor.Repaint();
            }
        }
        
        // Draw the timeline graphics
        if (Event.current.type == EventType.Repaint)
        {
            Color timelineColor = isDisabled ? Color.gray : AccentGreen;
            float centerX = timelineRect.center.x;
            float centerY = fullVersionItemRect.center.y;
            
            // Draw selection circle if this version is selected
            if (isSelected && !isDisabled)
            {
                Handles.color = timelineColor;
                Handles.DrawWireDisc(new Vector3(centerX, centerY, 0), Vector3.forward, dotRadius + 3f);
            }
            
            // Draw the dot
            Handles.color = timelineColor;
            Handles.DrawSolidDisc(new Vector3(centerX, centerY, 0), Vector3.forward, dotRadius);

            // Draw the connecting lines spanning the full item height (including spaces)
            float lineWidth = 2f;
            if (!isFirst)
            {
                Handles.DrawAAPolyLine(lineWidth, new Vector3(centerX, fullVersionItemRect.yMin, 0), new Vector3(centerX, centerY - dotRadius, 0));
            }
            if (!isLast)
            {
                Handles.DrawAAPolyLine(lineWidth, new Vector3(centerX, centerY + dotRadius, 0), new Vector3(centerX, fullVersionItemRect.yMax, 0));
            }
        }
    }
    
    private void DrawScopeLabel(string text, Color textColor)
    {
        EditorUIUtils.DrawChipLabel(text.ToLower(), new Color(0.28f, 0.28f, 0.28f), textColor, new Color(0.46f, 0.46f, 0.46f));
    }

    private Color GetColorForScope(Scope scope) => scope switch
    {
        Scope.PUBLIC => AccentGreen,
        Scope.BETA => Color.yellow,
        Scope.ALPHA => Color.red,
        _ => Color.magenta
    };
    
    public void DrawUpdateNotification()
    {
        var recommended = editor.recommendedVersion;
        var applied = editor.customBaseTarget.appliedCustomBaseVersion;

        if (recommended != null && applied != null && editor.CompareVersions(recommended.version, applied.version) > 0)
        {
            GUI.contentColor = AccentGreen;
            EditorGUILayout.LabelField($"New version released: v{recommended.version}!", EditorStyles.boldLabel);
            GUI.contentColor = Color.white;
            if (!string.IsNullOrEmpty(recommended.changelog))
            {
                EditorGUILayout.HelpBox(recommended.changelog, MessageType.None);
            }
            EditorGUILayout.Space();
        }
    }
}
#endif
