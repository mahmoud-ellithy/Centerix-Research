using System.Globalization;
using System.Text.Json;

using Centerix.Application.Common.Interfaces;

namespace Centerix.API.Localization;

public class JsonLocalizer : ILocalizer
{
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private static readonly string[] SupportedCultures = ["en", "ar"];
    private const string DefaultCulture = "en";

    public JsonLocalizer(IWebHostEnvironment env)
    {
        _translations = [];
        var localizationPath = Path.Combine(env.ContentRootPath, "Localization");

        foreach (var culture in SupportedCultures)
        {
            var filePath = Path.Combine(localizationPath, $"{culture}.json");
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                _translations[culture] = entries ?? [];
            }
            else
            {
                _translations[culture] = [];
            }
        }
    }

    public string Translate(string key)
    {
        var cultureName = CultureInfo.CurrentUICulture.Name;

        return Lookup(key, cultureName);
    }

    public string TranslateFormat(string key, params object[] args)
    {
        var value = Translate(key);

        return args.Length > 0 ? string.Format(value, args) : value;
    }

    private string Lookup(string key, string cultureName)
    {
        if (_translations.TryGetValue(cultureName, out var cultureDict) &&
            cultureDict.TryGetValue(key, out var translatedValue))
        {
            return translatedValue;
        }

        if (_translations.TryGetValue(DefaultCulture, out var enDict) &&
            enDict.TryGetValue(key, out var defaultValue))
        {
            return defaultValue;
        }

        return key;
    }
}
