using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Futures.Analyzer;
using Futures.App.Commands;
using Futures.Core;

namespace Futures.App;

internal static class DesktopLauncher
{
    public static int Run(string workspace)
    {
        DesktopState.WorkspacePath = workspace;
        return AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }
}

internal static class DesktopState
{
    public static string WorkspacePath { get; set; } = Environment.CurrentDirectory;
}

internal sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());

        // Paleta B&W estricta para Slider y controles de línea de tiempo
        Resources["SliderTrackValueFill"] = SolidColorBrush.Parse("#FFFFFF");
        Resources["SliderTrackValueFillPointerOver"] = SolidColorBrush.Parse("#FFFFFF");
        Resources["SliderTrackValueFillPressed"] = SolidColorBrush.Parse("#E4E4E7");
        Resources["SliderTrackValueFillDisabled"] = SolidColorBrush.Parse("#3F3F46");
        Resources["SliderTrackFill"] = SolidColorBrush.Parse("#18181B");
        Resources["SliderTrackFillPointerOver"] = SolidColorBrush.Parse("#27272A");
        Resources["SliderTrackFillPressed"] = SolidColorBrush.Parse("#27272A");
        Resources["SliderTrackFillDisabled"] = SolidColorBrush.Parse("#18181B");
        Resources["SliderThumbBackground"] = SolidColorBrush.Parse("#FFFFFF");
        Resources["SliderThumbBackgroundPointerOver"] = SolidColorBrush.Parse("#FFFFFF");
        Resources["SliderThumbBackgroundPressed"] = SolidColorBrush.Parse("#E4E4E7");
        Resources["SliderThumbBackgroundDisabled"] = SolidColorBrush.Parse("#52525B");
        Resources["SliderThumbBorderBrush"] = SolidColorBrush.Parse("#000000");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(DesktopState.WorkspacePath);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed record DateComboOption(string Label, bool HasData, int Value)
{
    public override string ToString() => Label;
}

internal static class DatasetDateCatalog
{
    private static readonly HashSet<int> ohlcvDates = new();
    private static readonly HashSet<int> tbboDates = new();
    private static bool isInitialized;
    private static readonly object initLock = new();

