#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UIElements;

public enum MCBInteractionIconKind
{
    Like,
    Comment,
    Edit,
    Delete
}

public class MCBInteractionIconElement : VisualElement
{
    private readonly MCBInteractionIconKind kind;

    public MCBInteractionIconElement(MCBInteractionIconKind kind)
    {
        this.kind = kind;
        pickingMode = PickingMode.Ignore;
        generateVisualContent += DrawIcon;
    }

    private void DrawIcon(MeshGenerationContext context)
    {
        Rect rect = contentRect;
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return;
        }

        var painter = context.painter2D;
        painter.fillColor = Color.white;
        painter.strokeColor = Color.white;
        painter.lineWidth = 0f;

        switch (kind)
        {
            case MCBInteractionIconKind.Like:
                DrawLike(painter, rect);
                break;
            case MCBInteractionIconKind.Comment:
                DrawComment(painter, rect);
                break;
            case MCBInteractionIconKind.Edit:
                DrawEdit(painter, rect);
                break;
            case MCBInteractionIconKind.Delete:
                DrawDelete(painter, rect);
                break;
        }
    }

    private static void DrawLike(Painter2D painter, Rect rect)
    {
        DrawBox(painter, rect, 32f, 32f, 2f, 16f, 5f, 14f);

        Vector2[] points =
        {
            new Vector2(9f, 30f),
            new Vector2(23f, 30f),
            new Vector2(26.2f, 29.2f),
            new Vector2(28.7f, 26.7f),
            new Vector2(30f, 23f),
            new Vector2(30f, 16f),
            new Vector2(28.8f, 13.2f),
            new Vector2(26f, 12f),
            new Vector2(18f, 12f),
            new Vector2(18f, 6f),
            new Vector2(17.1f, 3.9f),
            new Vector2(15f, 3f),
            new Vector2(14.1f, 3.2f),
            new Vector2(12.9f, 4.7f),
            new Vector2(12f, 10.6f),
            new Vector2(9f, 15.2f)
        };
        DrawPolygon(painter, rect, 32f, 32f, points);
    }

    private static void DrawComment(Painter2D painter, Rect rect)
    {
        Vector2[] points =
        {
            new Vector2(4f, 0f),
            new Vector2(60f, 0f),
            new Vector2(64f, 4f),
            new Vector2(64f, 44f),
            new Vector2(60f, 48f),
            new Vector2(33.7f, 48f),
            new Vector2(18.8f, 62.8f),
            new Vector2(16f, 64f),
            new Vector2(13.1f, 62.8f),
            new Vector2(12f, 60f),
            new Vector2(12f, 48f),
            new Vector2(4f, 48f),
            new Vector2(0f, 44f),
            new Vector2(0f, 4f)
        };
        DrawPolygon(painter, rect, 64f, 64f, points);
    }

    private static void DrawEdit(Painter2D painter, Rect rect)
    {
        Vector2[] nib =
        {
            new Vector2(18.94f, 3.12f),
            new Vector2(21.06f, 5.24f),
            new Vector2(21.84f, 6.98f),
            new Vector2(21.06f, 8.72f),
            new Vector2(19.26f, 11.29f),
            new Vector2(12.89f, 4.92f),
            new Vector2(14.70f, 3.12f),
            new Vector2(16.45f, 2.34f)
        };
        DrawPolygon(painter, rect, 24f, 24f, nib);

        Vector2[] body =
        {
            new Vector2(11.83f, 5.98f),
            new Vector2(3.71f, 14.11f),
            new Vector2(2.85f, 15.91f),
            new Vector2(2.45f, 19.53f),
            new Vector2(3.15f, 21.04f),
            new Vector2(4.66f, 21.73f),
            new Vector2(8.27f, 21.34f),
            new Vector2(10.07f, 20.48f),
            new Vector2(18.20f, 12.35f)
        };
        DrawPolygon(painter, rect, 24f, 24f, body);
    }

    private static void DrawDelete(Painter2D painter, Rect rect)
    {
        Vector2[] lid =
        {
            new Vector2(3f, 4f),
            new Vector2(8f, 4f),
            new Vector2(8f, 3f),
            new Vector2(9f, 2f),
            new Vector2(15f, 2f),
            new Vector2(16f, 3f),
            new Vector2(16f, 4f),
            new Vector2(21f, 4f),
            new Vector2(22f, 5f),
            new Vector2(21f, 6f),
            new Vector2(3f, 6f),
            new Vector2(2f, 5f)
        };
        DrawPolygon(painter, rect, 24f, 24f, lid);

        Vector2[] body =
        {
            new Vector2(4f, 8f),
            new Vector2(20f, 8f),
            new Vector2(18.25f, 20.28f),
            new Vector2(17.48f, 21.55f),
            new Vector2(16.27f, 22f),
            new Vector2(7.74f, 22f),
            new Vector2(6.52f, 21.55f),
            new Vector2(5.76f, 20.28f)
        };
        DrawPolygon(painter, rect, 24f, 24f, body);
    }

    private static void DrawBox(Painter2D painter, Rect rect, float viewWidth, float viewHeight, float x, float y, float width, float height)
    {
        Vector2[] points =
        {
            new Vector2(x, y),
            new Vector2(x + width, y),
            new Vector2(x + width, y + height),
            new Vector2(x, y + height)
        };
        DrawPolygon(painter, rect, viewWidth, viewHeight, points);
    }

    private static void DrawPolygon(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2[] points)
    {
        float scale = Mathf.Min(rect.width / viewWidth, rect.height / viewHeight);
        float offsetX = rect.x + (rect.width - viewWidth * scale) * 0.5f;
        float offsetY = rect.y + (rect.height - viewHeight * scale) * 0.5f;

        painter.BeginPath();
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 point = new Vector2(offsetX + points[i].x * scale, offsetY + points[i].y * scale);
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
