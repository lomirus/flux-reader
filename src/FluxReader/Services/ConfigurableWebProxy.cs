using System.Net;
using FluxReader.Models;

namespace FluxReader.Services;

internal sealed class ConfigurableWebProxy : IWebProxy
{
    private IWebProxy? _proxy = HttpClient.DefaultProxy;

    public ICredentials? Credentials
    {
        get => Volatile.Read(ref _proxy)?.Credentials;
        set
        {
            var proxy = Volatile.Read(ref _proxy);
            if (proxy is not null)
            {
                proxy.Credentials = value;
            }
        }
    }

    public Uri GetProxy(Uri destination) =>
        Volatile.Read(ref _proxy)?.GetProxy(destination) ?? destination;

    public bool IsBypassed(Uri host) =>
        Volatile.Read(ref _proxy)?.IsBypassed(host) ?? true;

    public void Configure(ProxyMode mode, string customProxyAddress)
    {
        IWebProxy? proxy = mode switch
        {
            ProxyMode.Disabled => null,
            ProxyMode.System => HttpClient.DefaultProxy,
            ProxyMode.Custom when TryNormalizeAddress(customProxyAddress, out var normalizedAddress) =>
                new WebProxy(normalizedAddress),
            _ => throw new ArgumentException("The proxy configuration is invalid.", nameof(customProxyAddress))
        };

        Volatile.Write(ref _proxy, proxy);
    }

    public static bool TryNormalizeAddress(string? value, out string normalizedAddress)
    {
        normalizedAddress = string.Empty;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) ||
            !IsSupportedScheme(uri.Scheme) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        normalizedAddress = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    private static bool IsSupportedScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase);
}
