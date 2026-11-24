using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;

// All authentication responses are generated in memory. No real credentials or network calls.
foreach (var settings in new[]
{
    new Settings { Login = "", Password = "synthetic-password" },
    new Settings { Login = "synthetic-user", Password = "   " }
})
{
    using var factory = new FakeFactory((_, _) => throw new Exception("Blank credentials must not make HTTP requests."));
    var service = new AuthorizationService(factory, new SettingsService { Settings = settings });
    using var target = new HttpClient();
    var exception = await RequireFailureAsync<UnauthorizedAccessException>(() => service.GetAuthorizedHttpClient(target));
    Require(factory.RequestCount == 0, "Blank credentials caused an HTTP request.");
    Require(target.DefaultRequestHeaders.Authorization == null, "Blank credentials produced an auth header.");
    Require(!exception.Message.Contains("synthetic"), "Error exposed a credential value.");
}
Console.WriteLine("PASS blank credentials fail before HTTP without secret values");

foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
{
    using var factory = new FakeFactory((_, _) => Response(status, "synthetic-server-secret"));
    var service = new AuthorizationService(factory, ConfiguredSettings());
    using var target = new HttpClient();
    var exception = await RequireFailureAsync<UnauthorizedAccessException>(() => service.GetAuthorizedHttpClient(target));
    Require(factory.RequestCount == 1, "Rejected credentials should make one authentication request.");
    Require(target.DefaultRequestHeaders.Authorization == null, "Rejected credentials produced an auth header.");
    Require(!exception.Message.Contains("synthetic"), "Error exposed credentials or server response content.");
}
Console.WriteLine("PASS 401/403 produce an explicit safe authentication failure");

foreach (var payload in new[] { "null", "{}", "{\"access_token\":\" \"}" })
{
    using var factory = new FakeFactory((_, _) => Response(HttpStatusCode.OK, payload));
    var service = new AuthorizationService(factory, ConfiguredSettings());
    using var target = new HttpClient();
    await RequireFailureAsync<UnauthorizedAccessException>(() => service.GetAuthorizedHttpClient(target));
    Require(target.DefaultRequestHeaders.Authorization == null, "Missing access token produced an auth header.");
}
Console.WriteLine("PASS missing/empty access tokens fail without forwarding an auth header");

using (var factory = new FakeFactory((_, _) => Response(HttpStatusCode.ServiceUnavailable, "synthetic-server-secret")))
{
    var service = new AuthorizationService(factory, ConfiguredSettings());
    using var target = new HttpClient();
    var exception = await RequireFailureAsync<HttpRequestException>(() => service.GetAuthorizedHttpClient(target));
    Require(exception.StatusCode == HttpStatusCode.ServiceUnavailable, "HTTP error status was lost.");
    Require(target.DefaultRequestHeaders.Authorization == null, "HTTP failure produced an auth header.");
    Require(!exception.Message.Contains("synthetic"), "HTTP error exposed server response content.");
}
Console.WriteLine("PASS service outage stays an HTTP failure without exposing response content");

using (var factory = new FakeFactory((request, number) =>
{
    Require(request.Method == HttpMethod.Post, "Authentication must POST.");
    Require(request.RequestUri == new Uri("http://auth.invalid/api/v1/authenticate"), "Authentication endpoint changed.");
    var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    Require(payload.RootElement.GetProperty("username").GetString() == "synthetic-user", "Username was not forwarded.");
    Require(payload.RootElement.GetProperty("password").GetString() == "synthetic-password", "Password was not forwarded.");
    return Response(HttpStatusCode.OK, $"{{\"access_token\":\"synthetic-token-{number}\"}}");
}))
{
    var service = new AuthorizationService(factory, ConfiguredSettings());
    using var first = new HttpClient();
    await service.GetAuthorizedHttpClient(first);
    Require(first.DefaultRequestHeaders.Authorization?.Scheme == "Bearer", "Bearer scheme missing.");
    Require(first.DefaultRequestHeaders.Authorization?.Parameter == "synthetic-token-1", "First token was not forwarded.");
    using var cached = new HttpClient();
    await service.GetAuthorizedHttpClient(cached);
    Require(factory.RequestCount == 1, "Fresh token was not reused.");
    Require(cached.DefaultRequestHeaders.Authorization?.Parameter == "synthetic-token-1", "Cached token was not forwarded.");
    typeof(AuthorizationService).GetField("_lastAuthTime", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(service, DateTime.Now.AddMinutes(-2));
    using var refreshed = new HttpClient();
    await service.GetAuthorizedHttpClient(refreshed);
    Require(factory.RequestCount == 2, "Expired token was not refreshed.");
    Require(refreshed.DefaultRequestHeaders.Authorization?.Parameter == "synthetic-token-2", "Refreshed token was not forwarded.");
}
Console.WriteLine("PASS successful authorization forwards, reuses and refreshes the bearer token");

using (var factory = new FakeFactory((_, number) => number == 1
    ? Response(HttpStatusCode.OK, "{\"access_token\":\"synthetic-initial-token\"}")
    : Response(HttpStatusCode.Unauthorized, "synthetic-secret-response")))
{
    var service = new AuthorizationService(factory, ConfiguredSettings());
    using var initial = new HttpClient();
    await service.GetAuthorizedHttpClient(initial);
    typeof(AuthorizationService).GetField("_lastAuthTime", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(service, DateTime.Now.AddMinutes(-2));
    using var failedRefresh = new HttpClient();
    await RequireFailureAsync<UnauthorizedAccessException>(() => service.GetAuthorizedHttpClient(failedRefresh));
    Require(failedRefresh.DefaultRequestHeaders.Authorization == null, "Failed refresh forwarded an expired token.");
    using var nextAttempt = new HttpClient();
    await RequireFailureAsync<UnauthorizedAccessException>(() => service.GetAuthorizedHttpClient(nextAttempt));
    Require(factory.RequestCount == 3, "A failed refresh incorrectly extended token freshness.");
}
Console.WriteLine("PASS failed refresh cannot forward or prolong an expired token");

static SettingsService ConfiguredSettings() => new()
{
    Settings = new Settings { BaseUrl = "http://auth.invalid", Login = "synthetic-user", Password = "synthetic-password" }
};
static HttpResponseMessage Response(HttpStatusCode status, string content) => new(status)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json")
};
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task<T> RequireFailureAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeFactory(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
{
    public int RequestCount { get; private set; }
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request, ++RequestCount));
}

namespace ProjectTestingUtility.Services
{
    public sealed class SettingsService
    {
        public Settings Settings { get; set; } = new();
    }
}
