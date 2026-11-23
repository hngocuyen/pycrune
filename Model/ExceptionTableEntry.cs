namespace PycRune.Model;

public sealed record ExceptionTableEntry(
    int Start,
    int End,
    int Target,
    int Depth,
    bool LastI
)
{
    public bool Encloses(int offset) => offset >= Start && offset < End;
}
