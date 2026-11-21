using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NimCap.Screenshot.Localization;
using WPFLocalizeExtension.Engine;

namespace NimCap.Screenshot.Services;

public sealed record InterfaceLanguage(string Code, string NativeName)
{
    public string DisplayName => Code.Length == 0 ? UiText.Get("FollowSystem") : NativeName;
    public override string ToString() => DisplayName;
}

public static class LocalizationService
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly (string Code, string Name)[] AdditionalLanguages =
    [
        ("zh-CN", "简体中文")
    ];
    public static IReadOnlyList<InterfaceLanguage> Languages { get; } = DiscoverLanguages();
    public static event EventHandler? LanguageChanged;

    private static IReadOnlyList<InterfaceLanguage> DiscoverLanguages()
    {
        var languages = new List<InterfaceLanguage> { new("", ""), new("en", "English") };
        languages.AddRange(AdditionalLanguages.Select(language => new InterfaceLanguage(language.Code, language.Name)));
        // SDK satellite resources are published in culture-named subdirectories.
        // Adding a Strings.<culture>.resx automatically adds it to the selector.
        foreach (var directory in Directory.EnumerateDirectories(AppContext.BaseDirectory).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, "NimCap.Screenshot.Core.resources.dll"))) continue;
            try
            {
                var culture = CultureInfo.GetCultureInfo(Path.GetFileName(directory));
                if (culture.Name.Length == 0 || culture.Name == "en" ||
                    UiText.Resources.GetResourceSet(culture, true, false) is null) continue;
                if (languages.All(language => !language.Code.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)))
                    languages.Add(new(culture.Name, culture.Name == "zh-CN" ? "简体中文" : culture.NativeName));
            }
            catch (CultureNotFoundException) { }
        }
        return languages.AsReadOnly();
    }

    internal static CultureInfo ResolveCulture(string? preference, CultureInfo? systemCulture = null)
    {
        CultureInfo requested;
        try { requested = string.IsNullOrWhiteSpace(preference) ? systemCulture ?? SystemCulture : CultureInfo.GetCultureInfo(preference); }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en"); }
        if (requested.Name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo("zh-CN");
        for (var candidate = requested; candidate.Name.Length > 0; candidate = candidate.Parent)
            if (Languages.Any(language => language.Code.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        // Chinese OS variants can use Simplified Chinese when no exact pack exists.
        if (requested.TwoLetterISOLanguageName == "zh" && Languages.Any(language => language.Code == "zh-CN"))
            return CultureInfo.GetCultureInfo("zh-CN");
        return CultureInfo.GetCultureInfo("en");
    }

    public static void Apply(string? preference)
    {
        var culture = ResolveCulture(preference);
        LocalizeDictionary.Instance.SetCurrentThreadCulture = false;
        UiText.SetCulture(culture);
        LocalizeDictionary.Instance.Culture = culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Bind(DependencyObject target, DependencyProperty property, string key, params object?[] arguments) =>
        BindingOperations.SetBinding(target, property, new System.Windows.Data.Binding($"[{key}]")
        {
            Source = UiText.Instance, Mode = BindingMode.OneWay,
            Converter = arguments.Length == 0 ? null : new ResourceFormatConverter(arguments)
        });

    private sealed class ResourceFormatConverter(object?[] arguments) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Format(UiText.Culture, (string)value, arguments);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }
}
