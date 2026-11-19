using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LinkFlow.App.Resources;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class UiLanguageTests : IDisposable
{
    private static readonly CultureInfo EnglishWindows = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo PortugueseWindows = CultureInfo.GetCultureInfo("pt-BR");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-lang-" + Guid.NewGuid().ToString("N"));

    public UiLanguageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Command_line_wins_over_the_saved_language()
    {
        Assert.Equal(UiLanguage.English, UiLanguage.Resolve("EN", UiLanguage.Portuguese, PortugueseWindows));
        Assert.Equal(UiLanguage.Portuguese, UiLanguage.Resolve(" pt-br ", UiLanguage.English, EnglishWindows));
    }

    [Fact]
    public void Saved_language_is_used_and_unknown_values_are_ignored()
    {
        Assert.Equal(UiLanguage.English, UiLanguage.Resolve(null, "en", PortugueseWindows));
        Assert.Equal(UiLanguage.Portuguese, UiLanguage.Resolve("klingon", "fr-FR", EnglishWindows));
    }

    [Fact]
    public void Without_a_choice_stays_in_portuguese_until_the_translation_is_finished()
    {
        Assert.False(UiLanguage.FollowWindowsByDefault);
        Assert.Equal(UiLanguage.Portuguese, UiLanguage.Resolve(null, null, EnglishWindows));
    }

    [Theory]
    [InlineData("pt-BR", UiLanguage.Portuguese)]
    [InlineData("pt-PT", UiLanguage.Portuguese)]
    [InlineData("en-GB", UiLanguage.English)]
    [InlineData("de-DE", UiLanguage.English)]
    public void Following_windows_picks_portuguese_or_falls_back_to_english(string windows, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(null, null, CultureInfo.GetCultureInfo(windows), followWindows: true));
    }

    [Fact]
    public void Saved_language_is_read_without_touching_a_broken_file()
    {
        string path = Path.Combine(_dir, "ui.json");
        Assert.Null(UiLanguage.ReadSavedLanguage(path));

        File.WriteAllText(path, """{ "closeToTray": true, "language": "en" }""");
        Assert.Equal("en", UiLanguage.ReadSavedLanguage(path));

        File.WriteAllText(path, """{ "language": 3 }""");
        Assert.Null(UiLanguage.ReadSavedLanguage(path));

        File.WriteAllText(path, """{ "language": "en" """);
        Assert.Null(UiLanguage.ReadSavedLanguage(path));
        Assert.Equal(["ui.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Ui_settings_round_trip_the_language()
    {
        string old = AppPaths.UserDir;
        AppPaths.UserDir = _dir;
        try
        {
            var service = new UiSettingsService();
            Assert.Null(service.Current.Language);
            service.Update(s => s.Language = UiLanguage.English);
            service.Flush();
            Assert.Equal(UiLanguage.English, UiLanguage.ReadSavedLanguage(AppPaths.UiSettingsFile));
        }
        finally
        {
            AppPaths.UserDir = old;
        }
    }

    [Fact]
    public void Texts_are_portuguese_until_a_language_is_applied_whatever_the_windows_language()
    {
        Assert.Equal(UiLanguage.Portuguese, Strings.Culture?.Name);
        Assert.Equal("Ventoinhas", Strings.Nav_Fans);
    }

    [Fact]
    public void Startup_option_selects_the_language_for_one_run()
    {
        Assert.Equal("en", App.StartupOptions.Parse(["--tray", "--lang", "en"]).Language);
        Assert.Null(App.StartupOptions.Parse(["--tray"]).Language);
    }
}

/// <summary>Os arquivos de textos: mesmas chaves nos dois idiomas e a classe Strings gerada em dia.</summary>
public sealed class StringResourceTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    internal static string AppSourceDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "LinkFlow.slnx")))
                    return Path.Combine(dir.FullName, "src", "LinkFlow.App");
            throw new DirectoryNotFoundException("Raiz do repositório (LinkFlow.slnx) não encontrada.");
        }
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(AppSourceDir, "Resources", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    [Fact]
    public void Every_language_has_the_same_keys_as_portuguese()
    {
        var pt = Read("Strings.resx");
        foreach (var file in Directory.GetFiles(Path.Combine(AppSourceDir, "Resources"), "Strings.*.resx"))
        {
            var other = Read(Path.GetFileName(file));
            Assert.Equal(pt.Keys.Order(), other.Keys.Order());
        }
        Assert.Equal(UiLanguage.Supported.Count - 1, Directory.GetFiles(Path.Combine(AppSourceDir, "Resources"), "Strings.*.resx").Length);
    }

    [Fact]
    public void Translations_are_not_empty_and_keep_the_placeholders()
    {
        var pt = Read("Strings.resx");
        var en = Read("Strings.en.resx");
        foreach (var (key, value) in pt)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} vazio em português");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"{key} vazio em inglês");
            Assert.True(Placeholders(value).SetEquals(Placeholders(en[key])), $"{key}: os {{0}} do inglês não batem com os do português");
        }
    }

    private static HashSet<string> Placeholders(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void Generated_strings_class_matches_the_portuguese_file()
    {
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name);
        Assert.Equal(Read("Strings.resx").Keys.Order(), properties.Order());
    }

    [Fact]
    public void English_comes_from_the_satellite_assembly()
    {
        var en = CultureInfo.GetCultureInfo("en");
        Assert.Equal("The language changes when LinkFlow restarts.", Strings.ResourceManager.GetString(nameof(Strings.Language_RestartNotice), en));
        Assert.Equal("O idioma muda quando o LinkFlow reiniciar.",
            Strings.ResourceManager.GetString(nameof(Strings.Language_RestartNotice), CultureInfo.GetCultureInfo("pt-BR")));
    }
}

