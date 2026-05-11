#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UIElements;

public class MCBGlowSurfaceElement : VisualElement
{
    private readonly Color backgroundColor;
    private readonly Color headerColor;
    private readonly float headerHeight;
    private readonly float glowTopOffset;
    private readonly float glowHeight;
    private IVisualElementScheduledItem repaintSchedule;

    public MCBGlowSurfaceElement(Color backgroundColor, float glowTopOffset, float glowHeight)
        : this(backgroundColor, backgroundColor, 0f, glowTopOffset, glowHeight)
    {
    }

    public MCBGlowSurfaceElement(Color backgroundColor, Color headerColor, float headerHeight, float glowTopOffset, float glowHeight)
    {
        this.backgroundColor = backgroundColor;
        this.headerColor = headerColor;
        this.headerHeight = headerHeight;
        this.glowTopOffset = glowTopOffset;
        this.glowHeight = glowHeight;
        generateVisualContent += DrawSurface;
        pickingMode = PickingMode.Ignore;
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

    private void DrawSurface(MeshGenerationContext context)
    {
        Rect rect = contentRect;
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return;
        }

        DrawRect(context.painter2D, rect, backgroundColor);
        if (headerHeight > 0f)
        {
            DrawRect(context.painter2D, new Rect(rect.x, rect.y, rect.width, Mathf.Min(headerHeight, rect.height)), headerColor);
        }

        var glowRect = new Rect(rect.x, rect.y + glowTopOffset, rect.width, glowHeight);
        MCBLogoElement.DrawAnimatedGlows(context, glowRect);
    }

    private static void DrawRect(Painter2D painter, Rect rect, Color color)
    {
        painter.fillColor = color;
        painter.BeginPath();
        painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
        painter.LineTo(new Vector2(rect.xMax, rect.yMin));
        painter.LineTo(new Vector2(rect.xMax, rect.yMax));
        painter.LineTo(new Vector2(rect.xMin, rect.yMax));
        painter.ClosePath();
        painter.Fill(FillRule.NonZero);
    }
}
#endif
