namespace PycRune.Decompiler;

using PycRune.AST;

public sealed class SymbolicStack
{
    private readonly List<ExprNode> _items = [];

    public int Count => _items.Count;

    public void Clear() => _items.Clear();

    public void Push(ExprNode expr) => _items.Add(expr);

    public ExprNode Pop()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("Stack underflow");
        }
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        return last;
    }

    public ExprNode Peek(int depth = 0)
    {
        if (depth >= _items.Count)
        {
            throw new InvalidOperationException($"Stack depth out of range: {depth} >= {_items.Count}");
        }
        return _items[_items.Count - 1 - depth];
    }

    public void Swap(int n)
    {
        if (n >= _items.Count)
        {
            throw new InvalidOperationException($"Swap index out of range: {n} >= {_items.Count}");
        }
        var topIdx = _items.Count - 1;
        var targetIdx = topIdx - n;
        var top = _items[topIdx];
        _items[topIdx] = _items[targetIdx];
        _items[targetIdx] = top;
    }

    public void Copy(int n)
    {
        if (n > _items.Count)
        {
            throw new InvalidOperationException($"Copy index out of range: {n} > {_items.Count}");
        }
        _items.Add(_items[_items.Count - n]);
    }

    public List<ExprNode> PopCount(int count)
    {
        if (count > _items.Count)
        {
            throw new InvalidOperationException($"Cannot pop {count} items from stack of {_items.Count}");
        }
        var result = _items.GetRange(_items.Count - count, count);
        _items.RemoveRange(_items.Count - count, count);
        return result;
    }

    public SymbolicStack Clone()
    {
        var copy = new SymbolicStack();
        copy._items.AddRange(_items);
        return copy;
    }

    public void RestoreFrom(SymbolicStack other)
    {
        _items.Clear();
        _items.AddRange(other._items);
    }
}
