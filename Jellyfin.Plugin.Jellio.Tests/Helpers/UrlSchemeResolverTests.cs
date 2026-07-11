using Jellyfin.Plugin.Jellio.Helpers;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Jellio.Tests.Helpers;

public class UrlSchemeResolverTests
{
    [Fact]
    public void Resolve_FallsBackToRequestScheme_WhenNoProxyHeadersPresent()
    {
        var request = NewRequest(scheme: "http");

        Assert.Equal("http", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_PrefersXForwardedProto_OverRequestScheme()
    {
        var request = NewRequest(scheme: "http");
        request.Headers["X-Forwarded-Proto"] = "https";

        // Without this, Cloudflare-tunneled deployments (HTTPS client -> HTTP origin)
        // would emit http:// URLs that the proxy then 404s.
        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_TakesFirstValueFromCommaSeparatedXForwardedProto()
    {
        var request = NewRequest(scheme: "http");
        request.Headers["X-Forwarded-Proto"] = "https, http";

        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_HonorsRfc7239ForwardedHeader()
    {
        var request = NewRequest(scheme: "http");
        request.Headers["Forwarded"] = "proto=https;host=jellyfin.example.com";

        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_HandlesQuotedProtoInForwardedHeader()
    {
        var request = NewRequest(scheme: "http");
        request.Headers["Forwarded"] = "proto=\"https\";host=jellyfin.example.com";

        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_FallsBackToRequestScheme_WhenForwardedHeaderHasNoProto()
    {
        var request = NewRequest(scheme: "https");
        request.Headers["Forwarded"] = "host=jellyfin.example.com";

        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Fact]
    public void Resolve_XForwardedProtoWinsOverForwardedHeader()
    {
        var request = NewRequest(scheme: "http");
        request.Headers["X-Forwarded-Proto"] = "https";
        request.Headers["Forwarded"] = "proto=http";

        Assert.Equal("https", UrlSchemeResolver.Resolve(request));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8096")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:8096")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8096")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.42")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    public void IsLocalHost_TrueForLoopbackAndPrivateAddresses(string host)
    {
        Assert.True(UrlSchemeResolver.IsLocalHost(host));
    }

    [Theory]
    [InlineData("jellyfin.corter.xyz")]
    [InlineData("jellyfin.example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // outside the 172.16/12 private range
    [InlineData("11.0.0.1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-host")]
    public void IsLocalHost_FalseForPublicHosts(string host)
    {
        Assert.False(UrlSchemeResolver.IsLocalHost(host));
    }

    private static HttpRequest NewRequest(string scheme)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        return context.Request;
    }
}
