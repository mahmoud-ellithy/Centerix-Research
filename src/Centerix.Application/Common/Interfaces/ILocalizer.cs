namespace Centerix.Application.Common.Interfaces;

public interface ILocalizer
{
    string Translate(string key);
    string TranslateFormat(string key, params object[] args);
}
