namespace Parrot.Application.Auth;

public class FrontendSettings
{
    public const string SectionName = "FrontendSettings";

    private string _baseUrl = string.Empty;

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value.TrimEnd('/');
    }
}
