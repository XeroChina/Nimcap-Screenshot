using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Plugins;
using NimCap.Screenshot.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace NimCap.Screenshot.Views;

public partial class RecognitionWindow : Window
{
    private sealed record TranslationLanguage(string Code, string Name)
    {
        public override string ToString() => Name;
    }
    private static readonly TranslationLanguage[] TranslationLanguages =
    [
        new("cs", "Čeština"), new("de", "Deutsch"), new("de-AT", "Deutsch (Österreich)"), new("de-CH", "Deutsch (Schweiz)"),
        new("en", "English"), new("es", "Español"), new("es-ES", "Español (España)"), new("es-419", "Español (Latinoamérica)"),
        new("fr", "Français"), new("fr-FR", "Français (France)"), new("fr-CA", "Français (Canada)"), new("it", "Italiano"),
        new("ja", "日本語"), new("ko", "한국어"), new("pl", "Polski"), new("pt-BR", "Português (Brasil)"),
        new("pt-PT", "Português (Portugal)"), new("ru", "Русский"), new("tr", "Türkçe"), new("zh-CN", "中文（中国大陆）"),
        new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文")
    ];
    private BitmapSource _image;
    private readonly SettingsService _settings;
    private readonly PluginCatalog? _plugins;
    private RecognitionDocument? _document;
    private ObservableCollection<RecognizedCell> _cells = [];
    private BitmapSource? _cutout;
    private CancellationTokenSource? _operation;
    private string _baseline = "";
    private string _sourceText = "";
    private string _translatedText = "";
    private bool _hasTranslation;
    private string _tableBaseline = "[]";
    private IdentityDocumentResult? _identityDocument;
    private IdentityDocumentKind? _documentKindOverride;
    private string _identityBaseline = "";
    private string IdentitySnapshot => DocumentEditor?.Text ?? "";
    private string TableSnapshot => System.Text.Json.JsonSerializer.Serialize(_cells);
    private bool HasEdits => _sourceText != _baseline || TableSnapshot != _tableBaseline || IdentitySnapshot != _identityBaseline;
    private bool _busy, _closed, _syncing;
    public event Action<BitmapSource, CaptureAction>? OutputRequested;
    public event Action<string>? SettingsRequested;
    private string Mode => (ModeBox.SelectedItem as ListBoxItem)?.Tag.ToString() ?? "OCR";
    private string CurrentTextView => (TextViewBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Source";
    public RecognitionWindow(BitmapSource image, SettingsService settings, PluginCatalog? plugins = null, string mode = "OCR", bool autoRun = false)
    {
        _image = image.IsFrozen ? image : image.Clone(); if (!_image.IsFrozen) _image.Freeze();
        _settings = settings; _plugins = plugins;
        InitializeComponent();
        InitializeWatermark();
        ResultTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, ResultTabs)) RefreshCommonActions(); };
        TargetLanguageBox.ItemsSource = TranslationLanguages;
        TargetLanguageBox.SelectedItem = TranslationLanguages.FirstOrDefault(x => x.Code.Equals(settings.Current.TranslationTarget, StringComparison.OrdinalIgnoreCase))
            ?? TranslationLanguages.First(x => x.Code == "zh-CN");
        TextViewBox.SelectedIndex = 0;
        ModeBox.SelectedItem = ModeBox.Items.OfType<ListBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == mode) ?? ModeBox.Items[0];
        CutoutModels.Migrate(settings.Current);
        var cutoutModels = CutoutModels.ForDevice(settings.Current.CutoutOnnxDevice);
        CutoutModelBox.ItemsSource = cutoutModels;
        CutoutModelBox.SelectedItem = cutoutModels.FirstOrDefault(x => x.Id == settings.Current.CutoutDefaultModelId) ?? cutoutModels.FirstOrDefault();
        CutoutModelBox.SelectionChanged += async (_, _) =>
        {
            if (!_modelControlsUpdating && CutoutModelBox.SelectedItem is CutoutModelDefinition selected)
            {
                _quickModels["Cutout"] = selected.Id;
                if (IsLoaded) await EnsureSelectedModel();
            }
        };
        void UpdateMode()
        {
            UpdateWatermarkMode();
            CutoutModelBox.Visibility = Mode == "Cutout" ? Visibility.Visible : Visibility.Collapsed;
            DocumentResultTab.Visibility = Mode == "Document" ? Visibility.Visible : Visibility.Collapsed;
            TableResultTab.Visibility = Mode == "Table" ? Visibility.Visible : Visibility.Collapsed;
            CutoutResultTab.Visibility = Mode == "Cutout" ? Visibility.Visible : Visibility.Collapsed;
            TextResultTab.Visibility = Mode is "Cutout" or "Watermark" ? Visibility.Collapsed : Visibility.Visible;
            RunButton.Content = L.Get(Mode == "Watermark" ? "WatermarkRemoval" : Mode == "Cutout" ? "RecognitionCutoutCompact" : "RecognitionRunCompact");
            ResultTabs.SelectedItem = Mode switch { "Watermark" => WatermarkResultTab, "Cutout" => CutoutResultTab, "Table" => TableResultTab, "Document" => DocumentResultTab, _ => TextResultTab };
            RefreshWorkspaceLayout();
            SetPicture(_image);
        }
        ModeBox.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();
        SetPicture(_image); CellsGrid.ItemsSource = _cells;
        Loaded += async (_, _) =>
        {
            // Fit after the viewport's first layout, otherwise a zero-sized viewport
            // can shrink a large clipboard picture to an apparently blank preview.
            await Dispatcher.InvokeAsync(FitImage, System.Windows.Threading.DispatcherPriority.Loaded);
            if (_closed) return;
            Status.Text = L.Get("RecognitionChooseTool");
            if (autoRun && Mode is "OCR" or "Summary") await Run();
        };
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (_busy || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var path = (e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[])?.FirstOrDefault(File.Exists);
            if (path is null) { Status.Text = L.Get("ImageDropInvalid"); return; }
            try
            {
                CellsGrid.CommitEdit();
                if (HasEdits && MessageBox.Show(this, L.Get("RecognitionTransformConfirm"), L.Get("Confirm"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var image = ImageFileService.Load(path);
                ReplaceImage(image, L.Format("ImageLoaded", image.PixelWidth, image.PixelHeight));
            }
            catch (Exception ex) { Status.Text = ex.GetBaseException().Message; }
        };
    }

    private void ReplaceImage(BitmapSource image, string status)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        _document = null; _identityDocument = null; DocumentEditor.Clear(); _identityBaseline = IdentitySnapshot;
        DocumentTypeText.Text = DocumentTypeName(_documentKindOverride ?? IdentityDocumentKind.Unknown);
        SetSourceText(""); _baseline = _sourceText; _cells.Clear(); _tableBaseline = TableSnapshot;
        _cutout = null; _cutoutUndo.Clear();
        ResetWatermarkImage();
        SetPicture(_image); PictureScroll.UpdateLayout(); FitImage();
        RefreshComparison();
        Status.Text = status;
    }
    private void SetPicture(BitmapSource image)
    {
        foreach (var highlight in PictureCanvas.Children.OfType<Rectangle>().Where(x => !ReferenceEquals(x, WatermarkRegionOutline)).ToArray()) PictureCanvas.Children.Remove(highlight);
        Picture.Source = image; PictureCanvas.Width = image.PixelWidth; PictureCanvas.Height = image.PixelHeight;
    }
    private void TransformImage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        CellsGrid.CommitEdit();
        if (HasEdits && MessageBox.Show(this, L.Get("RecognitionTransformConfirm"), L.Get("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var action = ((FrameworkElement)sender).Tag?.ToString();
        // Quarter turns and reflection can preserve source pixels without PNG
        // encode/decode or repeated alpha conversion through the editor pipeline.
        Transform transform = action switch
        {
            "Left" => new RotateTransform(-90), "Right" => new RotateTransform(90),
            "Half" => new RotateTransform(180), "Vertical" => new ScaleTransform(1, -1), _ => Transform.Identity
        };
        ReplaceImage(new TransformedBitmap(_image, transform), L.Get("RecognitionImageTransformed"));
    }
    private async void Run_Click(object sender, RoutedEventArgs e) => await Run();
    private async Task Run()
    {
        if (_busy) return;
        if (!HasSelectedModel) { Status.Text = L.Get("RecognitionModelUnavailable"); return; }
        if (Mode == "Watermark") { await RepairWatermark(); return; }
        if (!await EnsureSelectedModel()) return;
        CellsGrid.CommitEdit();
        if (HasEdits && MessageBox.Show(this, L.Get("RunningAgainWillReplaceManuallyEditedText"), L.Get("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); _operation = new CancellationTokenSource();
        _pluginCells = null;
        try
        {
            var settings = QuickSettings(); var service = new LocalRecognitionService(); Status.Text = L.Get("ProcessingLocallyClickStopToCancel");
            if ((Mode is "OCR" or "Document" or "Summary" || Mode == "Table" && string.IsNullOrWhiteSpace(settings.TableRecognizerId))
                && !await ResolveQuickOcr(settings, _operation.Token))
            { Status.Text = L.Get("RecognitionModelUnavailable"); return; }
            if (Mode == "Cutout")
            {
                var model = (CutoutModelDefinition)CutoutModelBox.SelectedItem;
                if (string.IsNullOrWhiteSpace(settings.CutoutModel) || !File.Exists(settings.CutoutModel))
                {
                    Status.Text = L.Format("0IsNotConfiguredOrItsFile", model.Id) + " " + L.Get("RecognitionModelUnavailable");
                    return;
                }
                var result = await OnnxImageRuntime.Shared.RemoveBackground(_image, settings, _operation.Token);
                if (_closed) return;
                SaveResultUndo(_cutoutUndo, _cutout); _cutout = result; _document = null;
                SetPicture(_image); RefreshComparison(); ResultTabs.SelectedItem = CutoutResultTab;
                Status.Text = L.Format("01BackgroundRemovedPreservingOriginalResolution", model.Id,
                    settings.CutoutOnnxDevice is "CPU" or "DirectML" ? settings.CutoutOnnxDevice : settings.OnnxDevice); return;
            }
            RecognitionDocument document;
            if (Mode == "Formula") document = string.IsNullOrWhiteSpace(settings.FormulaRecognizerId)
                ? await service.RecognizeFormula(_image, settings, _operation.Token) : await PluginDocument(settings, _operation.Token);
            else if (Mode == "Table" && !string.IsNullOrWhiteSpace(settings.TableRecognizerId)) document = await PluginDocument(settings, _operation.Token);
            else if (Mode == "Table")
            {
                var table = await service.RecognizeTable(_image, settings, _operation.Token);
                document = table.Document; _pluginCells = table.Cells;
            }
            else
            {
                if (Mode != "Barcode" && settings.OcrBackend == "PaddleOCR" &&
                    new[] { settings.OcrDetectionModel, settings.OcrRecognitionModel, settings.OcrDictionary }.Any(path => string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
                {
                    Status.Text = L.Get("LocalONNXPaddleOCRIsTheDefaultOpen");
                    return;
                }
                document = await (Mode == "Barcode" ? service.Barcodes(_image, settings, _operation.Token)
                    : Mode == "Document" ? service.RecognizeIdentityDocument(_image, settings, _operation.Token) : service.Recognize(_image, settings, _operation.Token));
            }
            if (_closed) return;
            _document = document; SetPicture(_image);
            SetSourceText(Mode == "Document" ? string.Join(Environment.NewLine, document.Blocks.Where(b => !b.Kind.StartsWith("document-", StringComparison.Ordinal)).Select(b => b.Text)) : document.Text);
            _baseline = _sourceText;
            _cells = new ObservableCollection<RecognizedCell>(_pluginCells ?? RecognitionExport.Draft(document)); _pluginCells = null; CellsGrid.ItemsSource = _cells;
            _tableBaseline = TableSnapshot;
            _identityDocument = null; DocumentEditor.Clear(); DocumentTypeText.Text = L.Get("DocumentUnknown"); _identityBaseline = IdentitySnapshot;
            ResultTabs.SelectedItem = Mode == "Table" ? TableResultTab : TextResultTab;
            Status.Text = document.Blocks.Count == 0 ? L.Get("NoContentRecognizedTryAnotherEngineCheck") : L.Format("01RegionsClickAnImageRegion", document.Backend, document.Blocks.Count);
            if (Mode == "Summary" && !string.IsNullOrWhiteSpace(document.Text))
            {
                Status.Text = L.Get("CaptureSummaryProcessing");
                var summary = await LocalTranslationService.Summarize(document.Text, settings, _operation.Token);
                if (_closed) return;
                SetSourceText(summary);
                _baseline = _sourceText;
                Status.Text = L.Get("CaptureSummaryComplete");
            }
            if (Mode == "Table") Status.Text += L.Get("RowsColumnsAndMergedCellsRecognizedReview");
            if (Mode == "Document") ShowDocument();
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = L.Get("ProcessingStoppedOrTimedOutExistingResults"); }
        catch (Exception ex) { if (!_closed) Status.Text = L.Get("ProcessingFailed") + ex.GetBaseException().Message; }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private RecognizedCell[]? _pluginCells;
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
    private async Task<RecognitionDocument> PluginDocument(AppSettings settings, CancellationToken token)
    {
        var mode = Mode;
        var id = mode == "Formula" ? settings.FormulaRecognizerId : settings.TableRecognizerId;
        if (string.IsNullOrWhiteSpace(id) || _plugins is null || !_plugins.TryGetRecognizer(id, out var plugin) || plugin is null)
            throw new InvalidOperationException(Mode == "Formula" ? L.Get("NoFormulaRecognitionPluginIsConfiguredInstall") : L.Get("TableModelPluginIsNotLoadedCheck"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(settings.RecognitionTimeoutSeconds));
        var result = await Task.Run(async () => await plugin.RecognizeAsync(_image, new PluginExecutionContext(new EmptyServices(), new Dictionary<string, object?> { ["task"] = mode }), timeout.Token), timeout.Token).WaitAsync(timeout.Token);
        if (result is null || result.Any(x => x is null || x.Text is null || x.Kind is null || x.Metadata is null)) throw new InvalidDataException(L.Get("ThePluginReturnedAnInvalidResult"));
        if (result.Count > 10000 || result.Sum(x => (long)x.Text.Length) > 500000) throw new InvalidDataException(L.Get("PluginResultIsTooLarge"));
        if (Mode == "Formula" && !result.Any(x => x.Kind.Equals("latex", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.Get("TheFormulaPluginDidNotReturnA"));
        double Meta(RecognitionResult value, string key, double fallback) => value.Metadata.TryGetValue(key, out var item) && double.TryParse(item?.ToString(), out var number) && double.IsFinite(number) ? number : fallback;
        if (Mode == "Table")
            _pluginCells = result.Select(x => new RecognizedCell { Text = x.Text, Row = (int)Meta(x, "row", 1), Column = (int)Meta(x, "column", 1), RowSpan = (int)Meta(x, "rowSpan", 1), ColumnSpan = (int)Meta(x, "columnSpan", 1) }).ToArray();
        var blocks = result.Where(x => Mode != "Formula" || x.Kind.Equals("latex", StringComparison.OrdinalIgnoreCase)).Select(value =>
        {
            var x = Math.Clamp(Meta(value, "x", 0), 0, _image.PixelWidth - 1); var y = Math.Clamp(Meta(value, "y", 0), 0, _image.PixelHeight - 1);
            return new RecognizedBlock(value.Text, new Rect(x, y, Math.Clamp(Meta(value, "width", _image.PixelWidth), 1, _image.PixelWidth - x), Math.Clamp(Meta(value, "height", _image.PixelHeight), 1, _image.PixelHeight - y)), Kind: value.Kind);
        }).ToArray();
        return new RecognitionDocument(blocks, L.Get("ModelPlugin") + id);
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; if (_closed) return; RunButton.IsEnabled = !busy; ModeBox.IsEnabled = !busy; CancelButton.IsEnabled = busy; CellsGrid.IsReadOnly = busy;
        DocumentEditor.IsReadOnly = busy; ChangeDocumentTypeButton.IsEnabled = !busy;
        ImageTransformTools.IsEnabled = !busy;
        CutoutModelBox.IsEnabled = TargetLanguageBox.IsEnabled = TextViewBox.IsEnabled = TranslateButton.IsEnabled = !busy;
        QuickModelBox.IsEnabled = EngineSettingsButton.IsEnabled = !busy;
        if (busy) ProcessingDeviceBox.IsEnabled = false;
        else RefreshModelToolbar();
        SetWatermarkBusy(busy);
        RefreshComparison();
        RefreshTextView();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private static string DocumentTypeName(IdentityDocumentKind kind) => L.Get(kind switch
    {
        IdentityDocumentKind.IdCardFront => "DocumentIdCardFront", IdentityDocumentKind.IdCardBack => "DocumentIdCardBack",
        IdentityDocumentKind.IdCardBoth => "DocumentIdCardBoth",
        IdentityDocumentKind.DrivingLicense => "DocumentDrivingLicense", IdentityDocumentKind.VehicleLicense => "DocumentVehicleLicense",
        IdentityDocumentKind.HouseholdRegister => "DocumentHouseholdRegister", IdentityDocumentKind.Passport => "DocumentPassport",
        IdentityDocumentKind.BankCard => "DocumentBankCard", IdentityDocumentKind.BusinessLicense => "DocumentBusinessLicense", _ => "DocumentUnknown"
    });
    private void ShowDocument(IdentityDocumentKind? kind = null)
    {
        if (_document is null) return;
        _identityDocument = IdentityDocumentRecognition.Recognize(_document, kind ?? _documentKindOverride);
        DocumentTypeText.Text = DocumentTypeName(_identityDocument.Kind);
        DocumentEditor.Text = string.Join(Environment.NewLine, _identityDocument.Fields.Select(f => f.Name + "：" + f.Value));
        _identityBaseline = IdentitySnapshot;
        ResultTabs.SelectedItem = DocumentResultTab;
        Status.Text = _identityDocument.Kind == IdentityDocumentKind.Unknown ? L.Get("DocumentUnknownHint")
            : L.Format("DocumentResultStatus", DocumentTypeName(_identityDocument.Kind), _identityDocument.Fields.Count(f => !string.IsNullOrWhiteSpace(f.Value)));
        var reviewFields = _identityDocument.Fields.Where(f => f.Value.Length > 0 && f.NeedsReview).Select(f => f.Name).ToArray();
        if (reviewFields.Length > 0) Status.Text += " " + L.Get("IdentityFieldReview") + ": " + string.Join(", ", reviewFields);
    }
    private void ChangeDocumentType_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) { Status.Text = L.Get("DocumentNoResult"); return; }
        var menu = new ContextMenu { PlacementTarget = ChangeDocumentTypeButton };
        foreach (var kind in Enum.GetValues<IdentityDocumentKind>())
        {
            var item = new MenuItem { Header = kind == IdentityDocumentKind.Unknown ? L.Get("DocumentTypeAuto") : DocumentTypeName(kind) };
            item.Click += (_, _) =>
            {
                if (IdentitySnapshot != _identityBaseline && MessageBox.Show(this, L.Get("RunningAgainWillReplaceManuallyEditedText"), L.Get("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _documentKindOverride = kind == IdentityDocumentKind.Unknown ? null : kind;
                ShowDocument();
            };
            menu.Items.Add(item);
        }
        ChangeDocumentTypeButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void DocumentText_Selected(object sender, RoutedEventArgs e)
    {
        if (PictureCanvas is null) return;
        foreach (var highlight in PictureCanvas.Children.OfType<Rectangle>().Where(x => !ReferenceEquals(x, WatermarkRegionOutline)).ToArray()) PictureCanvas.Children.Remove(highlight);
        if (DocumentEditor is null || _identityDocument is null) return;
        var prefix = DocumentEditor.Text[..Math.Min(DocumentEditor.CaretIndex, DocumentEditor.Text.Length)];
        var lineStart = prefix.LastIndexOf('\n') + 1;
        var line = DocumentEditor.Text[lineStart..].Split('\r', '\n')[0];
        var field = _identityDocument.Fields.FirstOrDefault(f => line.StartsWith(f.Name + "：", StringComparison.Ordinal) || line.StartsWith(f.Name + ":", StringComparison.Ordinal));
        if (field is null || field.Bounds.IsEmpty || field.Bounds.Width <= 0 || field.Bounds.Height <= 0) return;
        var r = field.Bounds;
        PictureCanvas.Children.Add(new Rectangle { Width = r.Width, Height = r.Height, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(r.Left, r.Top, 0, 0), Stroke = Brushes.DodgerBlue, StrokeThickness = 2 / PictureScale.ScaleX, IsHitTestVisible = false });
        PictureScroll.ScrollToHorizontalOffset(Math.Max(0, r.Left * PictureScale.ScaleX - PictureScroll.ViewportWidth / 3));
        PictureScroll.ScrollToVerticalOffset(Math.Max(0, r.Top * PictureScale.ScaleX - PictureScroll.ViewportHeight / 3));
    }
    private void CopyDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identityDocument is null) return;
        try
        {
            OutputActions.CopyText(DocumentEditor.Text);
            _identityBaseline = IdentitySnapshot; Status.Text = L.Get("DocumentFieldsCopied");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void ExportDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identityDocument is null) return;
        if (SaveBytes(Encoding.UTF8.GetBytes(DocumentEditor.Text), L.Get("TextTxt"), "NimCap-Screenshot-document")) _identityBaseline = IdentitySnapshot;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var previous = _settings.Current;
        SettingsRequested?.Invoke(Mode switch { "Watermark" => "Eraser", "Cutout" => "Scissors", _ => "ScanText" });
        if (ReferenceEquals(previous, _settings.Current)) return;
        if (Mode == "Watermark")
        {
            RefreshWatermarkChoices(_settings.Current.WatermarkDefaultModelId);
            WatermarkDeviceBox.SelectedValue = _settings.Current.WatermarkDevice;
            RefreshWatermarkModels();
        }
        RefreshModelToolbar(reset: true);
    }
    private void SetSourceText(string text)
    {
        _sourceText = text;
        InvalidateTranslation();
        TextViewBox.SelectedIndex = 0;
        RefreshTextView();
    }
    private void InvalidateTranslation()
    {
        _translatedText = "";
        _hasTranslation = false;
        if (CurrentTextView != "Source") TextViewBox.SelectedIndex = 0;
    }
    private void TargetLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetLanguageBox.SelectedItem is not TranslationLanguage || TextResultTab is null) return;
        InvalidateTranslation();
    }
    private void TextView_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentTextView != "Source" && !_hasTranslation)
        {
            TextViewBox.SelectedIndex = 0;
            if (Status is not null) Status.Text = L.Get("TranslateBeforeChoosingResultView");
            return;
        }
        RefreshTextView();
    }
    private void RefreshTextView()
    {
        if (TextEditor is null || TextViewBox?.SelectedItem is not ComboBoxItem selected) return;
        var view = selected.Tag?.ToString() ?? "Source";
        var text = view switch { "Translation" => _translatedText, "Bilingual" => BilingualText(_sourceText, _translatedText), _ => _sourceText };
        _syncing = true; TextEditor.Text = text; TextEditor.IsReadOnly = _busy || view != "Source"; _syncing = false;
    }
    private static string BilingualText(string source, string translation)
    {
        var sourceLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var translatedLines = translation.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>(Math.Max(sourceLines.Length, translatedLines.Length) * 2);
        for (var index = 0; index < Math.Max(sourceLines.Length, translatedLines.Length); index++)
        {
            if (index < sourceLines.Length) lines.Add(sourceLines[index]);
            if (index < translatedLines.Length) lines.Add(translatedLines[index]);
        }
        return string.Join(Environment.NewLine, lines);
    }
    private static string NormalizeTranslatedLine(string text) => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private void Text_Changed(object sender, TextChangedEventArgs e)
    {
        RefreshCommonActions();
        if (_syncing || CurrentTextView != "Source") return;
        _sourceText = TextEditor.Text;
        InvalidateTranslation();
        if (Status is not null && !_busy) Status.Text = L.Get("TextEditedCopyExportUsesTheEdited");
    }
    private void CopyText_Click(object sender, RoutedEventArgs e) { try { OutputActions.CopyText(TextEditor.Text); if (CurrentTextView == "Source") _baseline = _sourceText; Status.Text = L.Get("CurrentTextCopied"); } catch (Exception ex) { Status.Text = ex.Message; } }
    private void ExportText_Click(object sender, RoutedEventArgs e)
    {
        if (SaveBytes(Encoding.UTF8.GetBytes(TextEditor.Text), Mode == "Formula" ? L.Get("LaTeXTexTextTxt") : L.Get("TextTxt"), "NimCap-Screenshot-text") && CurrentTextView == "Source") _baseline = _sourceText;
    }
    private void ExportTable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CellsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !CellsGrid.CommitEdit(DataGridEditingUnit.Row, true)) throw new ArgumentException(L.Get("CorrectInvalidTableValuesFirst"));
            var csv = ((FrameworkElement)sender).Tag.ToString() == "csv";
            if (SaveBytes(csv ? RecognitionExport.Csv(_cells) : RecognitionExport.Xlsx(_cells), csv ? "CSV|*.csv" : "Excel|*.xlsx", "NimCap-Screenshot-table")) _tableBaseline = TableSnapshot;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private bool SaveBytes(byte[] bytes, string filter, string name)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = name, OverwritePrompt = false };
            if (WindowDialogService.ShowDialog(dialog, this) != true) return false;
            var overwrite = File.Exists(dialog.FileName);
            if (overwrite && MessageBox.Show(this, L.Get("TheFileAlreadyExistsReplaceIt"), L.Get("ConfirmOverwrite"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            OutputFileService.Write(dialog.FileName, bytes, overwrite); Status.Text = L.Get("Exported") + dialog.FileName; return true;
        }
        catch (Exception ex) { Status.Text = ex.Message; return false; }
    }
    private void CutoutOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_cutout is null) return;
        try
        {
            var action = Enum.Parse<CaptureAction>(((FrameworkElement)sender).Tag.ToString()!);
            if (action == CaptureAction.Save) SaveImageWindow.Open(_cutout, new SkiaImageService(), _settings, this, ".png");
            else OutputRequested?.Invoke(_cutout, action);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void TranslationSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke("Shield");
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))
                ?? throw new InvalidOperationException("Could not prepare translation settings.");
            settings.TranslationTarget = ((TranslationLanguage)TargetLanguageBox.SelectedItem).Code;
            var local = settings.TranslationProvider == "Local";
            if (local && (!File.Exists(settings.LocalTranslationModel) || !File.Exists(settings.LocalTranslationRuntime)))
            {
                Status.Text = L.Get("LocalTranslationUsesQwenLlamaCppNot");
                SettingsRequested?.Invoke("Shield"); return;
            }
            if (!local && (!settings.AllowOnlineTranslation || settings.TranslationProvider == "LibreTranslate" && string.IsNullOrWhiteSpace(settings.TranslationEndpoint)))
            {
                Status.Text = L.Get("InSettingsChooseATranslationEngineClick");
                SettingsRequested?.Invoke("Shield"); return;
            }
            var text = _sourceText;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException(L.Get("NoTextToTranslateRunOCROr"));
            if (!local && !PrivacyConsentDialog.Confirm(this, OnlineRecognitionService.TranslationUri(settings), text, null)) return;
            SetBusy(true); _operation = new CancellationTokenSource(); using var service = new OnlineRecognitionService();
            Status.Text = local ? L.Get("LoadingModelAndTranslatingLocally") + settings.LocalTranslationDevice + L.Get("FirstLoadMayBeSlow") : L.Get("CallingTheConfirmedTranslationAPI");
            var sourceLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var translatedLines = new string[sourceLines.Length];
            var linesToTranslate = sourceLines.Select((line, index) => (line, index)).Where(item => !string.IsNullOrWhiteSpace(item.line)).ToArray();
            if (local)
            {
                var translatedBatch = await LocalTranslationService.TranslateLines(linesToTranslate.Select(item => item.line).ToArray(), settings, _operation.Token);
                var values = translatedBatch.Split('\n');
                if (values.Length != linesToTranslate.Length) throw new InvalidDataException("Local translation returned an unexpected number of lines.");
                for (var index = 0; index < values.Length; index++) translatedLines[linesToTranslate[index].index] = NormalizeTranslatedLine(values[index]);
            }
            else
            {
                foreach (var item in linesToTranslate)
                    translatedLines[item.index] = NormalizeTranslatedLine(await service.Translate(item.line, settings, true, _operation.Token));
            }
            var translated = string.Join(Environment.NewLine, translatedLines);
            if (!_closed)
            {
                _translatedText = translated;
                _hasTranslation = true;
                TextViewBox.SelectedIndex = 1;
                Status.Text = (local ? L.Get("LocalTranslationCompleteNothingWasUploadedTo") : L.Get("APITranslationComplete")) + L.Get("SourceTextWasPreserved");
            }
        }
        catch (Exception ex) { if (!_closed) Status.Text = L.Get("TranslationIncompleteSourceTextPreserved") + ex.GetBaseException().Message; }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var settings = _settings.Current;
            if (!settings.AllowImageSearchUpload) throw new InvalidOperationException(L.Get("ConfigureAndAllowImageSearchUploadsIn"));
            var endpoint = OnlineRecognitionService.Endpoint(settings.ImageSearchEndpoint);
            if (!PrivacyConsentDialog.Confirm(this, endpoint, null, _image)) return;
            SetBusy(true); _operation = new CancellationTokenSource(); using var service = new OnlineRecognitionService();
            var png = await Task.Run(() => new SkiaImageService().Encode(_image, ".png"));
            var url = await service.Search(png, settings, true, _operation.Token);
            if (!_closed) Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { if (!_closed) Status.Text = L.Get("ImageSearchIncomplete") + ex.GetBaseException().Message; }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CellsGrid.CommitEdit();
        if (HasEdits && MessageBox.Show(this, L.Get("EditedTextOrTablesHaveNotBeen"), L.Get("DiscardChanges"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
    }
    internal static bool RunLayoutSelfTest(BitmapSource source)
    {
        var window = new RecognitionWindow(source, new SettingsService(), autoRun: false);
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(1140, 790)); content.Arrange(new Rect(0, 0, 1140, 790)); content.UpdateLayout();
            if (window.Picture.Source is not BitmapSource preview || preview.PixelWidth != source.PixelWidth || preview.PixelHeight != source.PixelHeight || window.Mode != "OCR" || window._busy)
                throw new InvalidOperationException("Clipboard entry must show the imported image before running a selected tool.");
            window.ModeBox.SelectedItem = window.ModeBox.Items.OfType<ListBoxItem>().Single(item => item.Tag?.ToString() == "Cutout");
            if (window.CutoutModelBox.Visibility != Visibility.Visible || window.ResultTabs.SelectedItem != window.CutoutResultTab)
                throw new InvalidOperationException("Clipboard image cutout mode selection failed.");
            window.ModeBox.SelectedIndex = 0;
            window._document = new RecognitionDocument([new("重复", new Rect(0, 0, 20, 10)), new("重复", new Rect(25, 15, 20, 10))], "test");
            window.SetSourceText(window._document.Text); window._baseline = window._sourceText;
            if (window.CurrentTextView != "Source" || window.TextEditor.IsReadOnly || window.TargetLanguageBox.Text != ((TranslationLanguage)window.TargetLanguageBox.SelectedItem).Name || window.PictureScroll.ActualWidth <= 200 || window.TextEditor.ActualWidth <= 200)
                throw new InvalidOperationException($"Recognition layout: view={window.CurrentTextView}, preview={window.PictureScroll.ActualWidth}, editor={window.TextEditor.ActualWidth}");
            window.TextViewBox.SelectedIndex = 1;
            if (window.CurrentTextView != "Source") throw new InvalidOperationException("Untranslated result view must return to the source text.");
            window._translatedText = "one" + Environment.NewLine + "two";
            window._hasTranslation = true;
            window.TextViewBox.SelectedIndex = 2;
            if (window.TextEditor.Text != string.Join(Environment.NewLine, window._document.Blocks[0].Text, "one", window._document.Blocks[1].Text, "two") || !window.TextEditor.IsReadOnly)
                throw new InvalidOperationException("Bilingual OCR result layout failed.");
            return true;
        }
        finally { window._baseline = window._sourceText; window._tableBaseline = window.TableSnapshot; window.Close(); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