    public static void Initialize(string workspacePath)
    {
        lock (initLock)
        {
            if (isInitialized) return;
            LoadDates(workspacePath, "ohlcv-1m", ohlcvDates,
                Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: false) ?? "NQ_1min_UTC_cierre.txt");
            LoadDates(workspacePath, "tbbo", tbboDates,
                Workspace.FindDatabentoTbboMinuteFile(workspacePath) ?? "NQ_1min_UTC_cierre.txt");
            isInitialized = true;
        }
    }

    private static void LoadDates(string workspacePath, string schema, HashSet<int> targetSet, string fallbackTxt)
    {
        try
        {
            string txtPath = Path.IsPathRooted(fallbackTxt) ? fallbackTxt
                : Path.Combine(workspacePath, "data", "databento", "NQ", schema, fallbackTxt);
            string cachePath = Path.Combine(Path.GetDirectoryName(txtPath)!, "dates_cache.json");
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                var list = JsonSerializer.Deserialize<List<int>>(json);
                if (list != null)
                {
                    foreach (int d in list) targetSet.Add(d);
                    return;
                }
            }

            if (File.Exists(txtPath))
            {
                using var reader = new StreamReader(txtPath);
                string? line;
                int last = -1;
                var list = new List<int>();
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length >= 8 && int.TryParse(line.AsSpan(0, 8), out int d) && d != last)
                    {
                        // Las velas TBBO se marcan por cierre; el calendario muestra el día del intervalo.
                        if (schema == "tbbo" && line.Length >= 15 && DateTime.TryParseExact(
                            line.AsSpan(0, 15), "yyyyMMdd HHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime close))
                        {
                            DateTime start = close.AddMinutes(-1);
                            d = start.Year * 10000 + start.Month * 100 + start.Day;
                        }
                        if (d == last) continue;
                        if (targetSet.Add(d)) list.Add(d);
                        last = d;
                    }
                }
                try
                {
                    File.WriteAllText(cachePath, JsonSerializer.Serialize(list));
                }
                catch { }
            }
        }
        catch { }
    }

    public static bool HasDate(bool tbbo, int year, int month, int day)
    {
        int key = year * 10000 + month * 100 + day;
        var set = tbbo ? tbboDates : ohlcvDates;
        if (set.Count > 0)
            return set.Contains(key);

        DateTime dt;
        try { dt = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc); }
        catch { return false; }
        if (dt.DayOfWeek == DayOfWeek.Saturday) return false;
        DateTime min = new(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc);
        DateTime max = LastDate(tbbo);
        return dt >= min && dt <= max;
    }

    public static bool HasMonth(bool tbbo, int year, int month)
    {
        int prefixMin = year * 10000 + month * 100 + 1;
        int prefixMax = year * 10000 + month * 100 + 31;
        var set = tbbo ? tbboDates : ohlcvDates;
        if (set.Count > 0)
        {
            for (int k = prefixMin; k <= prefixMax; k++)
                if (set.Contains(k)) return true;
            return false;
        }
        DateTime min = new(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc);
        DateTime max = LastDate(tbbo);
        DateTime mStart = new(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime mEnd = new(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return mEnd >= min && mStart <= max;
    }

    public static bool HasYear(bool tbbo, int year)
    {
        var dates = tbbo ? tbboDates : ohlcvDates;
        return dates.Count > 0 ? dates.Any(d => d / 10000 == year)
            : year >= FirstDate(tbbo).Year && year <= LastDate(tbbo).Year;
    }

    public static DateTime FirstDate(bool tbbo) => DateBound(tbbo, first: true);
    public static DateTime LastDate(bool tbbo) => DateBound(tbbo, first: false);

    private static DateTime DateBound(bool tbbo, bool first)
    {
        var dates = tbbo ? tbboDates : ohlcvDates;
        if (dates.Count == 0)
            return first ? new DateTime(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc)
                : tbbo ? new DateTime(2018, 6, 26, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        int key = first ? dates.Min() : dates.Max();
        return new DateTime(key / 10000, key / 100 % 100, key % 100, 0, 0, 0, DateTimeKind.Utc);
    }
}

internal sealed class RolloverEntryDto
{
    [JsonPropertyName("bar_index")]
    public int BarIndex { get; set; }

    [JsonPropertyName("close_time_utc")]
    public DateTimeOffset CloseTimeUtc { get; set; }

    [JsonPropertyName("from_instrument_id")]
    public long FromInstrumentId { get; set; }

    [JsonPropertyName("to_instrument_id")]
    public long ToInstrumentId { get; set; }

    [JsonPropertyName("previous_close")]
    public double PreviousClose { get; set; }

    [JsonPropertyName("new_open")]
    public double NewOpen { get; set; }

    [JsonPropertyName("adjustment_points")]
    public double AdjustmentPoints { get; set; }
}

internal sealed record RolloverComputedRow(
    int SequenceNumber,
    DateTime CloseTimeUtc,
    string QuarterName,
    long FromInstrumentId,
    long ToInstrumentId,
    double PreviousClose,
    double NewOpen,
    double AdjustmentPoints,
    double CumulativeFrom2010,
    double CumulativeBackFrom2026);

internal sealed class MainWindow : Window
{
    private readonly string workspaceRoot;
    private readonly string defaultDatasetPath;
    private readonly string adjustedDatasetPath;
    private readonly string tbboDatasetPath;
    private readonly string tbboMinuteDatasetPath;
    private readonly string? rolloversFilePath;

    // Controles de navegación del menú inferior
    private readonly Button navChartBtn;
    private readonly Button navRolloversBtn;
    private readonly Button navAnalyzerBtn;
    private readonly Button navPropFirmBtn;
    private readonly Button navSchemaBtn;
    private readonly Button navDateTfBtn;
    private readonly Button navConfigBtn;
    private readonly Button navAnalyzerDateBtn;
    private readonly Button navAnalyzerStrategyBtn;
    private readonly Button navAnalyzerRiskBtn;
    private readonly Button navAnalyzerRunBtn;

    // Ventana flotante modal centrada
    private readonly Grid modalOverlay;
    private readonly Border modalCard;
    private readonly Control[] modalViews;

    private readonly Control chartPageView;
    private readonly Control rolloversPageView;
    private readonly Control analyzerPageView;
    private readonly Control propFirmPageView;

    // Controles Página 1: Gráfico e interacciones
    private readonly ComboBox schemaBox;
    private readonly TextBlock schemaCoverageText;
    private readonly ComboBox yearJumpBox;
    private readonly ComboBox monthJumpBox;
    private readonly ComboBox dayJumpBox;
    private readonly TextBox fromBox;
    private readonly TextBox toBox;
    private readonly TextBlock dateRangeStatusText;
    private readonly ComboBox timeframeBox;
    private readonly Button loadBtn;
    private readonly TextBlock statusBanner;
    private readonly TextBlock headerSubtitle;
    private readonly TextBlock infoBarsText;
    private readonly TextBlock infoRangeText;
    private readonly TextBlock infoPriceBoundsText;
    private readonly TextBlock infoOrderFlowText;
    private readonly Slider timelineSlider;
    private readonly TextBlock timelinePositionBadge;
    private readonly ChartSurface chartSurface;
    private bool suppressDateComboEvents;
    private bool suppressTimeframeEvents;

    // Control de reproducción en tiempo real (Play / Detener) y Tick Replay
    private int playbackSpeedMultiplier = 1;
    private DispatcherTimer? playbackTimer;
    private bool isPlaying;
    private bool isTickReplayMode;
    private List<Tick>? currentCandleTicks;
    private int currentTickIndex;
    private Button? playPauseBtn;
    private Button? replayModeBtn;
    private Button? speedBtn;
    private Button? stepBackBtn;
    private Button? stepFwdBtn;

    // Controles Página 2: Historial de Vencimientos
    private readonly StackPanel rolloverTableRows;
    private readonly RolloverAccumulationSurface rolloverChartSurface;
    private readonly Button sortToggleBtn;
    private List<RolloverComputedRow> rolloverRowsData = new();
    private bool sortDescending;

    // Controles Página 3: Analizador de Estrategias & Fondeo
    private ComboBox? btStrategyBox;
    private ComboBox? btInstrumentBox;
    private ComboBox? btDataModeBox;
    private TextBox? btFromBox;
    private TextBox? btToBox;
    private TextBox? btSessionStartBox;
    private TextBox? btSessionEndBox;
    private TextBox? btFastBox;
    private TextBox? btSlowBox;
    private TextBox? btQtyBox;
    private TextBox? btSlTicksBox;
    private TextBox? btTpTicksBox;
    private TextBox? btBeTriggerBox;
    private TextBox? btBeOffsetBox;
    private TextBox? btTrailTicksBox;
    private TextBox? btCommBox;
    private TextBox? btSlippageBox;
    private TextBox? btMaxDailyLossBox;
    private TextBox? btMaxDailyTradesBox;
    private Button? btRunBtn;
    private TextBlock? btStatusText;
    private TextBlock? kpiNetPnlVal;
    private TextBlock? kpiNetPnlSub;
    private TextBlock? kpiWinRateVal;
    private TextBlock? kpiWinRateSub;
    private TextBlock? kpiPfVal;
    private TextBlock? kpiPfSub;
    private TextBlock? kpiDdVal;
    private TextBlock? kpiDdSub;
    private TextBlock? kpiSharpeVal;
    private TextBlock? kpiSharpeSub;
    private TextBlock? kpiDaysVal;
    private TextBlock? kpiDaysSub;
    private EquityCurveSurface? btEquitySurface;
    private TextBlock? btGraphFinalNetText;
    private TextBlock? btGraphPeakText;
    private TextBlock? btGraphMaxDdText;
    private TextBlock? btGraphWinRateText;

    // Vistas estilo NinjaTrader Strategy Analyzer
    private Control? btSummaryContainer;
    private Control? btGraphContainer;
    private Control? btTradesContainer;
    private Control? btPeriodsContainer;
    private Control? btMetricsContainer;

    private Button? btTabSummaryBtn;
    private Button? btTabGraphBtn;
    private Button? btTabTradesBtn;
    private Button? btTabPeriodsBtn;
    private Button? btTabMetricsBtn;

    // Vistas Página 4: Simulador Multi-Cuenta Prop Firm
    private Control? pfSimContainer;
    private Control? pfCostContainer;
    private Control? pfRulesContainer;

    private Button? pfTabSimBtn;
    private Button? pfTabCostBtn;
    private Button? pfTabRulesBtn;

    // Controles Simulador Multi-Cuenta Prop Firm
    private ComboBox? pfPlanCombo;
    private ComboBox? pfStrategyModeCombo;
    private ComboBox? pfAccountsCountCombo;
    private TextBox? pfAccountPriceBox;
    private TextBox? pfActivationPriceBox;
    private TextBlock? pfKpiEvalPassVal;
    private TextBlock? pfKpiEvalPassSub;
    private TextBlock? pfKpiPayoutPassVal;
    private TextBlock? pfKpiPayoutPassSub;
    private TextBlock? pfKpiTotalWithdrawnVal;
    private TextBlock? pfKpiTotalWithdrawnSub;
    private TextBlock? pfKpiTotalCostVal;
    private TextBlock? pfKpiTotalCostSub;
    private TextBlock? pfKpiNetRoiVal;
    private TextBlock? pfKpiNetRoiSub;
    private StackPanel? pfAccountsTableRows;
    private TextBlock? pfEvText;
    private TextBlock? pfVerdictText;
    private Border? pfPassBarFill;
    private Border? pfPayoutBarFill;
    private Border? pfBlownBarFill;
    private TextBlock? pfDistributionLegend;
    private Grid? pfDistTrack;
    private StackPanel? pfCostTableRows;
    private TextBlock? pfCostTitleText;

    private DailyTradesChartSurface? btDailyTradesSurface;
    private TextBlock? metAvgTradesDayVal;
    private TextBlock? metAvgTradesDaySub;
    private TextBlock? metExtremesTradesVal;
    private TextBlock? metExtremesTradesSub;
    private TextBlock? metAvgPnlDayVal;
    private TextBlock? metAvgPnlDaySub;
    private TextBlock? metWinningDaysVal;
    private TextBlock? metWinningDaysSub;
    private TextBlock? metAvgDurationVal;
    private TextBlock? metAvgDurationSub;
    private StackPanel? metDowStack;
    private StackPanel? metFreqBucketsStack;
    private TextBlock? metBestDayVal;
    private TextBlock? metBestDaySub;
    private TextBlock? metWorstDayVal;
    private TextBlock? metWorstDaySub;
    private TextBlock? metAvgIntradayDdVal;
    private TextBlock? metAvgIntradayDdSub;
    private TextBlock? metCommImpactVal;
    private TextBlock? metCommImpactSub;
    private BacktestResult? lastBacktestResult;

    private StackPanel? btSummaryRows;
    private StackPanel? btPeriodsRows;
    private StackPanel? btTradesRows;
    private bool isRunningBacktest;

    private IReadOnlyList<TradeRecord> currentAllTrades = Array.Empty<TradeRecord>();
    private int tradesCurrentPage;
    private const int TradesPageSize = 500;
    private TextBlock? btTradesPageInfoText;
    private Button? btTradesPrevBtn;
    private Button? btTradesNextBtn;
    private Button? btTradesFirstBtn;
    private Button? btTradesLastBtn;
    private ProgressBar? btProgressBar;

    private IReadOnlyList<Bar> rawMinuteBars = Array.Empty<Bar>();
    private IReadOnlyList<Tick> rawTicks = Array.Empty<Tick>();
    private bool suppressSliderEvents;
    private bool isLoading;

    private bool IsTbboMode => schemaBox.SelectedIndex == 1;

    public MainWindow(string workspacePath)
    {
        workspaceRoot = workspacePath;
        defaultDatasetPath = Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: false)
            ?? Path.Combine(workspacePath, "data", "databento", "NQ", "ohlcv-1m", "NQ_1min_UTC_cierre.txt");
        adjustedDatasetPath = Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: true)
            ?? Path.Combine(workspacePath, "data", "databento", "NQ", "ohlcv-1m", "NQ_1min_UTC_cierre_ajustado.txt");
        tbboDatasetPath = Workspace.FindDatabentoTbboFile(workspacePath)
            ?? Path.Combine(workspacePath, "data", "databento", "NQ", "tbbo", "ticks");
        tbboMinuteDatasetPath = Workspace.FindDatabentoTbboMinuteFile(workspacePath)
            ?? Path.Combine(workspacePath, "data", "databento", "NQ", "tbbo", "NQ_1min_UTC_cierre.txt");
        rolloversFilePath = Workspace.FindDatabentoRolloversFile(workspacePath);

        DatasetDateCatalog.Initialize(workspacePath);

        Title = "Ascent Futures Lab · Analizador Profesional, Gráfico & Vencimientos (NQ / ES)";
        Width = 1480;
        Height = 920;
        MinWidth = 1080;
        MinHeight = 680;
        Background = SolidColorBrush.Parse("#09090B");

        var outerGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("54,*,Auto"),
            Margin = new Thickness(16, 12, 16, 12)
        };

        // === 1. BARRA SUPERIOR (SOLO TÍTULO E INFO) ===
        var topBar = BuildTopBar(out headerSubtitle);
        Grid.SetRow(topBar, 0);
        outerGrid.Children.Add(topBar);

        // === 2. CONTENEDOR CENTRAL DE PÁGINAS ===
        chartPageView = BuildChartPage(
            out schemaBox,
            out schemaCoverageText,
            out yearJumpBox,
            out monthJumpBox,
            out dayJumpBox,
            out fromBox,
            out toBox,
            out dateRangeStatusText,
            out timeframeBox,
            out loadBtn,
            out statusBanner,
            out infoBarsText,
            out infoRangeText,
            out infoPriceBoundsText,
            out infoOrderFlowText,
            out timelineSlider,
            out timelinePositionBadge,
            out chartSurface,
            out var chartModalViews);

        rolloversPageView = BuildRolloversPage(
            out rolloverTableRows,
            out rolloverChartSurface,
            out sortToggleBtn);

        analyzerPageView = BuildAnalyzerPage(
            out var analyzerDateModal,
            out var analyzerStrategyModal,
            out var analyzerRiskModal);

        propFirmPageView = BuildPropFirmPage();

        var pageContainer = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12)
        };
        pageContainer.Children.Add(chartPageView);
        pageContainer.Children.Add(rolloversPageView);
        pageContainer.Children.Add(analyzerPageView);
        pageContainer.Children.Add(propFirmPageView);
        Grid.SetRow(pageContainer, 1);
        outerGrid.Children.Add(pageContainer);

        // === 3. MENÚ INFERIOR CON 3 SECCIONES (HERRAMIENTAS · NAVEGACIÓN · CONFIGURACIÓN) ===
        var bottomMenu = BuildBottomMenuBar(
            out navChartBtn,
            out navRolloversBtn,
            out navAnalyzerBtn,
            out navPropFirmBtn,
            out navSchemaBtn,
            out navDateTfBtn,
            out navConfigBtn,
            out navAnalyzerDateBtn,
            out navAnalyzerStrategyBtn,
            out navAnalyzerRiskBtn,
            out navAnalyzerRunBtn);
        Grid.SetRow(bottomMenu, 2);
        outerGrid.Children.Add(bottomMenu);

        navSchemaBtn.Click += (_, _) => { SwitchPage(0); OpenModal(0); };
        navDateTfBtn.Click += (_, _) => { SwitchPage(0); OpenModal(1); };
        navConfigBtn.Click += (_, _) => OpenModal(2);

        navAnalyzerDateBtn.Click += (_, _) => OpenModal(3);
        navAnalyzerStrategyBtn.Click += (_, _) => OpenModal(4);
        navAnalyzerRiskBtn.Click += (_, _) => OpenModal(5);
        navAnalyzerRunBtn.Click += async (_, _) => await RunInteractiveBacktestAsync();

        // === 4. CAPA MODAL CENTRADA FLOTANTE ===
        modalViews = new Control[6];
        modalViews[0] = chartModalViews[0];
        modalViews[1] = chartModalViews[1];
        modalViews[2] = chartModalViews[2];
        modalViews[3] = analyzerDateModal;
        modalViews[4] = analyzerStrategyModal;
        modalViews[5] = analyzerRiskModal;

        var modalContentHolder = new Grid();
        for (int i = 0; i < modalViews.Length; i++)
        {
            modalViews[i].IsVisible = false;
            modalContentHolder.Children.Add(modalViews[i]);
        }

        modalCard = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 16),
            MinWidth = 440,
            MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = modalContentHolder
        };

        modalOverlay = new Grid
        {
            Background = SolidColorBrush.Parse("#85000000"),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        modalOverlay.PointerPressed += (s, e) =>
        {
            if (e.Source == modalOverlay)
                CloseModal();
        };
        modalOverlay.Children.Add(modalCard);

        var rootGrid = new Grid();
        rootGrid.Children.Add(outerGrid);
        rootGrid.Children.Add(modalOverlay);

        Content = rootGrid;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && modalOverlay.IsVisible)
            {
                CloseModal();
                e.Handled = true;
            }
            else if (e.Key == Key.F && !modalOverlay.IsVisible)
            {
                chartSurface.ToggleAutoScale();
                e.Handled = true;
            }
        };

        SyncDateDropdowns(new DateTime(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc));
        LoadRolloverHistory();
        SwitchPage(0);

        Opened += async (_, _) => await LoadMarketDataAsync();
    }

    private void SwitchPage(int pageIndex)
    {
        if (pageIndex != 0)
            StopPlayback();

        chartPageView.IsVisible = pageIndex == 0;
        rolloversPageView.IsVisible = pageIndex == 1;
        analyzerPageView.IsVisible = pageIndex == 2;
        propFirmPageView.IsVisible = pageIndex == 3;

        navChartBtn.Background = pageIndex == 0 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navChartBtn.Foreground = SolidColorBrush.Parse(pageIndex == 0 ? "#09090B" : "#A1A1AA");

        navRolloversBtn.Background = pageIndex == 1 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navRolloversBtn.Foreground = SolidColorBrush.Parse(pageIndex == 1 ? "#09090B" : "#A1A1AA");

        navAnalyzerBtn.Background = pageIndex == 2 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navAnalyzerBtn.Foreground = SolidColorBrush.Parse(pageIndex == 2 ? "#09090B" : "#A1A1AA");

        navPropFirmBtn.Background = pageIndex == 3 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navPropFirmBtn.Foreground = SolidColorBrush.Parse(pageIndex == 3 ? "#09090B" : "#A1A1AA");

        bool isChart = pageIndex == 0;
        bool isAnalyzer = pageIndex == 2;

        navSchemaBtn.IsVisible = isChart;
        navDateTfBtn.IsVisible = isChart;

        navAnalyzerDateBtn.IsVisible = isAnalyzer;
        navAnalyzerStrategyBtn.IsVisible = isAnalyzer;
        navAnalyzerRiskBtn.IsVisible = isAnalyzer;
        navAnalyzerRunBtn.IsVisible = isAnalyzer;

        if (pageIndex == 3 && pfAccountsTableRows != null && pfAccountsTableRows.Children.Count == 0)
        {
            RunPropFirmSimulation();
        }
    }

    private void OpenModal(int viewIndex)
    {
        for (int i = 0; i < modalViews.Length; i++)
        {
            modalViews[i].IsVisible = i == viewIndex;
        }
        modalOverlay.IsVisible = true;
    }

    private void CloseModal()
    {
        modalOverlay.IsVisible = false;
    }

    private Control BuildTopBar(out TextBlock subtitleBlock)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        var leftStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        leftStack.Children.Add(new Border
        {
            Background = SolidColorBrush.Parse("#FFFFFF"),
            CornerRadius = new CornerRadius(8),
            Width = 34,
            Height = 34,
            Child = new TextBlock
            {
                Text = "AF",
                Foreground = SolidColorBrush.Parse("#09090B"),
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "ASCENT FUTURES LAB",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.Bold,
            FontSize = 14
        });
        subtitleBlock = new TextBlock
        {
            Text = "CME Globex · NQ Continuo (OHLCV)",
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11.5
        };
        titleStack.Children.Add(subtitleBlock);
        leftStack.Children.Add(titleStack);
        Grid.SetColumn(leftStack, 0);
        grid.Children.Add(leftStack);

        var rightBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightBadges.Children.Add(MakeBadge("CME Globex · NQ", "#18181B", "#E4E4E7"));
        rightBadges.Children.Add(MakeBadge("Arrastrar: Mover · Rueda: Zoom", "#18181B", "#71717A"));
        rightBadges.Children.Add(MakeBadge("v0.4.0", "#18181B", "#A1A1AA"));

        Grid.SetColumn(rightBadges, 2);
        grid.Children.Add(rightBadges);

        return new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8),
            Child = grid
        };
    }

    private static Button MakeBottomNavButton(string text) => new()
    {
        Content = text,
        Background = SolidColorBrush.Parse("#121214"),
        Foreground = SolidColorBrush.Parse("#F4F4F5"),
        BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
        BorderThickness = new Thickness(1),
        FontSize = 11.5,
        FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(11, 5),
        CornerRadius = new CornerRadius(6),
        Cursor = new Cursor(StandardCursorType.Hand)
    };

    private static Control BuildModalView(string title, Control content, Action onClose)
    {
        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 14)
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(new Border
        {
            Width = 3,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            Background = SolidColorBrush.Parse("#FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.Bold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        headerRow.Children.Add(titleStack);

        var closeBtn = new Button
        {
            Content = "✕",
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(8, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        closeBtn.Click += (_, _) => onClose();
        Grid.SetColumn(closeBtn, 2);
        headerRow.Children.Add(closeBtn);

        var stack = new StackPanel
        {
            Spacing = 12,
            Children = { headerRow, content }
        };
        return stack;
    }

    private Control BuildBottomMenuBar(
        out Button chartTabBtn,
        out Button rolloversTabBtn,
        out Button analyzerTabBtn,
        out Button propFirmTabBtn,
        out Button schemaBtn,
        out Button dateTfBtn,
        out Button configBtn,
        out Button analyzerDateBtn,
        out Button analyzerStrategyBtn,
        out Button analyzerRiskBtn,
        out Button analyzerRunBtn)
    {
        // 1. Herramientas del gráfico y analizador (extremo izquierdo)
        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        schemaBtn = MakeBottomNavButton("Esquema");
        dateTfBtn = MakeBottomNavButton("Fecha y Temporalidad");

        analyzerDateBtn = MakeBottomNavButton("Fecha");
        analyzerStrategyBtn = MakeBottomNavButton("Estrategia");
        analyzerRiskBtn = MakeBottomNavButton("Gestión de Riesgo");
        analyzerRunBtn = MakeBottomNavButton("Ejecutar");
        analyzerRunBtn.Background = SolidColorBrush.Parse("#FFFFFF");
        analyzerRunBtn.Foreground = SolidColorBrush.Parse("#09090B");
        analyzerRunBtn.FontWeight = FontWeight.Bold;

        analyzerDateBtn.IsVisible = false;
        analyzerStrategyBtn.IsVisible = false;
        analyzerRiskBtn.IsVisible = false;
        analyzerRunBtn.IsVisible = false;

        leftGroup.Children.Add(schemaBtn);
        leftGroup.Children.Add(dateTfBtn);
        leftGroup.Children.Add(analyzerDateBtn);
        leftGroup.Children.Add(analyzerStrategyBtn);
        leftGroup.Children.Add(analyzerRiskBtn);
        leftGroup.Children.Add(analyzerRunBtn);

        // 2. Pestañas centrales de navegación (las más importantes)
        var menuButtonsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        chartTabBtn = new Button
        {
            Content = "Gráfico",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Padding = new Thickness(20, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        chartTabBtn.Click += (_, _) => SwitchPage(0);

        rolloversTabBtn = new Button
        {
            Content = "Rollovers",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Padding = new Thickness(20, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        rolloversTabBtn.Click += (_, _) => SwitchPage(1);

        analyzerTabBtn = new Button
        {
            Content = "Analizador",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Padding = new Thickness(20, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        analyzerTabBtn.Click += (_, _) => SwitchPage(2);

        propFirmTabBtn = new Button
        {
            Content = "Prop Firm",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Padding = new Thickness(20, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        propFirmTabBtn.Click += (_, _) => SwitchPage(3);

        menuButtonsStack.Children.Add(chartTabBtn);
        menuButtonsStack.Children.Add(rolloversTabBtn);
        menuButtonsStack.Children.Add(analyzerTabBtn);
        menuButtonsStack.Children.Add(propFirmTabBtn);

        var dockBorder = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = menuButtonsStack
        };

        // 3. Botón de Configuración (extremo derecho)
        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        configBtn = MakeBottomNavButton("Configuración");
        rightGroup.Children.Add(configBtn);

        // Layout de precisión matemática:
        // sideGrid distribuye los extremos (leftGroup al inicio, rightGroup al final).
        // dockBorder se coloca en bottomGrid con HorizontalAlignment.Center, quedando
        // 100% centrado respecto al cuadro/contenedor del menú inferior sin verse afectado
        // por la asimetría de tamaño entre la izquierda y la derecha.
        var bottomGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var sideGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(leftGroup, 0);
        Grid.SetColumn(rightGroup, 2);

        sideGrid.Children.Add(leftGroup);
        sideGrid.Children.Add(rightGroup);

        dockBorder.HorizontalAlignment = HorizontalAlignment.Center;
        dockBorder.VerticalAlignment = VerticalAlignment.Center;

        bottomGrid.Children.Add(sideGrid);
        bottomGrid.Children.Add(dockBorder);

        return new Border
        {
            Background = SolidColorBrush.Parse("#000000"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = bottomGrid
        };
    }

    private Control BuildChartPage(
        out ComboBox schemaCombo,
        out TextBlock coverageText,
        out ComboBox yearCombo,
        out ComboBox monthCombo,
        out ComboBox dayCombo,
        out TextBox fromInput,
        out TextBox toInput,
        out TextBlock dateStatusBadge,
        out ComboBox tfCombo,
        out Button reloadButton,
        out TextBlock statusText,
        out TextBlock barsInfo,
        out TextBlock rangeInfo,
        out TextBlock boundsInfo,
        out TextBlock orderFlowInfo,
        out Slider scrubber,
        out TextBlock positionBadge,
        out ChartSurface surface,
        out Control[] modalViewsOut)
    {
        schemaCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "ohlcv-1m · Velas 1 Minuto (2010–2026)",
                $"tbbo · Ticks L1 Bid/Ask ({TbboYearRange})"
            },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12
        };
        schemaCombo.SelectionChanged += async (_, _) => await OnSchemaChangedAsync();

        coverageText = new TextBlock
        {
            Text = TbboCoverage,
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 10.5
        };

        yearCombo = new ComboBox
        {
            ItemTemplate = MakeDateComboTemplate(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11.5
        };

        monthCombo = new ComboBox
        {
            ItemTemplate = MakeDateComboTemplate(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11.5
        };

        dayCombo = new ComboBox
        {
            ItemTemplate = MakeDateComboTemplate(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11.5
        };

        var capturedYear = yearCombo;
        var capturedMonth = monthCombo;
        var capturedDay = dayCombo;

        capturedYear.SelectionChanged += async (_, _) =>
        {
            if (suppressDateComboEvents) return;
            var opt = capturedYear.SelectedItem as DateComboOption;
            int yr = opt?.Value ?? (IsTbboMode ? 2010 : 2026);
            suppressDateComboEvents = true;
            try
            {
                RebuildMonthCombo(yr);
                var mOpt = capturedMonth.SelectedItem as DateComboOption;
                int mo = mOpt?.Value ?? (IsTbboMode ? 6 : 10);
                RebuildDayCombo(yr, mo);
            }
            finally
            {
                suppressDateComboEvents = false;
            }
            await UpdateDatesFromDropdownsAsync();
        };

        capturedMonth.SelectionChanged += async (_, _) =>
        {
            if (suppressDateComboEvents) return;
            var yOpt = capturedYear.SelectedItem as DateComboOption;
            int yr = yOpt?.Value ?? (IsTbboMode ? 2010 : 2026);
            var mOpt = capturedMonth.SelectedItem as DateComboOption;
            int mo = mOpt?.Value ?? (IsTbboMode ? 6 : 10);
            suppressDateComboEvents = true;
            try
            {
                RebuildDayCombo(yr, mo);
            }
            finally
            {
                suppressDateComboEvents = false;
            }
            await UpdateDatesFromDropdownsAsync();
        };

        capturedDay.SelectionChanged += async (_, _) =>
        {
            if (suppressDateComboEvents) return;
            await UpdateDatesFromDropdownsAsync();
        };

        var prevDayBtn = MakeBottomNavButton("‹ Día Ant.");
        prevDayBtn.Click += async (_, _) => await ShiftDateStepAsync(-1);

        var nextDayBtn = MakeBottomNavButton("Día Sig. ›");
        nextDayBtn.Click += async (_, _) => await ShiftDateStepAsync(1);

        var stepDateGrid = TwoColumn(prevDayBtn, nextDayBtn);

        dateStatusBadge = new TextBlock
        {
            Text = "● 2010-06-07 UTC · Sesión activa con datos",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var dateRangeBadge = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Child = dateStatusBadge
        };

        fromInput = MakeInput("2010-06-07", "YYYY-MM-DD");
        toInput = MakeInput("2010-06-07", "YYYY-MM-DD");
        fromInput.IsVisible = false;
        toInput.IsVisible = false;

        tfCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "100 Ticks por vela (TBBO)",
                "250 Ticks por vela (TBBO)",
                "500 Ticks por vela (TBBO)",
                "1,000 Ticks por vela (TBBO)",
                "15 segundos (TBBO)",
                "1 minuto (TBBO)",
                "5 minutos (TBBO)",
                "15 minutos (TBBO)",
                "1 hora (TBBO)"
            },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF")
        };
        tfCombo.SelectionChanged += (_, _) =>
        {
            if (!suppressTimeframeEvents)
                RebuildDisplayBars();
        };

        reloadButton = new Button
        {
            Content = "Actualizar Gráfico",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8)
        };
        reloadButton.Click += async (_, _) => await LoadMarketDataAsync();

        statusText = new TextBlock
        {
            Text = "Listo para visualizar datos.",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5
        };

        barsInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        rangeInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        boundsInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        orderFlowInfo = new TextBlock
        {
            Text = "Disponible en esquema TBBO (Ticks L1)",
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };

        // Construcción de las 3 vistas para las ventanas flotantes centradas
        modalViewsOut = new Control[3];

        // 0. Modal: Esquema
        var schemaContent = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border
                {
                    Background = SolidColorBrush.Parse("#161618"),
                    BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "NQ · E-mini Nasdaq-100 (GLBX.MDP3)",
                                Foreground = SolidColorBrush.Parse("#FFFFFF"),
                                FontWeight = FontWeight.Bold,
                                FontSize = 13
                            },
                            coverageText
                        }
                    }
                },
                Labeled("Esquema Databento activo", schemaCombo),
                new TextBlock
                {
                    Text = $"• Esquema ohlcv-1m: Velas históricas de 1 minuto (2010 a 2026).\n• Esquema tbbo: Operaciones con bid/ask y cálculo de delta de volumen ({TbboYearRange}).",
                    Foreground = SolidColorBrush.Parse("#71717A"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                }
            }
        };
        modalViewsOut[0] = BuildModalView("ESQUEMA", schemaContent, CloseModal);

        // 1. Modal: Fecha, Periodo y Temporalidad de Velas (Unificado)
        var dateTfContent = new StackPanel
        {
            Spacing = 11,
            Children =
            {
                TwoColumn(
                    Labeled("Año", yearCombo),
                    Labeled("Mes", monthCombo)),
                TwoColumn(
                    Labeled("Día de negociación", dayCombo),
                    Labeled("Agrupar velas en", tfCombo)),
                stepDateGrid,
                dateRangeBadge,
                reloadButton,
                new Border
                {
                    Background = SolidColorBrush.Parse("#161618"),
                    BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(11, 8),
                    Child = statusText
                },
                new TextBlock
                {
                    Text = "• Días y meses en gris (Sin datos): Fines de semana y feriados CME.\n• La agregación de velas (1m, 5m, 15m, 1h, 4h, Diario) se procesa en memoria instantáneamente.",
                    Foreground = SolidColorBrush.Parse("#71717A"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                }
            }
        };
        modalViewsOut[1] = BuildModalView("FECHA Y TEMPORALIDAD", dateTfContent, CloseModal);

        // 2. Modal: Configuración del Sistema
        var configContent = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border
                {
                    Background = SolidColorBrush.Parse("#161618"),
                    BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Ascent Futures Lab · Terminal Institucional",
                                Foreground = SolidColorBrush.Parse("#FFFFFF"),
                                FontWeight = FontWeight.Bold,
                                FontSize = 12.5
                            },
                            new TextBlock
                            {
                                Text = "Tema Minimalista Monocromático B&W con velas Glassmorphic Oscuro",
                                Foreground = SolidColorBrush.Parse("#A1A1AA"),
                                FontSize = 11
                            }
                        }
                    }
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "ATAJOS Y CONTROLES DEL GRÁFICO",
                            Foreground = SolidColorBrush.Parse("#FFFFFF"),
                            FontWeight = FontWeight.SemiBold,
                            FontSize = 11
                        },
                        new TextBlock
                        {
                            Text = "• Rueda del ratón: Zoom fluido automático anclado a la posición del cursor (estilo NinjaTrader).\n" +
                                   "• Botón F / Tecla 'F': Auto-Escala (Fit to Screen) automática vertical y horizontal.\n" +
                                   "• Eje de precios: Arrastrar verticalmente para expandir/comprimir escala manual; doble clic para resetear a F.\n" +
                                   "• Clic y arrastrar: Desplazamiento horizontal fluido (Pan).\n" +
                                   "• Barra de tiempo: Reproducción con ▶ Play / ⏹ Detener (1x, 2x, 5x) y slider interactivo.\n" +
                                   "• Tecla Escape: Cerrar cualquier ventana flotante.",
                            Foreground = SolidColorBrush.Parse("#A1A1AA"),
                            FontSize = 11,
                            LineHeight = 17
                        }
                    }
                },
                new Border
                {
                    Background = SolidColorBrush.Parse("#161618"),
                    BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Child = new TextBlock
                    {
                        Text = $"Workspace: {Path.GetFileName(workspaceRoot)} · .NET 10 (C# 13)",
                        Foreground = SolidColorBrush.Parse("#71717A"),
                        FontSize = 10.5
                    }
                }
            }
        };
        modalViewsOut[2] = BuildModalView("CONFIGURACIÓN", configContent, CloseModal);

        // Área del Gráfico a ancho completo (100% de la pantalla, sin sidebar)
        surface = new ChartSurface();
        var capturedSurface = surface;
        surface.ViewportChanged += OnChartViewportChanged;

        scrubber = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            Background = SolidColorBrush.Parse("#18181B"),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        scrubber.Resources["SliderTrackValueFill"] = SolidColorBrush.Parse("#FFFFFF");
        scrubber.Resources["SliderTrackValueFillPointerOver"] = SolidColorBrush.Parse("#FFFFFF");
        scrubber.Resources["SliderTrackValueFillPressed"] = SolidColorBrush.Parse("#E4E4E7");
        scrubber.Resources["SliderTrackValueFillDisabled"] = SolidColorBrush.Parse("#3F3F46");
        scrubber.Resources["SliderTrackFill"] = SolidColorBrush.Parse("#18181B");
        scrubber.Resources["SliderTrackFillPointerOver"] = SolidColorBrush.Parse("#27272A");
        scrubber.Resources["SliderTrackFillPressed"] = SolidColorBrush.Parse("#27272A");
        scrubber.Resources["SliderTrackFillDisabled"] = SolidColorBrush.Parse("#18181B");
        scrubber.Resources["SliderThumbBackground"] = SolidColorBrush.Parse("#FFFFFF");
        scrubber.Resources["SliderThumbBackgroundPointerOver"] = SolidColorBrush.Parse("#FFFFFF");
        scrubber.Resources["SliderThumbBackgroundPressed"] = SolidColorBrush.Parse("#E4E4E7");
        scrubber.Resources["SliderThumbBackgroundDisabled"] = SolidColorBrush.Parse("#52525B");
        scrubber.Resources["SliderThumbBorderBrush"] = SolidColorBrush.Parse("#000000");
        var capturedScrubber = scrubber;
        scrubber.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !suppressSliderEvents)
            {
                capturedSurface.SetRightEdgeIndex((int)Math.Round(capturedScrubber.Value));
            }
        };

        var chartContainer = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = BuildChartContainerContent(
                capturedSurface,
                capturedScrubber,
                out positionBadge,
                out playPauseBtn,
                out replayModeBtn,
                out speedBtn,
                out stepBackBtn,
                out stepFwdBtn)
        };

        if (playPauseBtn != null) playPauseBtn.Click += (_, _) => TogglePlayback();
        if (replayModeBtn != null) replayModeBtn.Click += (_, _) => ToggleReplayMode();
        if (speedBtn != null) speedBtn.Click += (_, _) => CyclePlaybackSpeed();
        if (stepBackBtn != null) stepBackBtn.Click += (_, _) => StepReplay(-1);
        if (stepFwdBtn != null) stepFwdBtn.Click += (_, _) => StepReplay(1);

        return chartContainer;
    }

    private Control BuildRolloversPage(
        out StackPanel rowsStack,
        out RolloverAccumulationSurface accumChart,
        out Button orderBtn)
    {
        var pageGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,12,190,12,*")
        };

        // 1. Tarjetas superiores de resumen de vencimientos
        var summaryGrid = new UniformGrid { Columns = 4 };
        summaryGrid.Children.Add(MakeSummaryCard(
            "TOTAL DE VENCIMIENTOS",
            "66 Rollovers",
            "Trimestrales (Mar · Jun · Sep · Dic) · 2010 a 2026",
            "#FFFFFF"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "PRECIO INICIAL (JUN 2010)",
            "1,826.25 pts",
            "Primer contrato registrado (NQM0)",
            "#E4E4E7"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "ACUMULADO POR VENCIMIENTOS",
            "+3,769.75 pts",
            "Suma neta de los 66 cambios de contrato ($75,395 / contrato)",
            "#00E599"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "PRECIO FINAL (OCT 2026)",
            "29,110.25 pts",
            "1,826.25 (Inicial) + 23,514.25 (Mercado) + 3,769.75 (Rollovers)",
            "#FFFFFF"));

        Grid.SetRow(summaryGrid, 0);
        pageGrid.Children.Add(summaryGrid);

        // 2. Gráfico visual de la curva de acumulación por vencimiento
        accumChart = new RolloverAccumulationSurface();
        var chartBorder = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "CURVA DE PUNTOS ACUMULADOS POR VENCIMIENTO (2010 → 2026)",
                        Foreground = SolidColorBrush.Parse("#F4F4F5"),
                        FontWeight = FontWeight.Bold,
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    accumChart
                }
            }
        };
        Grid.SetRow(accumChart, 1);
        Grid.SetRow(chartBorder, 2);
        pageGrid.Children.Add(chartBorder);

        // 3. Tabla completa de los 66 vencimientos
        var tableOuterGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };

        var tableTopBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        tableTopBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "HISTORIAL DETALLADO DE ACUMULACIONES TRIMESTRALES",
                    Foreground = SolidColorBrush.Parse("#FFFFFF"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = "Haz clic en 'Ver en Gráfico' para inspeccionar las velas de cualquier vencimiento",
                    Foreground = SolidColorBrush.Parse("#71717A"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });

        orderBtn = new Button
        {
            Content = "Orden: 2010 → 2026 (Cronológico)",
            Background = SolidColorBrush.Parse("#18181B"),
            Foreground = SolidColorBrush.Parse("#F4F4F5"),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(6)
        };
        orderBtn.Click += (_, _) =>
        {
            sortDescending = !sortDescending;
            sortToggleBtn.Content = sortDescending
                ? "Orden: 2026 → 2010 (Más recientes primero)"
                : "Orden: 2010 → 2026 (Cronológico)";
            RenderRolloverTableRows();
        };
        Grid.SetColumn(orderBtn, 1);
        tableTopBar.Children.Add(orderBtn);
        Grid.SetRow(tableTopBar, 0);
        tableOuterGrid.Children.Add(tableTopBar);

        // Encabezado de columnas
        var colHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var colHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,155,100,135,135,145,175,175,*")
        };
        AddTableHeader(colHeaderGrid, 0, "#");
        AddTableHeader(colHeaderGrid, 1, "Fecha Vencimiento UTC");
        AddTableHeader(colHeaderGrid, 2, "Trimestre");
        AddTableHeader(colHeaderGrid, 3, "Cierre Anterior");
        AddTableHeader(colHeaderGrid, 4, "Nueva Apertura");
        AddTableHeader(colHeaderGrid, 5, "Salto del Vencimiento");
        AddTableHeader(colHeaderGrid, 6, "Acumulado (2010 → Hoy)");
        AddTableHeader(colHeaderGrid, 7, "Ajuste Inverso (2026 → 2010)");
        AddTableHeader(colHeaderGrid, 8, "Acción");
        colHeader.Child = colHeaderGrid;
        Grid.SetRow(colHeader, 1);
        tableOuterGrid.Children.Add(colHeader);

        rowsStack = new StackPanel { Spacing = 4 };
        var rowsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rowsStack
        };
        Grid.SetRow(rowsScroll, 2);
        tableOuterGrid.Children.Add(rowsScroll);

        var tableCard = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = tableOuterGrid
        };
        Grid.SetRow(tableCard, 4);
        pageGrid.Children.Add(tableCard);

        return pageGrid;
    }

    private void LoadRolloverHistory()
    {
        if (rolloversFilePath is null || !File.Exists(rolloversFilePath))
            return;

        try
        {
            string json = File.ReadAllText(rolloversFilePath);
            var rawList = JsonSerializer.Deserialize<List<RolloverEntryDto>>(json) ?? new List<RolloverEntryDto>();

            double totalSum = rawList.Sum(r => r.AdjustmentPoints);
            double runningFrom2010 = 0.0;
            double runningBackFrom2026 = totalSum;

            var computed = new List<RolloverComputedRow>(rawList.Count);
            for (int i = 0; i < rawList.Count; i++)
            {
                var item = rawList[i];
                runningFrom2010 = Math.Round(runningFrom2010 + item.AdjustmentPoints, 2);
                DateTime utc = item.CloseTimeUtc.UtcDateTime;
                string quarter = $"{GetSpanishMonthAbbrev(utc.Month)} {utc.Year}";

                computed.Add(new RolloverComputedRow(
                    SequenceNumber: i + 1,
                    CloseTimeUtc: utc,
                    QuarterName: quarter,
                    FromInstrumentId: item.FromInstrumentId,
                    ToInstrumentId: item.ToInstrumentId,
                    PreviousClose: item.PreviousClose,
                    NewOpen: item.NewOpen,
                    AdjustmentPoints: item.AdjustmentPoints,
                    CumulativeFrom2010: runningFrom2010,
                    CumulativeBackFrom2026: Math.Round(runningBackFrom2026, 2)));

                runningBackFrom2026 -= item.AdjustmentPoints;
            }

            rolloverRowsData = computed;
            rolloverChartSurface.SetRows(computed);
            RenderRolloverTableRows();
        }
        catch
        {
            // Ignorar si el JSON no está disponible
        }
    }

    private void RenderRolloverTableRows()
    {
        rolloverTableRows.Children.Clear();
        IEnumerable<RolloverComputedRow> ordered = sortDescending
            ? rolloverRowsData.AsEnumerable().Reverse()
            : rolloverRowsData;

        foreach (var row in ordered)
        {
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("50,155,100,135,135,145,175,175,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            AddTableCell(rowGrid, 0, $"#{row.SequenceNumber}", "#71717A", true);
            AddTableCell(rowGrid, 1, row.CloseTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "#F4F4F5");
            AddTableCell(rowGrid, 2, row.QuarterName, "#FFFFFF", true);
            AddTableCell(rowGrid, 3, $"{row.PreviousClose:N2}", "#A1A1AA");
            AddTableCell(rowGrid, 4, $"{row.NewOpen:N2}", "#FFFFFF", true);

            bool posStep = row.AdjustmentPoints >= 0;
            string stepSign = posStep ? "+" : "";
            var stepBadge = MakeBadge(
                $"{stepSign}{row.AdjustmentPoints:N2} pts",
                posStep ? "#14291F" : "#2A1417",
                posStep ? "#00E599" : "#FF355E");
            stepBadge.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(stepBadge, 5);
            rowGrid.Children.Add(stepBadge);

            bool posCum = row.CumulativeFrom2010 >= 0;
            string cumSign = posCum ? "+" : "";
            AddTableCell(rowGrid, 6, $"{cumSign}{row.CumulativeFrom2010:N2} pts", posCum ? "#00E599" : "#FF355E", true);

            AddTableCell(rowGrid, 7, $"{(row.CumulativeBackFrom2026 >= 0 ? "+" : "")}{row.CumulativeBackFrom2026:N2} pts", "#71717A");

            var viewBtn = new Button
            {
                Content = "↗ Ver en Gráfico",
                Background = SolidColorBrush.Parse("#18181B"),
                Foreground = SolidColorBrush.Parse("#F4F4F5"),
                BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            DateTime capturedDate = row.CloseTimeUtc;
            viewBtn.Click += async (_, _) => await JumpToRolloverDateOnChartAsync(capturedDate);
            Grid.SetColumn(viewBtn, 8);
            rowGrid.Children.Add(viewBtn);

            rolloverTableRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse("#141416"),
                BorderBrush = SolidColorBrush.Parse("#1F1F23"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6),
                Child = rowGrid
            });
        }
    }

    private async Task JumpToRolloverDateOnChartAsync(DateTime rolloverUtc)
    {
        fromBox.Text = rolloverUtc.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        toBox.Text = rolloverUtc.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SwitchPage(0);
        await LoadMarketDataAsync();
    }

    private Control BuildAnalyzerPage(
        out Control dateModalView,
        out Control strategyModalView,
        out Control riskModalView)
    {
        // 1. Inicialización de inputs y selectores
        btStrategyBox = new ComboBox
        {
            ItemsSource = new[]
            {
                "Entradas Aleatorias (1:1 Ratio · 50% Win Rate)",
                "Dubins-Savage (Bold Play · 1 Trade/Día · Bloques $1,000)",
                "El Arte de la Reversión (Estocástico 20,5,5 · 2 Balas DCA)",
                "SmaCrossover (Cruce SMA Rápida/Lenta)"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12
        };

        btInstrumentBox = new ComboBox
        {
            ItemsSource = new[]
            {
                "NQ · E-mini Nasdaq ($20/pt · $5/tick)",
                "MNQ · Micro Nasdaq ($2/pt · $0.50/tick)",
                "ES · E-mini S&P 500 ($50/pt · $12.50/tick)",
                "MES · Micro S&P 500 ($5/pt · $1.25/tick)"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12
        };

        btDataModeBox = new ComboBox
        {
            ItemsSource = new[]
            {
                $"TBBO 1m · Barras Derivadas L1 ({TbboYearRange})",
                $"TBBO Ticks L1 · Bid/Ask ({TbboYearRange})",
                "OHLCV 1m · Serie Original (2010–2026)",
                "OHLCV 1m · Serie Back-Adjusted (2010–2026)"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 12
        };

        btQtyBox = MakeInput("1", "1");
        btFastBox = MakeInput("10", "10");
        btSlowBox = MakeInput("20", "20");

        btFromBox = MakeInput("2010-06-07", "YYYY-MM-DD");
        btToBox = MakeInput(MaxTbboDatasetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "YYYY-MM-DD");
        btSessionStartBox = MakeInput("13:30", "HH:mm");
        btSessionEndBox = MakeInput("20:59", "HH:mm");

        btSlTicksBox = MakeInput("40", "Ticks (ej: 40)");
        btTpTicksBox = MakeInput("40", "Ticks (ej: 40)");
        btBeTriggerBox = MakeInput("", "Ticks (opcional)");
        btBeOffsetBox = MakeInput("2", "Ticks");
        btTrailTicksBox = MakeInput("", "Ticks (opcional)");
        btCommBox = MakeInput("2.05", "USD/lado");
        btSlippageBox = MakeInput("0", "Ticks");
        btMaxDailyLossBox = MakeInput("1500", "USD");
        btMaxDailyTradesBox = MakeInput("4", "Máx/día");

        btStrategyBox.SelectionChanged += (_, _) =>
        {
            if (btStrategyBox.SelectedIndex == 1) // Dubins-Savage Bold Play
            {
                if (btQtyBox is not null) btQtyBox.Text = "2";
                if (btSlTicksBox is not null) btSlTicksBox.Text = "98";
                if (btTpTicksBox is not null) btTpTicksBox.Text = "102";
                if (btMaxDailyTradesBox is not null) btMaxDailyTradesBox.Text = "1";
                if (btMaxDailyLossBox is not null) btMaxDailyLossBox.Text = "1200";
            }
            else if (btStrategyBox.SelectedIndex == 2) // El Arte de la Reversión (Estocástico 20,5,5 · 2 Balas DCA)
            {
                if (btQtyBox is not null) btQtyBox.Text = "1";
                if (btFastBox is not null) btFastBox.Text = "20";
                if (btSlowBox is not null) btSlowBox.Text = "5";
                if (btSlTicksBox is not null) btSlTicksBox.Text = "120";
                if (btTpTicksBox is not null) btTpTicksBox.Text = "80";
                if (btMaxDailyTradesBox is not null) btMaxDailyTradesBox.Text = "6";
                if (btMaxDailyLossBox is not null) btMaxDailyLossBox.Text = "1500";
            }
            else if (btStrategyBox.SelectedIndex == 0) // Entradas Aleatorias estándar
            {
                if (btQtyBox is not null) btQtyBox.Text = "1";
                if (btSlTicksBox is not null) btSlTicksBox.Text = "40";
                if (btTpTicksBox is not null) btTpTicksBox.Text = "40";
                if (btMaxDailyTradesBox is not null) btMaxDailyTradesBox.Text = "4";
                if (btMaxDailyLossBox is not null) btMaxDailyLossBox.Text = "1500";
            }
        };

        Button MakeModalAcceptButton()
        {
            var btn = new Button
            {
                Content = "Aceptar",
                Background = SolidColorBrush.Parse("#FFFFFF"),
                Foreground = SolidColorBrush.Parse("#09090B"),
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Padding = new Thickness(20, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btn.Click += (_, _) => CloseModal();
            return btn;
        }

        // 2. Modales Flotantes Centrados al Viewport
        var dateStack = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                TwoColumn(
                    Labeled("Desde (UTC)", btFromBox),
                    Labeled("Hasta (UTC)", btToBox)),
                TwoColumn(
                    Labeled("Inicio Sesión UTC", btSessionStartBox),
                    Labeled("Cierre Sesión UTC", btSessionEndBox)),
                new Border
                {
                    Background = SolidColorBrush.Parse("#161618"),
                    BorderBrush = SolidColorBrush.Parse("#27272A"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Child = new TextBlock
                    {
                        Text = "Formato de fecha: AAAA-MM-DD (ej: 2026-10-01 a 2026-10-30).\nSesión RTH CME: 13:30 a 20:59 UTC.",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                MakeModalAcceptButton()
            }
        };
        dateModalView = BuildModalView("FECHA Y SESIÓN OPERATIVA", dateStack, CloseModal);

        var strategyStack = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Labeled("Estrategia activa (Core)", btStrategyBox),
                Labeled("Instrumento y Valor por Punto", btInstrumentBox),
                Labeled("Fuente de datos histórica", btDataModeBox),
                TwoColumn(
                    Labeled("Contratos", btQtyBox),
                    TwoColumn(Labeled("SMA Ráp.", btFastBox), Labeled("SMA Lenta", btSlowBox))),
                MakeModalAcceptButton()
            }
        };
        strategyModalView = BuildModalView("ESTRATEGIA E INSTRUMENTO", strategyStack, CloseModal);

        var riskStack = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                TwoColumn(
                    Labeled("Stop Loss (Ticks)", btSlTicksBox),
                    Labeled("Take Profit (Ticks)", btTpTicksBox)),
                TwoColumn(
                    Labeled("Break-Even Trigger", btBeTriggerBox),
                    TwoColumn(Labeled("BE Offset", btBeOffsetBox), Labeled("Trail Stop", btTrailTicksBox))),
                TwoColumn(
                    Labeled("Comisión / lado ($)", btCommBox),
                    Labeled("Slippage (Ticks)", btSlippageBox)),
                TwoColumn(
                    Labeled("Pérdida Diaria (DLL $)", btMaxDailyLossBox),
                    Labeled("Máx. Trades / Día", btMaxDailyTradesBox)),
                MakeModalAcceptButton()
            }
        };
        riskModalView = BuildModalView("GESTIÓN DE RIESGO Y COSTOS REALES", riskStack, CloseModal);

        // 3. Contenedor principal con Barra Lateral Izquierda (Sidebar) para las Vistas
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,12,*")
        };

        // --- BARRA LATERAL IZQUIERDA (SIDEBAR DE NAVEGACIÓN) ---
        var sidebarBorder = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 14)
        };

        var sidebarStack = new StackPanel { Spacing = 6 };

        var sidebarHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(4, 0, 0, 8)
        };
        sidebarHeader.Children.Add(new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(2),
            Background = SolidColorBrush.Parse("#00E599"),
            VerticalAlignment = VerticalAlignment.Center
        });
        sidebarHeader.Children.Add(new TextBlock
        {
            Text = "VISTAS",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        sidebarStack.Children.Add(sidebarHeader);

        btTabSummaryBtn = MakeSidebarNavButton("Resumen", true);
        btTabGraphBtn = MakeSidebarNavButton("Gráfico", false);
        btTabTradesBtn = MakeSidebarNavButton("Operaciones", false);
        btTabPeriodsBtn = MakeSidebarNavButton("Periodos", false);
        btTabMetricsBtn = MakeSidebarNavButton("Métricas", false);

        btTabSummaryBtn.Click += (_, _) => SwitchAnalyzerView(0);
        btTabGraphBtn.Click += (_, _) => SwitchAnalyzerView(1);
        btTabTradesBtn.Click += (_, _) => SwitchAnalyzerView(2);
        btTabPeriodsBtn.Click += (_, _) => SwitchAnalyzerView(3);
        btTabMetricsBtn.Click += (_, _) => SwitchAnalyzerView(4);

        sidebarStack.Children.Add(btTabSummaryBtn);
        sidebarStack.Children.Add(btTabGraphBtn);
        sidebarStack.Children.Add(btTabTradesBtn);
        sidebarStack.Children.Add(btTabPeriodsBtn);
        sidebarStack.Children.Add(btTabMetricsBtn);

        sidebarBorder.Child = sidebarStack;
        Grid.SetColumn(sidebarBorder, 0);
        bodyGrid.Children.Add(sidebarBorder);

        // --- ÁREA DERECHA PRINCIPAL ---
        var rightGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,8,Auto,8,*")
        };
        Grid.SetColumn(rightGrid, 2);
        bodyGrid.Children.Add(rightGrid);

        btStatusText = new TextBlock
        {
            Text = "Configura Fecha, Estrategia o Riesgo en el menú inferior y pulsa 'Ejecutar'.",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        btRunBtn = new Button
        {
            Content = "Ejecutar Analizador",
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(16, 7),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        btRunBtn.Click += async (_, _) => await RunInteractiveBacktestAsync();

        var topBarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = SolidColorBrush.Parse("#00E599"),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "ANALIZADOR DE ESTRATEGIAS",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        topBarGrid.Children.Add(titleStack);

        var statusBorder = new Border
        {
            Margin = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = btStatusText
        };
        Grid.SetColumn(statusBorder, 1);
        topBarGrid.Children.Add(statusBorder);

        Grid.SetColumn(btRunBtn, 2);
        topBarGrid.Children.Add(btRunBtn);

        btProgressBar = new ProgressBar
        {
            Height = 3.5,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            IsIndeterminate = false,
            IsVisible = false,
            Background = SolidColorBrush.Parse("#1A1A1E"),
            Foreground = SolidColorBrush.Parse("#00E599"),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var topActionBarStack = new StackPanel
        {
            Children =
            {
                topBarGrid,
                btProgressBar
            }
        };

        var topActionBar = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 7),
            Child = topActionBarStack
        };
        Grid.SetRow(topActionBar, 0);
        rightGrid.Children.Add(topActionBar);

        var kpiGrid = new UniformGrid { Columns = 6 };
        kpiGrid.Children.Add(MakeDynamicKpiCard("P&L NETO TOTAL", "—", "Bruto: — | Com: —", "#00E599", out kpiNetPnlVal, out kpiNetPnlSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("WIN RATE & TRADES", "—", "Gan: — | Perd: —", "#FFFFFF", out kpiWinRateVal, out kpiWinRateSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("PROFIT FACTOR & PAYOFF", "—", "Esperanza: — / trade", "#FFFFFF", out kpiPfVal, out kpiPfSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("DRAWDOWN REAL", "—", "DD Cerrado: —", "#FF355E", out kpiDdVal, out kpiDdSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("SHARPE & SORTINO", "—", "Recovery Factor: —", "#FFFFFF", out kpiSharpeVal, out kpiSharpeSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("CONSISTENCIA DIARIA", "—", "Rachas: — | Duración: —", "#E4E4E7", out kpiDaysVal, out kpiDaysSub));
        Grid.SetRow(kpiGrid, 2);
        rightGrid.Children.Add(kpiGrid);

        // --- VISTAS DEL REPORTE (Resumen · Gráfico · Operaciones · Periodos) ---
        // Vista 1: Resumen
        var sumGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var sumHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var sumHeaderCols = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("340,160,160,160")
        };
        AddTableHeader(sumHeaderCols, 0, "Métrica / Indicador");
        AddTableHeader(sumHeaderCols, 1, "Todos");
        AddTableHeader(sumHeaderCols, 2, "Largos");
        AddTableHeader(sumHeaderCols, 3, "Cortos");
        sumHeader.Child = sumHeaderCols;
        Grid.SetRow(sumHeader, 0);
        sumGrid.Children.Add(sumHeader);

        btSummaryRows = new StackPanel { Spacing = 2 };
        var sumScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = btSummaryRows
        };
        Grid.SetRow(sumScroll, 1);
        sumGrid.Children.Add(sumScroll);
        btSummaryContainer = sumGrid;

        // Vista 2: Gráfico
        btEquitySurface = new EquityCurveSurface();
        var graphGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            IsVisible = false
        };

        btGraphFinalNetText = new TextBlock
        {
            Text = "Capital: —",
            Foreground = SolidColorBrush.Parse("#00E599"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        btGraphPeakText = new TextBlock
        {
            Text = "Pico: —",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        btGraphMaxDdText = new TextBlock
        {
            Text = "Máx DD: —",
            Foreground = SolidColorBrush.Parse("#FF355E"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        btGraphWinRateText = new TextBlock
        {
            Text = "WR: —",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };

        var leftTitleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 13,
                    CornerRadius = new CornerRadius(2),
                    Background = SolidColorBrush.Parse("#00E599"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "GRÁFICO · CURVA DE CAPITAL & DRAWDOWN (ESTILO NINJATRADER)",
                    Foreground = SolidColorBrush.Parse("#F4F4F5"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(leftTitleStack, 0);

        var rightBadgesStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                btGraphFinalNetText,
                btGraphPeakText,
                btGraphMaxDdText,
                btGraphWinRateText
            }
        };
        Grid.SetColumn(rightBadgesStack, 2);

        var graphHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        graphHeaderGrid.Children.Add(leftTitleStack);
        graphHeaderGrid.Children.Add(rightBadgesStack);

        var graphHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = graphHeaderGrid
        };
        Grid.SetRow(graphHeader, 0);
        graphGrid.Children.Add(graphHeader);

        var graphBorder = new Border
        {
            Background = SolidColorBrush.Parse("#0D0D0E"),
            BorderBrush = SolidColorBrush.Parse("#1F1F23"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = btEquitySurface
        };
        Grid.SetRow(graphBorder, 1);
        graphGrid.Children.Add(graphBorder);
        btGraphContainer = graphGrid;

        // Vista 3: Operaciones
        var trGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            IsVisible = false
        };
        var trHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7),
            Margin = new Thickness(0, 0, 0, 5)
        };
        var trHeaderCols = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("45,75,130,90,130,90,95,105,120,75,110,*")
        };
        AddTableHeader(trHeaderCols, 0, "#");
        AddTableHeader(trHeaderCols, 1, "Lado/Cant");
        AddTableHeader(trHeaderCols, 2, "Entrada UTC");
        AddTableHeader(trHeaderCols, 3, "Precio Ent.");
        AddTableHeader(trHeaderCols, 4, "Salida UTC");
        AddTableHeader(trHeaderCols, 5, "Precio Sal.");
        AddTableHeader(trHeaderCols, 6, "Pts / Ticks");
        AddTableHeader(trHeaderCols, 7, "P&L Neto");
        AddTableHeader(trHeaderCols, 8, "MAE / MFE ($)");
        AddTableHeader(trHeaderCols, 9, "Múlt. R");
        AddTableHeader(trHeaderCols, 10, "Equidad Acum.");
        AddTableHeader(trHeaderCols, 11, "Motivo Salida / Acción");
        trHeader.Child = trHeaderCols;
        Grid.SetRow(trHeader, 0);
        trGrid.Children.Add(trHeader);

        btTradesRows = new StackPanel { Spacing = 4 };
        var trScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = btTradesRows
        };
        Grid.SetRow(trScroll, 1);
        trGrid.Children.Add(trScroll);

        btTradesFirstBtn = MakePageButton("⏮ Primera");
        btTradesPrevBtn = MakePageButton("◀ Anterior");
        btTradesNextBtn = MakePageButton("Siguiente ▶");
        btTradesLastBtn = MakePageButton("⏭ Última");

        btTradesFirstBtn.Click += (_, _) => PopulateTradesPage(0);
        btTradesPrevBtn.Click += (_, _) => PopulateTradesPage(tradesCurrentPage - 1);
        btTradesNextBtn.Click += (_, _) => PopulateTradesPage(tradesCurrentPage + 1);
        btTradesLastBtn.Click += (_, _) =>
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(currentAllTrades.Count / (double)TradesPageSize));
            PopulateTradesPage(totalPages - 1);
        };

        btTradesPageInfoText = new TextBlock
        {
            Text = "Mostrando operaciones: —",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };

        var paginationBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        paginationBar.Children.Add(btTradesPageInfoText);
        Grid.SetColumn(btTradesPageInfoText, 0);

        var paginationBtnStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                btTradesFirstBtn,
                btTradesPrevBtn,
                btTradesNextBtn,
                btTradesLastBtn
            }
        };
        Grid.SetColumn(paginationBtnStack, 2);
        paginationBar.Children.Add(paginationBtnStack);

        var paginationBorder = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = paginationBar
        };
        Grid.SetRow(paginationBorder, 2);
        trGrid.Children.Add(paginationBorder);

        btTradesContainer = trGrid;

        // Vista 4: Periodos (Diario)
        var perGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            IsVisible = false
        };
        var perHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7),
            Margin = new Thickness(0, 0, 0, 5)
        };
        var perHeaderCols = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,85,110,95,115,110,120,130,*")
        };
        AddTableHeader(perHeaderCols, 0, "Fecha (UTC)");
        AddTableHeader(perHeaderCols, 1, "Trades");
        AddTableHeader(perHeaderCols, 2, "Gan / Perd");
        AddTableHeader(perHeaderCols, 3, "% Win Día");
        AddTableHeader(perHeaderCols, 4, "P&L Bruto");
        AddTableHeader(perHeaderCols, 5, "Comisiones");
        AddTableHeader(perHeaderCols, 6, "P&L Neto Día");
        AddTableHeader(perHeaderCols, 7, "Max DD Intradía");
        AddTableHeader(perHeaderCols, 8, "Equidad Acumulada");
        perHeader.Child = perHeaderCols;
        Grid.SetRow(perHeader, 0);
        perGrid.Children.Add(perHeader);

        btPeriodsRows = new StackPanel { Spacing = 4 };
        var perScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = btPeriodsRows
        };
        Grid.SetRow(perScroll, 1);
        perGrid.Children.Add(perScroll);
        btPeriodsContainer = perGrid;

        // Vista 5: Métricas Detalladas (Frecuencia, Promedios Diarios y Distribución)
        btDailyTradesSurface = new DailyTradesChartSurface { Height = 280, MinHeight = 240 };

        var metHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var metTitleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 13,
                    CornerRadius = new CornerRadius(2),
                    Background = SolidColorBrush.Parse("#00E599"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "MÉTRICAS DETALLADAS · FRECUENCIA & PROMEDIOS DE OPERACIONES AL DÍA",
                    Foreground = SolidColorBrush.Parse("#F4F4F5"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(metTitleStack, 0);
        metHeaderGrid.Children.Add(metTitleStack);

        var btOpenMetricsWindowBtn = new Button
        {
            Content = "🗗 Abrir en Ventana Independiente",
            Background = SolidColorBrush.Parse("#18181B"),
            Foreground = SolidColorBrush.Parse("#F4F4F5"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(5),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        btOpenMetricsWindowBtn.Click += (_, _) =>
        {
            if (lastBacktestResult is not null)
            {
                var win = new MetricsWindow(lastBacktestResult);
                win.Show();
            }
        };
        Grid.SetColumn(btOpenMetricsWindowBtn, 2);
        metHeaderGrid.Children.Add(btOpenMetricsWindowBtn);

        var metHeaderBorder = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7),
            Child = metHeaderGrid
        };

        // KPI Cards Row (5 Cards)
        var metKpiGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 8) };
        metKpiGrid.Children.Add(MakeDynamicKpiCard("PROMEDIO TRADES / DÍA", "—", "Total trades en periodo", "#FFB224", out metAvgTradesDayVal, out metAvgTradesDaySub));
        metKpiGrid.Children.Add(MakeDynamicKpiCard("MÁX / MÍN EN UN DÍA", "—", "Rango de frecuencia diaria", "#FFFFFF", out metExtremesTradesVal, out metExtremesTradesSub));
        metKpiGrid.Children.Add(MakeDynamicKpiCard("P&L MEDIO DIARIO", "—", "Ganancia / pérdida media día", "#00E599", out metAvgPnlDayVal, out metAvgPnlDaySub));
        metKpiGrid.Children.Add(MakeDynamicKpiCard("CONSISTENCIA DIARIA", "—", "Días positivos vs negativos", "#38BDF8", out metWinningDaysVal, out metWinningDaysSub));
        metKpiGrid.Children.Add(MakeDynamicKpiCard("DURACIÓN MEDIA", "—", "Tiempo de permanencia en trade", "#E4E4E7", out metAvgDurationVal, out metAvgDurationSub));

        // Center split: Chart on left (60%), DOW breakdown & Frequency buckets on right (40%)
        var metCenterGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("60*,8,40*"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var chartBorder = new Border
        {
            Background = SolidColorBrush.Parse("#0D0D0E"),
            BorderBrush = SolidColorBrush.Parse("#1F1F23"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = btDailyTradesSurface
        };
        Grid.SetColumn(chartBorder, 0);
        metCenterGrid.Children.Add(chartBorder);

        // Right side: Stack with DOW and Frequency Buckets
        var rightStatsStack = new StackPanel { Spacing = 8 };

        // 1. DOW Breakdown Card
        var dowHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("70,55,60,60,*")
        };
        AddTableHeader(dowHeaderGrid, 0, "Día");
        AddTableHeader(dowHeaderGrid, 1, "Trades");
        AddTableHeader(dowHeaderGrid, 2, "Media/D");
        AddTableHeader(dowHeaderGrid, 3, "% Win");
        AddTableHeader(dowHeaderGrid, 4, "P&L Neto Total");

        metDowStack = new StackPanel { Spacing = 3 };

        var dowCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DESGLOSE POR DÍA DE LA SEMANA",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 0, 0, 2)
                    },
                    new Border
                    {
                        Background = SolidColorBrush.Parse("#18181B"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = SolidColorBrush.Parse("#27272A"),
                        Padding = new Thickness(4, 4),
                        Child = dowHeaderGrid
                    },
                    metDowStack
                }
            }
        };
        rightStatsStack.Children.Add(dowCard);

        // 2. Frequency Buckets Card
        metFreqBucketsStack = new StackPanel { Spacing = 4 };
        var freqCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DISTRIBUCIÓN DE FRECUENCIA DE TRADES / DÍA",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 0, 0, 2)
                    },
                    metFreqBucketsStack
                }
            }
        };
        rightStatsStack.Children.Add(freqCard);

        Grid.SetColumn(rightStatsStack, 2);
        metCenterGrid.Children.Add(rightStatsStack);

        // Bottom row: Advanced stats cards (4 cards)
        var metBottomGrid = new UniformGrid { Columns = 4 };
        metBottomGrid.Children.Add(MakeDynamicKpiCard("MEJOR DÍA", "—", "Mayor ganancia en 1 sesión", "#00E599", out metBestDayVal, out metBestDaySub));
        metBottomGrid.Children.Add(MakeDynamicKpiCard("PEOR DÍA", "—", "Mayor pérdida en 1 sesión", "#FF355E", out metWorstDayVal, out metWorstDaySub));
        metBottomGrid.Children.Add(MakeDynamicKpiCard("DD INTRADÍA MEDIO", "—", "Riesgo medio por día", "#FF355E", out metAvgIntradayDdVal, out metAvgIntradayDdSub));
        metBottomGrid.Children.Add(MakeDynamicKpiCard("COMISIONES / BRUTO", "—", "Impacto de costes de ejecución", "#E4E4E7", out metCommImpactVal, out metCommImpactSub));

        var metContentStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                metHeaderBorder,
                metKpiGrid,
                metCenterGrid,
                metBottomGrid
            }
        };

        var metScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = metContentStack,
            IsVisible = false
        };

        btMetricsContainer = metScrollViewer;

        var viewsHolder = new Grid();
        viewsHolder.Children.Add(btSummaryContainer);
        viewsHolder.Children.Add(btGraphContainer);
        viewsHolder.Children.Add(btTradesContainer);
        viewsHolder.Children.Add(btPeriodsContainer);
        viewsHolder.Children.Add(btMetricsContainer);

        var mainReportCard = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Child = viewsHolder
        };
        Grid.SetRow(mainReportCard, 4);
        rightGrid.Children.Add(mainReportCard);

        return bodyGrid;
    }

    private void SwitchAnalyzerView(int index)
    {
        if (btSummaryContainer is null || btGraphContainer is null || btTradesContainer is null || btPeriodsContainer is null || btMetricsContainer is null)
            return;

        btSummaryContainer.IsVisible = index == 0;
        btGraphContainer.IsVisible = index == 1;
        btTradesContainer.IsVisible = index == 2;
        btPeriodsContainer.IsVisible = index == 3;
        btMetricsContainer.IsVisible = index == 4;

        UpdateTabBtn(btTabSummaryBtn, index == 0);
        UpdateTabBtn(btTabGraphBtn, index == 1);
        UpdateTabBtn(btTabTradesBtn, index == 2);
        UpdateTabBtn(btTabPeriodsBtn, index == 3);
        UpdateTabBtn(btTabMetricsBtn, index == 4);
    }

    private Control BuildPropFirmPage()
    {
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,12,*")
        };

        // --- SIDEBAR IZQUIERDA (VISTAS PROP FIRM) ---
        var sidebarBorder = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };

        var sidebarStack = new StackPanel { Spacing = 6 };

        var sidebarHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(4, 0, 0, 8)
        };
        sidebarHeader.Children.Add(new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(2),
            Background = SolidColorBrush.Parse("#00E599"),
            VerticalAlignment = VerticalAlignment.Center
        });
        sidebarHeader.Children.Add(new TextBlock
        {
            Text = "VISTAS",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        sidebarStack.Children.Add(sidebarHeader);

        pfTabSimBtn = MakeSidebarNavButton("Simulador", true);
        pfTabCostBtn = MakeSidebarNavButton("Costos y Lotes", false);
        pfTabRulesBtn = MakeSidebarNavButton("Reglas de Firmas", false);

        pfTabSimBtn.Click += (_, _) => SwitchPropFirmView(0);
        pfTabCostBtn.Click += (_, _) => SwitchPropFirmView(1);
        pfTabRulesBtn.Click += (_, _) => SwitchPropFirmView(2);

        sidebarStack.Children.Add(pfTabSimBtn);
        sidebarStack.Children.Add(pfTabCostBtn);
        sidebarStack.Children.Add(pfTabRulesBtn);

        sidebarBorder.Child = sidebarStack;
        Grid.SetColumn(sidebarBorder, 0);
        bodyGrid.Children.Add(sidebarBorder);

        // --- ÁREA DERECHA PRINCIPAL ---
        var rightGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,8,Auto,8,*")
        };
        Grid.SetColumn(rightGrid, 2);
        bodyGrid.Children.Add(rightGrid);

        var pfStatusText = new TextBlock
        {
            Text = "Simulador Multi-Cuenta · Pruebas de Viabilidad, Costos y Retiro en Cuentas de Fondeo",
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(pfStatusText, 0);
        rightGrid.Children.Add(pfStatusText);

        // Controles de cabecera
        pfPlanCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "Tradeify Select Flex 50K (Sin Buffer · Retiro 50%)",
                "Tradeify Select Daily 50K (Buffer $2,100 · Retiro Diario)",
                "Tradeify Growth 50K (Pase 1 Día · 5 Días Ganadores)",
                "Tradeify Lightning 50K (Fondeo Directo · 20% Consistencia)",
                "Topstep 50K Express (5 Días Ganadores · Retiro 50%)",
                "Apex 50K Full (Trailing Intradía · Buffer $2,600)"
            },
            SelectedIndex = 0,
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 235
        };

        pfStrategyModeCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "Dubins-Savage · Punto Dulce ($500 F2 + 2.º Retiro)",
                "Dubins-Savage · Retiro Mínimo ($200 F2 + 2.º Retiro)",
                "Dubins-Savage · Tope Máximo ($2,500 · Meta +$5K)",
                "Aleatoria Común ($200 fijo · 1–2 ops/día)",
                "Estrategia Actual (Trades del Backtest)"
            },
            SelectedIndex = 0,
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#00E599"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 245
        };

        pfAccountsCountCombo = new ComboBox
        {
            ItemsSource = new[] { "5 Cuentas", "10 Cuentas", "20 Cuentas", "50 Cuentas", "100 Cuentas" },
            SelectedIndex = 1,
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 98
        };

        pfAccountPriceBox = new TextBox
        {
            Text = "99",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            Width = 45,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        pfActivationPriceBox = new TextBox
        {
            Text = "0",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#3F3F46"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            Width = 45,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        pfPlanCombo.SelectionChanged += (_, _) =>
        {
            int pIdx = pfPlanCombo.SelectedIndex;
            if (pfAccountPriceBox is not null)
            {
                pfAccountPriceBox.Text = pIdx switch
                {
                    0 or 1 => "99",
                    2 => "87",
                    3 => "295",
                    4 => "49",
                    5 => "35",
                    _ => "99"
                };
            }
            if (pfActivationPriceBox is not null)
            {
                pfActivationPriceBox.Text = pIdx == 5 ? "140" : (pIdx == 4 ? "149" : "0");
            }
        };

        var pfSimulateBtn = new Button
        {
            Content = "▶ Simular Cartera",
            Background = SolidColorBrush.Parse("#00E599"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.Bold,
            FontSize = 11.5,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(5),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        pfSimulateBtn.Click += (_, _) => RunPropFirmSimulation();

        var pfControlsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,8,Auto,4,Auto,8,Auto,4,Auto,8,Auto,4,Auto,8,Auto,4,Auto,8,Auto,4,Auto,*,Auto")
        };

        var pfTitleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 13,
                    CornerRadius = new CornerRadius(2),
                    Background = SolidColorBrush.Parse("#00E599"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "PROP FIRM · SIMULADOR",
                    Foreground = SolidColorBrush.Parse("#F4F4F5"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(pfTitleStack, 0);
        pfControlsGrid.Children.Add(pfTitleStack);

        var pfPlanLabel = new TextBlock { Text = "Plan:", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pfPlanLabel, 2);
        pfControlsGrid.Children.Add(pfPlanLabel);
        Grid.SetColumn(pfPlanCombo, 4);
        pfControlsGrid.Children.Add(pfPlanCombo);

        var pfModeLabel = new TextBlock { Text = "Teoría / Origen:", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pfModeLabel, 6);
        pfControlsGrid.Children.Add(pfModeLabel);
        Grid.SetColumn(pfStrategyModeCombo, 8);
        pfControlsGrid.Children.Add(pfStrategyModeCombo);

        var pfCountLabel = new TextBlock { Text = "Lote:", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pfCountLabel, 10);
        pfControlsGrid.Children.Add(pfCountLabel);
        Grid.SetColumn(pfAccountsCountCombo, 12);
        pfControlsGrid.Children.Add(pfAccountsCountCombo);

        var pfCostLabel = new TextBlock { Text = "$ Eval:", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pfCostLabel, 14);
        pfControlsGrid.Children.Add(pfCostLabel);
        Grid.SetColumn(pfAccountPriceBox, 16);
        pfControlsGrid.Children.Add(pfAccountPriceBox);

        var pfActLabel = new TextBlock { Text = "$ Act:", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pfActLabel, 18);
        pfControlsGrid.Children.Add(pfActLabel);
        Grid.SetColumn(pfActivationPriceBox, 20);
        pfControlsGrid.Children.Add(pfActivationPriceBox);

        Grid.SetColumn(pfSimulateBtn, 22);
        pfControlsGrid.Children.Add(pfSimulateBtn);

        var pfHeaderBorder = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = pfControlsGrid
        };
        Grid.SetRow(pfHeaderBorder, 2);
        rightGrid.Children.Add(pfHeaderBorder);

        // === VISTA 0: SIMULADOR ===
        var pfKpiGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 8) };
        pfKpiGrid.Children.Add(MakeDynamicKpiCard("TASA EVALUACIÓN", "—", "Cuentas que pasaron Fase 1", "#FFFFFF", out pfKpiEvalPassVal, out pfKpiEvalPassSub));
        pfKpiGrid.Children.Add(MakeDynamicKpiCard("TASA DE RETIRO", "—", "Cuentas con cobro bancario", "#38BDF8", out pfKpiPayoutPassVal, out pfKpiPayoutPassSub));
        pfKpiGrid.Children.Add(MakeDynamicKpiCard("TOTAL RETIRADO", "—", "Cobros brutos recibidos", "#00E599", out pfKpiTotalWithdrawnVal, out pfKpiTotalWithdrawnSub));
        pfKpiGrid.Children.Add(MakeDynamicKpiCard("INVERSIÓN TOTAL", "—", "Gasto en cuentas y fees", "#FF355E", out pfKpiTotalCostVal, out pfKpiTotalCostSub));
        pfKpiGrid.Children.Add(MakeDynamicKpiCard("P&L NETO DEL TRADER", "—", "Beneficio neto real y ROI", "#00E599", out pfKpiNetRoiVal, out pfKpiNetRoiSub));

        var pfCenterGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("62*,8,38*"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        pfAccountsTableRows = new StackPanel { Spacing = 3 };
        var pfTableScroll = new ScrollViewer
        {
            Height = 440,
            MinHeight = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = pfAccountsTableRows
        };

        var pfTableHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("35,110,110,60,50,85,90,*")
        };
        AddTableHeader(pfTableHeaderGrid, 0, "#");
        AddTableHeader(pfTableHeaderGrid, 1, "Estado");
        AddTableHeader(pfTableHeaderGrid, 2, "Fase");
        AddTableHeader(pfTableHeaderGrid, 3, "Días");
        AddTableHeader(pfTableHeaderGrid, 4, "Ops");
        AddTableHeader(pfTableHeaderGrid, 5, "P&L Cta");
        AddTableHeader(pfTableHeaderGrid, 6, "Retiro ($)");
        AddTableHeader(pfTableHeaderGrid, 7, "Causa / Detalle");

        var pfTableCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new Border
                    {
                        Background = SolidColorBrush.Parse("#18181B"),
                        BorderBrush = SolidColorBrush.Parse("#27272A"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(4, 4),
                        Child = pfTableHeaderGrid
                    },
                    pfTableScroll
                }
            }
        };
        Grid.SetColumn(pfTableCard, 0);
        pfCenterGrid.Children.Add(pfTableCard);

        pfPassBarFill = new Border { Background = SolidColorBrush.Parse("#38BDF8"), Height = 10, CornerRadius = new CornerRadius(2, 0, 0, 2) };
        pfPayoutBarFill = new Border { Background = SolidColorBrush.Parse("#00E599"), Height = 10 };
        pfBlownBarFill = new Border { Background = SolidColorBrush.Parse("#FF355E"), Height = 10, CornerRadius = new CornerRadius(0, 2, 2, 0) };

        pfDistTrack = new Grid
        {
            Height = 10,
            ColumnDefinitions = new ColumnDefinitions("1*,1*,1*"),
            Margin = new Thickness(0, 4, 0, 6)
        };
        Grid.SetColumn(pfPayoutBarFill, 0);
        Grid.SetColumn(pfPassBarFill, 1);
        Grid.SetColumn(pfBlownBarFill, 2);
        pfDistTrack.Children.Add(pfPayoutBarFill);
        pfDistTrack.Children.Add(pfPassBarFill);
        pfDistTrack.Children.Add(pfBlownBarFill);

        pfDistributionLegend = new TextBlock
        {
            Text = "Verde: Retiró | Azul: Pasó Eval pero quebró en Fondeada | Rojo: Quebró Eval",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 9.5,
            Margin = new Thickness(0, 0, 0, 8)
        };

        pfEvText = new TextBlock
        {
            Text = "Esperanza Matemática (EV) por cuenta: —",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };

        pfVerdictText = new TextBlock
        {
            Text = "Pulsa 'Simular Cartera' para ejecutar una simulación Monte Carlo de las cuentas con la estrategia seleccionada.",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap
        };

        var pfStatsCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DISTRIBUCIÓN & ANÁLISIS DE VIABILIDAD",
                        Foreground = SolidColorBrush.Parse("#FFFFFF"),
                        FontSize = 11,
                        FontWeight = FontWeight.Bold
                    },
                    pfDistTrack,
                    pfDistributionLegend,
                    new Border { Height = 1, Background = SolidColorBrush.Parse("#222226"), Margin = new Thickness(0, 2, 0, 6) },
                    pfEvText,
                    pfVerdictText
                }
            }
        };
        Grid.SetColumn(pfStatsCard, 2);
        pfCenterGrid.Children.Add(pfStatsCard);

        var simContentStack = new StackPanel
        {
            Spacing = 6,
            Children = { pfKpiGrid, pfCenterGrid }
        };
        pfSimContainer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = simContentStack,
            IsVisible = true
        };

        // === VISTA 1: COSTOS Y LOTES ===
        pfCostTitleText = new TextBlock
        {
            Text = "COSTO DE LA CARTERA",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        };

        var pfCostHeaderGrid = new Grid { ColumnDefinitions = new ColumnDefinitions(PropFirmCostColumns) };
        AddTableHeader(pfCostHeaderGrid, 0, "Plan 50K");
        AddTableHeader(pfCostHeaderGrid, 1, "Precio base");
        AddTableHeader(pfCostHeaderGrid, 2, "Con cupón");
        AddTableHeader(pfCostHeaderGrid, 3, "Total base");
        AddTableHeader(pfCostHeaderGrid, 4, "Total con cupón");
        AddTableHeader(pfCostHeaderGrid, 5, "Activación");
        AddTableHeader(pfCostHeaderGrid, 6, "Recuperas con");

        pfCostTableRows = new StackPanel { Spacing = 3 };

        var pfCostCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    pfCostTitleText,
                    new Border
                    {
                        Background = SolidColorBrush.Parse("#18181B"),
                        BorderBrush = SolidColorBrush.Parse("#27272A"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(4, 4),
                        Child = pfCostHeaderGrid
                    },
                    pfCostTableRows,
                    new TextBlock
                    {
                        Text = "Precios aproximados (Tradeify según tradeify.co y sitios de reseñas; Topstep y Apex referenciales). El cupón varía según temporada. \"Recuperas con\" = retiros netos de ~$1,000 necesarios para cubrir el costo total del lote.",
                        Foreground = SolidColorBrush.Parse("#71717A"),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        pfAccountsCountCombo.SelectionChanged += (_, _) => RefreshPropFirmCostTable();
        pfPlanCombo.SelectionChanged += (_, _) => RefreshPropFirmCostTable();
        RefreshPropFirmCostTable();

        pfCostContainer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = pfCostCard,
            IsVisible = false
        };

        // === VISTA 2: REGLAS DE FIRMAS ===
        var rulesTable = BuildPropFirmRulesTable();
        pfRulesContainer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rulesTable,
            IsVisible = false
        };

        // Views holder
        var viewsHolder = new Grid();
        viewsHolder.Children.Add(pfSimContainer);
        viewsHolder.Children.Add(pfCostContainer);
        viewsHolder.Children.Add(pfRulesContainer);

        var mainCard = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Child = viewsHolder
        };
        Grid.SetRow(mainCard, 4);
        rightGrid.Children.Add(mainCard);

        return bodyGrid;
    }

    private static Control BuildPropFirmRulesTable()
    {
        const string rulesCols = "190,95,115,85,95,105,*";
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions(rulesCols) };
        AddTableHeader(headerGrid, 0, "Firma / Plan 50K");
        AddTableHeader(headerGrid, 1, "Drawdown");
        AddTableHeader(headerGrid, 2, "Tipo Drawdown");
        AddTableHeader(headerGrid, 3, "Días Mín.");
        AddTableHeader(headerGrid, 4, "Meta Eval");
        AddTableHeader(headerGrid, 5, "Buffer Cobro");
        AddTableHeader(headerGrid, 6, "Reglas de Retiro y Consistencia");

        var rowsStack = new StackPanel { Spacing = 3 };

        var rulesData = new[]
        {
            ("Tradeify Select Flex 50K", "$2,000", "EOD (Fin de Día)", "3 días", "$3,000", "Sin Buffer ($0)", "Retiro 50% al 5.º día ganador ($150 mín). Tope $2,500."),
            ("Tradeify Select Daily 50K", "$2,000", "EOD (Fin de Día)", "3 días", "$3,000", "$2,100 Buffer", "Retiro diario por encima del buffer ($2,100). Sin espera semanal."),
            ("Tradeify Growth 50K", "$2,000", "EOD (Fin de Día)", "1 día", "$3,000", "Sin Buffer ($0)", "Pase en 1 día. Consistencia 35% y límite diario de pérdida $1,250."),
            ("Tradeify Lightning 50K", "$2,000", "EOD (Fin de Día)", "Inmediato", "Sin Eval", "$2,000 Buffer", "Fondeo Directo sin evaluación. Regla de consistencia estricta 20%."),
            ("Topstep 50K Express", "$2,000", "EOD (Fin de Día)", "2 días", "$3,000", "$2,000 Buffer", "5 días ganadores de $150. Retiro del 50% del balance sobre el colchón."),
            ("Apex 50K Full", "$2,500", "Intradía Trailing", "7 días", "$3,000", "$2,600 Buffer", "Trailing persigue ganancias abiertas en vivo. 10 días para primer retiro.")
        };

        for (int i = 0; i < rulesData.Length; i++)
        {
            var r = rulesData[i];
            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions(rulesCols), VerticalAlignment = VerticalAlignment.Center };
            AddTableCell(rowGrid, 0, r.Item1, "#00E599", true);
            AddTableCell(rowGrid, 1, r.Item2, "#FF355E");
            AddTableCell(rowGrid, 2, r.Item3, r.Item3.Contains("Trailing") ? "#FFB224" : "#38BDF8");
            AddTableCell(rowGrid, 3, r.Item4, "#E4E4E7");
            AddTableCell(rowGrid, 4, r.Item5, "#00E599");
            AddTableCell(rowGrid, 5, r.Item6, r.Item6.Contains("Sin") ? "#00E599" : "#FFB224");
            AddTableCell(rowGrid, 6, r.Item7, "#A1A1AA");

            rowsStack.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse(i % 2 == 0 ? "#161619" : "#131316"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 6),
                Child = rowGrid
            });
        }

        return new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "MATRIZ COMPARATIVA DE REGLAS Y CONDICIONES (50K)",
                        Foreground = SolidColorBrush.Parse("#FFFFFF"),
                        FontSize = 11,
                        FontWeight = FontWeight.Bold
                    },
                    new Border
                    {
                        Background = SolidColorBrush.Parse("#18181B"),
                        BorderBrush = SolidColorBrush.Parse("#27272A"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(4, 4),
                        Child = headerGrid
                    },
                    rowsStack,
                    new TextBlock
                    {
                        Text = "EOD (End of Day): El drawdown solo se calcula con el saldo al cierre del mercado (16:00 CT). Las excursiones no te quiebran durante el día mientras no toques el límite.",
                        Foreground = SolidColorBrush.Parse("#71717A"),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
    }

    private void SwitchPropFirmView(int index)
    {
        if (pfSimContainer is null || pfCostContainer is null || pfRulesContainer is null)
            return;

        pfSimContainer.IsVisible = index == 0;
        pfCostContainer.IsVisible = index == 1;
        pfRulesContainer.IsVisible = index == 2;

        UpdateTabBtn(pfTabSimBtn, index == 0);
        UpdateTabBtn(pfTabCostBtn, index == 1);
        UpdateTabBtn(pfTabRulesBtn, index == 2);
    }

    private static void UpdateTabBtn(Button? btn, bool active)
    {
        if (btn is null) return;
        btn.Background = SolidColorBrush.Parse(active ? "#FFFFFF" : "#141416");
        btn.Foreground = SolidColorBrush.Parse(active ? "#09090B" : "#A1A1AA");
        btn.FontWeight = active ? FontWeight.Bold : FontWeight.SemiBold;
    }

    private static Button MakeSidebarNavButton(string text, bool isActive) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Background = SolidColorBrush.Parse(isActive ? "#FFFFFF" : "#141416"),
        Foreground = SolidColorBrush.Parse(isActive ? "#09090B" : "#A1A1AA"),
        FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold,
        FontSize = 12,
        Padding = new Thickness(14, 10),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
        Cursor = new Cursor(StandardCursorType.Hand)
    };

    internal static Border MakeDynamicKpiCard(string title, string initialVal, string initialSub, string accentHex, out TextBlock valBlock, out TextBlock subBlock)
    {
        valBlock = new TextBlock
        {
            Text = initialVal,
            Foreground = SolidColorBrush.Parse(accentHex),
            FontSize = 17,
            FontWeight = FontWeight.Bold
        };
        subBlock = new TextBlock
        {
            Text = initialSub,
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        return new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(11, 8),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = SolidColorBrush.Parse("#71717A"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold
                    },
                    valBlock,
                    subBlock
                }
            }
        };
    }

    private async Task RunInteractiveBacktestAsync()
    {
        if (isRunningBacktest || btRunBtn is null || btStatusText is null)
            return;

        isRunningBacktest = true;
        if (btProgressBar is not null)
        {
            btProgressBar.IsVisible = true;
            btProgressBar.IsIndeterminate = true;
            btProgressBar.Value = 0;
        }
        if (btRunBtn is not null)
        {
            btRunBtn.IsEnabled = false;
            btRunBtn.Content = "Cargando datos...";
        }
        if (navAnalyzerRunBtn is not null)
        {
            navAnalyzerRunBtn.IsEnabled = false;
            navAnalyzerRunBtn.Content = "Cargando...";
        }
        btStatusText.Text = "Leyendo serie de datos desde disco...";
        btStatusText.Foreground = SolidColorBrush.Parse("#A1A1AA");

        try
        {
            int strategyIdx = btStrategyBox?.SelectedIndex ?? 0;
            int instIdx = btInstrumentBox?.SelectedIndex ?? 0;
            int dataModeIdx = btDataModeBox?.SelectedIndex ?? 0;

            InstrumentSpec spec = instIdx switch
            {
                1 => InstrumentSpec.MNQ,
                2 => InstrumentSpec.ES,
                3 => InstrumentSpec.MES,
                _ => InstrumentSpec.NQ
            };

            string? resolvedPath = dataModeIdx switch
            {
                0 => Workspace.FindDatabentoTbboMinuteFile(workspaceRoot, spec.Symbol)
                    ?? Workspace.FindDatabentoMinuteFile(workspaceRoot, spec.Symbol, adjusted: false),
                1 => Workspace.FindDatabentoTbboFile(workspaceRoot, spec.Symbol),
                2 => Workspace.FindDatabentoMinuteFile(workspaceRoot, spec.Symbol, adjusted: false),
                3 => Workspace.FindDatabentoMinuteFile(workspaceRoot, spec.Symbol, adjusted: true)
                    ?? Workspace.FindDatabentoMinuteFile(workspaceRoot, spec.Symbol, adjusted: false),
                _ => Workspace.FindDatabentoTbboMinuteFile(workspaceRoot, spec.Symbol)
                    ?? Workspace.FindDatabentoMinuteFile(workspaceRoot, spec.Symbol, adjusted: false)
            };

            if (!Workspace.DatasetExists(resolvedPath))
            {
                btStatusText.Text = $"No se encontraron datos locales para {spec.Symbol} (revisa data/databento/{spec.Symbol}/)";
                btStatusText.Foreground = SolidColorBrush.Parse("#FF355E");
                return;
            }

            string sourcePath = resolvedPath!;

            DateTime? fromUtc = ParseOptionalUtc(btFromBox?.Text, false);
            DateTime? toUtc = ParseOptionalUtc(btToBox?.Text, true);

            int qty = int.TryParse(btQtyBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) && q >= 1 ? q : 1;
            int fast = int.TryParse(btFastBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f >= 1 ? f : 10;
            int slow = int.TryParse(btSlowBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > fast ? s : Math.Max(20, fast + 5);
            int? slTicks = int.TryParse(btSlTicksBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sl) && sl >= 1 ? sl : null;
            int? tpTicks = int.TryParse(btTpTicksBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tp) && tp >= 1 ? tp : null;
            int? beTrigger = int.TryParse(btBeTriggerBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bet) && bet >= 1 ? bet : null;
            int beOffset = int.TryParse(btBeOffsetBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int beo) ? beo : 0;
            int? trailTicks = int.TryParse(btTrailTicksBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int trt) && trt >= 1 ? trt : null;
            double comm = double.TryParse(btCommBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm) && cm >= 0 ? cm : 0.0;
            int slip = int.TryParse(btSlippageBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slp) && slp >= 0 ? slp : 0;
            double? maxDailyLoss = double.TryParse(btMaxDailyLossBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mdl) && mdl > 0 ? mdl : null;
            int? maxDailyTrades = int.TryParse(btMaxDailyTradesBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mdt) && mdt >= 1 ? mdt : null;

            TimeOnly? sessionStart = TimeOnly.TryParseExact(btSessionStartBox?.Text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly ss) ? ss : null;
            TimeOnly? sessionEnd = TimeOnly.TryParseExact(btSessionEndBox?.Text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly se) ? se : null;

            bool useTicks = dataModeIdx == 1;

            int stratIdx = btStrategyBox?.SelectedIndex ?? 0;

            var progressHandler = new Progress<double>(p =>
            {
                if (btProgressBar is null) return;
                btProgressBar.IsIndeterminate = false;
                int pct = (int)Math.Clamp(Math.Round(p * 100), 0, 100);
                btProgressBar.Value = pct;
                btStatusText.Text = $"Calculando simulación... {pct}% completado";
                if (btRunBtn is not null) btRunBtn.Content = $"Calculando {pct}%...";
                if (navAnalyzerRunBtn is not null) navAnalyzerRunBtn.Content = $"Calculando {pct}%";
            });

            var result = await Task.Run(() =>
            {
                IReadOnlyList<Bar> bars = useTicks ? Array.Empty<Bar>() : new NinjaTraderMinuteImporter().Read(sourcePath, fromUtc, toUtc);
                IReadOnlyList<Tick> ticks = useTicks ? new NinjaTraderTickImporter().Read(sourcePath, fromUtc, toUtc) : Array.Empty<Tick>();

                IStrategy strategy = stratIdx switch
                {
                    1 => new DubinsSavageStrategy(new StrategyParameters(
                        fast, slow, qty, slTicks ?? 98, tpTicks ?? 102, beTrigger, beOffset, trailTicks), seed: null),
                    2 => new ReversionPlaybookStrategy(new StrategyParameters(
                        fast > 1 ? fast : 20, slow > 1 ? slow : 5, qty, slTicks, tpTicks, beTrigger, beOffset, trailTicks)),
                    3 => new SmaCrossoverStrategy(new StrategyParameters(
                        fast, slow, qty, slTicks, tpTicks, beTrigger, beOffset, trailTicks)),
                    _ => new RandomEntryStrategy(new StrategyParameters(
                        fast, slow, qty, slTicks ?? 40, tpTicks ?? 40, beTrigger, beOffset, trailTicks), seed: null)
                };

                return new BacktestRunner().Run(new BacktestRequest
                {
                    Bars = bars,
                    Ticks = ticks,
                    Strategy = strategy,
                    Instrument = spec,
                    FastPeriod = fast,
                    SlowPeriod = slow,
                    Quantity = qty,
                    StopLossTicks = slTicks,
                    TakeProfitTicks = tpTicks,
                    BreakEvenTriggerTicks = beTrigger,
                    BreakEvenOffsetTicks = beOffset,
                    TrailingStopTicks = trailTicks,
                    CommissionPerContractPerSide = comm,
                    SlippageTicks = slip,
                    SessionStartUtc = sessionStart,
                    SessionEndUtc = sessionEnd,
                    MaxDailyLossUsd = maxDailyLoss,
                    MaxTradesPerDay = maxDailyTrades,
                    Progress = progressHandler
                });
            });

            PopulateAnalyzerResults(result);
            btStatusText.Text = $"Completado: {result.Summary.Bars:N0} velas, {result.Summary.ClosedTrades:N0} trades ({result.Configuration.Strategy} · {spec.Symbol}).";
            btStatusText.Foreground = SolidColorBrush.Parse("#00E599");
        }
        catch (Exception ex)
        {
            btStatusText.Text = $"Error en simulación: {ex.Message}";
            btStatusText.Foreground = SolidColorBrush.Parse("#FF355E");
        }
        finally
        {
            isRunningBacktest = false;
            if (btProgressBar is not null)
            {
                btProgressBar.IsVisible = false;
                btProgressBar.IsIndeterminate = false;
                btProgressBar.Value = 0;
            }
            if (btRunBtn is not null)
            {
                btRunBtn.IsEnabled = true;
                btRunBtn.Content = "Ejecutar Analizador";
            }
            if (navAnalyzerRunBtn is not null)
            {
                navAnalyzerRunBtn.IsEnabled = true;
                navAnalyzerRunBtn.Content = "▶  Ejecutar";
            }
        }
    }

    private void PopulateAnalyzerResults(BacktestResult result)
    {
        var s = result.Summary;

        if (kpiNetPnlVal is not null && kpiNetPnlSub is not null)
        {
            kpiNetPnlVal.Text = $"{s.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}";
            kpiNetPnlVal.Foreground = SolidColorBrush.Parse(s.NetProfit >= 0 ? "#00E599" : "#FF355E");
            kpiNetPnlSub.Text = $"Bruto: {s.GrossProfit:+$#,##0;-$#,##0;$0} | Com: ${s.ClosedTradeCommissions:#,##0.00}";
        }

        if (kpiWinRateVal is not null && kpiWinRateSub is not null)
        {
            kpiWinRateVal.Text = $"{s.WinRate * 100:F1}% ({s.ClosedTrades})";
            kpiWinRateSub.Text = $"+{s.WinningTrades} Gan | -{s.LosingTrades} Perd | L:{s.LongTrades} S:{s.ShortTrades}";
        }

        if (kpiPfVal is not null && kpiPfSub is not null)
        {
            string pfStr = s.ProfitFactor.HasValue ? s.ProfitFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
            string poStr = s.PayoffRatio.HasValue ? s.PayoffRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
            string rStr = s.AverageRMultiple.HasValue ? $" ({s.AverageRMultiple.Value:+0.00;-0.00;0.00}R)" : "";
            kpiPfVal.Text = $"PF {pfStr} · R/B {poStr}";
            kpiPfSub.Text = $"Esperanza: {s.ExpectancyUsd:+$0.00;-$0.00;$0.00}{rStr}";
        }

        if (kpiDdVal is not null && kpiDdSub is not null)
        {
            kpiDdVal.Text = $"${s.MaxIntradayTrailingDrawdown:#,##0.00}";
            kpiDdSub.Text = $"DD Cerrado: ${s.MaxClosedTradeDrawdown:#,##0.00} | MAE Med: ${s.AverageMaeUsd:F0}";
        }

        if (kpiSharpeVal is not null && kpiSharpeSub is not null)
        {
            string sh = s.SharpeRatio.HasValue ? s.SharpeRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
            string so = s.SortinoRatio.HasValue ? s.SortinoRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
            string rf = s.RecoveryFactor.HasValue ? s.RecoveryFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
            kpiSharpeVal.Text = $"{sh} / {so}";
            kpiSharpeSub.Text = $"Recovery Factor: {rf}";
        }

        if (kpiDaysVal is not null && kpiDaysSub is not null)
        {
            kpiDaysVal.Text = $"{s.WinningDays}+ / {s.LosingDays}- ({s.TradingDays}d)";
            kpiDaysSub.Text = $"Rachas: +{s.MaxConsecutiveWins}/-{s.MaxConsecutiveLosses} | Dur: {s.AverageTradeDurationMinutes:F1}m";
        }

        if (btGraphFinalNetText is not null)
        {
            btGraphFinalNetText.Text = $"Capital: {s.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}";
            btGraphFinalNetText.Foreground = SolidColorBrush.Parse(s.NetProfit >= 0 ? "#00E599" : "#FF355E");
        }
        double peak = result.Trades.Count > 0 ? Math.Max(0d, result.Trades.Max(t => t.CumulativeNetProfitUsd)) : 0d;
        if (btGraphPeakText is not null)
        {
            btGraphPeakText.Text = $"Pico: ${peak:#,##0.00}";
        }
        if (btGraphMaxDdText is not null)
        {
            btGraphMaxDdText.Text = $"Máx DD: -${s.MaxIntradayTrailingDrawdown:#,##0.00}";
        }
        if (btGraphWinRateText is not null)
        {
            btGraphWinRateText.Text = $"WR: {s.WinRate * 100:F1}% ({s.ClosedTrades} ops)";
            btGraphWinRateText.Foreground = SolidColorBrush.Parse(s.WinRate >= 0.5 ? "#00E599" : "#FF355E");
        }

        // Gráfico de Equidad
        btEquitySurface?.SetTrades(result.Trades);

        // Tabla 1: NinjaTrader Summary (All / Long / Short)
        PopulateNinjaTraderSummaryRows(s);

        // Tabla 2: NinjaTrader Trades (Operaciones detalladas con Paginación)
        currentAllTrades = result.Trades;
        PopulateTradesPage(0);

        // Tabla 3: NinjaTrader Periods (Desglose Diario)
        if (btPeriodsRows is not null && result.DailyPerformance is not null)
        {
            btPeriodsRows.Children.Clear();
            foreach (var dp in result.DailyPerformance)
            {
                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("130,85,110,95,115,110,120,130,*"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                bool isWin = dp.NetProfit > 0;
                double winPct = dp.Trades > 0 ? (double)dp.WinningTrades / dp.Trades * 100 : 0.0;

                AddTableCell(rowGrid, 0, dp.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "#FFFFFF", true);
                AddTableCell(rowGrid, 1, $"{dp.Trades}", "#A1A1AA");
                AddTableCell(rowGrid, 2, $"+{dp.WinningTrades} / -{dp.LosingTrades}", "#E4E4E7");
                AddTableCell(rowGrid, 3, $"{winPct:F1}%", winPct >= 50 ? "#00E599" : "#FF355E");
                AddTableCell(rowGrid, 4, $"{dp.GrossProfit:+$#,##0.00;-$#,##0.00;$0.00}", dp.GrossProfit >= 0 ? "#00E599" : "#FF355E");
                AddTableCell(rowGrid, 5, $"${dp.Commission:#,##0.00}", "#71717A");
                AddTableCell(rowGrid, 6, $"{dp.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}", isWin ? "#00E599" : "#FF355E", true);
                AddTableCell(rowGrid, 7, $"-${dp.MaxIntradayDrawdown:#,##0.00}", "#FF355E");
                AddTableCell(rowGrid, 8, $"{dp.CumulativeNetProfit:+$#,##0.00;-$#,##0.00;$0.00}", dp.CumulativeNetProfit >= 0 ? "#00E599" : "#FF355E", true);

                btPeriodsRows.Children.Add(new Border
                {
                    Background = SolidColorBrush.Parse("#141416"),
                    BorderBrush = SolidColorBrush.Parse("#1F1F23"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 6),
                    Child = rowGrid
                });
            }
        }

        lastBacktestResult = result;
        PopulateMetricsView(result);
    }

    private void PopulateMetricsView(BacktestResult result)
    {
        var days = result.DailyPerformance ?? Array.Empty<DailyPerformanceRecord>();
        btDailyTradesSurface?.SetDays(days);

        if (days.Count == 0)
        {
            if (metAvgTradesDayVal != null) metAvgTradesDayVal.Text = "—";
            if (metAvgTradesDaySub != null) metAvgTradesDaySub.Text = "Total trades en periodo";
            if (metExtremesTradesVal != null) metExtremesTradesVal.Text = "—";
            if (metExtremesTradesSub != null) metExtremesTradesSub.Text = "Rango de frecuencia diaria";
            if (metAvgPnlDayVal != null) metAvgPnlDayVal.Text = "—";
            if (metAvgPnlDaySub != null) metAvgPnlDaySub.Text = "Ganancia / pérdida media día";
            if (metWinningDaysVal != null) metWinningDaysVal.Text = "—";
            if (metWinningDaysSub != null) metWinningDaysSub.Text = "Días positivos vs negativos";
            if (metAvgDurationVal != null) metAvgDurationVal.Text = "—";
            if (metAvgDurationSub != null) metAvgDurationSub.Text = "Tiempo de permanencia en trade";
            metDowStack?.Children.Clear();
            metFreqBucketsStack?.Children.Clear();
            if (metBestDayVal != null) metBestDayVal.Text = "—";
            if (metWorstDayVal != null) metWorstDayVal.Text = "—";
            if (metAvgIntradayDdVal != null) metAvgIntradayDdVal.Text = "—";
            if (metCommImpactVal != null) metCommImpactVal.Text = "—";
            return;
        }

        int totalDays = days.Count;
        int totalTrades = days.Sum(d => d.Trades);
        double avgTrades = totalDays > 0 ? (double)totalTrades / totalDays : 0.0;
        int maxTrades = days.Max(d => d.Trades);
        int minTrades = days.Min(d => d.Trades);

        double variance = days.Average(d => Math.Pow(d.Trades - avgTrades, 2));
        double stdDev = Math.Sqrt(variance);

        double totalNet = days.Sum(d => d.NetProfit);
        double avgDailyNet = totalNet / totalDays;

        var winDaysList = days.Where(d => d.NetProfit > 0).ToList();
        var lossDaysList = days.Where(d => d.NetProfit < 0).ToList();
        int winDays = winDaysList.Count;
        int lossDays = lossDaysList.Count;
        double winDaysPct = (double)winDays / totalDays * 100.0;

        double avgWinDay = winDays > 0 ? winDaysList.Average(d => d.NetProfit) : 0.0;
        double avgLossDay = lossDays > 0 ? lossDaysList.Average(d => d.NetProfit) : 0.0;

        if (metAvgTradesDayVal != null && metAvgTradesDaySub != null)
        {
            metAvgTradesDayVal.Text = $"{avgTrades:F1} ops / día";
            metAvgTradesDayVal.Foreground = SolidColorBrush.Parse("#FFB224");
            metAvgTradesDaySub.Text = $"Total: {totalTrades:N0} ops en {totalDays:N0} días";
        }

        if (metExtremesTradesVal != null && metExtremesTradesSub != null)
        {
            metExtremesTradesVal.Text = $"Máx {maxTrades} | Mín {minTrades}";
            metExtremesTradesSub.Text = $"Desv. Estándar: ±{stdDev:F1} ops/día";
        }

        if (metAvgPnlDayVal != null && metAvgPnlDaySub != null)
        {
            metAvgPnlDayVal.Text = $"{avgDailyNet:+$#,##0.00;-$#,##0.00;$0.00} / día";
            metAvgPnlDayVal.Foreground = SolidColorBrush.Parse(avgDailyNet >= 0 ? "#00E599" : "#FF355E");
            metAvgPnlDaySub.Text = $"Día +: {avgWinDay:+$#,##0} | Día -: {avgLossDay:-$#,##0}";
        }

        if (metWinningDaysVal != null && metWinningDaysSub != null)
        {
            metWinningDaysVal.Text = $"{winDaysPct:F1}% ({winDays}d / {totalDays}d)";
            metWinningDaysVal.Foreground = SolidColorBrush.Parse(winDaysPct >= 50 ? "#00E599" : "#FF355E");
            metWinningDaysSub.Text = $"+{winDays} Días Gan | -{lossDays} Perd";
        }

        if (metAvgDurationVal != null && metAvgDurationSub != null)
        {
            double avgDur = result.Summary.AverageTradeDurationMinutes;
            metAvgDurationVal.Text = $"{avgDur:F1} min / trade";
            metAvgDurationSub.Text = $"Exposición media por posición";
        }

        // Desglose por Día de la Semana
        if (metDowStack != null)
        {
            metDowStack.Children.Clear();
            var dowOrder = new[]
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
                DayOfWeek.Sunday
            };

            foreach (var dow in dowOrder)
            {
                var match = days.Where(d => d.DateUtc.DayOfWeek == dow).ToList();
                if (match.Count == 0) continue;

                string name = dow switch
                {
                    DayOfWeek.Monday => "Lunes",
                    DayOfWeek.Tuesday => "Martes",
                    DayOfWeek.Wednesday => "Miércoles",
                    DayOfWeek.Thursday => "Jueves",
                    DayOfWeek.Friday => "Viernes",
                    DayOfWeek.Sunday => "Domingo",
                    _ => dow.ToString()
                };

                int dCount = match.Count;
                int dTrades = match.Sum(d => d.Trades);
                double dAvgTrades = (double)dTrades / dCount;
                int dWins = match.Sum(d => d.WinningTrades);
                double dWinRate = dTrades > 0 ? (double)dWins / dTrades * 100.0 : 0.0;
                double dNet = match.Sum(d => d.NetProfit);
                double dAvgNet = dNet / dCount;

                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("70,55,60,60,*"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AddTableCell(rowGrid, 0, name, "#FFFFFF", true);
                AddTableCell(rowGrid, 1, $"{dTrades} ops", "#A1A1AA");
                AddTableCell(rowGrid, 2, $"μ {dAvgTrades:F1}/d", "#FFB224");
                AddTableCell(rowGrid, 3, $"{dWinRate:F1}%", dWinRate >= 50 ? "#00E599" : "#FF355E");
                AddTableCell(rowGrid, 4, $"{dNet:+$#,##0;-$#,##0;$0} (μ {dAvgNet:+$#,##0;-$#,##0;$0})", dNet >= 0 ? "#00E599" : "#FF355E", true);

                metDowStack.Children.Add(new Border
                {
                    Background = SolidColorBrush.Parse("#161619"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 4),
                    Child = rowGrid
                });
            }
        }

        // Histograma de Frecuencia de Trades
        if (metFreqBucketsStack != null)
        {
            metFreqBucketsStack.Children.Clear();
            var buckets = new (string Label, Func<DailyPerformanceRecord, bool> Predicate)[]
            {
                ("1 trade / día", d => d.Trades == 1),
                ("2 trades / día", d => d.Trades == 2),
                ("3 – 4 trades", d => d.Trades >= 3 && d.Trades <= 4),
                ("5 – 6 trades", d => d.Trades >= 5 && d.Trades <= 6),
                ("7+ trades / día", d => d.Trades >= 7)
            };

            foreach (var (lbl, pred) in buckets)
            {
                int bCount = days.Count(pred);
                double bPct = totalDays > 0 ? (double)bCount / totalDays * 100.0 : 0.0;

                var bGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("80,*,80"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var lblBlock = new TextBlock
                {
                    Text = lbl,
                    Foreground = SolidColorBrush.Parse("#FFFFFF"),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lblBlock, 0);
                bGrid.Children.Add(lblBlock);

                var barTrack = new Border
                {
                    Background = SolidColorBrush.Parse("#222226"),
                    Height = 8,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                };

                var barFill = new Border
                {
                    Background = SolidColorBrush.Parse("#00E599"),
                    Height = 8,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(2, (bPct / 100.0) * 140.0)
                };
                barTrack.Child = barFill;
                Grid.SetColumn(barTrack, 1);
                bGrid.Children.Add(barTrack);

                var infoBlock = new TextBlock
                {
                    Text = $"{bCount} d ({bPct:F1}%)",
                    Foreground = SolidColorBrush.Parse("#A1A1AA"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(infoBlock, 2);
                bGrid.Children.Add(infoBlock);

                metFreqBucketsStack.Children.Add(new Border
                {
                    Background = SolidColorBrush.Parse("#161619"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 4),
                    Child = bGrid
                });
            }
        }

        // Métricas avanzadas inferiores
        if (days.Count > 0)
        {
            var best = days.MaxBy(d => d.NetProfit);
            var worst = days.MinBy(d => d.NetProfit);
            double avgDd = days.Average(d => d.MaxIntradayDrawdown);
            double comms = result.Summary.ClosedTradeCommissions;
            double gross = result.Summary.GrossProfit;
            double commPct = gross > 0 ? (comms / gross) * 100.0 : 0.0;

            if (metBestDayVal != null && metBestDaySub != null && best != null)
            {
                metBestDayVal.Text = $"{best.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}";
                metBestDaySub.Text = $"Fecha: {best.DateUtc:yyyy-MM-dd} ({best.Trades} ops)";
            }
            if (metWorstDayVal != null && metWorstDaySub != null && worst != null)
            {
                metWorstDayVal.Text = $"{worst.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}";
                metWorstDaySub.Text = $"Fecha: {worst.DateUtc:yyyy-MM-dd} ({worst.Trades} ops)";
            }
            if (metAvgIntradayDdVal != null && metAvgIntradayDdSub != null)
            {
                metAvgIntradayDdVal.Text = $"-${avgDd:#,##0.00}";
                metAvgIntradayDdSub.Text = $"Riesgo intradía medio por sesión";
            }
            if (metCommImpactVal != null && metCommImpactSub != null)
            {
                metCommImpactVal.Text = $"${comms:#,##0.00} ({commPct:F1}%)";
                metCommImpactSub.Text = $"Comisiones sobre ganancia bruta";
            }
        }
    }

    private const string PropFirmCostColumns = "210,85,80,90,110,80,*";

    // Precios aproximados por cuenta 50K (mismo orden que pfPlanCombo).
    // BasePrice = precio normal, CouponPrice = con cupón típico, Activation = cuota al fondearse.
    private static readonly (string Name, double BasePrice, double CouponPrice, double Activation)[] PropFirmPlanPrices =
    {
        ("Tradeify Select Flex 50K", 165, 99, 0),
        ("Tradeify Select Daily 50K", 165, 99, 0),
        ("Tradeify Growth 50K", 145, 87, 0),
        ("Tradeify Lightning 50K", 492, 295, 0),
        ("Topstep 50K (mensual)", 49, 49, 149),
        ("Apex 50K", 167, 35, 140)
    };

    private int GetPropFirmAccountCount() => pfAccountsCountCombo?.SelectedIndex switch
    {
        0 => 5,
        1 => 10,
        2 => 20,
        3 => 50,
        4 => 100,
        _ => 10
    };

    private void RefreshPropFirmCostTable()
    {
        if (pfCostTableRows is null) return;

        int n = GetPropFirmAccountCount();
        int selectedPlan = pfPlanCombo?.SelectedIndex ?? 0;

        if (pfCostTitleText is not null)
            pfCostTitleText.Text = $"COSTO DE LA CARTERA · {n} EVALUACIONES";

        pfCostTableRows.Children.Clear();
        for (int i = 0; i < PropFirmPlanPrices.Length; i++)
        {
            var p = PropFirmPlanPrices[i];
            double totalBase = p.BasePrice * n;
            double totalCoupon = p.CouponPrice * n;
            int payoutsToRecover = (int)Math.Ceiling(totalCoupon / 1000.0);
            bool isSelected = i == selectedPlan;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(PropFirmCostColumns),
                VerticalAlignment = VerticalAlignment.Center
            };

            string nameHex = isSelected ? "#00E599" : "#E4E4E7";
            AddTableCell(row, 0, (isSelected ? "▶ " : "") + p.Name, nameHex, isSelected);
            AddTableCell(row, 1, $"${p.BasePrice:N0}", "#A1A1AA");
            AddTableCell(row, 2, $"${p.CouponPrice:N0}", "#A1A1AA");
            AddTableCell(row, 3, $"${totalBase:N0}", "#E4E4E7");
            AddTableCell(row, 4, $"${totalCoupon:N0}", "#FFB224", true);
            AddTableCell(row, 5, p.Activation > 0 ? $"${p.Activation:N0} c/u" : "$0", p.Activation > 0 ? "#FF355E" : "#71717A");
            AddTableCell(row, 6, $"{payoutsToRecover} retiro(s)", "#38BDF8");

            pfCostTableRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse(isSelected ? "#0F2A22" : (i % 2 == 0 ? "#161619" : "#131316")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4),
                Child = row
            });
        }
    }

    private void RunPropFirmSimulation()
    {
        if (pfPlanCombo is null || pfAccountsCountCombo is null || pfAccountsTableRows is null)
            return;

        int planIdx = pfPlanCombo.SelectedIndex;
        if (planIdx < 0) planIdx = 0;

        int strategyModeIdx = pfStrategyModeCombo?.SelectedIndex ?? 0;
        if (strategyModeIdx < 0) strategyModeIdx = 0;

        int numAccounts = pfAccountsCountCombo.SelectedIndex switch
        {
            0 => 5,
            1 => 10,
            2 => 20,
            3 => 50,
            4 => 100,
            _ => 10
        };

        double costPerAccount = 40.0;
        if (pfAccountPriceBox != null && double.TryParse(pfAccountPriceBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedCost))
            costPerAccount = Math.Max(0, parsedCost);

        double activationFee = 0.0;
        if (pfActivationPriceBox != null && double.TryParse(pfActivationPriceBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedAct))
            activationFee = Math.Max(0, parsedAct);

        var random = new Random();
        int evalPassedCount = 0;
        int payoutCount = 0;
        int blownInEvalCount = 0;
        int blownInFundedCount = 0;
        double totalPayouts = 0.0;
        double totalInvested = numAccounts * costPerAccount;

        var backtestTrades = lastBacktestResult?.Trades;
        bool hasTrades = backtestTrades != null && backtestTrades.Count > 0;

        var results = new List<PropFirmAccountSimResult>(numAccounts);

        for (int acc = 1; acc <= numAccounts; acc++)
        {
            var sim = SimulateSingleAccount(acc, planIdx, strategyModeIdx, random, backtestTrades);
            results.Add(sim);

            if (sim.PassedEvaluation)
            {
                evalPassedCount++;
                totalInvested += activationFee;
            }
            else
            {
                blownInEvalCount++;
            }

            if (sim.AchievedPayout)
            {
                payoutCount++;
                totalPayouts += sim.PayoutReceived;
            }
            else if (sim.PassedEvaluation)
            {
                blownInFundedCount++;
            }
        }

        double netProfitTrader = totalPayouts - totalInvested;
        double roi = totalInvested > 0 ? (netProfitTrader / totalInvested) * 100.0 : 0.0;
        double evalPassRate = (double)evalPassedCount / numAccounts * 100.0;
        double payoutRate = (double)payoutCount / numAccounts * 100.0;

        // 1. Update KPI Cards
        if (pfKpiEvalPassVal != null && pfKpiEvalPassSub != null)
        {
            pfKpiEvalPassVal.Text = $"{evalPassRate:F1}% ({evalPassedCount}/{numAccounts})";
            pfKpiEvalPassVal.Foreground = SolidColorBrush.Parse(evalPassRate >= 30 ? "#00E599" : "#FFFFFF");
            pfKpiEvalPassSub.Text = $"{blownInEvalCount} quemadas en Fase 1";
        }

        if (pfKpiPayoutPassVal != null && pfKpiPayoutPassSub != null)
        {
            pfKpiPayoutPassVal.Text = $"{payoutRate:F1}% ({payoutCount}/{numAccounts})";
            pfKpiPayoutPassVal.Foreground = SolidColorBrush.Parse(payoutCount > 0 ? "#00E599" : "#FF355E");
            pfKpiPayoutPassSub.Text = $"{blownInFundedCount} quemadas en fondeada";
        }

        if (pfKpiTotalWithdrawnVal != null && pfKpiTotalWithdrawnSub != null)
        {
            pfKpiTotalWithdrawnVal.Text = $"{totalPayouts:+$#,##0.00;$0.00}";
            pfKpiTotalWithdrawnVal.Foreground = SolidColorBrush.Parse(totalPayouts > 0 ? "#00E599" : "#71717A");
            pfKpiTotalWithdrawnSub.Text = $"{payoutCount} cobros procesados";
        }

        if (pfKpiTotalCostVal != null && pfKpiTotalCostSub != null)
        {
            pfKpiTotalCostVal.Text = $"-${totalInvested:#,##0.00}";
            pfKpiTotalCostSub.Text = $"Eval: ${numAccounts * costPerAccount:N0} | Act: ${evalPassedCount * activationFee:N0}";
        }

        if (pfKpiNetRoiVal != null && pfKpiNetRoiSub != null)
        {
            string roiSign = netProfitTrader >= 0 ? "+" : "";
            pfKpiNetRoiVal.Text = $"{netProfitTrader:+$#,##0.00;-$#,##0.00;$0.00}";
            pfKpiNetRoiVal.Foreground = SolidColorBrush.Parse(netProfitTrader >= 0 ? "#00E599" : "#FF355E");
            pfKpiNetRoiSub.Text = $"Retorno: {roiSign}{roi:F1}% sobre inversión";
        }

        // 2. Update Distribution Bar and EV
        double payoutShare = numAccounts > 0 ? (double)payoutCount / numAccounts : 0;
        double passOnlyShare = numAccounts > 0 ? (double)blownInFundedCount / numAccounts : 0;
        double blownEvalShare = numAccounts > 0 ? (double)blownInEvalCount / numAccounts : 0;

        if (pfDistTrack != null)
        {
            pfDistTrack.ColumnDefinitions = new ColumnDefinitions($"{Math.Max(0.01, payoutShare * 100)}*,{Math.Max(0.01, passOnlyShare * 100)}*,{Math.Max(0.01, blownEvalShare * 100)}*");
        }

        if (pfDistributionLegend != null)
        {
            pfDistributionLegend.Text = $"Verde ({payoutRate:F0}%): Retiraron | Azul ({passOnlyShare * 100:F0}%): Pasaron Eval | Rojo ({blownEvalShare * 100:F0}%): Quebraron Eval";
        }

        double evPerAccount = numAccounts > 0 ? netProfitTrader / numAccounts : 0.0;
        if (pfEvText != null)
        {
            string evSign = evPerAccount >= 0 ? "+" : "";
            pfEvText.Text = $"Esperanza Matemática por cuenta: {evSign}${evPerAccount:F2} | Retorno: ${totalPayouts:N0} de ${totalInvested:N0} invertidos";
            pfEvText.Foreground = SolidColorBrush.Parse(evPerAccount >= 0 ? "#00E599" : "#FF355E");
        }

        if (pfVerdictText != null)
        {
            string modeTag = strategyModeIdx switch
            {
                0 => "[Dubins-Savage · Punto Dulce ($500 F2 + 2.º Retiro Free Roll)]",
                1 => "[Dubins-Savage · Retiro Mínimo ($200 F2 + 2.º Retiro Free Roll)]",
                2 => "[Dubins-Savage · Perseguir Tope Máximo ($2,500 / Meta +$5,000)]",
                3 => "[Aleatoria Común: $200 fijo en ambas fases (1–2 ops/día)]",
                4 => "[Estrategia Actual · Operaciones Reales del Backtest]",
                _ => "[Simulación Personalizada]"
            };

            if (strategyModeIdx == 4 && !hasTrades)
            {
                pfVerdictText.Text = "ℹ️ No hay un backtest ejecutado en el Analizador. Ve a la pestaña 'Analizador', pulsa 'Ejecutar', y regresa aquí para simular cuentas con tus operaciones históricas reales.";
                pfVerdictText.Foreground = SolidColorBrush.Parse("#FFB224");
            }
            else if (netProfitTrader > 0)
            {
                pfVerdictText.Text = $"🟢 RESULTADO FAVORABLE {modeTag}: Lograste {payoutCount} cuenta(s) con cobro que sumaron ${totalPayouts:N2}, superando los ${totalInvested:N2} invertidos en las {numAccounts} cuentas (+{roi:F1}% ROI, Pase F1: {evalPassRate:F1}%).";
                pfVerdictText.Foreground = SolidColorBrush.Parse("#00E599");
            }
            else
            {
                pfVerdictText.Text = $"🔴 RESULTADO NEGATIVO {modeTag}: Invertiste ${totalInvested:N2} en {numAccounts} cuentas y obtuviste ${totalPayouts:N2} en retiros ({roi:F1}% ROI, Pase F1: {evalPassRate:F1}%). El drawdown eliminó a {blownInEvalCount + blownInFundedCount} cuentas antes de cobrar.";
                pfVerdictText.Foreground = SolidColorBrush.Parse("#FF355E");
            }
        }

        // 3. Populate Accounts Table
        pfAccountsTableRows.Children.Clear();
        foreach (var sim in results)
        {
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("35,110,110,60,50,85,90,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            string statusHex = sim.FinalStatus.Contains("RETIRÓ") ? "#00E599" : (sim.FinalStatus.Contains("F2") ? "#FFB224" : "#FF355E");
            string pnlHex = sim.AccountEquity >= 0 ? "#00E599" : "#FF355E";
            string payoutHex = sim.PayoutReceived > 0 ? "#00E599" : "#71717A";

            AddTableCell(rowGrid, 0, $"#{sim.AccountNumber}", "#71717A", true);
            AddTableCell(rowGrid, 1, sim.FinalStatus, statusHex, true);
            AddTableCell(rowGrid, 2, sim.PhaseReached, "#A1A1AA");
            AddTableCell(rowGrid, 3, $"{sim.DaysTraded} d", "#E4E4E7");
            AddTableCell(rowGrid, 4, $"{sim.TotalTrades}", "#A1A1AA");
            AddTableCell(rowGrid, 5, $"{sim.AccountEquity:+$#,##0;-$#,##0;$0}", pnlHex);
            AddTableCell(rowGrid, 6, sim.PayoutReceived > 0 ? $"+${sim.PayoutReceived:#,##0.00}" : "$0.00", payoutHex, sim.PayoutReceived > 0);
            AddTableCell(rowGrid, 7, sim.Notes, "#71717A");

            pfAccountsTableRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse(sim.AccountNumber % 2 == 0 ? "#161619" : "#131316"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4),
                Child = rowGrid
            });
        }
    }

    private static PropFirmAccountSimResult SimulateSingleAccount(int accNum, int planIdx, int strategyModeIdx, Random random, IReadOnlyList<TradeRecord>? backtestTrades)
    {
        // planIdx:
        // 0: Tradeify Select Flex 50K (Target 3000, DD 2000 EOD, Buffer 0, 5 win days >= 150 -> 50% payout, max $2,500 cap)
        // 1: Tradeify Select Daily 50K (Target 3000, DD 2000 EOD, Buffer 2100 -> Daily payout above buffer)
        // 2: Tradeify Growth 50K (Target 3000, DD 2000 EOD, DLL 1250 soft, 5 win days >= 150 + 35% consistency)
        // 3: Tradeify Lightning 50K (Instant funded, DD 2000 EOD, 20% consistency)
        // 4: Topstep 50K Express (Target 3000, DD 2000 EOD, 5 win days >= 150 -> 50% payout)
        // 5: Apex 50K Full (Target 3000, DD 2500 Intraday Trailing, Buffer 2600, 10 days)
        //
        // strategyModeIdx:
        // 0: Dubins-Savage · Punto Dulce ($500/día en F2 -> Retira al 5.º día + Free Roll 2.º retiro)
        // 1: Dubins-Savage · Retiro Mínimo ($200/día en F2 -> Retira al 5.º día + Free Roll 2.º retiro)
        // 2: Dubins-Savage · Tope Máximo ($1,000/día en F2 -> Espera acumular +$5,000 para sacar el máximo $2,500)
        // 3: Aleatoria Estándar ($200 fijo en ambas fases, 1-2 trades/día)
        // 4: Estrategia Actual (Trades Reales del Backtest)

        bool useBacktestTrades = strategyModeIdx == 4;
        bool hasTrades = useBacktestTrades && backtestTrades != null && backtestTrades.Count > 0;

        if (useBacktestTrades && !hasTrades)
        {
            return new PropFirmAccountSimResult
            {
                AccountNumber = accNum,
                FinalStatus = "SIN BACKTEST",
                PhaseReached = "Inactivo",
                DaysTraded = 0,
                TotalTrades = 0,
                AccountEquity = 0,
                PayoutReceived = 0,
                PassedEvaluation = false,
                AchievedPayout = false,
                Notes = "Ejecuta un backtest en el Analizador para simular con trades reales."
            };
        }

        bool useDubinsSavage = strategyModeIdx <= 2;
        bool chaseMaxPayout = strategyModeIdx == 2;
        bool useSweetSpotF2 = strategyModeIdx == 0;
        bool isInstantFunded = planIdx == 3;
        bool passedEval = isInstantFunded;
        bool achievedPayout = false;
        double payout = 0.0;
        int totalDays = 0;
        int totalTrades = 0;
        double finalAccountPnl = 0.0;
        string finalStatus = "";
        string phaseReached = isInstantFunded ? "Fase 2 (Fondeada)" : "Fase 1 (Eval)";
        string reason = "";

        // --- FASE 1: EVALUACIÓN ---
        if (!isInstantFunded)
        {
            double balance = 50000;
            double peak = 50000;
            double target = 53000;
            double maxDd = planIdx == 5 ? 2500 : 2000;
            bool isIntradayTrailing = planIdx == 5;
            double consistencyLimit = planIdx == 4 ? 0.50 : 0.40;
            int minDays = planIdx == 2 ? 1 : (planIdx == 4 ? 2 : (planIdx == 5 ? 7 : 3));

            var dailyProfits = new List<double>();
            bool evalBlown = false;

            for (int day = 1; day <= 60; day++)
            {
                totalDays++;
                int tradesToday = 0;
                double dayPnl = 0.0;

                if (useBacktestTrades)
                {
                    tradesToday = random.Next(1, 3);
                    for (int t = 0; t < tradesToday; t++)
                    {
                        var tr = backtestTrades![random.Next(backtestTrades.Count)];
                        dayPnl += tr.NetProfit;
                    }
                }
                else if (useDubinsSavage)
                {
                    // Dubins-Savage Bold Play: estrictamente 1 operación al día en apertura NY.
                    // En planes EOD con 40% consistencia (3 días mín), bloque de 2 NQ con SL=98 ticks (-$980 bruto / -$988.20 neto)
                    // y TP=102 ticks (+$1,020 bruto / +$1,011.80 neto):
                    // 1) 2 derrotas seguidas (-$1,976.40) NO quiebran el Drawdown EOD de -$2,000 (quedan $23.60 de vida, exige 3 derrotas).
                    // 2) 3 victorias (+$3,035.40) superan los $3,000 en 3 días exactos con 33.3% de consistencia.
                    tradesToday = 1;
                    bool win1 = random.NextDouble() < 0.490; // P(win) = 98 / (98 + 102) = 49.0%
                    if (isIntradayTrailing)
                    {
                        dayPnl += win1 ? 445.60 : -454.40;
                    }
                    else
                    {
                        dayPnl += win1 ? 1011.80 : -988.20;
                    }
                }
                else
                {
                    tradesToday++;
                    bool win1 = random.NextDouble() < 0.50;
                    if (win1)
                    {
                        dayPnl += 196;
                    }
                    else
                    {
                        dayPnl -= 204;
                        if (random.NextDouble() < 0.65)
                        {
                            tradesToday++;
                            bool win2 = random.NextDouble() < 0.50;
                            dayPnl += win2 ? 196 : -204;
                        }
                    }
                }
                totalTrades += tradesToday;
                balance += dayPnl;
                dailyProfits.Add(Math.Max(0, dayPnl));

                if (isIntradayTrailing)
                {
                    double intradayMfe = dayPnl > 0 ? dayPnl + random.Next(20, 80) : random.Next(0, 40);
                    peak = Math.Max(peak, balance - dayPnl + intradayMfe);
                    double floor = peak - maxDd;
                    if (balance <= floor)
                    {
                        evalBlown = true;
                        finalAccountPnl = balance - 50000;
                        reason = $"Quebró por Trailing Intradía (Pico: ${peak:N0}, Suelo: ${floor:N0})";
                        break;
                    }
                }
                else
                {
                    peak = Math.Max(peak, balance);
                    double floor = peak - maxDd;
                    if (balance <= floor)
                    {
                        evalBlown = true;
                        finalAccountPnl = balance - 50000;
                        reason = $"Quebró por Drawdown EOD (-${maxDd:N0}) en día {day}";
                        break;
                    }
                }

                if (balance >= target && day >= minDays)
                {
                    double totalProf = balance - 50000;
                    double bestDay = dailyProfits.Count > 0 ? dailyProfits.Max() : 0;
                    if (bestDay / totalProf <= consistencyLimit)
                    {
                        passedEval = true;
                        finalAccountPnl = totalProf;
                        break;
                    }
                }
            }

            if (!passedEval && !evalBlown)
            {
                evalBlown = true;
                finalAccountPnl = balance - 50000;
                reason = "Tiempo límite de evaluación agotado (60 días)";
            }

            if (evalBlown)
            {
                finalStatus = "❌ QUEBRADA F1";
                phaseReached = "Fase 1 (Eval)";
                return new PropFirmAccountSimResult
                {
                    AccountNumber = accNum,
                    FinalStatus = finalStatus,
                    PhaseReached = phaseReached,
                    DaysTraded = totalDays,
                    TotalTrades = totalTrades,
                    AccountEquity = finalAccountPnl,
                    PayoutReceived = 0,
                    PassedEvaluation = false,
                    AchievedPayout = false,
                    Notes = reason
                };
            }
        }

        // --- FASE 2: CUENTA FONDEADA ---
        phaseReached = "Fase 2 (Fondeada)";
        double fBalance = 50000;
        double fPeak = 50000;
        double fMaxDd = planIdx == 5 ? 2500 : 2000;
        bool fIntradayTrailing = planIdx == 5;
        int winningDaysCount = 0;
        bool fundedBlown = false;
        bool hadUnlockedEarlyPayout = false;
        double maxUnlockedProfit = 0.0;
        var fundedDailyProfits = new List<double>();

        for (int day = 1; day <= 60; day++)
        {
            totalDays++;
            int tradesToday = 0;
            double dayPnl = 0.0;

            if (useBacktestTrades)
            {
                tradesToday = random.Next(1, 3);
                for (int t = 0; t < tradesToday; t++)
                {
                    var tr = backtestTrades![random.Next(backtestTrades.Count)];
                    dayPnl += tr.NetProfit;
                }
            }
            else if (useDubinsSavage)
            {
                tradesToday = 1;
                if (planIdx == 1) // Select Daily (Buffer $2,100)
                {
                    bool win1 = random.NextDouble() < 0.50;
                    dayPnl += win1 ? 745.60 : -754.40;
                }
                else if (planIdx == 3) // Lightning (20% consistencia)
                {
                    bool win1 = random.NextDouble() < 0.50;
                    dayPnl += win1 ? 385.60 : -394.40;
                }
                else if (chaseMaxPayout) // Modo 2: Buscar Tope Máximo ($5,000 de ganancia con bloques de $1,000)
                {
                    bool win1 = random.NextDouble() < 0.490;
                    dayPnl += win1 ? 1011.80 : -988.20;
                }
                else if (useSweetSpotF2) // Modo 0: Punto Dulce ($500 por día -> 1 NQ 100 ticks)
                {
                    bool win1 = random.NextDouble() < 0.50;
                    dayPnl += win1 ? 495.90 : -504.10;
                }
                else // Modo 1: Retiro Mínimo Seguro ($200 por día -> 1 NQ 40 ticks)
                {
                    bool win1 = random.NextDouble() < 0.50;
                    dayPnl += win1 ? 195.60 : -204.40;
                }
            }
            else
            {
                tradesToday++;
                bool win1 = random.NextDouble() < 0.50;
                if (win1)
                {
                    dayPnl += 196;
                }
                else
                {
                    dayPnl -= 204;
                    if (random.NextDouble() < 0.60)
                    {
                        tradesToday++;
                        bool win2 = random.NextDouble() < 0.50;
                        dayPnl += win2 ? 196 : -204;
                    }
                }
            }

            totalTrades += tradesToday;
            fBalance += dayPnl;
            fundedDailyProfits.Add(Math.Max(0, dayPnl));
            if (dayPnl >= 150) winningDaysCount++;

            if (winningDaysCount >= 5 && fBalance > 50200)
            {
                hadUnlockedEarlyPayout = true;
                maxUnlockedProfit = Math.Max(maxUnlockedProfit, fBalance - 50000);
            }

            if (fIntradayTrailing)
            {
                double intradayMfe = dayPnl > 0 ? dayPnl + random.Next(20, 80) : random.Next(0, 40);
                fPeak = Math.Max(fPeak, fBalance - dayPnl + intradayMfe);
                double lockThreshold = 50000 + 2600 + 100;
                double floor = fPeak >= lockThreshold ? 50100 : fPeak - fMaxDd;
                if (fBalance <= floor)
                {
                    fundedBlown = true;
                    finalAccountPnl = fBalance - 50000;
                    reason = hadUnlockedEarlyPayout && chaseMaxPayout
                        ? $"Quebró buscando Tope Máx (desperdició retiro con +${maxUnlockedProfit:N0})"
                        : "Quebró en Fondeada por Trailing Intradía";
                    break;
                }
            }
            else
            {
                fPeak = Math.Max(fPeak, fBalance);
                double lockThreshold = 50000 + fMaxDd + 100;
                double floor = fPeak >= lockThreshold ? 50100 : fPeak - fMaxDd;
                if (fBalance <= floor)
                {
                    fundedBlown = true;
                    finalAccountPnl = fBalance - 50000;
                    reason = hadUnlockedEarlyPayout && chaseMaxPayout
                        ? $"Quebró buscando Tope $55K (desperdició retiro con +${maxUnlockedProfit:N0})"
                        : $"Quebró en Fondeada por Drawdown EOD (-${fMaxDd:N0})";
                    break;
                }
            }

            // Payout check
            if (planIdx == 0) // Tradeify Select Flex 50K (Tope máximo $2,500 bruto -> $2,250 neto al 90%)
            {
                double targetBalanceF2 = chaseMaxPayout ? 55000.0 : 50100.0;
                if (winningDaysCount >= 5 && fBalance >= targetBalanceF2)
                {
                    achievedPayout = true;
                    double profit1 = fBalance - 50000;
                    double grossRequest1 = Math.Min(2500.0, profit1 * 0.50);
                    double payout1 = grossRequest1 * 0.90;
                    payout = payout1;
                    finalAccountPnl = profit1;

                    // Si estamos en Modo 0 o Modo 1 (Retiro al 5.º día), simulamos el 2.º Retiro Gratis (Free Roll) con el otro 50% que queda en la cuenta
                    if (useDubinsSavage && !chaseMaxPayout)
                    {
                        double remBalance = 50000 + (profit1 - grossRequest1); // El otro 50% queda como colchón sobre $50,000
                        int winDays2 = 0;
                        double payout2 = 0.0;
                        for (int d2 = 1; d2 <= 30; d2++)
                        {
                            totalDays++;
                            totalTrades++;
                            bool w2 = random.NextDouble() < 0.50;
                            double step2 = useSweetSpotF2 ? (w2 ? 495.90 : -504.10) : (w2 ? 295.60 : -304.40);
                            remBalance += step2;
                            if (step2 >= 150) winDays2++;
                            if (remBalance <= 50000) break; // Tocó el suelo de $50,000 en el Free Roll (el 1.er retiro ya está en el banco)
                            if (winDays2 >= 5 && remBalance > 50200)
                            {
                                double grossRequest2 = Math.Min(2500.0, (remBalance - 50000) * 0.50);
                                payout2 = grossRequest2 * 0.90;
                                finalAccountPnl = profit1 + (remBalance - (50000 + profit1 - grossRequest1));
                                break;
                            }
                        }

                        if (payout2 > 0)
                        {
                            payout = payout1 + payout2;
                            reason = $"2 Retiros: 1.º +${payout1:N0} + 2.º Free Roll +${payout2:N0} (Total: ${payout:N2})";
                        }
                        else
                        {
                            reason = $"1.er Retiro asegurado (+${payout1:N2}) · Free Roll cerró en BE ($50K)";
                        }
                    }
                    else
                    {
                        reason = chaseMaxPayout
                            ? $"Alcanzó Tope Máximo $55K (Saldo: ${fBalance:N0}, Retiro Máx: ${payout:N2})"
                            : $"Cumplió 5 días ganadores (Saldo: ${fBalance:N0}, Retiro 50%: ${payout:N2})";
                    }
                    break;
                }
            }
            else if (planIdx == 1) // Tradeify Select Daily
            {
                double targetDaily = chaseMaxPayout ? 54870.0 : 52100.0;
                if (fBalance >= targetDaily)
                {
                    achievedPayout = true;
                    payout = Math.Min(2500.0, fBalance - 52100) * 0.90;
                    finalAccountPnl = fBalance - 50000;
                    reason = $"Superó buffer de $52,100 (Retiró: ${payout:N2})";
                    break;
                }
            }
            else if (planIdx == 2) // Tradeify Growth
            {
                double targetGrowth = chaseMaxPayout ? 53000.0 : 52000.0;
                if (winningDaysCount >= 5 && fBalance >= targetGrowth)
                {
                    double prof = fBalance - 50000;
                    double best = fundedDailyProfits.Count > 0 ? fundedDailyProfits.Max() : 0;
                    if (best / prof <= 0.35)
                    {
                        achievedPayout = true;
                        payout = Math.Min(1500, prof * 0.50) * 0.90;
                        finalAccountPnl = prof;
                        reason = $"Cumplió 5 días ganadores y 35% consistencia (Retiro: ${payout:N2})";
                        break;
                    }
                }
            }
            else if (planIdx == 3) // Tradeify Lightning
            {
                if (fBalance >= 52000)
                {
                    double prof = fBalance - 50000;
                    double best = fundedDailyProfits.Count > 0 ? fundedDailyProfits.Max() : 0;
                    if (best / prof <= 0.20)
                    {
                        achievedPayout = true;
                        payout = Math.Min(1500, prof) * 0.90;
                        finalAccountPnl = prof;
                        reason = $"Cumplió meta y regla 20% consistencia (Retiro: ${payout:N2})";
                        break;
                    }
                }
            }
            else if (planIdx == 4) // Topstep 50K Express
            {
                double targetTopstep = chaseMaxPayout ? 54000.0 : 50100.0;
                if (winningDaysCount >= 5 && fBalance >= targetTopstep)
                {
                    achievedPayout = true;
                    double profit1 = fBalance - 50000;
                    double grossRequest1 = Math.Min(2000, profit1 * 0.50);
                    payout = grossRequest1 * 0.90;
                    finalAccountPnl = profit1;
                    reason = $"Cumplió 5 días ganadores de $150+ (Retiro 50%: ${payout:N2})";
                    break;
                }
            }
            else if (planIdx == 5) // Apex 50K
            {
                if (fBalance >= 52600 && day >= 10)
                {
                    achievedPayout = true;
                    payout = Math.Min(2000, fBalance - 52600) * 0.90;
                    finalAccountPnl = fBalance - 50000;
                    reason = $"Superó buffer de $52,600 con 10 días (Retiro: ${payout:N2})";
                    break;
                }
            }
        }

        if (achievedPayout)
        {
            finalStatus = "✅ RETIRÓ";
            phaseReached = "Cobro Completado";
        }
        else
        {
            finalStatus = "❌ QUEBRADA F2";
            phaseReached = "Fase 2 (Fondeada)";
            if (string.IsNullOrEmpty(reason))
                reason = fundedBlown ? "Quebró en cuenta fondeada por límite de drawdown" : "No logró calificar tras 60 días de fondeo";
        }

        return new PropFirmAccountSimResult
        {
            AccountNumber = accNum,
            FinalStatus = finalStatus,
            PhaseReached = phaseReached,
            DaysTraded = totalDays,
            TotalTrades = totalTrades,
            AccountEquity = finalAccountPnl,
            PayoutReceived = payout,
            PassedEvaluation = true,
            AchievedPayout = achievedPayout,
            Notes = reason
        };
    }

    private void PopulateTradesPage(int page)
    {
        if (btTradesRows is null || currentAllTrades.Count == 0)
        {
            if (btTradesRows is not null) btTradesRows.Children.Clear();
            if (btTradesPageInfoText is not null) btTradesPageInfoText.Text = "Mostrando operaciones: 0";
            return;
        }

        int totalTrades = currentAllTrades.Count;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalTrades / (double)TradesPageSize));
        tradesCurrentPage = Math.Clamp(page, 0, totalPages - 1);

        int startIdx = tradesCurrentPage * TradesPageSize;
        int count = Math.Min(TradesPageSize, totalTrades - startIdx);
        int endIdx = startIdx + count;

        btTradesRows.Children.Clear();

        for (int i = startIdx; i < endIdx; i++)
        {
            var tr = currentAllTrades[i];
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("45,75,130,90,130,90,95,105,120,75,110,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            bool isWin = tr.NetProfit > 0;
            bool isLong = string.Equals(tr.Side, "Long", StringComparison.OrdinalIgnoreCase);

            AddTableCell(rowGrid, 0, $"#{tr.TradeNumber}", "#71717A", true);
            AddTableCell(rowGrid, 1, $"{(isLong ? "LONG" : "SHORT")} x{tr.Quantity}", isLong ? "#00E599" : "#FF355E", true);
            AddTableCell(rowGrid, 2, tr.EntryTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "#A1A1AA");
            AddTableCell(rowGrid, 3, $"{tr.EntryPrice:N2}", "#FFFFFF");
            AddTableCell(rowGrid, 4, tr.ExitTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "#A1A1AA");
            AddTableCell(rowGrid, 5, $"{tr.ExitPrice:N2}", "#FFFFFF");
            AddTableCell(rowGrid, 6, $"{tr.Points:+0.00;-0.00} ({tr.Ticks:+0;-0}t)", isWin ? "#00E599" : "#FF355E");
            AddTableCell(rowGrid, 7, $"{tr.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}", isWin ? "#00E599" : "#FF355E", true);
            AddTableCell(rowGrid, 8, $"-${tr.MaxAdverseExcursionUsd:F0} / +${tr.MaxFavorableExcursionUsd:F0}", "#71717A");
            AddTableCell(rowGrid, 9, tr.RMultiple.HasValue ? $"{tr.RMultiple.Value:+0.00;-0.00}R" : "—", "#A1A1AA");
            AddTableCell(rowGrid, 10, $"{tr.CumulativeNetProfitUsd:+$#,##0.00;-$#,##0.00;$0.00}", tr.CumulativeNetProfitUsd >= 0 ? "#00E599" : "#FF355E", true);

            var actionStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actionStack.Children.Add(new TextBlock
            {
                Text = tr.ExitReason,
                Foreground = SolidColorBrush.Parse("#71717A"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            var jumpBtn = new Button
            {
                Content = "↗ Gráfico",
                Background = SolidColorBrush.Parse("#18181B"),
                Foreground = SolidColorBrush.Parse("#F4F4F5"),
                BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                BorderThickness = new Thickness(1),
                FontSize = 10.5,
                Padding = new Thickness(7, 2),
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            DateTime capturedEntry = tr.EntryTimeUtc;
            jumpBtn.Click += async (_, _) =>
            {
                fromBox.Text = capturedEntry.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                toBox.Text = capturedEntry.Date.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                SwitchPage(0);
                await LoadMarketDataAsync();
            };
            actionStack.Children.Add(jumpBtn);
            Grid.SetColumn(actionStack, 11);
            rowGrid.Children.Add(actionStack);

            btTradesRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse(i % 2 == 0 ? "#141416" : "#17171A"),
                BorderBrush = SolidColorBrush.Parse("#1F1F23"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 5),
                Child = rowGrid
            });
        }

        if (btTradesPageInfoText is not null)
        {
            btTradesPageInfoText.Text = $"Mostrando operaciones {startIdx + 1:N0} – {endIdx:N0} de {totalTrades:N0} (Página {tradesCurrentPage + 1} de {totalPages})";
        }

        if (btTradesPrevBtn is not null) btTradesPrevBtn.IsEnabled = tradesCurrentPage > 0;
        if (btTradesFirstBtn is not null) btTradesFirstBtn.IsEnabled = tradesCurrentPage > 0;
        if (btTradesNextBtn is not null) btTradesNextBtn.IsEnabled = tradesCurrentPage < totalPages - 1;
        if (btTradesLastBtn is not null) btTradesLastBtn.IsEnabled = tradesCurrentPage < totalPages - 1;
    }

    private static Button MakePageButton(string text) => new()
    {
        Content = text,
        Background = SolidColorBrush.Parse("#18181B"),
        Foreground = SolidColorBrush.Parse("#F4F4F5"),
        BorderBrush = SolidColorBrush.Parse("#3F3F46"),
        BorderThickness = new Thickness(1),
        FontSize = 11,
        Padding = new Thickness(10, 4),
        CornerRadius = new CornerRadius(5),
        Cursor = new Cursor(StandardCursorType.Hand)
    };

    private void PopulateNinjaTraderSummaryRows(BacktestSummary s)
    {
        if (btSummaryRows is null) return;
        btSummaryRows.Children.Clear();

        string FmtMoney(double val) => $"{val:+$#,##0.00;-$#,##0.00;$0.00}";
        string FmtColor(double val) => val >= 0 ? "#00E599" : "#FF355E";

        AddSummaryRow("Total Net Profit (Beneficio Neto)", FmtMoney(s.NetProfit), FmtMoney(s.LongNetProfitUsd), FmtMoney(s.ShortNetProfitUsd), FmtColor(s.NetProfit), FmtColor(s.LongNetProfitUsd), FmtColor(s.ShortNetProfitUsd));
        AddSummaryRow("Gross Profit (Ganancia Bruta)", FmtMoney(s.GrossProfit), "—", "—", "#00E599");
        AddSummaryRow("Gross Loss (Pérdida Bruta)", FmtMoney(-Math.Abs(s.GrossLossesUsd)), "—", "—", "#FF355E");
        AddSummaryRow("Commission (Comisiones)", $"${s.ClosedTradeCommissions:#,##0.00}", "—", "—", "#A1A1AA");
        AddSummaryRow("Profit Factor", s.ProfitFactor.HasValue ? s.ProfitFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A", "—", "—", s.ProfitFactor >= 1.0 ? "#00E599" : "#FF355E");
        AddSummaryRow("Payoff Ratio (Ratio Gan/Pérd)", s.PayoffRatio.HasValue ? s.PayoffRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A", "—", "—");
        AddSummaryRow("Expectancy (Esperanza / Trade)", $"{s.ExpectancyUsd:+$0.00;-$0.00;$0.00}", "—", "—", FmtColor(s.ExpectancyUsd));
        AddSummaryRow("Average R-Multiple", s.AverageRMultiple.HasValue ? $"{s.AverageRMultiple.Value:+0.00;-0.00;0.00}R" : "N/A", "—", "—", FmtColor(s.AverageRMultiple ?? 0));

        AddSummarySeparator();

        AddSummaryRow("Total Trades (Total Operaciones)", $"{s.ClosedTrades}", $"{s.LongTrades}", $"{s.ShortTrades}");
        int longWins = s.LongTrades > 0 ? (int)Math.Round(s.LongTrades * s.LongWinRate) : 0;
        int shortWins = s.ShortTrades > 0 ? (int)Math.Round(s.ShortTrades * s.ShortWinRate) : 0;
        int longLosses = s.LongTrades - longWins;
        int shortLosses = s.ShortTrades - shortWins;

        AddSummaryRow("Winning Trades (Ganadoras)", $"{s.WinningTrades} ({s.WinRate * 100:F1}%)", $"{longWins} ({s.LongWinRate * 100:F1}%)", $"{shortWins} ({s.ShortWinRate * 100:F1}%)", "#00E599", "#00E599", "#00E599");
        AddSummaryRow("Losing Trades (Perdedoras)", $"{s.LosingTrades} ({(1 - s.WinRate) * 100:F1}%)", $"{longLosses}", $"{shortLosses}", "#FF355E", "#FF355E", "#FF355E");
        AddSummaryRow("Breakeven Trades", $"{s.BreakevenTrades}", "—", "—");
        AddSummaryRow("Win Rate (% Acierto)", $"{s.WinRate * 100:F2}%", $"{s.LongWinRate * 100:F2}%", $"{s.ShortWinRate * 100:F2}%", s.WinRate >= 0.5 ? "#00E599" : "#FF355E", s.LongWinRate >= 0.5 ? "#00E599" : "#FF355E", s.ShortWinRate >= 0.5 ? "#00E599" : "#FF355E");

        AddSummarySeparator();

        AddSummaryRow("Average Trade (Trade Promedio)", FmtMoney(s.ClosedTrades > 0 ? s.NetProfit / s.ClosedTrades : 0), "—", "—", FmtColor(s.NetProfit));
        AddSummaryRow("Average Win (Ganancia Promedio)", FmtMoney(s.AverageWinUsd), "—", "—", "#00E599");
        AddSummaryRow("Average Loss (Pérdida Promedio)", FmtMoney(s.AverageLossUsd), "—", "—", "#FF355E");
        AddSummaryRow("Largest Winning Trade (Ganancia Mayor)", FmtMoney(s.LargestWinUsd), "—", "—", "#00E599");
        AddSummaryRow("Largest Losing Trade (Pérdida Mayor)", FmtMoney(s.LargestLossUsd), "—", "—", "#FF355E");
        AddSummaryRow("Max Consecutive Winners (Racha Ganadora)", $"{s.MaxConsecutiveWins}", "—", "—", "#00E599");
        AddSummaryRow("Max Consecutive Losers (Racha Perdedora)", $"{s.MaxConsecutiveLosses}", "—", "—", "#FF355E");
        AddSummaryRow("Average Trade Duration (Duración Media)", $"{s.AverageTradeDurationMinutes:F1} min", "—", "—");

        AddSummarySeparator();

        AddSummaryRow("Max Drawdown (Cerrado)", $"${s.MaxClosedTradeDrawdown:#,##0.00}", "—", "—", "#FF355E");
        AddSummaryRow("Max Intraday Trailing Drawdown", $"${s.MaxIntradayTrailingDrawdown:#,##0.00}", "—", "—", "#FF355E");
        AddSummaryRow("Average MAE (Excursión Adversa Media)", $"${s.AverageMaeUsd:#,##0.00}", "—", "—");
        AddSummaryRow("Average MFE (Excursión Favorable Media)", $"${s.AverageMfeUsd:#,##0.00}", "—", "—");

        AddSummarySeparator();

        AddSummaryRow("Sharpe Ratio (Anualizado)", s.SharpeRatio.HasValue ? s.SharpeRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A", "—", "—");
        AddSummaryRow("Sortino Ratio (Anualizado)", s.SortinoRatio.HasValue ? s.SortinoRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A", "—", "—");
        AddSummaryRow("Recovery Factor", s.RecoveryFactor.HasValue ? s.RecoveryFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A", "—", "—");
        AddSummaryRow("Trading Days (Días Operados)", $"{s.TradingDays} días (Ganadores: {s.WinningDays} [{s.DailyWinRate * 100:F1}%], Perdedores: {s.LosingDays})", "—", "—");
        AddSummaryRow("Best Day (Mejor Día)", FmtMoney(s.BestDayNetProfitUsd), "—", "—", "#00E599");
        AddSummaryRow("Worst Day (Peor Día)", FmtMoney(s.WorstDayNetProfitUsd), "—", "—", "#FF355E");
    }

    private void AddSummaryRow(string label, string allVal, string longVal, string shortVal, string? allHex = null, string? longHex = null, string? shortHex = null)
    {
        if (btSummaryRows is null) return;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("340,160,160,160"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        void AddCol(int col, string val, string? hex)
        {
            var tb = new TextBlock
            {
                Text = val,
                Foreground = SolidColorBrush.Parse(hex ?? "#F4F4F5"),
                FontWeight = FontWeight.SemiBold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        AddCol(1, allVal, allHex);
        AddCol(2, longVal, longHex);
        AddCol(3, shortVal, shortHex);

        btSummaryRows.Children.Add(new Border
        {
            Background = SolidColorBrush.Parse(btSummaryRows.Children.Count % 2 == 0 ? "#121214" : "#151518"),
            BorderBrush = SolidColorBrush.Parse("#1F1F23"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 6),
            Child = grid
        });
    }

    private void AddSummarySeparator()
    {
        if (btSummaryRows is null) return;
        btSummaryRows.Children.Add(new Border
        {
            Height = 1,
            Background = SolidColorBrush.Parse("#27272A"),
            Margin = new Thickness(0, 4, 0, 4)
        });
    }

    private static string GetSpanishMonthAbbrev(int month) => month switch
    {
        3 => "Mar",
        6 => "Jun",
        9 => "Sep",
        12 => "Dic",
        _ => month.ToString("00", CultureInfo.InvariantCulture)
    };

    internal static void AddTableHeader(Grid g, int col, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    internal static void AddTableCell(Grid g, int col, string text, string fgHex, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = SolidColorBrush.Parse(fgHex),
            FontSize = 11.5,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    private static Border MakeSummaryCard(string title, string mainValue, string subtitle, string accentHex)
    {
        return new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = SolidColorBrush.Parse("#71717A"),
                        FontSize = 10.5,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = mainValue,
                        Foreground = SolidColorBrush.Parse(accentHex),
                        FontSize = 19,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Control BuildChartContainerContent(
        ChartSurface surface,
        Slider scrubber,
        out TextBlock positionBadge,
        out Button playBtn,
        out Button modeBtn,
        out Button spdBtn,
        out Button stepBackBtn,
        out Button stepFwdBtn)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        // 1. Cabecera del gráfico con leyenda y botones de zoom
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleStack.Children.Add(new TextBlock
        {
            Text = "GRÁFICO DE VELAS (OHLC) Y VOLUMEN",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "● Alcista (#00E599)   ● Bajista (#FF355E)   ● Línea punteada: Último precio",
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        headerGrid.Children.Add(titleStack);

        var zoomStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        zoomStack.Children.Add(new TextBlock
        {
            Text = "Zoom:",
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        var zoomOutBtn = MakeQuickPillButton("−");
        zoomOutBtn.Width = 26;
        ToolTip.SetTip(zoomOutBtn, "Alejar velas (Zoom Out)");
        zoomOutBtn.Click += (_, _) => surface.ZoomStep(1.25);
        zoomStack.Children.Add(zoomOutBtn);

        var zoomInBtn = MakeQuickPillButton("+");
        zoomInBtn.Width = 26;
        ToolTip.SetTip(zoomInBtn, "Acercar velas (Zoom In)");
        zoomInBtn.Click += (_, _) => surface.ZoomStep(0.80);
        zoomStack.Children.Add(zoomInBtn);

        foreach (int count in new[] { 60, 120, 240 })
        {
            var btn = MakeQuickPillButton($"{count}v");
            int captured = count;
            btn.Click += (_, _) => surface.SetVisibleBarCount(captured);
            zoomStack.Children.Add(btn);
        }

        // Botón F (NinjaTrader Auto-Scale / Fit to Screen)
        var fBtn = new Button
        {
            Content = "F",
            FontWeight = FontWeight.Bold,
            FontSize = 11.5,
            Padding = new Thickness(9, 3),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ToolTip.SetTip(fBtn, "NinjaTrader Auto-Scale / Fit to Screen (Tecla 'F' o doble clic en eje de precios)");
        fBtn.Click += (_, _) => surface.ToggleAutoScale();

        surface.AutoScaleChanged += isAuto =>
        {
            if (isAuto)
            {
                fBtn.Background = SolidColorBrush.Parse("#FFFFFF");
                fBtn.Foreground = SolidColorBrush.Parse("#09090B");
                fBtn.Content = "F";
            }
            else
            {
                fBtn.Background = SolidColorBrush.Parse("#161618");
                fBtn.Foreground = SolidColorBrush.Parse("#E4E4E7");
                fBtn.Content = "F*";
            }
        };

        zoomStack.Children.Add(fBtn);
        Grid.SetColumn(zoomStack, 1);
        headerGrid.Children.Add(zoomStack);

        Grid.SetRow(headerGrid, 0);
        grid.Children.Add(headerGrid);

        // 2. Lienzo del Gráfico (Superficie interactiva)
        Grid.SetRow(surface, 1);
        grid.Children.Add(surface);

        // 3. Barra del tiempo minimalista integrada dentro del gráfico
        var timelineBar = BuildMinimalTimelineBar(
            surface,
            scrubber,
            out positionBadge,
            out playBtn,
            out modeBtn,
            out spdBtn,
            out stepBackBtn,
            out stepFwdBtn);
        Grid.SetRow(timelineBar, 2);
        grid.Children.Add(timelineBar);

        return grid;
    }

    private static Control BuildMinimalTimelineBar(
        ChartSurface surface,
        Slider scrubber,
        out TextBlock positionBadge,
        out Button playBtn,
        out Button modeBtn,
        out Button spdBtn,
        out Button stepBackBtn,
        out Button stepFwdBtn)
    {
        var barGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 1. Botón Play / Detener
        playBtn = new Button
        {
            Content = "▶ Play",
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.Bold,
            FontSize = 11.5,
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // 2. Botón Selector de Modo de Replay (Velas vs Tick Replay)
        modeBtn = new Button
        {
            Content = "🕯 Velas",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(5, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(modeBtn, "Modo de Replay: '🕯 Velas' (barra por barra) o '⚡ Ticks' (tick a tick intra-vela)");

        // 3. Velocidad de reproducción (1x, 2x, 5x)
        spdBtn = new Button
        {
            Content = "1x",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(5, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // 4. Paso de 1 unidad atrás
        stepBackBtn = new Button
        {
            Content = "◂",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(5, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // 5. Paso de 1 unidad adelante
        stepFwdBtn = new Button
        {
            Content = "▸",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 0, 6, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // 6. Slider de línea de tiempo (scrubber)

        // 7. Badge de posición temporal / tick info
        positionBadge = new TextBlock
        {
            Text = "— / —",
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0)
        };

        // 8. Botón de salto al inicio
        var startBtn = new Button
        {
            Content = "⇤",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(2, 0, 0, 0)
        };
        ToolTip.SetTip(startBtn, "Saltar a la primera vela (Inicio)");
        startBtn.Click += (_, _) => surface.JumpToStart();

        // 9. Botón de salto al final (Actual)
        var actualBtn = new Button
        {
            Content = "⇥",
            Background = SolidColorBrush.Parse("#161618"),
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ToolTip.SetTip(actualBtn, "Saltar a la última vela (Fin / Actual)");
        actualBtn.Click += (_, _) => surface.JumpToEnd();

        Grid.SetColumn(playBtn, 0);
        Grid.SetColumn(modeBtn, 1);
        Grid.SetColumn(spdBtn, 2);
        Grid.SetColumn(stepBackBtn, 3);
        Grid.SetColumn(stepFwdBtn, 4);
        Grid.SetColumn(scrubber, 5);
        Grid.SetColumn(positionBadge, 6);
        Grid.SetColumn(startBtn, 7);
        Grid.SetColumn(actualBtn, 8);

        barGrid.Children.Add(playBtn);
        barGrid.Children.Add(modeBtn);
        barGrid.Children.Add(spdBtn);
        barGrid.Children.Add(stepBackBtn);
        barGrid.Children.Add(stepFwdBtn);
        barGrid.Children.Add(scrubber);
        barGrid.Children.Add(positionBadge);
        barGrid.Children.Add(startBtn);
        barGrid.Children.Add(actualBtn);

        return new Border
        {
            Background = SolidColorBrush.Parse("#000000"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 8, 0, 0),
            Child = barGrid
        };
    }

    private int GetPlaybackIntervalMs()
    {
        if (isTickReplayMode)
        {
            return playbackSpeedMultiplier switch
            {
                2 => 30, // Rápido: ~33 ticks/seg
                5 => 12, // Ultra: ~80 ticks/seg
                _ => 65  // Normal: ~15 ticks/seg (visualmente óptimo para ver la vela)
            };
        }

        return playbackSpeedMultiplier switch
        {
            2 => 90,
            5 => 35,
            _ => 180
        };
    }

    private void ToggleReplayMode()
    {
        isTickReplayMode = !isTickReplayMode;
        chartSurface.ClearLiveTick();
        currentCandleTicks = null;
        currentTickIndex = 0;

        UpdateReplayModeUi();

        if (playbackTimer != null && isPlaying)
        {
            playbackTimer.Interval = TimeSpan.FromMilliseconds(GetPlaybackIntervalMs());
        }
    }

    private void UpdateReplayModeUi()
    {
        if (replayModeBtn == null) return;

        if (isTickReplayMode)
        {
            replayModeBtn.Content = "⚡ Ticks";
            replayModeBtn.Background = SolidColorBrush.Parse("#FFFFFF");
            replayModeBtn.Foreground = SolidColorBrush.Parse("#09090B");
            ToolTip.SetTip(replayModeBtn, "Modo TICK REPLAY activo (reproducción tick a tick intra-vela). Clic para volver a modo Velas.");
            if (stepFwdBtn != null) stepFwdBtn.Content = "1t ▸";
            if (stepBackBtn != null) stepBackBtn.Content = "◂ 1t";
        }
        else
        {
            replayModeBtn.Content = "🕯 Velas";
            replayModeBtn.Background = SolidColorBrush.Parse("#161618");
            replayModeBtn.Foreground = SolidColorBrush.Parse("#FFFFFF");
            ToolTip.SetTip(replayModeBtn, "Modo Replay por Velas. Clic para activar Tick Replay intra-vela.");
            if (stepFwdBtn != null) stepFwdBtn.Content = "▸";
            if (stepBackBtn != null) stepBackBtn.Content = "◂";
        }
    }

    private void StepReplay(int direction)
    {
        if (isTickReplayMode)
            StepTick(direction);
        else
            chartSurface.PanByBars(direction);
    }

    private void StepTick(int delta)
    {
        if (chartSurface == null || chartSurface.TotalBars == 0) return;

        int currentBarIdx = chartSurface.RightEdgeIndex;

        if (currentCandleTicks == null || currentCandleTicks.Count == 0)
        {
            currentCandleTicks = GetTicksForBar(currentBarIdx);
            currentTickIndex = delta > 0 ? 0 : Math.Max(0, currentCandleTicks.Count - 1);
        }

        if (currentCandleTicks == null || currentCandleTicks.Count == 0)
        {
            chartSurface.PanByBars(delta);
            return;
        }

        int nextTick = currentTickIndex + delta;

        if (nextTick >= currentCandleTicks.Count)
        {
            chartSurface.ClearLiveTick();
            if (chartSurface.CanPanForward)
            {
                chartSurface.PanByBars(1);
                currentBarIdx = chartSurface.RightEdgeIndex;
                currentCandleTicks = GetTicksForBar(currentBarIdx);
                currentTickIndex = 0;
                if (currentCandleTicks.Count > 0)
                {
                    ApplyTickToFormingBar(currentBarIdx, currentTickIndex);
                }
            }
            else
            {
                StopPlayback();
            }
            return;
        }

        if (nextTick < 0)
        {
            chartSurface.ClearLiveTick();
            if (currentBarIdx > 0)
            {
                chartSurface.PanByBars(-1);
                currentBarIdx = chartSurface.RightEdgeIndex;
                currentCandleTicks = GetTicksForBar(currentBarIdx);
                currentTickIndex = Math.Max(0, currentCandleTicks.Count - 1);
                if (currentCandleTicks.Count > 0)
                {
                    ApplyTickToFormingBar(currentBarIdx, currentTickIndex);
                }
            }
            return;
        }

        currentTickIndex = nextTick;
        ApplyTickToFormingBar(currentBarIdx, currentTickIndex);
    }

    private void ApplyTickToFormingBar(int barIdx, int tickIdx)
    {
        if (currentCandleTicks == null || tickIdx < 0 || tickIdx >= currentCandleTicks.Count) return;

        var barBase = chartSurface.GetBar(barIdx);
        DateTime closeTime = barBase?.CloseTimeUtc ?? currentCandleTicks[tickIdx].TimeUtc;

        double open = currentCandleTicks[0].Price;
        double high = open;
        double low = open;
        double close = open;
        long vol = 0;
        long deltaVol = 0;

        for (int i = 0; i <= tickIdx; i++)
        {
            var t = currentCandleTicks[i];
            if (t.Price > high) high = t.Price;
            if (t.Price < low) low = t.Price;
            close = t.Price;
            vol += t.Volume;
            deltaVol += t.SignedDeltaVolume;
        }

        var formingBar = new Bar(
            closeTime,
            open,
            high,
            low,
            close,
            vol,
            deltaVol,
            0,
            0,
            tickIdx + 1);

        var activeTick = currentCandleTicks[tickIdx];
        chartSurface.SetLiveTick(formingBar, activeTick, tickIdx, currentCandleTicks.Count);

        if (timelinePositionBadge != null)
        {
            timelinePositionBadge.Text = $"⚡ Tick {tickIdx + 1}/{currentCandleTicks.Count} · P: {activeTick.Price:F2} · Vela {barIdx + 1}/{chartSurface.TotalBars}";
        }
    }

    private List<Tick> GetTicksForBar(int barIndex)
    {
        if (barIndex < 0 || chartSurface == null) return new List<Tick>();

        // Caso 1: Esquema TBBO (Ticks L1 reales Databento)
        if (IsTbboMode && rawTicks.Count > 0)
        {
            int ticksPerBar = timeframeBox.SelectedIndex switch
            {
                0 => 100,
                1 => 250,
                2 => 500,
                3 => 1000,
                _ => 0
            };

            if (ticksPerBar > 0)
            {
                int startTick = barIndex * ticksPerBar;
                if (startTick < rawTicks.Count)
                {
                    int takeCount = Math.Min(ticksPerBar, rawTicks.Count - startTick);
                    var list = new List<Tick>(takeCount);
                    for (int i = 0; i < takeCount; i++)
                    {
                        list.Add(rawTicks[startTick + i]);
                    }
                    return list;
                }
            }
            else
            {
                var bars = chartSurface.Bars;
                if (barIndex < bars.Count)
                {
                    var bar = bars[barIndex];
                    DateTime barClose = bar.CloseTimeUtc;
                    DateTime barOpen = barIndex > 0 ? bars[barIndex - 1].CloseTimeUtc : DateTime.MinValue;

                    var ticks = new List<Tick>();
                    for (int i = 0; i < rawTicks.Count; i++)
                    {
                        var t = rawTicks[i];
                        if (t.TimeUtc > barOpen && t.TimeUtc <= barClose)
                        {
                            ticks.Add(t);
                        }
                        else if (t.TimeUtc > barClose && ticks.Count > 0)
                        {
                            break;
                        }
                    }

                    if (ticks.Count > 0) return ticks;
                }
            }
        }

        // Caso 2: Esquema OHLCV o fallback (Sintetizar micro-ticks reales de 0.25 pts NQ)
        var currentBar = chartSurface.GetBar(barIndex);
        if (currentBar != null)
        {
            return GenerateMicroTicksForBar(currentBar);
        }

        return new List<Tick>();
    }

    private static List<Tick> GenerateMicroTicksForBar(Bar b)
    {
        var ticks = new List<Tick>();
        const double tickSize = 0.25;
        DateTime baseTime = b.CloseTimeUtc.AddMinutes(-1);

        if (Math.Abs(b.High - b.Low) < 0.01)
        {
            ticks.Add(new Tick(b.CloseTimeUtc, b.Close, b.Close - tickSize, b.Close + tickSize, Math.Max(1, b.Volume)));
            return ticks;
        }

        var keyPoints = new List<double> { b.Open };
        if (b.Close >= b.Open)
        {
            if (b.Low < b.Open) keyPoints.Add(b.Low);
            if (b.High > b.Low) keyPoints.Add(b.High);
            keyPoints.Add(b.Close);
        }
        else
        {
            if (b.High > b.Open) keyPoints.Add(b.High);
            if (b.Low < b.High) keyPoints.Add(b.Low);
            keyPoints.Add(b.Close);
        }

        var prices = new List<double> { keyPoints[0] };
        for (int k = 0; k < keyPoints.Count - 1; k++)
        {
            double fromP = keyPoints[k];
            double toP = keyPoints[k + 1];
            double diff = toP - fromP;
            int steps = Math.Max(1, (int)Math.Round(Math.Abs(diff) / tickSize));
            double stepSize = diff / steps;

            for (int s = 1; s <= steps; s++)
            {
                double p = Math.Round((fromP + s * stepSize) / tickSize) * tickSize;
                prices.Add(p);
            }
        }

        long totalVol = Math.Max(prices.Count, b.Volume);
        long volPerTick = Math.Max(1, totalVol / prices.Count);
        double timeStepMs = 60000.0 / Math.Max(1, prices.Count);

        for (int i = 0; i < prices.Count; i++)
        {
            double p = prices[i];
            DateTime tTime = baseTime.AddMilliseconds(i * timeStepMs);
            double bid = p - tickSize;
            double ask = p + tickSize;
            ticks.Add(new Tick(tTime, p, bid, ask, volPerTick));
        }

        return ticks;
    }

    private void TogglePlayback()
    {
        if (isPlaying)
            StopPlayback();
        else
            StartPlayback();
    }

    private void StartPlayback()
    {
        if (chartSurface == null || chartSurface.TotalBars == 0) return;

        if (!chartSurface.CanPanForward)
        {
            int rewindCount = isTickReplayMode ? Math.Min(5, chartSurface.TotalBars) : Math.Min(60, chartSurface.VisibleBarCount);
            chartSurface.PanByBars(-rewindCount);
            currentCandleTicks = null;
            currentTickIndex = 0;
        }

        isPlaying = true;
        UpdatePlayButtonUi();

        if (playbackTimer == null)
        {
            playbackTimer = new DispatcherTimer();
            playbackTimer.Tick += (_, _) =>
            {
                if (!isPlaying || chartSurface == null)
                {
                    StopPlayback();
                    return;
                }

                if (isTickReplayMode)
                {
                    StepTick(1);
                }
                else
                {
                    if (chartSurface.CanPanForward)
                    {
                        chartSurface.PanByBars(1);
                    }
                    else
                    {
                        StopPlayback();
                    }
                }
            };
        }

        playbackTimer.Interval = TimeSpan.FromMilliseconds(GetPlaybackIntervalMs());
        playbackTimer.Start();
    }

    private void StopPlayback()
    {
        if (!isPlaying && (playbackTimer == null || !playbackTimer.IsEnabled)) return;
        isPlaying = false;
        playbackTimer?.Stop();
        UpdatePlayButtonUi();
    }

    private void CyclePlaybackSpeed()
    {
        playbackSpeedMultiplier = playbackSpeedMultiplier switch
        {
            1 => 2,
            2 => 5,
            _ => 1
        };

        if (speedBtn != null)
        {
            speedBtn.Content = $"{playbackSpeedMultiplier}x";
        }

        if (playbackTimer != null && isPlaying)
        {
            playbackTimer.Interval = TimeSpan.FromMilliseconds(GetPlaybackIntervalMs());
        }
    }

    private void UpdatePlayButtonUi()
    {
        if (playPauseBtn == null) return;

        if (isPlaying)
        {
            playPauseBtn.Content = "⏹ Detener";
            playPauseBtn.Background = SolidColorBrush.Parse("#FF355E");
            playPauseBtn.Foreground = SolidColorBrush.Parse("#FFFFFF");
        }
        else
        {
            playPauseBtn.Content = "▶ Play";
            playPauseBtn.Background = SolidColorBrush.Parse("#FFFFFF");
            playPauseBtn.Foreground = SolidColorBrush.Parse("#09090B");
        }
    }

    private void OnChartViewportChanged(int rightEdgeIndex, int totalBars)
    {
        if (!isPlaying && chartSurface != null)
        {
            chartSurface.ClearLiveTick();
            currentCandleTicks = null;
            currentTickIndex = 0;
        }

        suppressSliderEvents = true;
        try
        {
            timelineSlider.Minimum = 0;
            timelineSlider.Maximum = Math.Max(0, totalBars - 1);
            timelineSlider.Value = Math.Clamp(rightEdgeIndex, timelineSlider.Minimum, timelineSlider.Maximum);

            if (timelinePositionBadge != null)
            {
                if (totalBars == 0)
                {
                    timelinePositionBadge.Text = "0 / 0 velas";
                }
                else
                {
                    int currentIdx = Math.Clamp(rightEdgeIndex, 0, totalBars - 1);
                    var bar = chartSurface?.GetBar(currentIdx);
                    if (bar != null)
                    {
                        timelinePositionBadge.Text = $"Vela {currentIdx + 1:N0} de {totalBars:N0} · {bar.CloseTimeUtc:yyyy-MM-dd HH:mm} UTC";
                    }
                    else
                    {
                        timelinePositionBadge.Text = $"Vela {currentIdx + 1:N0} de {totalBars:N0}";
                    }
                }
            }
        }
        finally
        {
            suppressSliderEvents = false;
        }
    }

    private static readonly DateTime MinDatasetDate = new(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxOhlcvDatasetDate = new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime MaxTbboDatasetDate => DatasetDateCatalog.LastDate(true);
    private static string TbboYearRange => $"{DatasetDateCatalog.FirstDate(true).Year}–{MaxTbboDatasetDate.Year}";
    private static string TbboCoverage => $"Cobertura TBBO: {DatasetDateCatalog.FirstDate(true):yyyy-MM-dd} a {MaxTbboDatasetDate:yyyy-MM-dd} UTC";

    private DateTime CurrentMaxDatasetDate => IsTbboMode ? MaxTbboDatasetDate : MaxOhlcvDatasetDate;

    private async Task OnSchemaChangedAsync()
    {
        suppressTimeframeEvents = true;
        try
        {
            if (IsTbboMode)
            {
                schemaCoverageText.Text = TbboCoverage;
                timeframeBox.ItemsSource = new[]
                {
                    "100 Ticks por vela (TBBO)",
                    "250 Ticks por vela (TBBO)",
                    "500 Ticks por vela (TBBO)",
                    "1,000 Ticks por vela (TBBO)",
                    "15 segundos (TBBO)",
                    "1 minuto (TBBO)",
                    "5 minutos (TBBO)",
                    "15 minutos (TBBO)",
                    "1 hora (TBBO)"
                };
                timeframeBox.SelectedIndex = 1; // 250 ticks por defecto

                DateTime? currentFrom = ParseOptionalUtc(fromBox.Text, false);
                if (!currentFrom.HasValue || currentFrom.Value > MaxTbboDatasetDate)
                {
                    fromBox.Text = "2010-06-07";
                    toBox.Text = "2010-06-07";
                    SyncDateDropdowns(MinDatasetDate);
                }
                else
                {
                    SyncDateDropdowns(currentFrom.Value);
                }
            }
            else
            {
                schemaCoverageText.Text = "Cobertura: 2010-06-07 a 2026-10-30 UTC (5.56M velas)";
                timeframeBox.ItemsSource = new[]
                {
                    "1 minuto (Original)",
                    "5 minutos",
                    "15 minutos",
                    "1 hora",
                    "4 horas",
                    "1 día (Diario)"
                };
                timeframeBox.SelectedIndex = 0;

                DateTime? currentFrom = ParseOptionalUtc(fromBox.Text, false);
                if (currentFrom.HasValue)
                {
                    SyncDateDropdowns(currentFrom.Value);
                }
                else
                {
                    SyncDateDropdowns(MaxOhlcvDatasetDate);
                }
            }
        }
        finally
        {
            suppressTimeframeEvents = false;
        }

        await LoadMarketDataAsync();
    }

    private void RebuildYearCombo(int? selectYear = null)
    {
        var yearOptions = new List<DateComboOption>();
        for (int y = 2026; y >= 2010; y--)
        {
            bool hasData = DatasetDateCatalog.HasYear(IsTbboMode, y);
            string label = hasData ? y.ToString(CultureInfo.InvariantCulture) : $"{y} · Sin datos";
            yearOptions.Add(new DateComboOption(label, hasData, y));
        }

        yearJumpBox.ItemsSource = yearOptions;
        int target = selectYear ?? (IsTbboMode ? 2010 : 2026);
        int idx = yearOptions.FindIndex(o => o.Value == target);
        yearJumpBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void RebuildMonthCombo(int year, int? selectMonth = null)
    {
        var monthOptions = new List<DateComboOption>
        {
            new("Todo el año", true, 0)
        };

        for (int m = 1; m <= 12; m++)
        {
            bool hasData = DatasetDateCatalog.HasMonth(IsTbboMode, year, m);
            string mName = MonthNameSpanish(m);
            string label = hasData ? $"{m:D2} · {mName}" : $"{m:D2} · {mName} · Sin datos";
            monthOptions.Add(new DateComboOption(label, hasData, m));
        }

        monthJumpBox.ItemsSource = monthOptions;
        int target = selectMonth ?? (year == 2026 && !IsTbboMode ? 10 : (year == 2010 && IsTbboMode ? 6 : 1));
        int idx = monthOptions.FindIndex(o => o.Value == target);
        monthJumpBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void RebuildDayCombo(int year, int month, int? selectDay = null)
    {
        var dayOptions = new List<DateComboOption>();

        if (month <= 0)
        {
            dayOptions.Add(new DateComboOption("Todo el año", true, 0));
            dayJumpBox.ItemsSource = dayOptions;
            dayJumpBox.SelectedIndex = 0;
            return;
        }

        bool monthHasData = DatasetDateCatalog.HasMonth(IsTbboMode, year, month);
        dayOptions.Add(new DateComboOption("Todo el mes", monthHasData, 0));

        int daysInMonth = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= daysInMonth; d++)
        {
            DateTime dt = new(year, month, d, 0, 0, 0, DateTimeKind.Utc);
            bool hasData = DatasetDateCatalog.HasDate(IsTbboMode, year, month, d);
            string dow = DayOfWeekSpanish(dt.DayOfWeek);
            string label = hasData ? $"{d:D2} · {dow}" : $"{d:D2} · {dow} · Sin datos";
            dayOptions.Add(new DateComboOption(label, hasData, d));
        }

        dayJumpBox.ItemsSource = dayOptions;

        int target = selectDay ?? (year == 2026 && month == 10 ? 30 : (year == 2010 && month == 6 && IsTbboMode ? 7 : 1));
        int idx = dayOptions.FindIndex(o => o.Value == target);
        dayJumpBox.SelectedIndex = idx >= 0 ? idx : (dayOptions.Count > 1 ? 1 : 0);
    }

    private void SyncDateDropdowns(DateTime date, bool wholeMonth = false)
    {
        suppressDateComboEvents = true;
        try
        {
            RebuildYearCombo(date.Year);
            RebuildMonthCombo(date.Year, date.Month);
            RebuildDayCombo(date.Year, date.Month, wholeMonth ? 0 : date.Day);
        }
        finally
        {
            suppressDateComboEvents = false;
        }
    }

    private void SyncYearMonthCombos(DateTime date, bool wholeMonth = false)
    {
        SyncDateDropdowns(date, wholeMonth);
    }

    private async Task UpdateDatesFromDropdownsAsync()
    {
        if (suppressDateComboEvents) return;

        var yearOpt = yearJumpBox.SelectedItem as DateComboOption;
        var monthOpt = monthJumpBox.SelectedItem as DateComboOption;
        var dayOpt = dayJumpBox.SelectedItem as DateComboOption;

        int year = yearOpt?.Value ?? (IsTbboMode ? 2010 : 2026);
        int month = monthOpt?.Value ?? (IsTbboMode ? 6 : 10);
        int day = dayOpt?.Value ?? (IsTbboMode ? 7 : 30);

        if (day > 0 && month > 0)
        {
            int maxDay = DateTime.DaysInMonth(year, month);
            int clampedDay = Math.Clamp(day, 1, maxDay);
            string dateStr = $"{year:D4}-{month:D2}-{clampedDay:D2}";
            fromBox.Text = dateStr;
            toBox.Text = dateStr;

            bool hasData = DatasetDateCatalog.HasDate(IsTbboMode, year, month, clampedDay);
            if (!hasData)
            {
                dateRangeStatusText.Text = $"○ {dateStr} · Sin datos (Mercado cerrado)";
                dateRangeStatusText.Foreground = SolidColorBrush.Parse("#71717A");
                ApplyNoDataUiState($"El día {dateStr} no contiene datos (mercado cerrado / fin de semana CME).");
                return;
            }

            dateRangeStatusText.Text = $"● {dateStr} UTC · Sesión activa con datos";
            dateRangeStatusText.Foreground = SolidColorBrush.Parse("#FFFFFF");
        }
        else if (month > 0)
        {
            int maxDay = DateTime.DaysInMonth(year, month);
            fromBox.Text = $"{year:D4}-{month:D2}-01";
            toBox.Text = $"{year:D4}-{month:D2}-{maxDay:D2}";

            bool hasData = DatasetDateCatalog.HasMonth(IsTbboMode, year, month);
            dateRangeStatusText.Text = hasData
                ? $"● {year:D4}-{month:D2} · Mes completo"
                : $"○ {year:D4}-{month:D2} · Sin datos en este mes";
            dateRangeStatusText.Foreground = SolidColorBrush.Parse(hasData ? "#FFFFFF" : "#71717A");

            if (!hasData)
            {
                ApplyNoDataUiState($"El mes {year:D4}-{month:D2} no contiene datos de mercado.");
                return;
            }
        }
        else
        {
            fromBox.Text = $"{year:D4}-01-01";
            toBox.Text = $"{year:D4}-12-31";
            dateRangeStatusText.Text = $"● {year:D4} · Año completo";
            dateRangeStatusText.Foreground = SolidColorBrush.Parse("#FFFFFF");
        }

        await LoadMarketDataAsync();
    }

    private async Task ShiftDateStepAsync(int direction)
    {
        DateTime current = ParseOptionalUtc(fromBox.Text, false)?.Date
            ?? (IsTbboMode ? new DateTime(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));

        DateTime target = current.AddDays(direction);
        if (target < MinDatasetDate) target = MinDatasetDate;
        if (target > CurrentMaxDatasetDate) target = CurrentMaxDatasetDate;

        SyncDateDropdowns(target);
        await UpdateDatesFromDropdownsAsync();
    }

    private void ApplyNoDataUiState(string explanation)
    {
        rawMinuteBars = Array.Empty<Bar>();
        rawTicks = Array.Empty<Tick>();
        chartSurface.SetBars(Array.Empty<Bar>(), "1m");
        infoBarsText.Text = "0 velas";
        infoRangeText.Text = "Sin datos en el rango seleccionado";
        infoPriceBoundsText.Text = "—";
        infoOrderFlowText.Text = "Disponible en esquema TBBO (Ticks L1)";
        statusBanner.Text = explanation;
        statusBanner.Foreground = SolidColorBrush.Parse("#71717A");
    }

    private static string MonthNameSpanish(int month) => month switch
    {
        1 => "Enero",
        2 => "Febrero",
        3 => "Marzo",
        4 => "Abril",
        5 => "Mayo",
        6 => "Junio",
        7 => "Julio",
        8 => "Agosto",
        9 => "Septiembre",
        10 => "Octubre",
        11 => "Noviembre",
        12 => "Diciembre",
        _ => string.Empty
    };

    private static string DayOfWeekSpanish(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "Lun",
        DayOfWeek.Tuesday => "Mar",
        DayOfWeek.Wednesday => "Mié",
        DayOfWeek.Thursday => "Jue",
        DayOfWeek.Friday => "Vie",
        DayOfWeek.Saturday => "Sáb",
        DayOfWeek.Sunday => "Dom",
        _ => string.Empty
    };

    private async Task LoadMarketDataAsync()
    {
        StopPlayback();
        if (isLoading) return;

        bool tbbo = IsTbboMode;
        string activePath = tbbo ? tbboDatasetPath : defaultDatasetPath;

        if (!Workspace.DatasetExists(activePath))
        {
            statusBanner.Text = $"No se encontraron los datos del esquema en: {activePath}";
            statusBanner.Foreground = SolidColorBrush.Parse("#FF355E");
            return;
        }

        isLoading = true;
        loadBtn.IsEnabled = false;
        loadBtn.Content = "Cargando datos...";
        statusBanner.Text = tbbo
            ? "Leyendo ticks TBBO (Last, Bid, Ask, Vol) desde disco..."
            : "Leyendo barras históricas de 1 minuto desde disco...";
        statusBanner.Foreground = SolidColorBrush.Parse("#A1A1AA");

        DateTime? fromUtc = ParseOptionalUtc(fromBox.Text, false);
        DateTime? toUtc = ParseOptionalUtc(toBox.Text, true);

        try
        {
            if (tbbo)
            {
                var loadedTicks = await Task.Run(() =>
                    new NinjaTraderTickImporter().Read(activePath, fromUtc, toUtc));
                rawTicks = loadedTicks;
                rawMinuteBars = Array.Empty<Bar>();
            }
            else
            {
                var loadedBars = await Task.Run(() =>
                    new NinjaTraderMinuteImporter().Read(activePath, fromUtc, toUtc));
                rawMinuteBars = loadedBars;
                rawTicks = Array.Empty<Tick>();
            }

            RebuildDisplayBars();
        }
        catch (DataValidationException)
        {
            ApplyNoDataUiState("La fecha seleccionada no contiene operaciones registradas (mercado cerrado / fin de semana CME).");
        }
        catch (Exception ex)
        {
            statusBanner.Text = $"Error al leer datos: {ex.Message}";
            statusBanner.Foreground = SolidColorBrush.Parse("#FF355E");
        }
        finally
        {
            isLoading = false;
            loadBtn.IsEnabled = true;
            loadBtn.Content = "Actualizar Gráfico";
        }
    }

    private void RebuildDisplayBars()
    {
        if (IsTbboMode)
        {
            RebuildDisplayBarsFromTbbo();
            return;
        }

        if (rawMinuteBars.Count == 0)
        {
            chartSurface.SetBars(Array.Empty<Bar>(), "1m");
            infoBarsText.Text = "0 velas";
            infoRangeText.Text = "Sin datos en el rango seleccionado";
            infoPriceBoundsText.Text = "—";
            infoOrderFlowText.Text = "Disponible en esquema TBBO (Ticks L1)";
            statusBanner.Text = "No se encontraron velas en el rango de fechas indicado.";
            statusBanner.Foreground = SolidColorBrush.Parse("#E4E4E7");
            return;
        }

        int minutesPerBar = timeframeBox.SelectedIndex switch
        {
            1 => 5,
            2 => 15,
            3 => 60,
            4 => 240,
            5 => 1440,
            _ => 1
        };

        string tfLabel = timeframeBox.SelectedIndex switch
        {
            1 => "5m",
            2 => "15m",
            3 => "1h",
            4 => "4h",
            5 => "1D",
            _ => "1m"
        };

        IReadOnlyList<Bar> displayBars = minutesPerBar == 1
            ? rawMinuteBars
            : ResampleBars(rawMinuteBars, minutesPerBar);

        double minPrice = displayBars.Min(b => b.Low);
        double maxPrice = displayBars.Max(b => b.High);
        DateTime firstUtc = displayBars[0].CloseTimeUtc;
        DateTime lastUtc = displayBars[displayBars.Count - 1].CloseTimeUtc;

        infoBarsText.Text = $"{displayBars.Count:N0} velas ({tfLabel}) · Base: {rawMinuteBars.Count:N0} (1m)";
        infoRangeText.Text = $"{firstUtc:yyyy-MM-dd HH:mm} UTC  →  {lastUtc:yyyy-MM-dd HH:mm} UTC";
        infoPriceBoundsText.Text = $"Mín: {minPrice:N2}  |  Máx: {maxPrice:N2}";
        infoOrderFlowText.Text = "Cambia a 'tbbo · Ticks L1' para ver Delta Bid/Ask";
        infoOrderFlowText.Foreground = SolidColorBrush.Parse("#71717A");

        headerSubtitle.Text = $"NQ (ohlcv-1m) · {displayBars.Count:N0} velas ({tfLabel}) · {firstUtc:yyyy-MM-dd} a {lastUtc:yyyy-MM-dd} UTC";
        statusBanner.Text = $"Datos cargados: {displayBars.Count:N0} velas de {tfLabel} listas para explorar.";
        statusBanner.Foreground = SolidColorBrush.Parse("#00E599");

        chartSurface.SetBars(displayBars, tfLabel);
    }

    private void RebuildDisplayBarsFromTbbo()
    {
        if (rawTicks.Count == 0)
        {
            chartSurface.SetBars(Array.Empty<Bar>(), "TBBO");
            infoBarsText.Text = "0 ticks";
            infoRangeText.Text = $"Sin ticks en el rango · {TbboCoverage}";
            infoPriceBoundsText.Text = "—";
            infoOrderFlowText.Text = "—";
            statusBanner.Text = $"No hay ticks TBBO en ese rango. {TbboCoverage}.";
            statusBanner.Foreground = SolidColorBrush.Parse("#E4E4E7");
            return;
        }

        (IReadOnlyList<Bar> displayBars, string tfLabel) = timeframeBox.SelectedIndex switch
        {
            0 => (BuildTickCountBars(rawTicks, 100), "100t"),
            1 => (BuildTickCountBars(rawTicks, 250), "250t"),
            2 => (BuildTickCountBars(rawTicks, 500), "500t"),
            3 => (BuildTickCountBars(rawTicks, 1000), "1000t"),
            4 => (BuildTimeSecondsBars(rawTicks, 15), "15s"),
            5 => (BuildTimeSecondsBars(rawTicks, 60), "1m"),
            6 => (BuildTimeSecondsBars(rawTicks, 300), "5m"),
            7 => (BuildTimeSecondsBars(rawTicks, 900), "15m"),
            8 => (BuildTimeSecondsBars(rawTicks, 3600), "1h"),
            _ => (BuildTickCountBars(rawTicks, 250), "250t")
        };

        long askBuyVol = 0;
        long bidSellVol = 0;
        for (int i = 0; i < rawTicks.Count; i++)
        {
            long d = rawTicks[i].SignedDeltaVolume;
            if (d > 0) askBuyVol += d;
            else if (d < 0) bidSellVol += -d;
        }
        long netDelta = askBuyVol - bidSellVol;
        string deltaSign = netDelta >= 0 ? "+" : "";

        double minPrice = displayBars.Min(b => b.Low);
        double maxPrice = displayBars.Max(b => b.High);
        DateTime firstUtc = rawTicks[0].TimeUtc;
        DateTime lastUtc = rawTicks[rawTicks.Count - 1].TimeUtc;

        infoBarsText.Text = $"{displayBars.Count:N0} velas ({tfLabel}) · {rawTicks.Count:N0} Ticks TBBO";
        infoRangeText.Text = $"{firstUtc:yyyy-MM-dd HH:mm:ss}  →  {lastUtc:yyyy-MM-dd HH:mm:ss} UTC";
        infoPriceBoundsText.Text = $"Mín: {minPrice:N2}  |  Máx: {maxPrice:N2}";
        infoOrderFlowText.Text = $"Delta: {deltaSign}{netDelta:N0}  (Ask: {askBuyVol:N0} | Bid: {bidSellVol:N0})";
        infoOrderFlowText.Foreground = SolidColorBrush.Parse(netDelta >= 0 ? "#00E599" : "#FF355E");

        headerSubtitle.Text = $"NQ (TBBO L1) · {rawTicks.Count:N0} ticks → {displayBars.Count:N0} velas ({tfLabel}) · Delta {deltaSign}{netDelta:N0}";
        statusBanner.Text = $"TBBO cargado: {rawTicks.Count:N0} ticks con Bid/Ask agrupados en {displayBars.Count:N0} velas ({tfLabel}).";
        statusBanner.Foreground = SolidColorBrush.Parse("#00E599");

        chartSurface.SetBars(displayBars, $"TBBO · {tfLabel}");
    }

    private static IReadOnlyList<Bar> BuildTickCountBars(IReadOnlyList<Tick> ticks, int ticksPerBar)
    {
        var result = new List<Bar>(ticks.Count / Math.Max(1, ticksPerBar) + 1);
        int countInBucket = 0;
        double open = 0, high = 0, low = 0, close = 0;
        long vol = 0;
        DateTime closeTime = default;

        for (int i = 0; i < ticks.Count; i++)
        {
            Tick t = ticks[i];
            if (countInBucket == 0)
            {
                open = high = low = close = t.Price;
                vol = t.Volume;
                closeTime = t.TimeUtc;
                countInBucket = 1;
            }
            else
            {
                if (t.Price > high) high = t.Price;
                if (t.Price < low) low = t.Price;
                close = t.Price;
                vol += t.Volume;
                closeTime = t.TimeUtc;
                countInBucket++;
            }

            if (countInBucket >= ticksPerBar)
            {
                result.Add(new Bar(closeTime, open, high, low, close, vol));
                countInBucket = 0;
            }
        }

        if (countInBucket > 0)
        {
            result.Add(new Bar(closeTime, open, high, low, close, vol));
        }

        return result;
    }

    private static IReadOnlyList<Bar> BuildTimeSecondsBars(IReadOnlyList<Tick> ticks, int periodSeconds)
    {
        var result = new List<Bar>();
        long bucketTicks = TimeSpan.FromSeconds(periodSeconds).Ticks;
        long currentBucket = -1;
        DateTime bucketClose = default;
        double open = 0, high = 0, low = 0, close = 0;
        long vol = 0;

        for (int i = 0; i < ticks.Count; i++)
        {
            Tick t = ticks[i];
            long bucket = t.TimeUtc.Ticks / bucketTicks;
            if (bucket != currentBucket)
            {
                if (currentBucket != -1)
                {
                    result.Add(new Bar(bucketClose, open, high, low, close, vol));
                }
                currentBucket = bucket;
                bucketClose = new DateTime((bucket + 1) * bucketTicks, DateTimeKind.Utc);
                open = high = low = close = t.Price;
                vol = t.Volume;
            }
            else
            {
                if (t.Price > high) high = t.Price;
                if (t.Price < low) low = t.Price;
                close = t.Price;
                vol += t.Volume;
            }
        }

        if (currentBucket != -1)
        {
            result.Add(new Bar(bucketClose, open, high, low, close, vol));
        }

        return result;
    }

    private static IReadOnlyList<Bar> ResampleBars(IReadOnlyList<Bar> source, int periodMinutes)
    {
        var result = new List<Bar>(source.Count / Math.Max(1, periodMinutes) + 1);
        long bucketTicks = TimeSpan.FromMinutes(periodMinutes).Ticks;

        long currentBucket = -1;
        DateTime bucketTime = default;
        double open = 0, high = 0, low = 0, close = 0;
        long volume = 0;

        for (int i = 0; i < source.Count; i++)
        {
            Bar b = source[i];
            long bucket = b.CloseTimeUtc.Ticks / bucketTicks;
            if (bucket != currentBucket)
            {
                if (currentBucket != -1)
                {
                    result.Add(new Bar(bucketTime, open, high, low, close, volume));
                }
                currentBucket = bucket;
                bucketTime = new DateTime((bucket + 1) * bucketTicks, DateTimeKind.Utc);
                open = b.Open;
                high = b.High;
                low = b.Low;
                close = b.Close;
                volume = b.Volume;
            }
            else
            {
                if (b.High > high) high = b.High;
                if (b.Low < low) low = b.Low;
                close = b.Close;
                volume += b.Volume;
            }
        }

        if (currentBucket != -1)
        {
            result.Add(new Bar(bucketTime, open, high, low, close, volume));
        }

        return result;
    }

    private static DateTime? ParseOptionalUtc(string? text, bool endOfDayIfDateOnly)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string raw = text.Trim();
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            return null;
        if (endOfDayIfDateOnly && raw.Length <= 10 && parsed.TimeOfDay == TimeSpan.Zero)
            return parsed.Date.AddDays(1).AddTicks(-1);
        return parsed;
    }

    private static Border MakeBadge(string text, string bgHex, string fgHex) => new()
    {
        Background = SolidColorBrush.Parse(bgHex),
        BorderBrush = SolidColorBrush.Parse(bgHex == "#18181B" ? "#FFFFFF" : bgHex),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(9, 4),
        Child = new TextBlock
        {
            Text = text,
            Foreground = SolidColorBrush.Parse(fgHex),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold
        }
    };

    private static Border MakeSidebarCard(string title, string accentHex, IEnumerable<Control> children)
    {
        var stack = new StackPanel { Spacing = 9 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(new Border
        {
            Width = 3,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            Background = SolidColorBrush.Parse(accentHex),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = SolidColorBrush.Parse("#FFFFFF"),
            FontWeight = FontWeight.Bold,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        foreach (var child in children)
            stack.Children.Add(child);

        return new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 11),
            Child = stack
        };
    }

    private static Grid TwoColumn(Control left, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private static Grid ThreeColumn(Control c1, Control c2, Control c3)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*,6,*") };
        Grid.SetColumn(c1, 0);
        Grid.SetColumn(c2, 2);
        Grid.SetColumn(c3, 4);
        g.Children.Add(c1);
        g.Children.Add(c2);
        g.Children.Add(c3);
        return g;
    }

    private static IDataTemplate MakeDateComboTemplate() =>
        new FuncDataTemplate<DateComboOption>((opt, _) =>
            new TextBlock
            {
                Text = opt != null ? opt.Label : string.Empty,
                Foreground = SolidColorBrush.Parse(opt != null && opt.HasData ? "#FFFFFF" : "#71717A"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            });

    private static TextBox MakeInput(string value, string? watermark = null) => new()
    {
        Text = value,
        Watermark = watermark,
        Background = SolidColorBrush.Parse("#161618"),
        Foreground = SolidColorBrush.Parse("#F4F4F5"),
        BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
        CornerRadius = new CornerRadius(6),
        FontSize = 12,
        Padding = new Thickness(8, 6)
    };

    private static Button MakeNavButton(string text) => new()
    {
        Content = text,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = SolidColorBrush.Parse("#121214"),
        Foreground = SolidColorBrush.Parse("#F4F4F5"),
        BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
        BorderThickness = new Thickness(1),
        FontSize = 11.5,
        FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(12, 6),
        CornerRadius = new CornerRadius(6)
    };

    private static Button MakeQuickPillButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = SolidColorBrush.Parse("#18181B"),
        Foreground = SolidColorBrush.Parse("#71717A"),
        FontSize = 11,
        Padding = new Thickness(6, 4),
        Margin = new Thickness(1),
        CornerRadius = new CornerRadius(5)
    };

    private static Control Labeled(string label, Control input) => new StackPanel
    {
        Spacing = 3,
        Children =
        {
            new TextBlock
            {
                Text = label,
                Foreground = SolidColorBrush.Parse("#71717A"),
                FontSize = 11
            },
            input
        }
    };
}

internal sealed class EquityCurveSurface : Control
{
    private IReadOnlyList<TradeRecord> trades = Array.Empty<TradeRecord>();
    private Point? hoverPos;

    public EquityCurveSurface()
    {
        ClipToBounds = true;
    }

    public void SetTrades(IReadOnlyList<TradeRecord> closedTrades)
    {
        trades = closedTrades ?? Array.Empty<TradeRecord>();
        hoverPos = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hoverPos = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverPos = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 120 || bounds.Height < 90) return;

        context.DrawRectangle(
            SolidColorBrush.Parse("#09090B"),
            new Pen(SolidColorBrush.Parse("#222225"), 1),
            bounds, 8, 8);

        if (trades.Count == 0)
        {
            var ft = new FormattedText(
                "Ejecuta un backtest para visualizar la curva de capital acumulado y drawdown intradía (Estilo NinjaTrader).",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, Segoe UI, Sans-Serif"),
                11.5,
                SolidColorBrush.Parse("#71717A"));
            context.DrawText(ft, new Point(20, bounds.Height / 2 - 10));
            return;
        }

        const double marginLeft = 20;
        const double marginRight = 95;
        const double marginTop = 26;
        const double marginBottom = 28;
        const double panelGap = 16;

        double plotW = Math.Max(60, bounds.Width - marginLeft - marginRight);
        double totalPlotH = Math.Max(70, bounds.Height - marginTop - marginBottom - panelGap);

        double upperH = Math.Round(totalPlotH * 0.70);
        double lowerH = Math.Max(25, totalPlotH - upperH);

        var upperPlot = new Rect(marginLeft, marginTop, plotW, upperH);
        var lowerPlot = new Rect(marginLeft, upperPlot.Bottom + panelGap, plotW, lowerH);

        // Precalcular picos, equidad acumulada y drawdown desde pico
        int n = trades.Count;
        var peaks = new double[n];
        var drawdowns = new double[n];

        double runningPeak = 0d;
        double minEq = 0d;
        double maxEq = 100d;
        double minDd = 0d;

        for (int i = 0; i < n; i++)
        {
            double eq = trades[i].CumulativeNetProfitUsd;
            double prevEq = i == 0 ? 0d : trades[i - 1].CumulativeNetProfitUsd;
            double tradePeak = prevEq + trades[i].MaxFavorableExcursionUsd - trades[i].Commission * 0.5;
            runningPeak = Math.Max(runningPeak, Math.Max(tradePeak, eq));
            peaks[i] = runningPeak;

            double dd = eq - runningPeak;
            drawdowns[i] = dd;

            minEq = Math.Min(minEq, Math.Min(eq, prevEq - trades[i].MaxAdverseExcursionUsd));
            maxEq = Math.Max(maxEq, runningPeak);
            minDd = Math.Min(minDd, dd);
        }

        if (minDd > -10d) minDd = -50d;
        double spanEq = Math.Max(50d, maxEq - minEq);
        double spanDd = Math.Max(10d, 0d - minDd);

        // --- 1. PANELES Y FONDOS ---
        var panelBg = SolidColorBrush.Parse("#0C0C0E");
        var panelBorder = new Pen(SolidColorBrush.Parse("#1A1A1E"), 1);
        context.DrawRectangle(panelBg, panelBorder, upperPlot, 4, 4);
        context.DrawRectangle(panelBg, panelBorder, lowerPlot, 4, 4);

        // --- 2. LÍNEAS GUÍA Y ESCALA Y (PANEL SUPERIOR: CAPITAL) ---
        var gridPen = new Pen(SolidColorBrush.Parse("#17171B"), 1);
        var zeroPen = new Pen(SolidColorBrush.Parse("#27272A"), 1.2);

        double zeroEqY = upperPlot.Bottom - ((0d - minEq) / spanEq) * upperPlot.Height;
        if (zeroEqY >= upperPlot.Top && zeroEqY <= upperPlot.Bottom)
        {
            context.DrawLine(zeroPen, new Point(upperPlot.Left, zeroEqY), new Point(upperPlot.Right, zeroEqY));
            DrawText(context, "$0", new Point(upperPlot.Right + 8, zeroEqY - 6), "#A1A1AA", 9.5, true);
        }

        // Ticks de precios en panel superior
        int gridStepsUpper = 4;
        for (int s = 1; s <= gridStepsUpper; s++)
        {
            double frac = s / (double)gridStepsUpper;
            double val = minEq + frac * spanEq;
            double y = upperPlot.Bottom - (frac * upperPlot.Height);
            if (Math.Abs(y - zeroEqY) > 10)
            {
                context.DrawLine(gridPen, new Point(upperPlot.Left, y), new Point(upperPlot.Right, y));
                DrawText(context, $"{val:+$#,##0;-$#,##0;$0}", new Point(upperPlot.Right + 8, y - 6), "#71717A", 9.0);
            }
        }

        // --- 3. LÍNEAS GUÍA Y ESCALA Y (PANEL INFERIOR: DRAWDOWN) ---
        double zeroDdY = lowerPlot.Top;
        context.DrawLine(zeroPen, new Point(lowerPlot.Left, zeroDdY), new Point(lowerPlot.Right, zeroDdY));
        DrawText(context, "$0", new Point(lowerPlot.Right + 8, zeroDdY - 5), "#A1A1AA", 9.0, true);

        double midDdY = lowerPlot.Top + 0.5 * lowerPlot.Height;
        context.DrawLine(gridPen, new Point(lowerPlot.Left, midDdY), new Point(lowerPlot.Right, midDdY));
        DrawText(context, $"{minDd * 0.5:-$#,##0}", new Point(lowerPlot.Right + 8, midDdY - 6), "#71717A", 9.0);

        double botDdY = lowerPlot.Bottom;
        DrawText(context, $"{minDd:-$#,##0}", new Point(lowerPlot.Right + 8, botDdY - 10), "#FF355E", 9.0);

        // --- 4. TÍTULOS DE PANEL ---
        DrawText(context, "CURVA DE CAPITAL ACUMULADO (NET PROFIT & PEAK)", new Point(upperPlot.Left + 8, upperPlot.Top + 6), "#71717A", 9.0, true);
        DrawText(context, "DRAWDOWN INTRADÍA DESDE PICO (UNDERWATER)", new Point(lowerPlot.Left + 8, lowerPlot.Top + 5), "#EF4444", 8.5, true);

        // --- 5. LÍNEAS VERTICALES DE FECHAS (EJE X) ---
        int dateTicksCount = Math.Min(5, n);
        for (int d = 0; d < dateTicksCount; d++)
        {
            int idx = (int)Math.Round(d * (n - 1) / (double)(dateTicksCount - 1));
            double x = upperPlot.Left + (idx / (double)(n - 1)) * upperPlot.Width;
            context.DrawLine(gridPen, new Point(x, upperPlot.Top), new Point(x, lowerPlot.Bottom));
            string dateStr = trades[idx].ExitTimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            DrawText(context, dateStr, new Point(x - 18, lowerPlot.Bottom + 6), "#71717A", 9.0);
        }

        // --- 6. RENDERIZADO DE CURVAS CON OPTIMIZACIÓN POR PIXEL ---
        double finalNet = trades[n - 1].CumulativeNetProfitUsd;
        var eqPen = new Pen(SolidColorBrush.Parse(finalNet >= 0 ? "#00E599" : "#FF355E"), 2.0);
        var peakPen = new Pen(SolidColorBrush.Parse("#52525B"), 1.2, new DashStyle(new double[] { 3, 3 }, 0));
        var ddPen = new Pen(SolidColorBrush.Parse("#EF4444"), 1.6);

        Point prevEqPt = new(upperPlot.Left, zeroEqY);
        Point prevPeakPt = new(upperPlot.Left, zeroEqY);
        Point prevDdPt = new(lowerPlot.Left, zeroDdY);

        int pixelWidth = (int)Math.Max(10, upperPlot.Width);
        int step = Math.Max(1, n / (pixelWidth * 2));

        for (int i = 0; i < n; i += step)
        {
            double x = upperPlot.Left + (i / (double)(n - 1)) * upperPlot.Width;
            double eqY = upperPlot.Bottom - ((trades[i].CumulativeNetProfitUsd - minEq) / spanEq) * upperPlot.Height;
            double pkY = upperPlot.Bottom - ((peaks[i] - minEq) / spanEq) * upperPlot.Height;
            double ddY = lowerPlot.Top + ((0d - drawdowns[i]) / spanDd) * lowerPlot.Height;

            var eqPt = new Point(x, eqY);
            var pkPt = new Point(x, pkY);
            var ddPt = new Point(x, ddY);

            if (i > 0)
            {
                context.DrawLine(peakPen, prevPeakPt, pkPt);
                context.DrawLine(eqPen, prevEqPt, eqPt);
                context.DrawLine(ddPen, prevDdPt, ddPt);
            }

            prevEqPt = eqPt;
            prevPeakPt = pkPt;
            prevDdPt = ddPt;
        }

        if ((n - 1) % step != 0)
        {
            double lastX = upperPlot.Right;
            double lastEqY = upperPlot.Bottom - ((trades[n - 1].CumulativeNetProfitUsd - minEq) / spanEq) * upperPlot.Height;
            double lastPkY = upperPlot.Bottom - ((peaks[n - 1] - minEq) / spanEq) * upperPlot.Height;
            double lastDdY = lowerPlot.Top + ((0d - drawdowns[n - 1]) / spanDd) * lowerPlot.Height;

            var lastEqPt = new Point(lastX, lastEqY);
            var lastPkPt = new Point(lastX, lastPkY);
            var lastDdPt = new Point(lastX, lastDdY);

            context.DrawLine(peakPen, prevPeakPt, lastPkPt);
            context.DrawLine(eqPen, prevEqPt, lastEqPt);
            context.DrawLine(ddPen, prevDdPt, lastDdPt);
        }

        // --- 7. CURSOR INTERACTIVO & TOOLTIP FLOTANTE (ESTILO NINJATRADER) ---
        if (hoverPos.HasValue && hoverPos.Value.X >= upperPlot.Left && hoverPos.Value.X <= upperPlot.Right)
        {
            double relX = (hoverPos.Value.X - upperPlot.Left) / upperPlot.Width;
            int hIdx = (int)Math.Clamp(Math.Round(relX * (n - 1)), 0, n - 1);
            var tr = trades[hIdx];

            double curX = upperPlot.Left + (hIdx / (double)(n - 1)) * upperPlot.Width;
            double curEqY = upperPlot.Bottom - ((tr.CumulativeNetProfitUsd - minEq) / spanEq) * upperPlot.Height;
            double curDdY = lowerPlot.Top + ((0d - drawdowns[hIdx]) / spanDd) * lowerPlot.Height;

            // Guía vertical
            var guidePen = new Pen(SolidColorBrush.Parse("#71717A"), 1.0, new DashStyle(new double[] { 2, 2 }, 0));
            context.DrawLine(guidePen, new Point(curX, upperPlot.Top), new Point(curX, lowerPlot.Bottom));

            // Marcador en curva de equidad
            var eqMarkerBrush = SolidColorBrush.Parse(tr.NetProfit >= 0 ? "#00E599" : "#FF355E");
            context.DrawEllipse(eqMarkerBrush, new Pen(Brushes.White, 1.5), new Point(curX, curEqY), 4.5, 4.5);

            // Marcador en curva de drawdown
            context.DrawEllipse(SolidColorBrush.Parse("#EF4444"), new Pen(Brushes.White, 1.5), new Point(curX, curDdY), 4.0, 4.0);

            // Caja de inspección flotante (Tooltip)
            double tipW = 230;
            double tipH = 112;
            double tipX = (curX + 14 + tipW > bounds.Width - 10) ? curX - 14 - tipW : curX + 14;
            double tipY = Math.Clamp(hoverPos.Value.Y - 50, upperPlot.Top + 4, bounds.Height - tipH - 12);

            var tipRect = new Rect(tipX, tipY, tipW, tipH);
            context.DrawRectangle(
                SolidColorBrush.Parse("#161619"),
                new Pen(SolidColorBrush.Parse("#3F3F46"), 1),
                tipRect, 6, 6);

            double ty = tipY + 7;
            DrawText(context, $"Trade #{tr.TradeNumber} · {tr.ExitTimeUtc:yyyy-MM-dd HH:mm}", new Point(tipX + 10, ty), "#FFFFFF", 10.0, true);
            ty += 17;
            DrawText(context, $"Pos: {tr.Side} x{tr.Quantity} · Salida: {tr.ExitReason}", new Point(tipX + 10, ty), "#A1A1AA", 9.0);
            ty += 16;
            string pnlHex = tr.NetProfit >= 0 ? "#00E599" : "#FF355E";
            DrawText(context, $"PnL: {tr.NetProfit:+$#,##0.00;-$#,##0.00;$0.00} ({tr.Points:+#0.00;-#0.00;0.00} pts)", new Point(tipX + 10, ty), pnlHex, 10.0, true);
            ty += 18;
            DrawText(context, $"Equidad Acumulada: {tr.CumulativeNetProfitUsd:+$#,##0.00;-$#,##0.00;$0.00}", new Point(tipX + 10, ty), "#FFFFFF", 9.5);
            ty += 16;
            DrawText(context, $"Drawdown: {drawdowns[hIdx]:-$#,##0.00;$0.00} | Pico: ${peaks[hIdx]:#,##0}", new Point(tipX + 10, ty), "#EF4444", 9.0);
            ty += 15;
            DrawText(context, $"MAE: -${tr.MaxAdverseExcursionUsd:#,##0}  |  MFE: +${tr.MaxFavorableExcursionUsd:#,##0}", new Point(tipX + 10, ty), "#71717A", 8.5);
        }
    }

    private static void DrawText(DrawingContext context, string text, Point pt, string hex, double size, bool bold = false)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, Sans-Serif", FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            size,
            SolidColorBrush.Parse(hex));
        context.DrawText(ft, pt);
    }
}

internal sealed class DailyTradesChartSurface : Control
{
    private IReadOnlyList<DailyPerformanceRecord> days = Array.Empty<DailyPerformanceRecord>();
    private Point? hoverPos;

    public DailyTradesChartSurface()
    {
        ClipToBounds = true;
    }

    public void SetDays(IReadOnlyList<DailyPerformanceRecord>? dailyRecords)
    {
        days = dailyRecords ?? Array.Empty<DailyPerformanceRecord>();
        hoverPos = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hoverPos = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverPos = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 100 || bounds.Height < 70) return;

        context.DrawRectangle(
            SolidColorBrush.Parse("#09090B"),
            new Pen(SolidColorBrush.Parse("#222225"), 1),
            bounds, 8, 8);

        if (days.Count == 0)
        {
            var ft = new FormattedText(
                "Ejecuta un backtest para visualizar la distribución y frecuencia de trades diarios.",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, Segoe UI, Sans-Serif"),
                11.5,
                SolidColorBrush.Parse("#71717A"));
            context.DrawText(ft, new Point(20, bounds.Height / 2 - 10));
            return;
        }

        const double marginLeft = 40;
        const double marginRight = 75;
        const double marginTop = 28;
        const double marginBottom = 28;

        double plotW = Math.Max(60, bounds.Width - marginLeft - marginRight);
        double plotH = Math.Max(50, bounds.Height - marginTop - marginBottom);
        var plotRect = new Rect(marginLeft, marginTop, plotW, plotH);

        // Panel background
        context.DrawRectangle(
            SolidColorBrush.Parse("#0C0C0E"),
            new Pen(SolidColorBrush.Parse("#1A1A1E"), 1),
            plotRect, 4, 4);

        // Title
        DrawText(context, "FRECUENCIA DE TRADES DIARIOS (BARRAS) & LÍNEA DE PROMEDIO", new Point(plotRect.Left + 8, plotRect.Top + 6), "#71717A", 9.0, true);

        int maxTrades = Math.Max(4, days.Max(d => d.Trades));
        if (maxTrades % 2 != 0) maxTrades++;

        int totalTrades = days.Sum(d => d.Trades);
        double avgTrades = days.Count > 0 ? (double)totalTrades / days.Count : 0.0;

        // Grid lines
        var gridPen = new Pen(SolidColorBrush.Parse("#17171B"), 1);
        int gridSteps = 4;
        for (int s = 1; s <= gridSteps; s++)
        {
            double frac = s / (double)gridSteps;
            int val = (int)Math.Round(frac * maxTrades);
            double y = plotRect.Bottom - (frac * plotRect.Height);
            context.DrawLine(gridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));
            DrawText(context, $"{val} ops", new Point(plotRect.Right + 8, y - 5), "#71717A", 8.5);
        }

        // Zero line
        var zeroPen = new Pen(SolidColorBrush.Parse("#27272A"), 1.2);
        context.DrawLine(zeroPen, new Point(plotRect.Left, plotRect.Bottom), new Point(plotRect.Right, plotRect.Bottom));
        DrawText(context, "0 ops", new Point(plotRect.Right + 8, plotRect.Bottom - 5), "#A1A1AA", 8.5, true);

        // Bars
        int n = days.Count;
        double barSlotW = plotW / n;
        double barW = Math.Max(1.0, barSlotW - (n > 100 ? 0.3 : 1.2));
        var winBrush = SolidColorBrush.Parse("#00E599");
        var lossBrush = SolidColorBrush.Parse("#FF355E");
        var evenBrush = SolidColorBrush.Parse("#71717A");

        for (int i = 0; i < n; i++)
        {
            var dp = days[i];
            if (dp.Trades <= 0) continue;
            double x = plotRect.Left + (i / (double)n) * plotW;
            double h = (dp.Trades / (double)maxTrades) * plotRect.Height;
            double y = plotRect.Bottom - h;
            var brush = dp.NetProfit > 0 ? winBrush : (dp.NetProfit < 0 ? lossBrush : evenBrush);
            context.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1.0, barW), Math.Max(1.0, h)));
        }

        // Horizontal Average Line
        double avgY = plotRect.Bottom - (avgTrades / (double)maxTrades) * plotRect.Height;
        if (avgY >= plotRect.Top && avgY <= plotRect.Bottom)
        {
            var avgPen = new Pen(SolidColorBrush.Parse("#FFB224"), 1.8, new DashStyle(new double[] { 4, 3 }, 0));
            context.DrawLine(avgPen, new Point(plotRect.Left, avgY), new Point(plotRect.Right, avgY));

            var pillRect = new Rect(plotRect.Right + 4, avgY - 9, 68, 18);
            context.DrawRectangle(SolidColorBrush.Parse("#2A2008"), new Pen(SolidColorBrush.Parse("#FFB224"), 1), pillRect, 4, 4);
            DrawText(context, $"μ {avgTrades:F1}/d", new Point(pillRect.Left + 5, pillRect.Top + 3), "#FFB224", 9.0, true);
        }

        // Date markers (X-axis)
        int dateTicksCount = Math.Min(5, n);
        for (int d = 0; d < dateTicksCount; d++)
        {
            int idx = (int)Math.Round(d * (n - 1) / (double)(dateTicksCount - 1));
            double x = plotRect.Left + (idx / (double)(n - 1)) * plotRect.Width;
            context.DrawLine(gridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
            string dateStr = days[idx].DateUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            DrawText(context, dateStr, new Point(x - 18, plotRect.Bottom + 5), "#71717A", 8.5);
        }

        // Hover tooltip & cursor
        if (hoverPos.HasValue && hoverPos.Value.X >= plotRect.Left && hoverPos.Value.X <= plotRect.Right)
        {
            int hIdx = (int)Math.Clamp(Math.Floor((hoverPos.Value.X - plotRect.Left) / plotW * n), 0, n - 1);
            var hDay = days[hIdx];

            double curX = plotRect.Left + (hIdx / (double)n) * plotW;
            double curH = (hDay.Trades / (double)maxTrades) * plotRect.Height;
            double curBarTop = plotRect.Bottom - curH;

            var guidePen = new Pen(SolidColorBrush.Parse("#A1A1AA"), 1.0, new DashStyle(new double[] { 2, 2 }, 0));
            context.DrawLine(guidePen, new Point(curX + barW / 2, plotRect.Top), new Point(curX + barW / 2, plotRect.Bottom));

            var dotBrush = hDay.NetProfit >= 0 ? winBrush : lossBrush;
            context.DrawEllipse(dotBrush, new Pen(Brushes.White, 1.5), new Point(curX + barW / 2, curBarTop), 3.5, 3.5);

            double tipW = 215;
            double tipH = 96;
            double tipX = (curX + 12 + tipW > bounds.Width - 10) ? curX - 12 - tipW : curX + 12;
            double tipY = Math.Clamp(hoverPos.Value.Y - 45, plotRect.Top + 4, bounds.Height - tipH - 8);

            context.DrawRectangle(
                SolidColorBrush.Parse("#161619"),
                new Pen(SolidColorBrush.Parse("#3F3F46"), 1),
                new Rect(tipX, tipY, tipW, tipH), 6, 6);

            double ty = tipY + 6;
            DrawText(context, $"{hDay.DateUtc:yyyy-MM-dd (dddd)}", new Point(tipX + 8, ty), "#FFFFFF", 9.5, true);
            ty += 16;
            double diff = hDay.Trades - avgTrades;
            string diffSign = diff >= 0 ? "+" : "";
            DrawText(context, $"Operaciones: {hDay.Trades} ops ({diffSign}{diff:F1} vs media)", new Point(tipX + 8, ty), "#FFB224", 9.0, true);
            ty += 15;
            DrawText(context, $"Desglose: +{hDay.WinningTrades} Gan / -{hDay.LosingTrades} Perd", new Point(tipX + 8, ty), "#A1A1AA", 8.5);
            ty += 15;
            string dayPnlHex = hDay.NetProfit >= 0 ? "#00E599" : "#FF355E";
            DrawText(context, $"P&L Neto Día: {hDay.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}", new Point(tipX + 8, ty), dayPnlHex, 9.5, true);
            ty += 15;
            DrawText(context, $"DD Intradía: -${hDay.MaxIntradayDrawdown:#,##0.00}", new Point(tipX + 8, ty), "#FF355E", 8.5);
        }
    }

    private static void DrawText(DrawingContext context, string text, Point pt, string hex, double size, bool bold = false)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, Sans-Serif", FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            size,
            SolidColorBrush.Parse(hex));
        context.DrawText(ft, pt);
    }
}

internal sealed class MetricsWindow : Window
{
    public MetricsWindow(BacktestResult result)
    {
        Title = $"Métricas Detalladas & Frecuencia de Trades · {result.Configuration.Strategy} ({result.Configuration.Instrument})";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 560;
        Background = SolidColorBrush.Parse("#09090B");

        Content = BuildContent(result);
    }

    private Control BuildContent(BacktestResult result)
    {
        var days = result.DailyPerformance ?? Array.Empty<DailyPerformanceRecord>();
        int totalDays = days.Count;
        int totalTrades = days.Sum(d => d.Trades);
        double avgTrades = totalDays > 0 ? (double)totalTrades / totalDays : 0.0;
        int maxTrades = totalDays > 0 ? days.Max(d => d.Trades) : 0;
        int minTrades = totalDays > 0 ? days.Min(d => d.Trades) : 0;
        double stdDev = totalDays > 0 ? Math.Sqrt(days.Average(d => Math.Pow(d.Trades - avgTrades, 2))) : 0.0;
        double avgDailyNet = totalDays > 0 ? days.Average(d => d.NetProfit) : 0.0;
        var winDaysList = days.Where(d => d.NetProfit > 0).ToList();
        var lossDaysList = days.Where(d => d.NetProfit < 0).ToList();
        int winDays = winDaysList.Count;
        int lossDays = lossDaysList.Count;
        double winDaysPct = totalDays > 0 ? (double)winDays / totalDays * 100.0 : 0.0;
        double avgWinDay = winDays > 0 ? winDaysList.Average(d => d.NetProfit) : 0.0;
        double avgLossDay = lossDays > 0 ? lossDaysList.Average(d => d.NetProfit) : 0.0;

        // Header
        var headerBorder = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"MÉTRICAS & FRECUENCIA DE TRADES · {result.Configuration.Strategy.ToUpperInvariant()}",
                        Foreground = SolidColorBrush.Parse("#FFFFFF"),
                        FontWeight = FontWeight.Bold,
                        FontSize = 13
                    },
                    new TextBlock
                    {
                        Text = $"Instrumento: {result.Configuration.Instrument} | Total Días: {totalDays:N0} | Total Trades: {totalTrades:N0} | P&L Neto: {result.Summary.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 11
                    }
                }
            }
        };

        // 5 KPI Cards
        var kpiGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 10) };
        kpiGrid.Children.Add(MainWindow.MakeDynamicKpiCard("PROMEDIO TRADES / DÍA", $"{avgTrades:F1} ops / día", $"Total: {totalTrades:N0} ops en {totalDays:N0} días", "#FFB224", out _, out _));
        kpiGrid.Children.Add(MainWindow.MakeDynamicKpiCard("MÁX / MÍN EN UN DÍA", $"Máx {maxTrades} | Mín {minTrades}", $"Desv. Estándar: ±{stdDev:F1} ops/día", "#FFFFFF", out _, out _));
        kpiGrid.Children.Add(MainWindow.MakeDynamicKpiCard("P&L MEDIO DIARIO", $"{avgDailyNet:+$#,##0.00;-$#,##0.00;$0.00} / día", $"Día +: {avgWinDay:+$#,##0} | Día -: {avgLossDay:-$#,##0}", avgDailyNet >= 0 ? "#00E599" : "#FF355E", out _, out _));
        kpiGrid.Children.Add(MainWindow.MakeDynamicKpiCard("CONSISTENCIA DIARIA", $"{winDaysPct:F1}% ({winDays}d / {totalDays}d)", $"+{winDays} Días Gan | -{lossDays} Perd", winDaysPct >= 50 ? "#00E599" : "#FF355E", out _, out _));
        kpiGrid.Children.Add(MainWindow.MakeDynamicKpiCard("DURACIÓN MEDIA", $"{result.Summary.AverageTradeDurationMinutes:F1} min / trade", "Exposición media por posición", "#E4E4E7", out _, out _));

        // Center split
        var centerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("60*,10,40*"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var chart = new DailyTradesChartSurface { Height = 320, MinHeight = 260 };
        chart.SetDays(days);
        var chartBorder = new Border
        {
            Background = SolidColorBrush.Parse("#0D0D0E"),
            BorderBrush = SolidColorBrush.Parse("#1F1F23"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = chart
        };
        Grid.SetColumn(chartBorder, 0);
        centerGrid.Children.Add(chartBorder);

        // Right side
        var rightStack = new StackPanel { Spacing = 8 };

        // DOW Card
        var dowRows = new StackPanel { Spacing = 3 };
        var dowHeaderGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,55,60,60,*") };
        MainWindow.AddTableHeader(dowHeaderGrid, 0, "Día");
        MainWindow.AddTableHeader(dowHeaderGrid, 1, "Trades");
        MainWindow.AddTableHeader(dowHeaderGrid, 2, "Media/D");
        MainWindow.AddTableHeader(dowHeaderGrid, 3, "% Win");
        MainWindow.AddTableHeader(dowHeaderGrid, 4, "P&L Neto Total");

        var dowOrder = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday
        };
        foreach (var dow in dowOrder)
        {
            var match = days.Where(d => d.DateUtc.DayOfWeek == dow).ToList();
            if (match.Count == 0) continue;
            string name = dow switch
            {
                DayOfWeek.Monday => "Lunes", DayOfWeek.Tuesday => "Martes", DayOfWeek.Wednesday => "Miércoles",
                DayOfWeek.Thursday => "Jueves", DayOfWeek.Friday => "Viernes", DayOfWeek.Sunday => "Domingo",
                _ => dow.ToString()
            };
            int dCount = match.Count;
            int dTrades = match.Sum(d => d.Trades);
            double dAvgTrades = (double)dTrades / dCount;
            int dWins = match.Sum(d => d.WinningTrades);
            double dWinRate = dTrades > 0 ? (double)dWins / dTrades * 100.0 : 0.0;
            double dNet = match.Sum(d => d.NetProfit);
            double dAvgNet = dNet / dCount;

            var rGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,55,60,60,*"), VerticalAlignment = VerticalAlignment.Center };
            MainWindow.AddTableCell(rGrid, 0, name, "#FFFFFF", true);
            MainWindow.AddTableCell(rGrid, 1, $"{dTrades} ops", "#A1A1AA");
            MainWindow.AddTableCell(rGrid, 2, $"μ {dAvgTrades:F1}/d", "#FFB224");
            MainWindow.AddTableCell(rGrid, 3, $"{dWinRate:F1}%", dWinRate >= 50 ? "#00E599" : "#FF355E");
            MainWindow.AddTableCell(rGrid, 4, $"{dNet:+$#,##0;-$#,##0;$0} (μ {dAvgNet:+$#,##0;-$#,##0;$0})", dNet >= 0 ? "#00E599" : "#FF355E", true);

            dowRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse("#161619"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4),
                Child = rGrid
            });
        }

        var dowCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DESGLOSE POR DÍA DE LA SEMANA",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 0, 0, 2)
                    },
                    new Border
                    {
                        Background = SolidColorBrush.Parse("#18181B"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = SolidColorBrush.Parse("#27272A"),
                        Padding = new Thickness(4, 4),
                        Child = dowHeaderGrid
                    },
                    dowRows
                }
            }
        };
        rightStack.Children.Add(dowCard);

        // Frequency Buckets
        var freqRows = new StackPanel { Spacing = 4 };
        var buckets = new (string Label, Func<DailyPerformanceRecord, bool> Predicate)[]
        {
            ("1 trade / día", d => d.Trades == 1),
            ("2 trades / día", d => d.Trades == 2),
            ("3 – 4 trades", d => d.Trades >= 3 && d.Trades <= 4),
            ("5 – 6 trades", d => d.Trades >= 5 && d.Trades <= 6),
            ("7+ trades / día", d => d.Trades >= 7)
        };
        foreach (var (lbl, pred) in buckets)
        {
            int bCount = days.Count(pred);
            double bPct = totalDays > 0 ? (double)bCount / totalDays * 100.0 : 0.0;
            var bGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,80"), VerticalAlignment = VerticalAlignment.Center };
            var lblBlock = new TextBlock { Text = lbl, Foreground = SolidColorBrush.Parse("#FFFFFF"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lblBlock, 0);
            bGrid.Children.Add(lblBlock);

            var barTrack = new Border { Background = SolidColorBrush.Parse("#222226"), Height = 8, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            var barFill = new Border { Background = SolidColorBrush.Parse("#00E599"), Height = 8, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, (bPct / 100.0) * 140.0) };
            barTrack.Child = barFill;
            Grid.SetColumn(barTrack, 1);
            bGrid.Children.Add(barTrack);

            var infoBlock = new TextBlock { Text = $"{bCount} d ({bPct:F1}%)", Foreground = SolidColorBrush.Parse("#A1A1AA"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(infoBlock, 2);
            bGrid.Children.Add(infoBlock);

            freqRows.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse("#161619"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4),
                Child = bGrid
            });
        }

        var freqCard = new Border
        {
            Background = SolidColorBrush.Parse("#141416"),
            BorderBrush = SolidColorBrush.Parse("#27272A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DISTRIBUCIÓN DE FRECUENCIA DE TRADES / DÍA",
                        Foreground = SolidColorBrush.Parse("#A1A1AA"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 0, 0, 2)
                    },
                    freqRows
                }
            }
        };
        rightStack.Children.Add(freqCard);

        Grid.SetColumn(rightStack, 2);
        centerGrid.Children.Add(rightStack);

        // Bottom row
        var best = days.Count > 0 ? days.MaxBy(d => d.NetProfit) : null;
        var worst = days.Count > 0 ? days.MinBy(d => d.NetProfit) : null;
        double avgDd = days.Count > 0 ? days.Average(d => d.MaxIntradayDrawdown) : 0.0;
        double comms = result.Summary.ClosedTradeCommissions;
        double gross = result.Summary.GrossProfit;
        double commPct = gross > 0 ? (comms / gross) * 100.0 : 0.0;

        var bottomGrid = new UniformGrid { Columns = 4 };
        bottomGrid.Children.Add(MainWindow.MakeDynamicKpiCard("MEJOR DÍA", best != null ? $"{best.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}" : "—", best != null ? $"Fecha: {best.DateUtc:yyyy-MM-dd} ({best.Trades} ops)" : "—", "#00E599", out _, out _));
        bottomGrid.Children.Add(MainWindow.MakeDynamicKpiCard("PEOR DÍA", worst != null ? $"{worst.NetProfit:+$#,##0.00;-$#,##0.00;$0.00}" : "—", worst != null ? $"Fecha: {worst.DateUtc:yyyy-MM-dd} ({worst.Trades} ops)" : "—", "#FF355E", out _, out _));
        bottomGrid.Children.Add(MainWindow.MakeDynamicKpiCard("DD INTRADÍA MEDIO", $"-${avgDd:#,##0.00}", "Riesgo medio por día", "#FF355E", out _, out _));
        bottomGrid.Children.Add(MainWindow.MakeDynamicKpiCard("COMISIONES / BRUTO", $"${comms:#,##0.00} ({commPct:F1}%)", "Costes de ejecución sobre ganancia bruta", "#E4E4E7", out _, out _));

        var mainStack = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(14),
            Children =
            {
                headerBorder,
                kpiGrid,
                centerGrid,
                bottomGrid
            }
        };

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = mainStack
        };
    }
}

internal sealed class PropFirmAccountSimResult
{
    public int AccountNumber { get; set; }
    public string FinalStatus { get; set; } = "";
    public string PhaseReached { get; set; } = "";
    public int DaysTraded { get; set; }
    public int TotalTrades { get; set; }
    public double AccountEquity { get; set; }
    public double PayoutReceived { get; set; }
    public bool PassedEvaluation { get; set; }
    public bool AchievedPayout { get; set; }
    public string Notes { get; set; } = "";
}

internal sealed class RolloverAccumulationSurface : Control
{
    private IReadOnlyList<RolloverComputedRow> rows = Array.Empty<RolloverComputedRow>();

    public void SetRows(IReadOnlyList<RolloverComputedRow> computedRows)
    {
        rows = computedRows;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 100 || bounds.Height < 60) return;

        context.DrawRectangle(
            SolidColorBrush.Parse("#09090B"),
            new Pen(SolidColorBrush.Parse("#222225"), 1),
            bounds, 8, 8);

        if (rows.Count == 0) return;

        var plot = new Rect(16, 14, Math.Max(60, bounds.Width - 96), Math.Max(30, bounds.Height - 36));
        double minVal = Math.Min(0, rows.Min(r => r.CumulativeFrom2010));
        double maxVal = Math.Max(100, rows.Max(r => r.CumulativeFrom2010));
        double span = Math.Max(1, maxVal - minVal);

        var gridPen = new Pen(SolidColorBrush.Parse("#161618"), 1);
        double zeroY = plot.Bottom - ((0 - minVal) / span) * plot.Height;
        context.DrawLine(gridPen, new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));

        var linePen = new Pen(SolidColorBrush.Parse("#00E599"), 2);

        double stepX = plot.Width / rows.Count;
        double barW = Math.Clamp(stepX * 0.55, 2, 12);

        Point? prevPoint = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double cx = plot.Left + (i + 0.5) * stepX;
            double cy = plot.Bottom - ((r.CumulativeFrom2010 - minVal) / span) * plot.Height;

            // Barra de salto individual escalada (Glassmorphic)
            double barTop = Math.Min(cy, zeroY);
            double barH = Math.Max(1.5, Math.Abs( zeroY - cy ));
            context.FillRectangle(
                r.CumulativeFrom2010 >= 0 ? SolidColorBrush.Parse("#2600E599") : SolidColorBrush.Parse("#26FF355E"),
                new Rect(cx - barW / 2, barTop, barW, barH));

            var pt = new Point(cx, cy);
            if (prevPoint.HasValue)
            {
                context.DrawLine(linePen, prevPoint.Value, pt);
            }
            prevPoint = pt;

            if (i == 0 || i == rows.Count - 1 || i % 12 == 0)
            {
                DrawSmallText(context, r.CloseTimeUtc.ToString("yyyy", CultureInfo.InvariantCulture), new Point(cx - 12, plot.Bottom + 4), "#71717A");
            }
        }

        DrawSmallText(context, $"+{maxVal:N0} pts", new Point(plot.Right + 8, plot.Top - 2), "#00E599");
        DrawSmallText(context, "0 pts", new Point(plot.Right + 8, Math.Clamp(zeroY - 7, plot.Top, plot.Bottom - 12)), "#71717A");
    }

    private static void DrawSmallText(DrawingContext context, string text, Point pt, string hex)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, Sans-Serif"),
            10,
            SolidColorBrush.Parse(hex));
        context.DrawText(ft, pt);
    }
}

