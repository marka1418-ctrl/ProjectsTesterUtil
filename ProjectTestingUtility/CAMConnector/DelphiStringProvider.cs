using CAMLMInterfaces;

namespace ProjectTestingUtility.CAMConnector;

public class DelphiStringProvider(string val) : IST_GETSTRING
{
    public string GetString() => val;
}