using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class RemoteAuthenticationResultTests
{
    [TestMethod]
    public void LoginRedirectIsDistinctFromAnApiStatus()
    {
        var result = RemoteAuthenticationResult.FromReply(
            new BridgeReply(0, false, "sensitive-body", false, "private-url", LoginRedirect: true));

        Assert.IsFalse(result.Authenticated);
        Assert.IsTrue(result.LoginRedirect);
        Assert.IsNull(result.HttpStatus);
        Assert.AreEqual("login-redirect", result.DiagnosticCode);
    }

    [TestMethod]
    public void ApiUnauthorizedResponseRetainsStatusWithoutBecomingLoginRedirect()
    {
        var result = RemoteAuthenticationResult.FromReply(
            new BridgeReply(401, false, "sensitive-body", false, "private-url"));

        Assert.IsFalse(result.Authenticated);
        Assert.IsFalse(result.LoginRedirect);
        Assert.AreEqual(401, result.HttpStatus);
        Assert.AreEqual("http-status-401", result.DiagnosticCode);
    }

    [TestMethod]
    public void ExceptionDiagnosticKeepsOnlyTheExceptionClass()
    {
        var result = RemoteAuthenticationResult.FromException(
            new InvalidOperationException("private-url and response body"));

        Assert.IsFalse(result.Authenticated);
        Assert.AreEqual("InvalidOperationException", result.FailureClass);
        Assert.AreEqual("exception-InvalidOperationException", result.DiagnosticCode);
        Assert.IsFalse(result.DiagnosticCode.Contains("private-url", StringComparison.Ordinal));
        Assert.IsFalse(result.DiagnosticCode.Contains("response body", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UgreenLinkDesktopRedirectIsClassifiedAsLoginRedirect()
    {
        var loginPage = new Uri("https://login.eur3.ug.link/desktop/");
        var unrelatedPage = new Uri("https://login.eur3.ug.link/account/");

        Assert.IsTrue(RemoteBridge.IsUgreenLinkLoginPage(loginPage));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkLoginPage(unrelatedPage));

        var result = RemoteAuthenticationResult.FromException(new UgreenLinkLoginRedirectException());
        Assert.IsTrue(result.LoginRedirect);
        Assert.AreEqual("login-redirect", result.DiagnosticCode);
    }

    [TestMethod]
    public void SuccessfulApiResponseMarksAuthentication()
    {
        var result = RemoteAuthenticationResult.FromReply(new BridgeReply(200, true, "{\"name\":\"\",\"isDirectory\":true}", false, null));

        Assert.IsTrue(result.Authenticated);
        Assert.AreEqual(200, result.HttpStatus);
        Assert.AreEqual("http-status-200", result.DiagnosticCode);
    }

    [TestMethod]
    public void StaleAuthenticationProbeCannotRestoreOrClearNewerNavigationState()
    {
        var state = new RemoteAuthenticationState();
        var staleGeneration = state.CaptureGeneration();
        state.Invalidate();
        var currentGeneration = state.CaptureGeneration();

        Assert.IsFalse(state.TrySetResult(staleGeneration, authenticated: true));
        Assert.IsFalse(state.IsAuthenticated);
        Assert.IsTrue(state.TrySetResult(currentGeneration, authenticated: true));
        Assert.IsTrue(state.IsAuthenticated);
        Assert.IsFalse(state.TrySetResult(staleGeneration, authenticated: false));
        Assert.IsTrue(state.IsAuthenticated);

        state.Invalidate();
        Assert.IsFalse(state.IsAuthenticated);
    }

    [TestMethod]
    public void AuthenticationProbeIsPausedDuringShortcutTicketCleanup()
    {
        Assert.IsTrue(RemoteProbePolicy.CanProbe(
            serviceRootActive: true, loginFlowActive: false, cleanupRequired: false,
            cleanupInProgress: false, probeInProgress: false));
        Assert.IsFalse(RemoteProbePolicy.CanProbe(
            serviceRootActive: true, loginFlowActive: false, cleanupRequired: true,
            cleanupInProgress: false, probeInProgress: false));
        Assert.IsFalse(RemoteProbePolicy.CanProbe(
            serviceRootActive: true, loginFlowActive: false, cleanupRequired: false,
            cleanupInProgress: true, probeInProgress: false));
        Assert.IsFalse(RemoteProbePolicy.CanProbe(
            serviceRootActive: true, loginFlowActive: true, cleanupRequired: false,
            cleanupInProgress: false, probeInProgress: false));
        Assert.IsFalse(RemoteProbePolicy.CanProbe(
            serviceRootActive: true, loginFlowActive: false, cleanupRequired: false,
            cleanupInProgress: false, probeInProgress: true));
    }

    [TestMethod]
    public void Http200WithoutExpectedRootDirectoryStatIsRejected()
    {
        foreach (var body in new[] { "{}", "<html>login</html>", "{\"name\":\"document.txt\",\"isDirectory\":false}", "not-json" })
        {
            var result = RemoteAuthenticationResult.FromReply(new BridgeReply(200, true, body, false, null));
            Assert.IsFalse(result.Authenticated);
            Assert.AreEqual(200, result.HttpStatus);
            Assert.AreEqual("invalid-root-stat-response", result.DiagnosticCode);
        }
    }

    [TestMethod]
    public void OnlyRootStatHttp200CountsAsAuthenticated()
    {
        var noContent = RemoteAuthenticationResult.FromReply(new BridgeReply(204, true, "", false, null));

        Assert.IsFalse(noContent.Authenticated);
        Assert.AreEqual(204, noContent.HttpStatus);
    }

    [TestMethod]
    public void LoginRedirectDomainRequiresHttpsAndDesktopPath()
    {
        Assert.IsTrue(RemoteBridge.IsUgreenLinkLoginPage(new Uri("https://login.eur3.ug.link/desktop/")));
        Assert.IsTrue(RemoteBridge.IsUgreenLinkLoginPage(new Uri("https://login.eur3.ug.link/desktop/#/")));
        Assert.IsTrue(RemoteBridge.IsUgreenLinkLoginPage(new Uri("https://login.eur3.ug.link/desktop/#/apps")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkLoginPage(new Uri("http://login.eur3.ug.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkLoginPage(new Uri("https://ug.link.attacker.example/desktop/")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkLoginPage(new Uri("https://login.eur3.ug.link/desktop/#external")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkLoginPage(
            new Uri("https://login.eur3.ug.link/desktop/#/" + new string('x', 513))));
    }

    [TestMethod]
    public void BrowserReplyMustComeFromCurrentConfiguredDocument()
    {
        var configured = new Uri("https://remote.ugdocker.link/");

        Assert.IsTrue(RemoteBridge.SameConfiguredDocument(
            "https://remote.ugdocker.link/", "https://remote.ugdocker.link/", configured));
        Assert.IsFalse(RemoteBridge.SameConfiguredDocument(
            "https://login.eur3.ug.link/desktop/", "https://remote.ugdocker.link/", configured));
        Assert.IsFalse(RemoteBridge.SameConfiguredDocument(
            "https://remote.ugdocker.link/", "https://remote.ugdocker.link/api/v1/health", configured));
        Assert.IsFalse(RemoteBridge.SameConfiguredDocument(
            "https://remote.ugdocker.link/", "https://remote.ugdocker.link/api/ugreen/auth?ticket=example", configured));
    }

    [TestMethod]
    public void HttpLoginRedirectUpgradeIsOneShotAndBoundToServiceNavigation()
    {
        var expected = new Uri("http://login.eur3.ug.link/desktop/?state=example");
        Assert.IsTrue(RemoteBridge.IsExpectedHttpLoginRedirect(expected));
        var spaRoot = new Uri("http://login.eur3.ug.link/desktop/?state=example#/");
        Assert.IsTrue(RemoteBridge.IsExpectedHttpLoginRedirect(spaRoot));
        Assert.IsTrue(RemoteBridge.ShouldUpgradeHttpLoginRedirect(spaRoot, true, 43, 43, true, false));
        Assert.IsTrue(RemoteBridge.IsExpectedHttpLoginRedirect(
            new Uri("http://login.eur3.ug.link/desktop/?state=example#/apps")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(
            new Uri("http://login.eur3.ug.link/desktop/?state=example#external")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(
            new Uri("http://login.eur3.ug.link/desktop/?state=example#/" + new string('x', 513))));
        Assert.IsTrue(RemoteBridge.ShouldUpgradeHttpLoginRedirect(expected, true, 42, 42, true, false));
        Assert.IsFalse(RemoteBridge.ShouldUpgradeHttpLoginRedirect(expected, false, 42, 42, true, false));
        Assert.IsFalse(RemoteBridge.ShouldUpgradeHttpLoginRedirect(expected, true, 42, 41, true, false));
        Assert.IsFalse(RemoteBridge.ShouldUpgradeHttpLoginRedirect(expected, true, 42, 42, false, false));
        Assert.IsFalse(RemoteBridge.ShouldUpgradeHttpLoginRedirect(expected, true, 42, 42, true, true));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(new Uri("http://nas.ugdocker.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(new Uri("http://login.eur3.ug.link/account/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(new Uri("https://login.eur3.ug.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpLoginRedirect(
            new Uri("http://login.eur3.ug.link/desktop/?state=" + new string('x', 2050))));
    }

    [TestMethod]
    public void DeferredHttpsLoginTargetMustRemainOnExpectedUgreenDesktopOrigin()
    {
        Assert.IsTrue(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link/desktop/?state=example")));
        Assert.IsTrue(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link/desktop/?state=example#/")));
        Assert.IsTrue(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link/desktop/?state=example#/apps")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link/desktop/?state=example#external")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("http://login.eur3.ug.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://ug.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link/account/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link:8443/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://login.eur3.ug.link.attacker.example/desktop/")));
        Assert.IsFalse(RemoteBridge.IsExpectedHttpsLoginRedirect(new Uri("https://user@login.eur3.ug.link/desktop/")));
    }

    [TestMethod]
    public void ExistingApprovedLoginDocumentMatchesWithoutComparingQueryState()
    {
        var target = new Uri("https://login.eur3.ug.link/desktop/?state=new-state");

        Assert.IsTrue(RemoteBridge.IsSameExpectedHttpsLoginDocument(
            "https://login.eur3.ug.link/desktop/?state=old-state", target));
        Assert.IsFalse(RemoteBridge.IsSameExpectedHttpsLoginDocument(
            "https://other.eur3.ug.link/desktop/?state=old-state", target));
        Assert.IsFalse(RemoteBridge.IsSameExpectedHttpsLoginDocument(
            "https://login.eur3.ug.link/desktop/home?state=old-state", target));
        Assert.IsFalse(RemoteBridge.IsSameExpectedHttpsLoginDocument(
            "http://login.eur3.ug.link/desktop/?state=old-state", target));
        Assert.IsFalse(RemoteBridge.IsSameExpectedHttpsLoginDocument(
            "https://login.eur3.ug.link.attacker.example/desktop/?state=old-state", target));
    }

    [TestMethod]
    public void PopupFromTrustedUgreenDesktopMayOnlyOpenExactConfiguredShortcutRoot()
    {
        var trustedDesktop = "https://nas.eur3.ug.link/desktop/";
        var configuredRoot = new Uri("https://remote.ugdocker.link/");

        Assert.IsTrue(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("https://remote.ugdocker.link/"), configuredRoot));
        Assert.IsTrue(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("https://remote.ugdocker.link"), configuredRoot),
            "Uri canonicalizes a host-only URL to the same root path; a trailing slash is not an origin mismatch.");
        var sameOriginNoSlash = RemoteBridge.ComparePopupTarget(new Uri("https://remote.ugdocker.link"), configuredRoot);
        Assert.IsTrue(sameOriginNoSlash.Https);
        Assert.IsTrue(sameOriginNoSlash.HostMatchesConfigured);
        Assert.IsTrue(sameOriginNoSlash.RootPath);
        Assert.IsTrue(sameOriginNoSlash.QueryAbsent);
        var alternateDockerHost = RemoteBridge.ComparePopupTarget(new Uri("https://other.ugdocker.link"), configuredRoot);
        Assert.IsFalse(alternateDockerHost.HostMatchesConfigured);
        Assert.IsTrue(alternateDockerHost.UgreenDockerHost);
        Assert.IsTrue(alternateDockerHost.RootPath);
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            "https://nas.eur3.ug.link/desktop/apps", trustedDesktop,
            new Uri("https://remote.ugdocker.link/"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("https://other.ugdocker.link/"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("http://remote.ugdocker.link/"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("https://remote.ugdocker.link/?ticket=secret"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            trustedDesktop, trustedDesktop, new Uri("https://remote.ugdocker.link/api/v1/stat"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedInAppPopupTarget(
            "https://remote.ugdocker.link/", trustedDesktop,
            new Uri("https://remote.ugdocker.link/"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsTrustedUgreenDesktopDocument("https://nas.eur3.ug.link/desktop/apps"));
        Assert.IsFalse(RemoteBridge.IsTrustedUgreenDesktopDocument("http://nas.eur3.ug.link/desktop/"));
        Assert.IsTrue(RemoteBridge.IsTrustedUgreenDesktopDocument("https://nas.eur3.ug.link/desktop/#/"));
        Assert.IsTrue(RemoteBridge.IsTrustedUgreenDesktopDocument("https://nas.eur3.ug.link/desktop/#/apps"));
        Assert.IsFalse(RemoteBridge.IsTrustedUgreenDesktopDocument("https://nas.eur3.ug.link/desktop/#external"));
    }

    [TestMethod]
    public void AuthBootstrapPopupRequiresExactConfiguredOriginTrustedFrameAndBoundedQuery()
    {
        var desktop = "https://nas.eur3.ug.link/desktop/";
        var configuredRoot = new Uri("https://remote.ugdocker.link/");
        var approved = new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=example");

        Assert.IsTrue(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(desktop, desktop, approved, configuredRoot));
        Assert.IsTrue(RemoteBridge.IsExpectedAuthBootstrapNavigation(approved, approved, isRedirected: false));
        Assert.IsFalse(RemoteBridge.IsExpectedAuthBootstrapNavigation(approved, approved, isRedirected: true));
        Assert.IsFalse(RemoteBridge.IsExpectedAuthBootstrapNavigation(
            new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=other"), approved, isRedirected: false));

        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            "https://other.eur3.ug.link/desktop/", desktop, approved, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, "https://other.eur3.ug.link/desktop/", approved, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("http://remote.ugdocker.link/api/ugreen/auth?ticket=example"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("https://other.ugdocker.link/api/ugreen/auth?ticket=example"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth/extra?ticket=example"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=example#fragment"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop, new Uri("https://user@remote.ugdocker.link/api/ugreen/auth?ticket=example"), configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            desktop, desktop,
            new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=" + new string('x', 2049)), configuredRoot));

        var comparison = RemoteBridge.ComparePopupTarget(approved, configuredRoot);
        Assert.IsTrue(comparison.ExactAuthBootstrapPath);
        Assert.IsTrue(comparison.NonemptyBoundedQuery);
    }

    [TestMethod]
    public void BridgeSendsOnlyFromConfirmedRootAndOnlyRootStatBeforeAuthentication()
    {
        var configuredRoot = new Uri("https://remote.ugdocker.link/");
        var root = new Uri("https://remote.ugdocker.link/");
        var authRoute = new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=example");

        Assert.IsTrue(RemoteBridge.CanSendApiRequestFromDocument(root, configuredRoot, authenticationProbe: true, authenticated: false));
        Assert.IsFalse(RemoteBridge.CanSendApiRequestFromDocument(root, configuredRoot, authenticationProbe: false, authenticated: false));
        Assert.IsTrue(RemoteBridge.CanSendApiRequestFromDocument(root, configuredRoot, authenticationProbe: false, authenticated: true));
        Assert.IsFalse(RemoteBridge.CanSendApiRequestFromDocument(authRoute, configuredRoot, authenticationProbe: true, authenticated: false));
        Assert.IsFalse(RemoteBridge.CanSendApiRequestFromDocument(authRoute, configuredRoot, authenticationProbe: false, authenticated: true));

        Assert.IsTrue(RemoteBridge.IsRootStatAuthenticationProbe(
            "GET", new Uri("https://remote.ugdocker.link/api/v1/stat?path=")));
        Assert.IsFalse(RemoteBridge.IsRootStatAuthenticationProbe(
            "POST", new Uri("https://remote.ugdocker.link/api/v1/stat?path=")));
        Assert.IsFalse(RemoteBridge.IsRootStatAuthenticationProbe(
            "GET", new Uri("https://remote.ugdocker.link/api/v1/stat?path=somewhere")));
    }
}
