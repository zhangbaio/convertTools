namespace ShortDrama.Infrastructure.Automation;

public sealed class MapleleafException : Exception
{
    public int Code { get; }

    public MapleleafException(string message, int code = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
