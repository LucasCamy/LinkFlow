using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed record CurveFallbackOption(CurveFallbackPolicy Value, string Label);

public static class ReliabilityUiText
{
    private static string T(string pt, string en) => UiLanguage.Culture.TwoLetterISOLanguageName == "pt" ? pt : en;
    public static string MissingSource => T("Se o sensor escolhido ficar indisponível", "If the selected sensor becomes unavailable");
    public static string SafeFallback => T("Manter velocidade de proteção e sinalizar", "Use protective speed and report the missing source");
    public static string CpuGpuFallback => T("Permitir outro sensor de CPU/GPU", "Allow another CPU/GPU sensor");
    public static string AnyFallback => T("Permitir CPU/GPU/líquido com piso de proteção", "Allow CPU/GPU/coolant with protective minimum");
    public static string FallbackMinimum => T("Potência mínima durante a substituição", "Minimum power while using a fallback");
    public static string FallbackHelp => T("A substituição é informada no painel. O modo de segurança tem prioridade sobre esta opção.", "Fallback is reported in the control panel. Fail-safe protection takes priority over this setting.");
    public static string RestoreConfiguration => T("Restaurar configuração anterior à atualização", "Restore the pre-update configuration");
    public static string RestoreReview => T("Restaurar os perfis, curvas, iluminação e limites salvos antes da atualização? Os ajustes atuais serão substituídos após a verificação da revisão. A versão instalada permanece a mesma.", "Restore the profiles, curves, lighting and limits saved before the update? Current settings will be replaced after revision verification. The installed version stays the same.");
    public static string ReadOnlyEngine => T("Serviço do Windows · somente leitura", "Windows service · read only");
}
