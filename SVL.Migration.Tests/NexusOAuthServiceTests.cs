using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public class NexusOAuthServiceTests
{
    [TestMethod]
    public void CreateAuthorizationUrl_ShouldContainRequiredFields()
    {
        var service = new NexusOAuthService();

        var start = service.CreateAuthorizationUrl();

        Assert.IsFalse(string.IsNullOrWhiteSpace(start.AuthorizeUrl));
        StringAssert.Contains(start.AuthorizeUrl, "response_type=code");
        StringAssert.Contains(start.AuthorizeUrl, "code_challenge_method=S256");
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.State));
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.CodeVerifier));
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.RedirectUri));
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithCallbackUrl_ShouldSucceed()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?code=test-code&state=test-state",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("test-code", code);
        Assert.AreEqual("test-state", state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithRawCode_ShouldSucceed()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "raw-code",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("raw-code", code);
        Assert.IsNull(state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithUrlEncodedCode_ShouldDecode()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?code=abc%2Bdef%2Fxyz%3D&state=s1",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("abc+def/xyz=", code);
        Assert.AreEqual("s1", state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithoutCode_ShouldFail()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?state=test-only",
            out _,
            out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithInvalidUrl_ShouldFail()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/%zz",
            out _,
            out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void CreateAuthorizationUrl_WithCustomRedirect_ShouldUseCustomRedirect()
    {
        var service = new NexusOAuthService();

        var start = service.CreateAuthorizationUrl("http://127.0.0.1:13001");

        Assert.AreEqual("http://127.0.0.1:13001", start.RedirectUri);
        StringAssert.Contains(start.AuthorizeUrl, "redirect_uri=http%3A%2F%2F127.0.0.1%3A13001");
    }

    [TestMethod]
    public void TokenResultFailed_ShouldKeepFailureReasonAndAuthorizeUrl()
    {
        var result = NexusOAuthTokenResult.Failed(
            "timeout",
            NexusOAuthFailureReason.Timeout,
            "https://users.nexusmods.com/oauth/authorize");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NexusOAuthFailureReason.Timeout, result.FailureReason);
        Assert.AreEqual("https://users.nexusmods.com/oauth/authorize", result.AuthorizeUrl);
    }

    [TestMethod]
    public void TokenResultFailed_WithoutReason_ShouldUseUnknown()
    {
        var result = NexusOAuthTokenResult.Failed("unexpected");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NexusOAuthFailureReason.Unknown, result.FailureReason);
    }
}
