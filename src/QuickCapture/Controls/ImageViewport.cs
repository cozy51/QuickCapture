using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuickCapture.Models;

namespace QuickCapture.Controls;

/// What a left drag on the picture writes: nothing, a wash of colour, a line, or
/// a label placed and moved by hand.
public enum DrawingTool { None, Highlighter, Pen, Text }

public sealed class ImageViewport : FrameworkElement, IDisposable
{
    private readonly ImageDocument document;
    private Point previous;
    private bool panning;
    private TextAnnotation? heldText;
    private TextAnnotation? draggedText;
    private TextAnnotation? selectedText;
    private Point textGrab;
    /// The pen this drag writes with: Shift or Ctrl can borrow one for a stroke.
    private HighlighterTool? drawing;
    private static readonly Pen LabelEdge = Frozen(Color.FromArgb(190, 47, 123, 246), 1, true);
    private static readonly Pen SelectedEdge = Frozen(Color.FromRgb(47, 123, 246), 2, false);
    private static Pen Frozen(Color color, double thickness, bool dashed)
    {
        var brush = new SolidColorBrush(color); brush.Freeze();
        var pen = new Pen(brush, thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        pen.Freeze(); return pen;
    }
    public HighlighterTool Highlighter { get; } = new();
    public HighlighterTool Pen { get; } = new() { Opaque = true, Width = 4, Opacity = 1, Color = DrawingPalette.Parse(AppSettings.DefaultPenColor) };
    public Color TextColor { get; set; } = DrawingPalette.Parse(AppSettings.DefaultTextColor);
    public double TextSize { get; set; } = TextAnnotation.DefaultSize;
    public DrawingTool Tool { get; private set; }
    public bool IsHighlighting => Tool == DrawingTool.Highlighter;
    /// The pen a drag draws with right now.
    public HighlighterTool Stroke => Tool == DrawingTool.Pen ? Pen : Highlighter;
    public bool IsInteracting => panning || drawing != null || heldText != null;
    /// The label the label tool is working on, outlined so it is easy to see.
    public TextAnnotation? SelectedText => selectedText;
    public ZoomController Zoom { get; } = new();
    public event Action? ViewChanged;
    public event Action<Point, Point>? ZoomSizeChanged;
    /// The label tool was clicked on empty picture: the window opens an editor.
    public event Action<Point>? TextRequested;
    internal enum DragAction { MoveWindow, PanImage, Highlight }
    /// Ctrl borrows the highlighter and Shift the plain pen for one drag.
    internal static DragAction ResolveDrag(bool drawing, ModifierKeys modifiers, bool space) =>
        space || (modifiers & ModifierKeys.Alt) != 0 ? DragAction.PanImage
        : drawing || (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ? DragAction.Highlight : DragAction.MoveWindow;
    private HighlighterTool StrokeFor(ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Shift) != 0 ? Pen : (modifiers & ModifierKeys.Control) != 0 && Tool != DrawingTool.Pen ? Highlighter : Stroke;
    public ImageViewport(ImageDocument document)
    {
        this.document = document;
        Focusable = true; ClipToBounds = true; Cursor = Cursors.SizeAll;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        document.Changed += Refresh;
        Loaded += (_, _) =>
        {
            Zoom.SetDpi(VisualTreeHelper.GetDpi(this).DpiScaleX, Center);
            if (document.Width / Zoom.DpiScale <= ActualWidth && document.Height / Zoom.DpiScale <= ActualHeight) { Zoom.ActualSize(RenderSize, ImageSize); Refresh(); } else Fit();
            Focus();
        };
        SizeChanged += (_, e) =>
        {
            if (Zoom.IsFit) Fit();
            else
            {
                // Native placement/monitor changes can resize after Loaded. Keep the
                // same source pixel in the center instead of leaving the image offscreen.
                Zoom.Pan(new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2));
                Refresh();
            }
        };
    }
    private Size ImageSize => new(document.Width, document.Height);
    public Point Center => new(ActualWidth / 2, ActualHeight / 2);
    public void Fit() { Zoom.Fit(RenderSize, ImageSize); Refresh(); }
    public void ActualSize()
    {
        var screen = IsLoaded ? PointToScreen(Center) : new Point();
        Zoom.ActualSize(RenderSize, ImageSize);
        if (IsLoaded) ZoomSizeChanged?.Invoke(new Point(document.Width / 2d, document.Height / 2d), screen);
        Refresh();
    }
    public void ToggleFit() { if (Zoom.IsFit) ActualSize(); else Fit(); }
    public void Refresh() { InvalidateVisual(); ViewChanged?.Invoke(); }
    public void SetTool(DrawingTool tool) { CancelInteraction(); selectedText = null; Tool = tool; UpdateCursor(); Refresh(); }
    public void ToggleHighlighter() => SetTool(Tool == DrawingTool.Highlighter ? DrawingTool.None : DrawingTool.Highlighter);
    public void TogglePen() => SetTool(Tool == DrawingTool.Pen ? DrawingTool.None : DrawingTool.Pen);
    public void ToggleText() => SetTool(Tool == DrawingTool.Text ? DrawingTool.None : DrawingTool.Text);
    /// The colour of whichever tool is in hand; picking one with no tool in hand
    /// reaches for the highlighter, as choosing a colour always has.
    public void SetColor(Color color)
    {
        switch (Tool)
        {
            case DrawingTool.Pen: Pen.Color = color; break;
            case DrawingTool.Text: TextColor = color; ReplaceSelected(label => label.Recoloured(color)); break;
            default: Highlighter.Color = color; if (Tool == DrawingTool.None) Tool = DrawingTool.Highlighter; break;
        }
        UpdateCursor(); Refresh();
    }
    public Color ToolColor => Tool switch { DrawingTool.Pen => Pen.Color, DrawingTool.Text => TextColor, _ => Highlighter.Color };
    /// Thinner or thicker for the pens; smaller or larger for the labels, which
    /// also resizes the one picked out, so a label can be adjusted after writing.
    public void ChangeWidth(int direction)
    {
        if (Tool == DrawingTool.Text)
        {
            TextSize = Math.Clamp(TextSize + direction * 2, 8, 200);
            ReplaceSelected(label => label.Resized(TextSize));
        }
        else Stroke.ChangeWidth(direction);
        Refresh();
    }
    private void ReplaceSelected(Func<TextAnnotation, TextAnnotation> change)
    {
        if (selectedText == null) return;
        var next = change(selectedText);
        document.Replace(selectedText, next);
        selectedText = next;
    }
    /// The label the window's editor just finished, in image pixels.
    public void AddText(Point origin, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        document.Add(new TextAnnotation(text, origin, TextColor, TextSize));
        Refresh();
    }
    /// Picks out the label at that point, as clicking it with the label tool does.
    public TextAnnotation? SelectText(Point image) { selectedText = TextAt(image); Refresh(); return selectedText; }
    private TextAnnotation? TextAt(Point image)
    {
        for (int i = document.Annotations.Count - 1; i >= 0; i--)
            if (document.Annotations[i] is TextAnnotation label && label.Contains(image)) return label;
        return null;
    }
    private bool Inside(Point image) => new Rect(0, 0, document.Width, document.Height).Contains(image);
    public void UpdateCursor() => Cursor = heldText != null ? Cursors.SizeAll : drawing != null ? Cursors.Pen : panning ? Cursors.Hand
        : ResolveDrag(Tool is DrawingTool.Highlighter or DrawingTool.Pen, Keyboard.Modifiers, Keyboard.IsKeyDown(Key.Space)) switch
        {
            DragAction.PanImage => Cursors.Hand,
            DragAction.Highlight => Cursors.Pen,
            _ => Tool == DrawingTool.Text ? Cursors.IBeam : Cursors.SizeAll
        };
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Zoom.SetDpi(newDpi.DpiScaleX, Center);
        if (Zoom.IsFit) Fit(); else Refresh();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 32)), null, new Rect(RenderSize));
        if (document.Image == null) return;
        dc.PushTransform(new MatrixTransform(Zoom.Matrix));
        dc.DrawImage(document.Image, new Rect(0, 0, document.Width, document.Height));
        // A label being dragged is drawn where it is going, not where it was.
        DrawingLayer.Render(dc, document, draggedText ?? drawing?.Preview, heldText);
        if (Tool == DrawingTool.Text) RenderLabelEdges(dc);
        dc.Pop();
    }
    /// Only on screen, never in the exported image: every label is outlined while
    /// the label tool is in hand, and the one picked out is outlined solidly.
    private void RenderLabelEdges(DrawingContext dc)
    {
        double scale = 1 / Math.Max(0.05, Zoom.ViewScale);
        var edge = LabelEdge.Clone(); edge.Thickness = LabelEdge.Thickness * scale; edge.Freeze();
        var picked = SelectedEdge.Clone(); picked.Thickness = SelectedEdge.Thickness * scale; picked.Freeze();
        foreach (var annotation in document.Annotations)
        {
            if (annotation is not TextAnnotation label || ReferenceEquals(label, heldText)) continue;
            var bounds = label.Bounds; bounds.Inflate(3 * scale, 2 * scale);
            dc.DrawRectangle(null, ReferenceEquals(label, selectedText) ? picked : edge, bounds);
        }
        if (draggedText != null)
        {
            var bounds = draggedText.Bounds; bounds.Inflate(3 * scale, 2 * scale);
            dc.DrawRectangle(null, picked, bounds);
        }
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (IsInteracting) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && Tool != DrawingTool.None) { ChangeWidth(Math.Sign(e.Delta)); return; }
        ZoomAt(e.Delta, e.GetPosition(this));
    }
    internal void ZoomAt(int delta, Point anchor)
    {
        var imageAnchor = Zoom.ToImage(anchor);
        var screenAnchor = PointToScreen(anchor);
        Zoom.Wheel(delta, anchor);
        ZoomSizeChanged?.Invoke(imageAnchor, screenAnchor);
        Refresh();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var action = ResolveDrag(Tool is DrawingTool.Highlighter or DrawingTool.Pen, Keyboard.Modifiers, Keyboard.IsKeyDown(Key.Space));
        var point = e.GetPosition(this);
        if (Tool == DrawingTool.Text && action == DragAction.MoveWindow)
        {
            var target = Zoom.ToImage(point);
            var label = TextAt(target);
            if (label != null)
            {
                selectedText = label; heldText = label; textGrab = target;
                CaptureMouse(); UpdateCursor(); Refresh(); e.Handled = true; return;
            }
            if (Inside(target)) { selectedText = null; TextRequested?.Invoke(target); Refresh(); e.Handled = true; return; }
        }
        if (e.ClickCount == 2 && action == DragAction.MoveWindow) { ToggleFit(); e.Handled = true; return; }
        if (action == DragAction.MoveWindow) { e.Handled = true; Window.GetWindow(this)?.DragMove(); return; }
        if (action == DragAction.Highlight)
        {
            var target = Zoom.ToImage(point);
            if (Inside(target)) { drawing = StrokeFor(Keyboard.Modifiers); drawing.Begin(target); CaptureMouse(); Refresh(); }
            e.Handled = true; return;
        }
        panning = true; previous = point; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdateCursor();
        if (heldText != null)
        {
            draggedText = heldText.Moved(Zoom.ToImage(e.GetPosition(this)) - textGrab);
            InvalidateVisual(); return;
        }
        if (drawing != null)
        {
            drawing.Add(Zoom.ToImage(e.GetPosition(this)), Math.Max(0.2, 0.75 / Zoom.ViewScale));
            InvalidateVisual(); return;
        }
        if (!panning) return;
        var point = e.GetPosition(this); Zoom.Pan(point - previous); previous = point; Refresh();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (heldText != null)
        {
            var (held, dragged) = (heldText, draggedText);
            heldText = null; draggedText = null; ReleaseMouseCapture();
            if (dragged != null) { document.Replace(held, dragged); selectedText = dragged; }
            e.Handled = true; UpdateCursor(); Refresh(); return;
        }
        if (drawing != null)
        {
            drawing.Add(Zoom.ToImage(e.GetPosition(this)), 0.01);
            var stroke = drawing.Finish(); drawing = null;
            if (stroke != null) document.Add(stroke);
        }
        panning = false; ReleaseMouseCapture(); e.Handled = true; UpdateCursor(); Refresh();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        panning = false; Highlighter.Cancel(); Pen.Cancel(); drawing = null; heldText = null; draggedText = null;
        InvalidateVisual(); base.OnLostMouseCapture(e);
    }
    public void CancelInteraction()
    {
        panning = false; Highlighter.Cancel(); Pen.Cancel(); drawing = null; heldText = null; draggedText = null;
        ReleaseMouseCapture(); Refresh();
    }
    public void CancelMode() { CancelInteraction(); selectedText = null; Tool = DrawingTool.None; UpdateCursor(); Refresh(); }
    public void Dispose() { document.Changed -= Refresh; CancelMode(); ViewChanged = null; ZoomSizeChanged = null; TextRequested = null; }
}
