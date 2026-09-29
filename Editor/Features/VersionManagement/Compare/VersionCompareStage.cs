using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

internal enum CompareLook { Changes, Clay, Textured }

internal enum CompareLayout { Slider, SideBySide, Overlay }

/// <summary>
/// The 3D stage of the version comparison: the avatar before and after, rendered into textures shown by UI Toolkit.
/// Slider: one view, a handle wipes between before and after. Side by side: two views turning together. Overlay: after,
/// with before as an x-ray ghost. Drag turns, scroll zooms, right-drag moves, double-click frames again, and holding
/// Space shows the before side. The camera glides to where it is sent instead of jumping.
/// </summary>
internal sealed class VersionCompareStage : VisualElement, IDisposable
{
    private const float FieldOfView = 28f;
    private const float Gap = 6f;

    private readonly VisualElement beforePane, afterPane, divider, handle;
    private readonly Image beforeImage, afterImage;
    private readonly Label beforeTag, afterTag, hint;

    private PreviewRenderUtility preview;
    private RenderTexture beforeTexture, afterTexture;
    private Material clay, ghost, unchanged, backdrop, floor;
    private Mesh quad;
    private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    private readonly IVisualElementScheduledItem ticker;

    private VersionMeshComparison comparison;
    private CompareLook look = CompareLook.Changes;
    private CompareLayout layout = CompareLayout.Slider;
    private ComparedPart focused;
    private bool showContext, turntable, peeking, dirty;
    private float split = 0.5f, shownSplit = 0.5f;
    private double lastTick, pulseStart = -10;

    // Orbit: the shown values glide to the targets.
    private float yaw = 180f, pitch = 6f, distance = 3f, targetYaw = 180f, targetPitch = 6f, targetDistance = 3f;
    private Vector3 pivot, targetPivot;
    private Bounds frame = new Bounds(Vector3.up, Vector3.one);
    // What the camera turns around when nothing is focused: the hips when the avatar has them.
    private Vector3 home = Vector3.up;

    private int dragPointer = -1;
    private bool dragPans, dragSplit;
    private Vector2 lastPointer;

    public string BeforeLabel { set => beforeTag.text = value; }
    public string AfterLabel { set => afterTag.text = value; }

    public VersionCompareStage()
    {
        AddToClassList("mcb-cmp-stage");
        focusable = true;

        afterPane = Pane("mcb-cmp-stage__pane--after", out afterImage);
        beforePane = Pane("mcb-cmp-stage__pane--before", out beforeImage);

        divider = new VisualElement { pickingMode = PickingMode.Ignore };
        divider.AddToClassList("mcb-cmp-stage__divider");
        Add(divider);
        handle = new VisualElement { tooltip = "Drag to compare" };
        handle.AddToClassList("mcb-cmp-stage__handle");
        handle.Add(new CompareGlyph(CompareGlyph.Kind.Swap) { pickingMode = PickingMode.Ignore });
        Add(handle);

        beforeTag = Tag("mcb-cmp-stage__tag--before");
        afterTag = Tag("mcb-cmp-stage__tag--after");
        hint = new Label("Drag to turn  ·  Scroll to zoom  ·  Right-drag to move  ·  Hold Space to see before") { pickingMode = PickingMode.Ignore };
        hint.AddToClassList("mcb-cmp-stage__hint");
        Add(hint);

        RegisterCallback<PointerDownEvent>(OnPointerDown);
        RegisterCallback<PointerMoveEvent>(OnPointerMove);
        RegisterCallback<PointerUpEvent>(OnPointerUp);
        RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
        RegisterCallback<WheelEvent>(OnWheel);
        RegisterCallback<KeyDownEvent>(OnKeyDown);
        RegisterCallback<KeyUpEvent>(OnKeyUp);
        RegisterCallback<FocusOutEvent>(_ => SetPeek(false));
        RegisterCallback<GeometryChangedEvent>(_ => { PlacePanes(); dirty = true; });
        RegisterCallback<DetachFromPanelEvent>(_ => Dispose());

        ticker = schedule.Execute(Tick).Every(15);
    }

    private VisualElement Pane(string modifier, out Image image)
    {
        var pane = new VisualElement { pickingMode = PickingMode.Ignore };
        pane.AddToClassList("mcb-cmp-stage__pane");
        pane.AddToClassList(modifier);
        image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
        image.AddToClassList("mcb-cmp-stage__image");
        pane.Add(image);
        Add(pane);
        return pane;
    }

