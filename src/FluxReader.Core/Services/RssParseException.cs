namespace FluxReader.Core.Services;

public sealed class RssParseException : Exception
{
    public RssParseException(string message)
        : base(message)
    {
    }

    public RssParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