internal sealed class ChartSurface : Control
{
    private IReadOnlyList<Bar> bars = Array.Empty<Bar>();
    private string timeframeLabel = "1m";
    private int visibleBarCount = 180;
    private int rightEdgeIndex;

    private Point? hoverPoint;
    private bool isDragging;
    private bool isDraggingPriceAxis;
    private Point dragStartPoint;
    private int dragStartRightEdgeIndex;
    private double dragStartPriceSpanFactor = 1.0;
    private double dragStartCenterOffset = 0.0;
    private double manualPriceSpanFactor = 1.0;
    private double manualPriceCenterOffset = 0.0;
    private bool isAutoScale = true;
    private double lastPriceSpan = 1.0;
    private double lastCandleRectHeight = 100.0;

    // Estado de la vela viva en formación (Tick Replay)
    private Bar? liveFormingBar;
    private Tick? currentLiveTick;
    private int liveTickIndex;
    private int liveTickTotal;

    public int VisibleBarCount => visibleBarCount;
    public int RightEdgeIndex => rightEdgeIndex;
    public int TotalBars => bars.Count;
    public IReadOnlyList<Bar> Bars => bars;
    public bool CanPanForward => bars.Count > 0 && rightEdgeIndex < bars.Count - 1;
    public bool IsAutoScale => isAutoScale;
    public Bar? GetBar(int index) => (index >= 0 && index < bars.Count) ? bars[index] : null;

