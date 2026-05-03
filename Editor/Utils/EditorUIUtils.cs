using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// A collection of static helper methods for drawing custom UI elements in the editor.
#if UNITY_EDITOR
public static class EditorUIUtils
{
    public static readonly Color OrangeColor = new Color(1.0f, 0.65f, 0.0f);
    private static readonly Color BackgroundColor = new Color(0.28f, 0.28f, 0.28f);
    private static readonly Color BorderColor = new Color(0.46f, 0.46f, 0.46f);

    public static void DrawChipLabel(string text, Color backgroundColor, Color textColor, Color? borderColor = null,
        int width = 65, int height = 18, float cornerRadius = 8f, float borderWidth = 1f)
    {
        Rect rect = GUILayoutUtility.GetRect(width, height);
        Handles.BeginGUI();
        Color oldColor = Handles.color;

        if (borderColor.HasValue)
        {
            Handles.color = borderColor.Value;
            DrawRoundedRect(rect, cornerRadius);
            Rect innerRect = new Rect(rect.x + borderWidth, rect.y + borderWidth, rect.width - borderWidth * 2f, rect.height - borderWidth * 2f);
            float innerRadius = Mathf.Max(0, cornerRadius - borderWidth * 0.5f);
            Handles.color = backgroundColor;
            DrawRoundedRect(innerRect, innerRadius);
        }
        else
        {
            Handles.color = backgroundColor;
            DrawRoundedRect(rect, cornerRadius);
        }

        Handles.color = oldColor;
        Handles.EndGUI();

        var labelStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = textColor },
            padding = new RectOffset(4, 4, 1, 1)
        };
        GUI.Label(rect, text, labelStyle);
    }

    public static void DrawProgressBar(string label, float progress, int width = 300, int height = 20, 
        Color? backgroundColor = null, Color? progressColor = null, Color? textColor = null)
    {
        progress = Mathf.Clamp01(progress);
        
        Color bgColor = backgroundColor ?? new Color(0.3f, 0.3f, 0.3f);
        Color progColor = progressColor ?? new Color(0.2f, 0.8f, 0.2f);
        Color txtColor = textColor ?? Color.white;

        Rect rect = GUILayoutUtility.GetRect(width, height);
        
        // Draw background
        Handles.BeginGUI();
        Color oldColor = Handles.color;
        Handles.color = bgColor;
        DrawRoundedRect(rect, 4f);
        
        // Draw progress fill
        if (progress > 0)
        {
            Rect progressRect = new Rect(rect.x, rect.y, rect.width * progress, rect.height);
            Handles.color = progColor;
            DrawRoundedRect(progressRect, 4f);
        }
        
        Handles.color = oldColor;
        Handles.EndGUI();

        // Draw text
        var labelStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 10,
            fontStyle = FontStyle.Normal,
            normal = { textColor = txtColor },
            padding = new RectOffset(4, 4, 2, 2)
        };
        
        string displayText = $"{label} ({Mathf.RoundToInt(progress * 100)}%)";
        GUI.Label(rect, displayText, labelStyle);
    }

    public static void DrawCircularAvatar(Rect rect, Texture2D texture, Color backgroundColor, Color borderColor, float borderWidth = 1f)
    {
        float size = Mathf.Min(rect.width, rect.height);
        Rect squareRect = new Rect(rect.x + (rect.width - size) * 0.5f, rect.y + (rect.height - size) * 0.5f, size, size);
        Vector3 center = new Vector3(squareRect.center.x, squareRect.center.y, 0f);
        float radius = size * 0.5f;

        Handles.BeginGUI();
        Color oldColor = Handles.color;
        Handles.color = backgroundColor;
        Handles.DrawSolidDisc(center, Vector3.forward, radius);
        Handles.color = oldColor;
        Handles.EndGUI();

        if (texture != null)
        {
            GUI.DrawTexture(squareRect, texture, ScaleMode.ScaleAndCrop, true);
        }

        if (borderWidth > 0f)
        {
            Handles.BeginGUI();
            oldColor = Handles.color;
            Handles.color = borderColor;
            Handles.DrawWireDisc(center, Vector3.forward, radius);
            Handles.color = oldColor;
            Handles.EndGUI();
        }
    }

    private static void DrawRoundedRect(Rect rect, float radius)
    {
        List<Vector3> verts = new List<Vector3>();
        AddArc(verts, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f);
        verts.Add(new Vector2(rect.xMax - radius, rect.yMin));
        AddArc(verts, new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 270f, 360f);
        verts.Add(new Vector2(rect.xMax, rect.yMax - radius));
        AddArc(verts, new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 0f, 90f);
        verts.Add(new Vector2(rect.xMin + radius, rect.yMax));
        AddArc(verts, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f);
        verts.Add(new Vector2(rect.xMin, rect.yMin + radius));
        Handles.DrawAAConvexPolygon(verts.ToArray());
    }

    private static void AddArc(List<Vector3> verts, Vector2 center, float radius, float startAngle, float endAngle, int segments = 8)
    {
        float angleStep = (endAngle - startAngle) / segments;
        for (int i = 0; i <= segments; i++)
        {
            float angle = (startAngle + i * angleStep) * Mathf.Deg2Rad;
            verts.Add(new Vector2(center.x + Mathf.Cos(angle) * radius, center.y + Mathf.Sin(angle) * radius));
        }
    }
}
#endif