/// <summary>
/// Telas já traduzidas não podem voltar a ter texto fixo no XAML. As que ainda faltam estão em
/// <see cref="NotYetTranslated"/>; ao traduzir uma tela, tire-a da lista.
/// </summary>
public sealed class XamlHardcodedTextTests
{
    private static readonly HashSet<string> TextProperties =
    [
        "Text", "Content", "Header", "ToolTip", "Title", "Detail", "Description", "PlaceholderText",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "ToolTipService.ToolTip",
    ];

    /// <summary>Elementos cujo conteúdo é texto mostrado (o conteúdo de Color, FontFamily etc. não é).</summary>
    private static readonly HashSet<string> TextElements =
    [
        "TextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "Paragraph", "Label", "Button",
        "CheckBox", "RadioButton", "ToggleButton", "ToolTip", "MenuItem", "ComboBoxItem", "ListBoxItem", "TabItem",
        "GroupBox", "Expander", "String",
    ];

    // Textos dentro de {Binding ...}: StringFormat='Remover {0}', TargetNullValue=..., FallbackValue=...
    private static readonly Regex BindingText = new(
        @"\b(?:StringFormat|TargetNullValue|FallbackValue)\s*=\s*(?:'(?<v>[^']*)'|(?<v>(?:\{[^{}]*\}|[^,{}'])+))", RegexOptions.Compiled);

