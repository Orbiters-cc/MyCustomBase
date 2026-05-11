#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class MCBLogoElement : VisualElement
{
    private readonly bool drawLogo;
    private readonly bool drawGlows;
    private IVisualElementScheduledItem repaintSchedule;

    private static readonly Vector2[][] Paths =
    {
        new[]
        {
            new Vector2(0.5f, 0.5f),
            new Vector2(0.499964f, 207.5f),
            new Vector2(224.5f, 207.5f),
            new Vector2(193.5f, 104f),
            new Vector2(224.5f, 0.500039f),
            new Vector2(142.442f, 0.500025f),
            new Vector2(103.471f, 68f),
            new Vector2(64.5f, 0.500011f)
        },
        new[]
        {
            new Vector2(450.5f, 0.500079f),
            new Vector2(246.5f, 0.500043f),
            new Vector2(212.5f, 104f),
            new Vector2(246.5f, 207.5f),
            new Vector2(450.5f, 207.5f),
            new Vector2(421.5f, 143.5f),
            new Vector2(328.5f, 143.5f),
            new Vector2(328.5f, 68.0001f),
            new Vector2(421.5f, 68.0001f)
        },
        new[]
        {
            new Vector2(606.5f, 0.500106f),
            new Vector2(470.5f, 0.500082f),
            new Vector2(427.755f, 104f),
            new Vector2(470.5f, 207.5f),
            new Vector2(606.5f, 207.5f),
            new Vector2(643.5f, 155.5f),
            new Vector2(606.5f, 104f),
            new Vector2(643.5f, 52.0001f)
        }
    };

    public MCBLogoElement(bool drawLogo = true, bool drawGlows = true)
    {
        this.drawLogo = drawLogo;
        this.drawGlows = drawGlows;
        pickingMode = PickingMode.Ignore;
        generateVisualContent += DrawLogo;
        if (drawGlows)
        {
            repaintSchedule = schedule.Execute(() =>
            {
                if (panel != null)
                {
                    MarkDirtyRepaint();
                }
            }).Every(80);
            repaintSchedule.Pause();
            RegisterCallback<AttachToPanelEvent>(_ => repaintSchedule?.Resume());
            RegisterCallback<DetachFromPanelEvent>(_ => repaintSchedule?.Pause());
        }
    }

    private void DrawLogo(MeshGenerationContext context)
    {
        Rect rect = contentRect;
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return;
        }

        if (drawGlows)
        {
            DrawAnimatedGlows(context, rect);
        }

        if (!drawLogo)
        {
            return;
        }

        Rect logoRect = GetLogoRect(rect);
        float scale = Mathf.Min(logoRect.width / 645f, logoRect.height / 208f);
        float offsetX = logoRect.x + (logoRect.width - (645f * scale)) * 0.5f;
        float offsetY = logoRect.y + (logoRect.height - (208f * scale)) * 0.5f;
        var painter = context.painter2D;

        painter.fillColor = new Color(0.90f, 0.90f, 0.90f);
        painter.strokeColor = Color.black;
        painter.lineWidth = Mathf.Max(1f, scale);

        for (int i = 0; i < Paths.Length; i++)
        {
            DrawPath(painter, Paths[i], scale, offsetX, offsetY, true);
        }

        for (int i = 0; i < Paths.Length; i++)
        {
            DrawPath(painter, Paths[i], scale, offsetX, offsetY, false);
        }
    }

    private static void DrawPath(Painter2D painter, Vector2[] points, float scale, float offsetX, float offsetY, bool fill)
    {
        if (points == null || points.Length == 0)
        {
            return;
        }

        painter.BeginPath();
        painter.MoveTo(Transform(points[0], scale, offsetX, offsetY));
        for (int i = 1; i < points.Length; i++)
        {
            painter.LineTo(Transform(points[i], scale, offsetX, offsetY));
        }
        painter.ClosePath();

        if (fill)
        {
            painter.Fill(FillRule.NonZero);
        }
        else
        {
            painter.Stroke();
        }
    }

    private static Vector2 Transform(Vector2 point, float scale, float offsetX, float offsetY)
    {
        return new Vector2(offsetX + point.x * scale, offsetY + point.y * scale);
    }

    private static Rect GetLogoRect(Rect rect)
    {
        float width = rect.width * 0.74f;
        float height = width * (208f / 645f);
        float maxHeight = rect.height * 0.64f;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * (645f / 208f);
        }

        return new Rect(
            rect.x + (rect.width - width) * 0.5f,
            rect.y + (rect.height - height) * 0.5f,
            width,
            height);
    }

    public static void DrawAnimatedGlows(MeshGenerationContext context, Rect rect)
    {
        double time = EditorApplication.timeSinceStartup;
        var painter = context.painter2D;
        var center = rect.center;

        DrawGlow(painter, center, rect, time, 0.10f, 0.18f, 0.38f, new Color(0.45f, 0.16f, 1f, 0.28f), 0.56f, 0.42f, 0.86f);
        DrawGlow(painter, center, rect, time, 0.64f, 0.46f, 0.46f, new Color(0.04f, 0.44f, 1f, 0.25f), 0.52f, 0.50f, 0.92f);
        DrawGlow(painter, center, rect, time, 1.28f, 0.66f, 0.32f, new Color(0.78f, 0.20f, 1f, 0.22f), 0.46f, 0.36f, 0.88f);
        DrawGlow(painter, center, rect, time, 2.02f, 0.36f, 0.54f, new Color(0.10f, 0.20f, 1f, 0.20f), 0.62f, 0.30f, 0.78f);
        DrawGlow(painter, center, rect, time, 2.76f, 0.52f, 0.26f, new Color(0.28f, 0.68f, 1f, 0.16f), 0.50f, 0.24f, 0.98f);
        DrawGlow(painter, center, rect, time, 3.38f, 0.78f, 0.50f, new Color(0.52f, 0.26f, 1f, 0.15f), 0.38f, 0.28f, 0.82f);
        DrawGlow(painter, center, rect, time, 4.10f, 0.28f, 0.20f, new Color(0.06f, 0.58f, 1f, 0.13f), 0.42f, 0.32f, 0.96f);
        DrawGlow(painter, center, rect, time, 4.82f, 0.58f, 0.60f, new Color(0.72f, 0.10f, 1f, 0.12f), 0.48f, 0.22f, 0.74f);
    }

    private static void DrawGlow(Painter2D painter, Vector2 center, Rect rect, double time, float phase, float xBias, float yBias, Color color, float baseSize, float speed, float heightScale)
    {
        float t = (float)(time * speed + phase);
        float driftX = Mathf.Sin(t * 0.73f) * rect.width * 0.08f;
        float driftY = Mathf.Cos(t * 0.61f) * rect.height * 0.055f;
        float pulse = 0.90f + Mathf.Sin(t * 0.82f) * 0.10f;
        float fade = 0.72f + Mathf.Sin(t * 0.33f + phase) * 0.28f;
        float rotation = t * 0.10f + phase;
        Vector2 glowCenter = new Vector2(
            Mathf.Lerp(rect.xMin, rect.xMax, xBias) + driftX,
            Mathf.Lerp(rect.yMin, rect.yMax, yBias) + driftY);
        glowCenter = Vector2.Lerp(glowCenter, center, 0.34f);

        Vector2 radii = new Vector2(rect.width * baseSize * pulse, rect.height * baseSize * heightScale * pulse);
        color.a *= fade;
        DrawSoftEllipse(painter, glowCenter, radii, rotation, color);
    }

    private static void DrawSoftEllipse(Painter2D painter, Vector2 center, Vector2 radii, float rotation, Color color)
    {
        const int layers = 6;
        for (int layer = layers; layer >= 1; layer--)
        {
            float layerT = layer / (float)layers;
            Color layerColor = color;
            layerColor.a *= Mathf.Pow(1f - layerT * 0.82f, 1.4f) * 0.42f;
            DrawEllipsePolygon(painter, center, radii * layerT, rotation, layerColor);
        }
    }

    private static void DrawEllipsePolygon(Painter2D painter, Vector2 center, Vector2 radii, float rotation, Color color)
    {
        const int segments = 24;
        float cos = Mathf.Cos(rotation);
        float sin = Mathf.Sin(rotation);

        painter.fillColor = color;
        painter.BeginPath();
        for (int i = 0; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * radii.x;
            float y = Mathf.Sin(angle) * radii.y;
            var point = new Vector2(center.x + x * cos - y * sin, center.y + x * sin + y * cos);
            if (i == 0)
            {
                painter.MoveTo(point);
            }
            else
            {
                painter.LineTo(point);
            }
        }
        painter.ClosePath();
        painter.Fill(FillRule.NonZero);
    }
}
#endif
