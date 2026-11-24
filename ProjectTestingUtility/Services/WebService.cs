using NLog;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ProjectTestingUtility.Services;

public class WebService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly AuthorizationService _authorizationService;
    private readonly HttpClient _httpClient;

    public WebService(IHttpClientFactory httpClientFactory,
                      AuthorizationService authorizationService,
                      SettingsService settingsService)
    {
        _authorizationService = authorizationService;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = string.IsNullOrEmpty(settingsService.Settings.BaseUrl) ? throw new Exception("BaseUrl is empty") : new Uri(settingsService.Settings.BaseUrl);
    }

    public async Task<int[]> GetProjectsIdsAsync(int pageSize, string filters)
    {
        try
        {
            Logger.Debug($"Get project ids, pageSize - {pageSize}, filters - {filters}");

            await _authorizationService.GetAuthorizedHttpClient(_httpClient);

            var response = await _httpClient.GetAsync($"/api/v2/projects?pageSize={pageSize}&filters={filters}&sort=id&sort=desc");
            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            var ids = jsonResponse?["Content"]?
                      .AsArray()
                      .Select(item => item?["Id"]?.GetValue<int>() ?? 0)
                      .ToArray();

            return ids ?? Array.Empty<int>();
        }
        catch (Exception e)
        {
            Logger.Error(e, "An error occurred while getting projects ids");
            throw;
        }
    }

    public async Task<int> GetProjectsCountAsync(string filters)
    {
        try
        {
            Logger.Debug($"Get project count, filters - {filters}");

            await _authorizationService.GetAuthorizedHttpClient(_httpClient);

            var response = await _httpClient.GetAsync($"/api/v2/projects/count?filters={filters}");
            response.EnsureSuccessStatusCode();
            var stringResponse = await response.Content.ReadAsStringAsync();
            if (int.TryParse(stringResponse, out var count)) return count;
            
            Logger.Error($"Failed to parse project count from response: {stringResponse}");
            throw new InvalidOperationException("Failed to parse project count from response.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "An error occurred while getting projects count");
            throw;
        }
    }

    public async Task<bool> DownloadProjectByIdAsync(int id, string projectPath, CancellationToken cancellationToken = default)
    {
        try
        {
            Logger.Debug($"Download project, id - {id}");

            await _authorizationService.GetAuthorizedHttpClient(_httpClient);

            using var response = await _httpClient.GetAsync($"/api/v1/download-project/{id}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var fileStream = new FileStream(projectPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream, cancellationToken);

            Logger.Debug($"Project with id {id} has been downloaded");

            return true;
        }
        catch (Exception e)
        {
            Logger.Error(e, "An error occurred while downloading project");
            return false;
        }
    }
}