    private static readonly Regex Words = new(@"\p{L}{2,}", RegexOptions.Compiled);
    private static readonly Regex Format = new(@"\{\d*(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    /// <summary>Iguais em qualquer idioma: unidades e nomes de produto.</summary>
    private static readonly HashSet<string> Universal = ["RPM", "°C", "GB", "MB", "MHz", "GHz", "min", "LinkFlow", "iCUE LINK", "LCD", "RGB", "CPU", "GPU", "RAM"];

    /// <summary>Telas que ainda não foram traduzidas (v3.0, fases 1 a 4).</summary>
    private static readonly HashSet<string> NotYetTranslated =
    [
        "Views/DriverInstallDialog.xaml", "Views/DriversPage.xaml", "Views/ImportDialog.xaml", "Views/LcdPage.xaml",
        "Views/LightingPage.xaml", "Views/SettingsPage.xaml", "Views/WidgetPage.xaml", "Views/WidgetView.xaml",
        "Views/WindowsMaintenancePage.xaml", "Views/WindowsNetworkPage.xaml", "Views/WindowsPage.xaml",
    ];

    private static IEnumerable<string> XamlFiles(string root) =>
        Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'));

    internal static List<string> FindHardcodedText(string path)
    {
        var found = new List<string>();
        foreach (var element in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            foreach (var attribute in element.Attributes())
            {
                string value = attribute.Value;
                if (value.StartsWith('{'))
                {
                    foreach (Match m in BindingText.Matches(value))
                        if (IsText(m.Groups["v"].Value)) found.Add($"{line}: {attribute.Name.LocalName} {m.Value}");
                }
                else if (TextProperties.Contains(attribute.Name.LocalName) && IsText(value))
                    found.Add($"{line}: {attribute.Name.LocalName}=\"{value}\"");
            }

            if (element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value is { } property &&
                TextProperties.Contains(property) && element.Attribute("Value")?.Value is { } setterValue &&
                !setterValue.StartsWith('{') && IsText(setterValue))
                found.Add($"{line}: Setter {property}=\"{setterValue}\"");

            if (TextElements.Contains(element.Name.LocalName))
                foreach (var text in element.Nodes().OfType<XText>())
                    if (IsText(text.Value)) found.Add($"{line}: texto \"{text.Value.Trim()}\"");
        }
        return found;
    }

    private static bool IsText(string value)
    {
        if (value.TrimStart().StartsWith("{x:Static", StringComparison.Ordinal)) return false;   // texto vindo de Strings
        string text = Format.Replace(value, " ").Trim();
        return !Universal.Contains(text) && Words.Matches(text).Any(w => !Universal.Contains(w.Value));
    }

    [Fact]
    public void Translated_screens_have_no_hardcoded_text()
    {
        string root = StringResourceTests.AppSourceDir;
        var problems = XamlFiles(root).Where(f => !NotYetTranslated.Contains(f))
            .SelectMany(f => FindHardcodedText(Path.Combine(root, f)).Select(p => $"{f}:{p}")).ToList();
        Assert.True(problems.Count == 0, "Texto fixo em tela traduzida (use {x:Static r:Strings.Chave}):\n" + string.Join('\n', problems));
    }

    [Fact]
    public void Untranslated_list_has_no_stale_entries()
    {
        string root = StringResourceTests.AppSourceDir;
        var files = XamlFiles(root).ToHashSet();
        foreach (var entry in NotYetTranslated)
        {
            Assert.True(files.Contains(entry), $"{entry} não existe mais: tire da lista");
            Assert.True(FindHardcodedText(Path.Combine(root, entry)).Count > 0, $"{entry} já não tem texto fixo: tire da lista");
        }
    }

    [Fact]
    public void Detector_finds_the_usual_kinds_of_hardcoded_text()
    {
        string file = Path.Combine(Path.GetTempPath(), $"linkflow-xaml-{Guid.NewGuid():N}.xaml");
        File.WriteAllText(file, """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:r="clr-namespace:LinkFlow.App.Resources">
              <TextBlock Text="Ventoinhas"/>
              <TextBlock Text="{Binding Name, StringFormat='Remover {0}'}"/>
              <TextBlock>Texto solto</TextBlock>
              <Button AutomationProperties.Name="Fechar"/>
              <Style TargetType="Button"><Setter Property="ToolTip" Value="Ajuda"/></Style>
              <TextBlock Text="{x:Static r:Strings.Language_English}"/>
              <TextBlock Text="{Binding Rpm, StringFormat={}{0:0} RPM}"/>
              <TextBlock Text="{Binding Since, StringFormat={x:Static r:Strings.Dash_ReadAt}}"/>
              <TextBlock Text="&#xE7BA;"/>
              <Run Text=" · "/>
              <TextBlock Text="°C"/>
            </Grid>
            """);
        try
        {
            var found = FindHardcodedText(file);
            Assert.Equal(5, found.Count);
            Assert.Contains(found, f => f.Contains("Ventoinhas"));
            Assert.Contains(found, f => f.Contains("Remover"));
            Assert.Contains(found, f => f.Contains("Texto solto"));
            Assert.Contains(found, f => f.Contains("Fechar"));
            Assert.Contains(found, f => f.Contains("Ajuda"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
