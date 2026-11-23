namespace PycRune.Model;

public sealed class FailFastException : InvalidOperationException
{
    public FailFastException(string message, Exception? inner = null) : base(message, inner) { }
}
