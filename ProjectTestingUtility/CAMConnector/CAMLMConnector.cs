using CAMLMInterfaces;
using NLog;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;
using System.Runtime.InteropServices;

namespace ProjectTestingUtility.CAMConnector;

public class CAMLMConnector
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Ключ авторизации CAMLM не хранится в исходниках:
    // берётся из settings.json (CamlmAuthKey) или из переменной окружения
    private const string AUTH_KEY_ENV_VARIABLE = "PROJECTTESTER_CAMLM_KEY";

    private readonly SettingsService _settingsService;

    public CAMLMConnector(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [DllImport("CAMLM.dll")]
    private static extern int GetAccountDataI(ref IST_GETSTRING Key, ref IST_GETSTRING Login,
                                              ref IST_GETSTRING Password, ref bool Remember);

    private string GetAuthKey()
    {
        var key = _settingsService.Settings.CamlmAuthKey;
        if (!string.IsNullOrEmpty(key)) return key;
        return Environment.GetEnvironmentVariable(AUTH_KEY_ENV_VARIABLE) ?? "";
    }

    public User LoadUser()
    {
        User result = null;

        var authKey = GetAuthKey();
        if (string.IsNullOrEmpty(authKey))
        {
            Logger.Warn($"CAMLM auth key is not set (CamlmAuthKey in settings.json or {AUTH_KEY_ENV_VARIABLE} environment variable), user is not loaded");
            return result;
        }

        IST_GETSTRING key = new DelphiStringProvider(authKey);
        IST_GETSTRING login = new DelphiStringProvider("");
        IST_GETSTRING pass = new DelphiStringProvider("");
        var remember = false;

        try
        {
            _ = GetAccountDataI(ref key, ref login, ref pass, ref remember);
            if (login.GetString() != null && pass.GetString() != null)
            {
                result = new User
                {
                    Login = login.GetString(),
                    Pass = pass.GetString()
                };
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to load user");
        }

        return result;
    }
}
