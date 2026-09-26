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
            OrbitersGlow.DrawAnimatedGlows(context, rect);
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

}
#endif
