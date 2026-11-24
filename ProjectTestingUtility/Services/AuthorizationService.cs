using ProjectTestingUtility.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ProjectTestingUtility.Services;

public class AuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly Settings _settings;
    private DateTime _lastAuthTime;
    private string _bearerToken = "";

    public AuthorizationService(IHttpClientFactory httpClientFactory,
                                SettingsService settingsService)
    {
        _settings = settingsService.Settings;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = string.IsNullOrEmpty(_settings.AuthorizerUrl) ? throw new Exception("AuthorizerUrl is empty") : new Uri(_settings.AuthorizerUrl);
    }

    public async Task<bool> Authorize()
    {
        // Учётные данные не хранятся в исходниках - они обязаны быть заданы в settings.json
        if (string.IsNullOrWhiteSpace(_settings.Login) || string.IsNullOrWhiteSpace(_settings.Password))
            throw new UnauthorizedAccessException("Login and Password must be set in settings.json");

        using var response = await _httpClient.PostAsJsonAsync("/api/v1/authenticate",
                                                          new { username = _settings.Login, password = _settings.Password });

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("The project service rejected the configured credentials.");
        response.EnsureSuccessStatusCode();

        var tokenInfo = await response.Content.ReadFromJsonAsync<TokenInfo>();
        if (string.IsNullOrWhiteSpace(tokenInfo?.AccessToken))
            throw new UnauthorizedAccessException("The project service did not return an access token.");
        _bearerToken = tokenInfo.AccessToken;
        _lastAuthTime = DateTime.Now;

        return true;
    }

    public async Task GetAuthorizedHttpClient(HttpClient httpClient)
    {
        if (string.IsNullOrEmpty(_bearerToken) || DateTime.Now - _lastAuthTime > TimeSpan.FromMinutes(1))
            await Authorize();

        if (!string.IsNullOrEmpty(_bearerToken))
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
    }
}