    private Label Tag(string modifier)
    {
        var tag = new Label { pickingMode = PickingMode.Ignore };
        tag.AddToClassList("mcb-cmp-stage__tag");
        tag.AddToClassList(modifier);
        Add(tag);
        return tag;
    }

    public ComparedPart Focused => focused;
    public bool Turntable => turntable;
    public bool ShowContext => showContext;

    /// <summary>Shows a comparison: the camera flies in to frame the avatar and the changes pulse once.</summary>
    public void Show(VersionMeshComparison shown, bool intro)
    {
        comparison = shown;
        focused = null;
        if (comparison == null) { dirty = true; return; }
        frame = comparison.Bounds;
        home = comparison.Pivot;
        targetPivot = home;
        targetYaw = 180f;
        targetPitch = 6f;
        targetDistance = FitFrame();
        if (intro)
        {
            // From a little further, turned a quarter, so the avatar swings round to face the viewer.
            yaw = 128f;
            pitch = 16f;
            distance = targetDistance * 1.5f;
            pivot = targetPivot + Vector3.up * frame.extents.y * 0.2f;
        }
        Pulse();
        dirty = true;
    }

    public void SetLook(CompareLook value)
    {
        look = value;
        if (look == CompareLook.Changes) Pulse();
        dirty = true;
    }

    public void SetLayout(CompareLayout value)
    {
        if (layout == value) return;
        layout = value;
        // A short dip hides the views being resized.
        AddToClassList("mcb-cmp-stage--switching");
        schedule.Execute(() =>
        {
            PlacePanes();
            dirty = true;
            RemoveFromClassList("mcb-cmp-stage--switching");
        }).StartingIn(90);
    }

    public void SetContext(bool shown)
    {
        showContext = shown;
        if (shown) comparison?.BuildContext();
        dirty = true;
    }

    public void SetTurntable(bool on)
    {
        turntable = on;
        dirty = true;
    }

    /// <summary>Flies to a part's change and dims the rest; null frames the whole avatar again.</summary>
    public void FocusPart(ComparedPart part)
    {
        focused = part;
        if (part == null)
        {
            targetPivot = home;
            targetDistance = FitFrame();
        }
        else
        {
            var bounds = part.Focus;
            // Never so close that the change loses its surroundings.
            bounds.Expand(Mathf.Max(frame.size.magnitude * 0.06f, 0.02f));
            targetPivot = bounds.center;
            targetDistance = Mathf.Max(Fit(bounds), FitFrame() * 0.18f);
            // Face the change: from the side it is on.
            Vector3 direction = bounds.center - home;
            direction.y = 0f;
            if (direction.sqrMagnitude > frame.extents.x * frame.extents.x * 0.05f)
                targetYaw = Mathf.Atan2(-direction.x, -direction.z) * Mathf.Rad2Deg + 360f * Mathf.Round((targetYaw - Mathf.Atan2(-direction.x, -direction.z) * Mathf.Rad2Deg) / 360f);
        }
        Pulse();
        dirty = true;
    }

    public void ResetView()
    {
        focused = null;
        targetPivot = home;
        targetDistance = FitFrame();
        targetPitch = 6f;
        targetYaw = 180f + 360f * Mathf.Round((targetYaw - 180f) / 360f);
        dirty = true;
    }

    private void Pulse() => pulseStart = EditorApplication.timeSinceStartup;

    // The whole avatar seen from its pivot: the frame made symmetric around it, so nothing leaves the view as it turns.
    private float FitFrame()
    {
        var around = frame;
        around.Encapsulate(2f * home - frame.min);
        around.Encapsulate(2f * home - frame.max);
        return Fit(around);
    }

    private static float Fit(Bounds bounds)
    {
        float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
        return radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 0.9f;
    }

    // ---- Input -------------------------------------------------------------------------------------

    private void OnPointerDown(PointerDownEvent evt)
    {
        Focus();
        if (comparison == null || !comparison.Ready) return;
        if (evt.button == 0 && evt.clickCount == 2) { ResetView(); evt.StopPropagation(); return; }
        dragSplit = evt.button == 0 && layout == CompareLayout.Slider && handle.worldBound.Contains(evt.position);
        dragPans = evt.button == 1 || evt.button == 2 || (evt.button == 0 && evt.shiftKey);
        dragPointer = evt.pointerId;
        lastPointer = evt.position;
        this.CapturePointer(dragPointer);
        AddToClassList(dragSplit ? "mcb-cmp-stage--splitting" : "mcb-cmp-stage--dragging");
        evt.StopPropagation();
    }

