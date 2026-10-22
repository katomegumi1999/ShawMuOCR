using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public interface IEditorCommand
{
    void Execute();
    void Undo();
}

public sealed class AddElementCommand : IEditorCommand
{
    private readonly IList<IImageElement> _list;
    private readonly IImageElement _element;

    public AddElementCommand(IList<IImageElement> list, IImageElement element)
    {
        _list = list;
        _element = element;
    }

    public void Execute()
    {
        if (!_list.Contains(_element))
        {
            _list.Add(_element);
        }
    }

    public void Undo()
    {
        _list.Remove(_element);
    }
}

public sealed class RemoveElementCommand : IEditorCommand
{
    private readonly IList<IImageElement> _list;
    private readonly IImageElement _element;
    private int _index = -1;

    public RemoveElementCommand(IList<IImageElement> list, IImageElement element)
    {
        _list = list;
        _element = element;
    }

    public void Execute()
    {
        _index = _list.IndexOf(_element);
        if (_index >= 0)
        {
            _list.RemoveAt(_index);
        }
    }

    public void Undo()
    {
        if (_index >= 0 && _index <= _list.Count)
        {
            _list.Insert(_index, _element);
        }
        else
        {
            _list.Add(_element);
        }
    }
}

public sealed class MoveElementCommand : IEditorCommand
{
    private readonly IImageElement _element;
    private readonly float _dx;
    private readonly float _dy;

    public MoveElementCommand(IImageElement element, float dx, float dy)
    {
        _element = element;
        _dx = dx;
        _dy = dy;
    }

    public void Execute()
    {
        _element.MoveBy(_dx, _dy);
    }

    public void Undo()
    {
        _element.MoveBy(-_dx, -_dy);
    }
}

public sealed class TransformElementCommand : IEditorCommand
{
    private readonly IImageElement _element;
    private readonly IImageElement _before;
    private readonly IImageElement _after;

    public TransformElementCommand(IImageElement element, IImageElement before, IImageElement after)
    {
        _element = element;
        _before = before;
        _after = after;
    }

    public void Execute()
    {
        _element.Rotation = _after.Rotation;
        _element.Resize(_after.BoundingBox);
    }

    public void Undo()
    {
        _element.Rotation = _before.Rotation;
        _element.Resize(_before.BoundingBox);
    }
}

public sealed class ClearAllElementsCommand : IEditorCommand
{
    private readonly IList<IImageElement> _list;
    private readonly List<IImageElement> _snapshot;

    public ClearAllElementsCommand(IList<IImageElement> list)
    {
        _list = list;
        _snapshot = new List<IImageElement>(list);
    }

    public void Execute()
    {
        _list.Clear();
    }

    public void Undo()
    {
        _list.Clear();
        foreach (var item in _snapshot)
        {
            _list.Add(item);
        }
    }
}

public sealed class CropDocumentCommand : IEditorCommand
{
    private readonly ImageDocument _doc;
    private readonly ShawMuOCR.Core.OCR.OcrImage _oldImage;
    private readonly ShawMuOCR.Core.OCR.OcrImage _newImage;
    private readonly List<IImageElement> _oldElements;
    private readonly List<IImageElement> _newElements;

    public CropDocumentCommand(ImageDocument doc, ShawMuOCR.Core.OCR.OcrImage newImage, List<IImageElement> newElements)
    {
        _doc = doc;
        _oldImage = doc.OriginalImage;
        _newImage = newImage;
        _oldElements = new List<IImageElement>(doc.Elements);
        _newElements = newElements;
    }

    public void Execute()
    {
        _doc.SetImageAndElements(_newImage, _newElements);
    }

    public void Undo()
    {
        _doc.SetImageAndElements(_oldImage, _oldElements);
    }
}

public sealed class UndoRedoManager
{
    private readonly Stack<IEditorCommand> _undoStack = new();
    private readonly Stack<IEditorCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? StateChanged;

    public void Execute(IEditorCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();
        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke();
    }
}
