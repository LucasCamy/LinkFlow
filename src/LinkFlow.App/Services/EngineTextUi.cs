using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Traduz os textos que o engine manda por código (<see cref="EngineText"/>) para o idioma da interface, com os textos do
/// Core (Resources\EngineStrings.resx). Sem o código (serviço de outra versão) ou com uma tradução que não formata, usa
/// o português que veio junto.
/// </summary>
public static class EngineTextUi
{
    public static string Localize(EngineText? text, string fallback) =>
        text == null ? fallback : EngineTexts.TryFormat(text, UiLanguage.Culture) ?? fallback;

    public static string LocalizedTitle(this EngineAlert alert) => Localize(alert.TitleText, alert.Title);

    public static string LocalizedMessage(this EngineAlert alert) => Localize(alert.MessageText, alert.Message);
}