    private void OnPointerMove(PointerMoveEvent evt)
    {
        if (evt.pointerId != dragPointer || !this.HasPointerCapture(dragPointer)) return;
        Vector2 delta = (Vector2)evt.position - lastPointer;
        lastPointer = evt.position;
        if (dragSplit)
        {
            float width = contentRect.width;
            split = width > 0f ? Mathf.Clamp01(this.WorldToLocal(evt.position).x / width) : 0.5f;
            shownSplit = split;
            PlacePanes();
        }
        else if (dragPans)
        {
            float perPixel = 2f * distance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, contentRect.height);
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            targetPivot += (rotation * Vector3.left * delta.x + rotation * Vector3.up * delta.y) * perPixel;
            pivot = targetPivot;
        }
        else
        {
            targetYaw += delta.x * 0.45f;
            targetPitch = Mathf.Clamp(targetPitch + delta.y * 0.3f, -75f, 75f);
        }
        dirty = true;
        evt.StopPropagation();
    }

    private void OnPointerUp(PointerUpEvent evt)
    {
        if (evt.pointerId != dragPointer) return;
        if (this.HasPointerCapture(dragPointer)) this.ReleasePointer(dragPointer);
        EndDrag();
    }

    private void EndDrag()
    {
        dragPointer = -1;
        dragSplit = dragPans = false;
        RemoveFromClassList("mcb-cmp-stage--dragging");
        RemoveFromClassList("mcb-cmp-stage--splitting");
    }

    private void OnWheel(WheelEvent evt)
    {
        if (comparison == null) return;
        float fit = FitFrame();
        targetDistance = Mathf.Clamp(targetDistance * Mathf.Exp(evt.delta.y * 0.06f), fit * 0.06f, fit * 3f);
        dirty = true;
        evt.StopPropagation();
        evt.PreventDefault();
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        if (evt.keyCode == KeyCode.Space) { SetPeek(true); evt.StopPropagation(); }
        else if (evt.keyCode == KeyCode.F && comparison != null) { FocusPart(focused); evt.StopPropagation(); }
        else if (evt.keyCode == KeyCode.R) { ResetView(); evt.StopPropagation(); }
    }

    private void OnKeyUp(KeyUpEvent evt)
    {
        if (evt.keyCode == KeyCode.Space) { SetPeek(false); evt.StopPropagation(); }
    }

    /// <summary>While held, the whole view shows the before side.</summary>
    public void SetPeek(bool on)
    {
        if (peeking == on) return;
        peeking = on;
        EnableInClassList("mcb-cmp-stage--peeking", on);
        PlacePanes();
        dirty = true;
    }

    // ---- Layout of the two views -------------------------------------------------------------------

    private void PlacePanes()
    {
        float width = contentRect.width;
        if (float.IsNaN(width) || width <= 0f) return;
        bool side = layout == CompareLayout.SideBySide && !peeking;
        bool overlay = layout == CompareLayout.Overlay && !peeking;
        float half = (width - Gap) * 0.5f;
        float beforeWidth = peeking ? width : side ? half : overlay ? 0f : width * shownSplit;

        afterPane.style.left = side ? half + Gap : 0f;
        afterPane.style.width = side ? half : width;
        beforePane.style.width = beforeWidth;
        beforePane.style.display = beforeWidth > 0.5f ? DisplayStyle.Flex : DisplayStyle.None;
        beforeImage.style.width = side ? half : width;

        bool slider = layout == CompareLayout.Slider && !peeking;
        divider.style.display = handle.style.display = slider ? DisplayStyle.Flex : DisplayStyle.None;
        divider.style.left = width * shownSplit;
        handle.style.left = width * shownSplit;

        beforeTag.style.opacity = overlay ? 0f : slider ? Mathf.Clamp01((width * shownSplit - 70f) / 60f) : 1f;
        afterTag.style.opacity = peeking ? 0f : slider ? Mathf.Clamp01((width * (1f - shownSplit) - 70f) / 60f) : 1f;
    }

    // ---- Rendering ---------------------------------------------------------------------------------

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = Mathf.Clamp((float)(now - lastTick), 0f, 0.1f);
        lastTick = now;
        if (panel == null || comparison == null || !comparison.Ready) return;

        if (turntable && dragPointer < 0) targetYaw += dt * 18f;
        float k = 1f - Mathf.Exp(-dt * 9f);
        bool moving = false;
        yaw = Glide(yaw, targetYaw, k, 0.01f, ref moving);
        pitch = Glide(pitch, targetPitch, k, 0.01f, ref moving);
        distance = Glide(distance, targetDistance, k, targetDistance * 0.0005f, ref moving);
        if ((pivot - targetPivot).sqrMagnitude > frame.size.sqrMagnitude * 1e-8f) { pivot = Vector3.Lerp(pivot, targetPivot, k); moving = true; }
        else pivot = targetPivot;

        bool pulsing = now - pulseStart < PulseSeconds && look == CompareLook.Changes;
        if (moving || pulsing || dirty) Render(now);
    }

    private const float PulseSeconds = 2.4f;

    private static float Glide(float value, float target, float k, float epsilon, ref bool moving)
    {
        if (Mathf.Abs(target - value) <= epsilon) return target;
        moving = true;
        return Mathf.Lerp(value, target, k);
    }

    private void Render(double now)
    {
        dirty = false;
        var rect = contentRect;
        if (comparison == null || !comparison.Ready || rect.width < 8f || rect.height < 8f || float.IsNaN(rect.width)) return;
        EnsureResources();

        bool side = layout == CompareLayout.SideBySide && !peeking;
        bool overlay = layout == CompareLayout.Overlay && !peeking;
        float scale = EditorGUIUtility.pixelsPerPoint;
        int height = Mathf.Clamp(Mathf.RoundToInt(rect.height * scale), 8, 2048);
        int width = Mathf.Clamp(Mathf.RoundToInt((side ? (rect.width - Gap) * 0.5f : rect.width) * scale), 8, 2048);
        EnsureTexture(ref beforeTexture, width, height, "MCB Compare Before");
        EnsureTexture(ref afterTexture, width, height, "MCB Compare After");

        float t = (float)(now - pulseStart) / PulseSeconds;
        float pulse = t < 1f ? Mathf.Sin(t * Mathf.PI * 3f) * Mathf.Sin(t * Mathf.PI) * 0.5f + 0.5f * (1f - t) : 0f;
        if (beforePane.style.display != DisplayStyle.None) Draw(beforeTexture, true, false, Mathf.Max(0f, pulse));
        Draw(afterTexture, false, overlay, Mathf.Max(0f, pulse));
        beforeImage.image = beforeTexture;
        afterImage.image = afterTexture;
        beforeImage.MarkDirtyRepaint();
        afterImage.MarkDirtyRepaint();
    }

    private void Draw(RenderTexture texture, bool before, bool ghostBefore, float pulse)
    {
        var camera = preview.camera;
        camera.targetTexture = texture;
        camera.aspect = (float)texture.width / texture.height;
        camera.fieldOfView = FieldOfView;
        var rotation = Quaternion.Euler(pitch, yaw, 0f);
        camera.transform.rotation = rotation;
        camera.transform.position = pivot - rotation * Vector3.forward * distance;
        float reach = frame.size.magnitude;
        camera.nearClipPlane = Mathf.Max(0.001f, distance * 0.02f);
        camera.farClipPlane = distance + reach * 4f;
        preview.lights[0].intensity = 1.05f;
        preview.lights[0].color = new Color(1f, 0.97f, 0.93f);
        preview.lights[0].transform.rotation = rotation * Quaternion.Euler(28f, -32f, 0f);
        preview.lights[1].intensity = 0.55f;
        preview.lights[1].color = new Color(0.8f, 0.87f, 1f);
        preview.lights[1].transform.rotation = rotation * Quaternion.Euler(-10f, 150f, 0f);
        preview.ambientColor = new Color(0.34f, 0.35f, 0.39f);

        preview.DrawMesh(quad, Matrix4x4.identity, backdrop, 0);
        float floorSize = Mathf.Max(frame.size.x, frame.size.z, frame.size.y * 0.35f) * 2.6f;
        preview.DrawMesh(quad, Matrix4x4.TRS(new Vector3(home.x, frame.min.y, home.z), Quaternion.identity, new Vector3(floorSize, 1f, floorSize)), floor, 0);

        bool textured = look == CompareLook.Textured;
        bool anyChange = comparison.Parts.Any(p => p.Changed);
        foreach (var part in comparison.Parts)
        {
            var mesh = before ? part.BeforeMesh : part.AfterMesh;
            if (mesh == null) continue;
            var materials = before ? part.BeforeMaterials : part.AfterMaterials;
            // With the changes shown, what the version leaves as it is turns see-through, so it reads as untouched.
            if (look == CompareLook.Changes && part.Change == Orbiters.Toolkit.Editor.Meshes.PartChange.Same && anyChange)
            {
                for (int sub = 0; sub < mesh.subMeshCount; sub++) preview.DrawMesh(mesh, Matrix4x4.identity, unchanged, sub);
                continue;
            }
            block.Clear();
            block.SetFloat(HeatId, look == CompareLook.Changes ? 1f : 0f);
            block.SetFloat(PulseId, pulse);
            block.SetFloat(DimId, focused != null && focused != part ? 1f : 0f);
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var material = textured && sub < materials.Length && materials[sub] != null ? materials[sub] : null;
                if (material != null) preview.DrawMesh(mesh, Matrix4x4.identity, material, sub);
                else preview.DrawMesh(mesh, Matrix4x4.identity, clay, sub, block);
            }
            // Only what changed shows its former shape, and only the focused part while one is.
            if (ghostBefore && part.BeforeMesh != null && part.Change != Orbiters.Toolkit.Editor.Meshes.PartChange.Same && (focused == null || focused == part))
                for (int sub = 0; sub < part.BeforeMesh.subMeshCount; sub++) preview.DrawMesh(part.BeforeMesh, Matrix4x4.identity, ghost, sub);
        }

        if (showContext)
        {
            block.Clear();
            block.SetFloat(HeatId, 0f);
            block.SetFloat(DimId, focused != null ? 0.6f : 0f);
            foreach (var part in comparison.Context)
            {
                for (int sub = 0; sub < part.Mesh.subMeshCount; sub++)
                {
                    var material = textured && sub < part.Materials.Length ? part.Materials[sub] : null;
                    if (material != null) preview.DrawMesh(part.Mesh, part.Matrix, material, sub);
                    else preview.DrawMesh(part.Mesh, part.Matrix, clay, sub, block);
                }
            }
        }

        try
        {
            preview.Render(true, false);
        }
        finally
        {
            // Render switches the editor's lighting settings to the preview scene and only EndPreview switches them back,
            // which a texture shown by UI Toolkit never calls: left switched, the Scene view reads settings that are gone.
            Unsupported.RestoreOverrideLightingSettings();
            camera.targetTexture = null;
        }
    }

    private static readonly int HeatId = Shader.PropertyToID("_Heat"), PulseId = Shader.PropertyToID("_Pulse"), DimId = Shader.PropertyToID("_Dim");

    private void EnsureResources()
    {
        if (preview != null) return;
        preview = new PreviewRenderUtility();
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = new Color(0.07f, 0.075f, 0.09f, 1f);
        preview.camera.allowHDR = false;
        clay = NewMaterial("Hidden/MCB/VersionCompare", -1);
        ghost = NewMaterial("Hidden/MCB/VersionCompareGhost", -1);
        unchanged = NewMaterial("Hidden/MCB/VersionCompareUnchanged", -1);
        backdrop = NewMaterial("Hidden/MCB/VersionCompareStage", (int)RenderQueue.Background);
        backdrop.SetFloat("_Mode", 0f);
        floor = NewMaterial("Hidden/MCB/VersionCompareStage", (int)RenderQueue.Transparent - 10);
        floor.SetFloat("_Mode", 1f);
        quad = new Mesh { name = "MCB Compare Quad", hideFlags = HideFlags.HideAndDontSave };
        quad.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
        quad.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
        quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        // Never culled: the backdrop is placed by its shader, not by the mesh.
        quad.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
    }

    private static Material NewMaterial(string shaderName, int queue)
    {
        var material = new Material(Shader.Find(shaderName)) { hideFlags = HideFlags.HideAndDontSave };
        if (queue >= 0) material.renderQueue = queue;
        return material;
    }

    private static void EnsureTexture(ref RenderTexture texture, int width, int height, string name)
    {
        if (texture != null && texture.width == width && texture.height == height && texture.IsCreated()) return;
        if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
        texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = name,
            antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing > 1 ? QualitySettings.antiAliasing : 4),
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.Create();
    }

    public void Dispose()
    {
        ticker?.Pause();
        if (preview != null) { preview.Cleanup(); preview = null; }
        foreach (var material in new[] { clay, ghost, unchanged, backdrop, floor }) if (material != null) UnityEngine.Object.DestroyImmediate(material);
        clay = ghost = unchanged = backdrop = floor = null;
        if (quad != null) UnityEngine.Object.DestroyImmediate(quad);
        quad = null;
        foreach (var texture in new[] { beforeTexture, afterTexture })
            if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
        beforeTexture = afterTexture = null;
    }
}

