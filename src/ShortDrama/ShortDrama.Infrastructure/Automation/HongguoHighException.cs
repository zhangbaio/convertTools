namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoHighException : Exception
{
    public int Code { get; }

    public HongguoHighException(string message, int code = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