    public event Action<int, int>? ViewportChanged;
    public event Action<bool>? AutoScaleChanged;

    public ChartSurface()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void SetLiveTick(Bar formingBar, Tick tick, int tickIndex, int tickTotal)
    {
        liveFormingBar = formingBar;
        currentLiveTick = tick;
        liveTickIndex = tickIndex;
        liveTickTotal = tickTotal;
        InvalidateVisual();
    }

    public void ClearLiveTick()
    {
        liveFormingBar = null;
        currentLiveTick = null;
        liveTickIndex = 0;
        liveTickTotal = 0;
        InvalidateVisual();
    }

    public void ResetAutoScale()
    {
        isAutoScale = true;
        manualPriceSpanFactor = 1.0;
        manualPriceCenterOffset = 0.0;
        AutoScaleChanged?.Invoke(true);
        InvalidateVisual();
    }

    public void ToggleAutoScale()
    {
        if (isAutoScale)
        {
            isAutoScale = false;
            AutoScaleChanged?.Invoke(false);
            InvalidateVisual();
        }
        else
        {
            ResetAutoScale();
        }
    }

    public void SetBars(IReadOnlyList<Bar> loadedBars, string tfLabel)
    {
        bars = loadedBars;
        timeframeLabel = tfLabel;
        rightEdgeIndex = Math.Max(0, bars.Count - 1);
        ResetAutoScale();
        NotifyViewport();
        InvalidateVisual();
    }

