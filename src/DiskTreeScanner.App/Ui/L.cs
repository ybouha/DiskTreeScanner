using System.Globalization;
using System.Resources;

namespace DiskTreeScanner.App.Ui;

/// <summary>EN/FR strings from Resources\Strings(.fr).resx, switchable at run time.</summary>
internal static class L
{
    private static readonly ResourceManager Manager = new("DiskTreeScanner.App.Resources.Strings", typeof(L).Assembly);

    public static string Language { get; private set; } = "en";

    public static CultureInfo Ui { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>Number and date formatting: en-GB style in English (as in the mockup), fr-FR in French.</summary>
    public static CultureInfo Numbers { get; private set; } = CultureInfo.GetCultureInfo("en-GB");

    public static event Action? Changed;

    public static void SetLanguage(string code)
    {
        Language = string.Equals(code, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        Ui = CultureInfo.GetCultureInfo(Language);
        Numbers = CultureInfo.GetCultureInfo(Language == "fr" ? "fr-FR" : "en-GB");
        Thread.CurrentThread.CurrentUICulture = Ui;
        CultureInfo.DefaultThreadCurrentUICulture = Ui;
        Fmt.RefreshUnits();
        Changed?.Invoke();
    }

    public static string S(string key) => Manager.GetString(key, Ui) ?? key;

    public static string F(string key, params object?[] args) => string.Format(Numbers, S(key), args);
}