/// <summary>Small line glyphs of the comparison window, drawn with the vector API.</summary>
internal sealed class CompareGlyph : VisualElement
{
    public enum Kind { Swap, Frame, Turntable, Compare }

    private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--glyph-color");
    private readonly Kind kind;
    private Color color = new Color(0.9f, 0.9f, 0.9f);

    public CompareGlyph(Kind kind)
    {
        this.kind = kind;
        AddToClassList("mcb-cmp-glyph");
        pickingMode = PickingMode.Ignore;
        generateVisualContent += Draw;
        RegisterCallback<CustomStyleResolvedEvent>(_ =>
        {
            if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; MarkDirtyRepaint(); }
        });
    }

    // Drawn in a 24 x 24 box, scaled to the element.
    private void Draw(MeshGenerationContext context)
    {
        var rect = contentRect;
        float scale = Mathf.Min(rect.width, rect.height) / 24f;
        if (scale <= 0f) return;
        var offset = new Vector2(rect.x + (rect.width - 24f * scale) * 0.5f, rect.y + (rect.height - 24f * scale) * 0.5f);
        Vector2 P(float x, float y) => offset + new Vector2(x, y) * scale;
        var painter = context.painter2D;
        painter.strokeColor = color;
        painter.fillColor = color;
        painter.lineWidth = 1.8f * scale;
        painter.lineCap = LineCap.Round;
        painter.lineJoin = LineJoin.Round;
        switch (kind)
        {
            case Kind.Swap:
                Triangle(painter, P(3f, 12f), P(9f, 7f), P(9f, 17f));
                Triangle(painter, P(21f, 12f), P(15f, 7f), P(15f, 17f));
                break;
            case Kind.Frame:
                foreach (var (x, y, dx, dy) in new[] { (4f, 4f, 1f, 1f), (20f, 4f, -1f, 1f), (4f, 20f, 1f, -1f), (20f, 20f, -1f, -1f) })
                {
                    painter.BeginPath();
                    painter.MoveTo(P(x, y + 5f * dy));
                    painter.LineTo(P(x, y));
                    painter.LineTo(P(x + 5f * dx, y));
                    painter.Stroke();
                }
                painter.BeginPath();
                painter.Arc(P(12f, 12f), 2.2f * scale, 0f, 360f);
                painter.Fill();
                break;
            case Kind.Turntable:
                painter.BeginPath();
                painter.Arc(P(12f, 13f), 8f * scale, 200f, 520f);
                painter.Stroke();
                Triangle(painter, P(4.6f, 6.2f), P(9.4f, 9.6f), P(3.4f, 11.6f));
                painter.BeginPath();
                painter.Arc(P(12f, 13f), 2.4f * scale, 0f, 360f);
                painter.Fill();
                break;
            case Kind.Compare:
                // Before as a solid half, after as a dashed one, split by the line of the slider.
                painter.BeginPath();
                painter.Arc(P(12f, 12f), 8.5f * scale, 90f, 270f);
                painter.Stroke();
                for (float start = 285f; start < 435f; start += 37.5f)
                {
                    painter.BeginPath();
                    painter.Arc(P(12f, 12f), 8.5f * scale, start, start + 18f);
                    painter.Stroke();
                }
                painter.lineWidth = 2.4f * scale;
                painter.BeginPath();
                painter.MoveTo(P(12f, 2f));
                painter.LineTo(P(12f, 22f));
                painter.Stroke();
                break;
        }
    }

    private static void Triangle(Painter2D painter, Vector2 a, Vector2 b, Vector2 c)
    {
        painter.BeginPath();
        painter.MoveTo(a);
        painter.LineTo(b);
        painter.LineTo(c);
        painter.ClosePath();
        painter.Fill();
    }
}