    public void SetVisibleBarCount(int count)
    {
        visibleBarCount = Math.Clamp(count, 15, 1500);
        NotifyViewport();
        InvalidateVisual();
    }

    public void ZoomStep(double factor, double? anchorX = null)
    {
        if (bars.Count == 0) return;

        int oldVisible = visibleBarCount;
        int newVisible = Math.Clamp((int)Math.Round(visibleBarCount * factor), 15, 1500);
        if (newVisible == oldVisible) return;

        double plotWidth = Math.Max(80, Bounds.Width - 76 - 14);
        double targetX = anchorX ?? (10 + plotWidth / 2);
        double cursorRatio = Math.Clamp((targetX - 10) / Math.Max(1.0, plotWidth), 0.0, 1.0);

        int countDiff = newVisible - oldVisible;
        int rightShift = (int)Math.Round(countDiff * (1.0 - cursorRatio));

        visibleBarCount = newVisible;
        int minRight = 0;
        rightEdgeIndex = Math.Clamp(rightEdgeIndex + rightShift, minRight, bars.Count - 1);

        NotifyViewport();
        InvalidateVisual();
    }

    public void SetRightEdgeIndex(int index)
    {
        if (bars.Count == 0) return;
        int minRight = 0;
        rightEdgeIndex = Math.Clamp(index, minRight, bars.Count - 1);
        NotifyViewport();
        InvalidateVisual();
    }

