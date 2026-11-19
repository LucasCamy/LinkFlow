using LinkFlow.App.Resources;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Nome de um perfil para mostrar: os prontos no idioma da interface, os salvos pelo usuário como estão.</summary>
public static class PresetNames
{
    /// <summary>Chave salva quando o usuário ajusta a potência à mão (não é traduzida na configuração).</summary>
    public const string CustomKey = "Personalizado";

    public static string Label(string key) =>
        FanPresets.Find(key)?.Label ?? (key == CustomKey ? Strings.Fan_Custom : key);
}
