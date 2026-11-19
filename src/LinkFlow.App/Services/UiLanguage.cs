using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

/// <summary>
/// Idioma da interface. É escolhido uma vez, na partida, antes de qualquer janela: trocar de idioma reinicia a
/// interface (o serviço e as ventoinhas não param). Os textos ficam em Resources\Strings.resx (português) e
/// Strings.&lt;idioma&gt;.resx; números e datas da interface seguem o mesmo idioma.
/// </summary>
public static class UiLanguage
{
    public const string Portuguese = "pt-BR";
    public const string English = "en";

    public static IReadOnlyList<string> Supported { get; } = [Portuguese, English];

    /// <summary>
    /// Sem escolha salva, seguir o idioma do Windows. Fica desligado enquanto a tradução da v3.0 não termina, para
    /// quem tem o Windows em inglês não ver telas metade em cada idioma; dá para testar com <c>--lang en</c>.
    /// </summary>
    internal const bool FollowWindowsByDefault = false;

    private static bool _applied;

    /// <summary>
    /// Até <see cref="Apply"/>, os textos saem em português, e não no idioma do Windows: vale para o que aparece antes
    /// da escolha do idioma e para os testes, que comparam textos em português em qualquer máquina.
    /// </summary>
    [ModuleInitializer]
    internal static void UsePortugueseUntilApplied() => Strings.Culture = CultureInfo.GetCultureInfo(Portuguese);

    /// <summary>Cultura da interface (textos, números e datas). Português até <see cref="Apply"/>.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Portuguese);

    /// <summary>Idioma a usar: o pedido na linha de comando, depois o salvo, depois o do Windows (ou português).</summary>
    public static string Resolve(string? requested, string? saved, CultureInfo windows, bool followWindows = FollowWindowsByDefault)
    {
        if (Match(requested) is { } fromArgs) return fromArgs;
        if (Match(saved) is { } fromSettings) return fromSettings;
        if (!followWindows) return Portuguese;
        return windows.TwoLetterISOLanguageName == "pt" ? Portuguese : English;
    }

    private static string? Match(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        foreach (var supported in Supported)
            if (string.Equals(supported, language.Trim(), StringComparison.OrdinalIgnoreCase)) return supported;
        return null;
    }

    /// <summary>
    /// Idioma salvo em ui.json, lido sem efeitos colaterais (o <see cref="UiSettingsService"/> só é criado depois,
    /// e alguns caminhos da partida, como os comandos de serviço elevados, nem chegam a criá-lo).
    /// </summary>
    public static string? ReadSavedLanguage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // o UiSettingsService avisa e recupera o arquivo quando for carregá-lo
        }
    }

    /// <summary>
    /// Aplica o idioma ao processo inteiro. Só pode ser chamado uma vez, antes de o WPF começar a despachar (no construtor
    /// do App): a cultura definida dentro de uma operação do dispatcher não vale para as operações seguintes.
    /// </summary>
    public static void Apply(string language)
    {
        if (_applied) throw new InvalidOperationException("O idioma da interface já foi aplicado.");
        _applied = true;

        var culture = CultureInfo.GetCultureInfo(Match(language) ?? Portuguese);
        Culture = culture;
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Sem isso o WPF formata as ligações (StringFormat, conversores) em en-US, qualquer que seja o idioma.
        // Run e os outros FrameworkContentElement herdam o valor do elemento em que estão.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }
}