    public void JumpToStart()
    {
        if (bars.Count == 0) return;
        rightEdgeIndex = 0;
        NotifyViewport();
        InvalidateVisual();
    }

    public void JumpToEnd()
    {
        if (bars.Count == 0) return;
        rightEdgeIndex = bars.Count - 1;
        NotifyViewport();
        InvalidateVisual();
    }

    public void PanByBars(int deltaBars)
    {
        if (bars.Count == 0) return;
        int minRight = 0;
        rightEdgeIndex = Math.Clamp(rightEdgeIndex + deltaBars, minRight, bars.Count - 1);
        NotifyViewport();
        InvalidateVisual();
    }

    private void NotifyViewport()
    {
        ViewportChanged?.Invoke(rightEdgeIndex, bars.Count);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (bars.Count == 0) return;

        Point pos = e.GetPosition(this);
        const double rightAxisWidth = 76;
        bool inPriceAxis = pos.X >= Bounds.Width - rightAxisWidth;

        if (inPriceAxis)
        {
            if (e.ClickCount >= 2)
            {
                ResetAutoScale();
                e.Handled = true;
                return;
            }

            isDraggingPriceAxis = true;
            dragStartPoint = pos;
            dragStartPriceSpanFactor = manualPriceSpanFactor;
            dragStartCenterOffset = manualPriceCenterOffset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else
        {
            isDragging = true;
            dragStartPoint = pos;
            dragStartRightEdgeIndex = rightEdgeIndex;
            dragStartCenterOffset = manualPriceCenterOffset;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (isDragging || isDraggingPriceAxis)
        {
            isDragging = false;
            isDraggingPriceAxis = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        hoverPoint = pos;

        const double rightAxisWidth = 76;
        bool inPriceAxis = pos.X >= Bounds.Width - rightAxisWidth;

        if (!isDragging && !isDraggingPriceAxis)
        {
            Cursor = inPriceAxis
                ? new Cursor(StandardCursorType.SizeNorthSouth)
                : new Cursor(StandardCursorType.Cross);
        }

        if (isDraggingPriceAxis)
        {
            double dy = pos.Y - dragStartPoint.Y;
            manualPriceSpanFactor = Math.Clamp(dragStartPriceSpanFactor * Math.Pow(1.006, dy), 0.15, 8.0);
            if (isAutoScale)
            {
                isAutoScale = false;
                AutoScaleChanged?.Invoke(false);
            }
            InvalidateVisual();
        }
        else if (isDragging && bars.Count > 0)
        {
            double plotWidth = Math.Max(80, Bounds.Width - rightAxisWidth - 14);
            double step = plotWidth / Math.Max(1, visibleBarCount);
            double dx = pos.X - dragStartPoint.X;
            int barDelta = (int)Math.Round(dx / step);
            int minRight = 0;
            rightEdgeIndex = Math.Clamp(dragStartRightEdgeIndex - barDelta, minRight, bars.Count - 1);

            if (!isAutoScale)
            {
                double dy = pos.Y - dragStartPoint.Y;
                double pricePerPixel = lastPriceSpan / Math.Max(1.0, lastCandleRectHeight);
                manualPriceCenterOffset = dragStartCenterOffset + (dy * pricePerPixel);
            }

            NotifyViewport();
            InvalidateVisual();
        }
        else
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverPoint = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (bars.Count == 0) return;

        const double rightAxisWidth = 76;
        Point pos = e.GetPosition(this);
        bool inPriceAxis = pos.X >= Bounds.Width - rightAxisWidth;

        if (inPriceAxis)
        {
            double factor = e.Delta.Y > 0 ? 0.90 : 1.10;
            manualPriceSpanFactor = Math.Clamp(manualPriceSpanFactor * factor, 0.15, 8.0);
            if (isAutoScale)
            {
                isAutoScale = false;
                AutoScaleChanged?.Invoke(false);
            }
            InvalidateVisual();
        }
        else
        {
            double factor = e.Delta.Y > 0 ? 0.85 : 1.18;
            ZoomStep(factor, pos.X);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 120 || bounds.Height < 120) return;

        var bgBrush = SolidColorBrush.Parse("#09090B");
        var borderPen = new Pen(SolidColorBrush.Parse("#222225"), 1);
        context.DrawRectangle(bgBrush, borderPen, bounds, 8, 8);

        if (bars.Count == 0)
        {
            DrawLabel(context, "Sin datos para mostrar en el gráfico.", new Point(24, 24), "#71717A", 13);
            return;
        }

        const double rightAxisWidth = 76;
        const double bottomAxisHeight = 26;
        const double topHudHeight = 32;

        double plotWidth = Math.Max(80, bounds.Width - rightAxisWidth - 14);
        double totalPlotHeight = Math.Max(100, bounds.Height - topHudHeight - bottomAxisHeight - 10);
        double candleHeight = totalPlotHeight * 0.76;
        double volumeHeight = totalPlotHeight * 0.20;

        var candleRect = new Rect(10, topHudHeight, plotWidth, candleHeight);
        var volumeRect = new Rect(10, candleRect.Bottom + totalPlotHeight * 0.04, plotWidth, volumeHeight);

        int endIdx = Math.Clamp(rightEdgeIndex, 0, bars.Count - 1);
        int startIdx = Math.Max(0, endIdx - visibleBarCount + 1);
        int count = endIdx - startIdx + 1;
        if (count <= 0) return;

        double minPrice = double.MaxValue;
        double maxPrice = double.MinValue;
        long maxVolume = 1;

        for (int i = startIdx; i <= endIdx; i++)
        {
            Bar b = (i == endIdx && liveFormingBar != null) ? liveFormingBar : bars[i];
            if (b.Low < minPrice) minPrice = b.Low;
            if (b.High > maxPrice) maxPrice = b.High;
            if (b.Volume > maxVolume) maxVolume = b.Volume;
        }

        double rawCenter = (maxPrice + minPrice) / 2.0;
        double rawSpan = Math.Max(0.25, maxPrice - minPrice);
        double pricePadding = Math.Max(1.0, rawSpan * 0.06);

        double priceSpan;
        if (!isAutoScale)
        {
            double baseSpan = rawSpan + (pricePadding * 2.0);
            priceSpan = Math.Max(0.1, baseSpan * manualPriceSpanFactor);
            double center = rawCenter + manualPriceCenterOffset;
            minPrice = center - (priceSpan / 2.0);
            maxPrice = center + (priceSpan / 2.0);
        }
        else
        {
            minPrice -= pricePadding;
            maxPrice += pricePadding;
            priceSpan = Math.Max(0.25, maxPrice - minPrice);
        }

        lastPriceSpan = priceSpan;
        lastCandleRectHeight = candleRect.Height;

        var gridPen = new Pen(SolidColorBrush.Parse("#161618"), 1);

        // Cuadrícula horizontal y eje derecho de precios
        const int priceSteps = 7;
        for (int i = 0; i <= priceSteps; i++)
        {
            double ratio = i / (double)priceSteps;
            double y = candleRect.Top + ratio * candleRect.Height;
            double priceVal = maxPrice - ratio * priceSpan;
            context.DrawLine(gridPen, new Point(candleRect.Left, y), new Point(candleRect.Right, y));
            DrawLabel(context, priceVal.ToString("F2", CultureInfo.InvariantCulture), new Point(candleRect.Right + 8, y - 7), "#71717A", 10.5);
        }

        double step = candleRect.Width / Math.Max(1, visibleBarCount);
        double bodyWidth = Math.Clamp(step * 0.68, 1.5, 20);

        // Cuadrícula vertical y eje inferior de tiempo UTC
        int timeSteps = Math.Min(7, count);
        for (int i = 0; i < timeSteps; i++)
        {
            int barOffset = (int)Math.Round(i * (count - 1) / (double)Math.Max(1, timeSteps - 1));
            int barIdx = startIdx + barOffset;
            double x = candleRect.Left + (barOffset + 0.5) * step;
            context.DrawLine(gridPen, new Point(x, candleRect.Top), new Point(x, volumeRect.Bottom));

            if (barIdx >= 0 && barIdx < bars.Count)
            {
                string timeLabel = bars[barIdx].CloseTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                DrawLabel(context, timeLabel, new Point(Math.Max(10, x - 44), volumeRect.Bottom + 6), "#A1A1AA", 10);
            }
        }

        // Separador de la barra inferior de tiempo
        var timeBarPen = new Pen(SolidColorBrush.Parse("#27272A"), 1);
        context.DrawLine(timeBarPen, new Point(candleRect.Left, volumeRect.Bottom + 2), new Point(candleRect.Right, volumeRect.Bottom + 2));

        // Separador entre velas y volumen
        context.DrawLine(borderPen, new Point(candleRect.Left, volumeRect.Top - 4), new Point(candleRect.Right, volumeRect.Top - 4));
        DrawLabel(context, "VOLUMEN", new Point(candleRect.Left + 6, volumeRect.Top + 2), "#52525B", 9.5, true);

        // Paleta Glassmorphic Dark Institucional
        var bullBrush = SolidColorBrush.Parse("#00E599");
        var bearBrush = SolidColorBrush.Parse("#FF355E");
        var bullBodyBrush = SolidColorBrush.Parse("#E600E599");
        var bearBodyBrush = SolidColorBrush.Parse("#E6FF355E");
        var bullVolBrush = SolidColorBrush.Parse("#2800E599");
        var bearVolBrush = SolidColorBrush.Parse("#28FF355E");
        var bullVolCapPen = new Pen(SolidColorBrush.Parse("#6600E599"), 1);
        var bearVolCapPen = new Pen(SolidColorBrush.Parse("#66FF355E"), 1);
        var bullPen = new Pen(bullBrush, 1.2);
        var bearPen = new Pen(bearBrush, 1.2);
        var bullOutlinePen = new Pen(bullBrush, 1.0);
        var bearOutlinePen = new Pen(bearBrush, 1.0);

        // Dibujar Velas OHLC y Barras de Volumen
        for (int i = startIdx; i <= endIdx; i++)
        {
            Bar bar = (i == endIdx && liveFormingBar != null) ? liveFormingBar : bars[i];
            int localIdx = i - startIdx;
            double centerX = candleRect.Left + (localIdx + 0.5) * step;

            double highY = PriceToY(bar.High, minPrice, priceSpan, candleRect);
            double lowY = PriceToY(bar.Low, minPrice, priceSpan, candleRect);
            double openY = PriceToY(bar.Open, minPrice, priceSpan, candleRect);
            double closeY = PriceToY(bar.Close, minPrice, priceSpan, candleRect);

            bool bullish = bar.Close >= bar.Open;
            context.DrawLine(bullish ? bullPen : bearPen, new Point(centerX, highY), new Point(centerX, lowY));

            double top = Math.Min(openY, closeY);
            double height = Math.Max(1.5, Math.Abs(closeY - openY));
            var bodyRect = new Rect(centerX - bodyWidth / 2, top, bodyWidth, height);

            if (bodyWidth >= 3.5)
            {
                context.DrawRectangle(
                    bullish ? bullBodyBrush : bearBodyBrush,
                    bullish ? bullOutlinePen : bearOutlinePen,
                    bodyRect, 1, 1);
            }
            else
            {
                context.FillRectangle(bullish ? bullBrush : bearBrush, bodyRect);
            }

            // Barra de Volumen con aspecto Glassmorphic
            double volRatio = bar.Volume / (double)maxVolume;
            double volBarHeight = Math.Max(1.0, volRatio * (volumeRect.Height - 4));
            double volTop = volumeRect.Bottom - volBarHeight;
            var volRect = new Rect(centerX - bodyWidth / 2, volTop, bodyWidth, volBarHeight);
            context.FillRectangle(bullish ? bullVolBrush : bearVolBrush, volRect);
            if (volBarHeight >= 3 && bodyWidth >= 2)
            {
                context.DrawLine(
                    bullish ? bullVolCapPen : bearVolCapPen,
                    new Point(volRect.Left, volTop),
                    new Point(volRect.Right, volTop));
            }
        }

        // Línea punteada del último precio visible (o precio del tick en vivo)
        Bar lastVisibleBar = (liveFormingBar != null && endIdx >= startIdx) ? liveFormingBar : bars[endIdx];
        double lastPrice = (liveFormingBar != null && currentLiveTick != null) ? currentLiveTick.Price : lastVisibleBar.Close;
        double lastCloseY = PriceToY(lastPrice, minPrice, priceSpan, candleRect);
        bool lastBull = lastVisibleBar.Close >= lastVisibleBar.Open;
        var lastPricePen = new Pen(SolidColorBrush.Parse(lastBull ? "#00E599" : "#FF355E"), 1, new DashStyle(new double[] { 4, 3 }, 0));
        context.DrawLine(lastPricePen, new Point(candleRect.Left, lastCloseY), new Point(candleRect.Right, lastCloseY));

        var lastTagRect = new Rect(candleRect.Right + 2, Math.Clamp(lastCloseY - 9, candleRect.Top, candleRect.Bottom - 18), rightAxisWidth - 6, 18);
        context.FillRectangle(SolidColorBrush.Parse(lastBull ? "#00C27F" : "#E63956"), lastTagRect, 4);
        string lastTagText = (liveFormingBar != null && currentLiveTick != null)
            ? $"{lastPrice:F2} ⚡"
            : lastVisibleBar.Close.ToString("F2", CultureInfo.InvariantCulture);
        DrawLabel(context, lastTagText, new Point(lastTagRect.Left + 5, lastTagRect.Top + 2), "#FFFFFF", 10.5, true);

        // Mira interactiva (Crosshair) y barra HUD superior con datos OHLCV
        Bar inspectedBar = lastVisibleBar;
        if (hoverPoint.HasValue && candleRect.WithHeight(totalPlotHeight).Contains(hoverPoint.Value))
        {
            Point hp = hoverPoint.Value;
            int hoveredLocal = (int)((hp.X - candleRect.Left) / step);
            bool overCandles = hoveredLocal >= 0 && hoveredLocal < count;

            if (overCandles)
            {
                inspectedBar = bars[startIdx + hoveredLocal];
                double snapX = candleRect.Left + (hoveredLocal + 0.5) * step;

                var crossPen = new Pen(SolidColorBrush.Parse("#3F3F46"), 1, new DashStyle(new double[] { 3, 3 }, 0));
                context.DrawLine(crossPen, new Point(snapX, candleRect.Top), new Point(snapX, volumeRect.Bottom));

                // Etiqueta flotante de fecha en la barra de tiempo (estricto blanco y negro)
                string hoverTimeText = inspectedBar.CloseTimeUtc.ToString("yyyy-MM-dd HH:mm UTC", CultureInfo.InvariantCulture);
                var timeTagRect = new Rect(Math.Clamp(snapX - 62, candleRect.Left, candleRect.Right - 124), volumeRect.Bottom + 3, 124, 18);
                context.DrawRectangle(
                    SolidColorBrush.Parse("#000000"),
                    new Pen(SolidColorBrush.Parse("#FFFFFF"), 1),
                    timeTagRect, 4, 4);
                DrawLabel(context, hoverTimeText, new Point(timeTagRect.Left + 8, timeTagRect.Top + 2), "#FFFFFF", 10, true);
            }

            if (candleRect.Contains(hp))
            {
                var crossPen = new Pen(SolidColorBrush.Parse("#3F3F46"), 1, new DashStyle(new double[] { 3, 3 }, 0));
                context.DrawLine(crossPen, new Point(candleRect.Left, hp.Y), new Point(candleRect.Right, hp.Y));

                double hoverPrice = maxPrice - ((hp.Y - candleRect.Top) / candleRect.Height) * priceSpan;
                var priceTagRect = new Rect(candleRect.Right + 2, Math.Clamp(hp.Y - 9, candleRect.Top, candleRect.Bottom - 18), rightAxisWidth - 6, 18);
                context.DrawRectangle(
                    SolidColorBrush.Parse("#18181B"),
                    new Pen(SolidColorBrush.Parse("#3F3F46"), 1),
                    priceTagRect, 4, 4);
                DrawLabel(context, hoverPrice.ToString("F2", CultureInfo.InvariantCulture), new Point(priceTagRect.Left + 6, priceTagRect.Top + 2), "#FFFFFF", 10.5, true);
            }
        }

        // HUD Superior con valores exactos de la vela bajo el cursor o en Tick Replay
        string hudText;
        string hudColor;

        if (liveFormingBar != null && currentLiveTick != null && !hoverPoint.HasValue)
        {
            double deltaLive = liveFormingBar.Close - liveFormingBar.Open;
            string liveSign = deltaLive >= 0 ? "+" : "";
            hudText = $"⚡ TICK REPLAY · Tick {liveTickIndex + 1}/{liveTickTotal} · " +
                      $"Tick: {currentLiveTick.Price:F2} (Vol: {currentLiveTick.Volume}) · " +
                      $"O: {liveFormingBar.Open:F2}  H: {liveFormingBar.High:F2}  L: {liveFormingBar.Low:F2}  C: {liveFormingBar.Close:F2}  " +
                      $"Var: {liveSign}{deltaLive:F2}  Vol Acum: {liveFormingBar.Volume:N0}";
            hudColor = liveFormingBar.Close >= liveFormingBar.Open ? "#00E599" : "#FF355E";
        }
        else
        {
            double changePts = inspectedBar.Close - inspectedBar.Open;
            double changePct = inspectedBar.Open != 0 ? (changePts / inspectedBar.Open) * 100.0 : 0.0;
            string sign = changePts >= 0 ? "+" : "";
            hudText =
                $"NQ ({timeframeLabel})  ·  {inspectedBar.CloseTimeUtc:yyyy-MM-dd HH:mm} UTC   " +
                $"O: {inspectedBar.Open:F2}   H: {inspectedBar.High:F2}   L: {inspectedBar.Low:F2}   C: {inspectedBar.Close:F2}   " +
                $"Cambio: {sign}{changePts:F2} ({sign}{changePct:F2}%)   Vol: {inspectedBar.Volume:N0}";
            hudColor = inspectedBar.Close >= inspectedBar.Open ? "#00E599" : "#FF355E";
        }

        DrawLabel(context, hudText, new Point(12, 8), hudColor, 11.5, true);
    }

    private static double PriceToY(double price, double min, double span, Rect rect)
        => rect.Bottom - ((price - min) / span) * rect.Height;

    private static void DrawLabel(DrawingContext context, string text, Point point, string colorHex, double size = 11, bool bold = false)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, Sans-Serif", FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size,
            SolidColorBrush.Parse(colorHex));
        context.DrawText(formatted, point);
    }
}
