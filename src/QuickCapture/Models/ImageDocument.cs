using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace QuickCapture.Models;

public sealed class ImageDocument(BitmapSource image, DateTimeOffset? capturedAt = null) : IDisposable
{
    public DateTimeOffset CapturedAt { get; } = capturedAt ?? DateTimeOffset.Now;
    private readonly List<IAnnotation> annotations = new();
    public BitmapSource? Image { get; private set; } = image;
    public int Width => Image?.PixelWidth ?? 0;
    public int Height => Image?.PixelHeight ?? 0;
    public IReadOnlyList<IAnnotation> Annotations => annotations.AsReadOnly();
    public UndoRedoManager History { get; } = new();
    public event Action? Changed;
    public void Add(IAnnotation annotation) { History.Execute(new AddAction(annotations, annotation)); Changed?.Invoke(); }
    /// Moving or recolouring a label puts an equal one in its place, as one step.
    public void Replace(IAnnotation existing, IAnnotation replacement)
    {
        int index = annotations.IndexOf(existing);
        if (index < 0) return;
        History.Execute(new ReplaceAction(annotations, index, existing, replacement)); Changed?.Invoke();
    }
    public void Clear() { if (annotations.Count == 0) return; History.Execute(new ClearAction(annotations)); Changed?.Invoke(); }
    public void Undo() { History.Undo(); Changed?.Invoke(); }
    public void Redo() { History.Redo(); Changed?.Invoke(); }
    public void Dispose() { Image = null; annotations.Clear(); History.Reset(); Changed = null; }
    private sealed class AddAction(List<IAnnotation> target, IAnnotation annotation) : IUndoableAction
    {
        public void Execute() => target.Add(annotation);
        public void Undo() => target.RemoveAt(target.Count - 1);
    }
    private sealed class ReplaceAction(List<IAnnotation> target, int index, IAnnotation from, IAnnotation to) : IUndoableAction
    {
        public void Execute() => target[index] = to;
        public void Undo() => target[index] = from;
    }
    private sealed class ClearAction(List<IAnnotation> target) : IUndoableAction
    {
        private readonly IAnnotation[] saved = target.ToArray();
        public void Execute() => target.Clear();
        public void Undo() => target.AddRange(saved);
    }
}
