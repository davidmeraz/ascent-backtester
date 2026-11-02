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
            LoadDates(workspacePath, "ohlcv-1m", ohlcvDates, "NQ_1min_UTC_cierre.txt");
            LoadDates(workspacePath, "tbbo", tbboDates, "NQ_1min_UTC_cierre.txt");
            isInitialized = true;
        }
    }

    private static void LoadDates(string workspacePath, string schema, HashSet<int> targetSet, string fallbackTxt)
    {
        try
        {
            string cachePath = Path.Combine(workspacePath, "data", "databento", schema, "dates_cache.json");
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

            string txtPath = Path.Combine(workspacePath, "data", "databento", schema, fallbackTxt);
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
                        targetSet.Add(d);
                        list.Add(d);
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
        DateTime max = tbbo ? new(2016, 9, 21, 0, 0, 0, DateTimeKind.Utc) : new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
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
        DateTime max = tbbo ? new(2016, 9, 21, 0, 0, 0, DateTimeKind.Utc) : new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        DateTime mStart = new(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime mEnd = new(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return mEnd >= min && mStart <= max;
    }

    public static bool HasYear(bool tbbo, int year)
    {
        DateTime min = new(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc);
        DateTime max = tbbo ? new(2016, 9, 21, 0, 0, 0, DateTimeKind.Utc) : new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        return year >= min.Year && year <= max.Year;
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
    private readonly string? rolloversFilePath;

    // Controles de navegación del menú inferior
    private readonly Button navChartBtn;
    private readonly Button navRolloversBtn;
    private readonly Button navAnalyzerBtn;
    private readonly Button navSchemaBtn;
    private readonly Button navDateTfBtn;
    private readonly Button navConfigBtn;

    // Ventana flotante modal centrada
    private readonly Grid modalOverlay;
    private readonly Border modalCard;
    private readonly Control[] modalViews;

    private readonly Control chartPageView;
    private readonly Control rolloversPageView;
    private readonly Control analyzerPageView;

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
    private StackPanel? btPropFirmsRows;
    private StackPanel? btTradesRows;
    private Control? btPropFirmsContainer;
    private Control? btTradesContainer;
    private Button? btTabPropFirmsBtn;
    private Button? btTabTradesBtn;
    private bool isRunningBacktest;

    private IReadOnlyList<Bar> rawMinuteBars = Array.Empty<Bar>();
    private IReadOnlyList<Tick> rawTicks = Array.Empty<Tick>();
    private bool suppressSliderEvents;
    private bool isLoading;

    private bool IsTbboMode => schemaBox.SelectedIndex == 1;

    public MainWindow(string workspacePath)
    {
        workspaceRoot = workspacePath;
        defaultDatasetPath = Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: false)
            ?? Path.Combine(workspacePath, "data", "databento", "ohlcv-1m", "NQ_1min_UTC_cierre.txt");
        adjustedDatasetPath = Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: true)
            ?? Path.Combine(workspacePath, "data", "databento", "ohlcv-1m", "NQ_1min_UTC_cierre_ajustado.txt");
        tbboDatasetPath = Workspace.FindDatabentoTbboFile(workspacePath)
            ?? Path.Combine(workspacePath, "data", "databento", "tbbo", "NQ_ticks_UTC_L1.txt");
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
            out modalViews);

        rolloversPageView = BuildRolloversPage(
            out rolloverTableRows,
            out rolloverChartSurface,
            out sortToggleBtn);

        analyzerPageView = BuildAnalyzerPage();

        var pageContainer = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12)
        };
        pageContainer.Children.Add(chartPageView);
        pageContainer.Children.Add(rolloversPageView);
        pageContainer.Children.Add(analyzerPageView);
        Grid.SetRow(pageContainer, 1);
        outerGrid.Children.Add(pageContainer);

        // === 3. MENÚ INFERIOR CON 3 SECCIONES (HERRAMIENTAS · NAVEGACIÓN · CONFIGURACIÓN) ===
        var bottomMenu = BuildBottomMenuBar(
            out navChartBtn,
            out navRolloversBtn,
            out navAnalyzerBtn,
            out navSchemaBtn,
            out navDateTfBtn,
            out navConfigBtn);
        Grid.SetRow(bottomMenu, 2);
        outerGrid.Children.Add(bottomMenu);

        navSchemaBtn.Click += (_, _) => { SwitchPage(0); OpenModal(0); };
        navDateTfBtn.Click += (_, _) => { SwitchPage(0); OpenModal(1); };
        navConfigBtn.Click += (_, _) => OpenModal(2);

        // === 4. CAPA MODAL CENTRADA FLOTANTE ===
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

        navChartBtn.Background = pageIndex == 0 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navChartBtn.Foreground = SolidColorBrush.Parse(pageIndex == 0 ? "#09090B" : "#A1A1AA");

        navRolloversBtn.Background = pageIndex == 1 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navRolloversBtn.Foreground = SolidColorBrush.Parse(pageIndex == 1 ? "#09090B" : "#A1A1AA");

        navAnalyzerBtn.Background = pageIndex == 2 ? SolidColorBrush.Parse("#FFFFFF") : Brushes.Transparent;
        navAnalyzerBtn.Foreground = SolidColorBrush.Parse(pageIndex == 2 ? "#09090B" : "#A1A1AA");
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
        out Button schemaBtn,
        out Button dateTfBtn,
        out Button configBtn)
    {
        // 1. Herramientas del gráfico (extremo izquierdo)
        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        schemaBtn = MakeBottomNavButton("Esquema");
        dateTfBtn = MakeBottomNavButton("Fecha y Temporalidad");

        leftGroup.Children.Add(schemaBtn);
        leftGroup.Children.Add(dateTfBtn);

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

        menuButtonsStack.Children.Add(chartTabBtn);
        menuButtonsStack.Children.Add(rolloversTabBtn);
        menuButtonsStack.Children.Add(analyzerTabBtn);

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
                "tbbo · Ticks L1 Bid/Ask (2010–2016)"
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
            Text = "Cobertura TBBO: 2010-06-07 a 2016-09-21 UTC (229.8M ticks)",
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
                    Text = "• Esquema ohlcv-1m: Velas históricas completas de 1 minuto (2010 a 2026).\n• Esquema tbbo: Ticks con bid/ask exacto y cálculo de delta de volumen (2010 a 2016).",
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

    private Control BuildAnalyzerPage()
    {
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("315,14,*")
        };

        var sidebarScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var sidebarStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 4, 0) };

        btStrategyBox = new ComboBox
        {
            ItemsSource = new[]
            {
                "VwapDeltaPullback (VWAP + ATR + Delta)",
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
                "OHLCV 1m · Serie Original (2010–2026)",
                "OHLCV 1m · Serie Back-Adjusted (2010–2026)",
                "TBBO Ticks L1 · Bid/Ask Exacto (2010–2016)"
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

        sidebarStack.Children.Add(MakeSidebarCard("1. ESTRATEGIA E INSTRUMENTO", "#FFFFFF", new Control[]
        {
            Labeled("Estrategia activa (Core)", btStrategyBox),
            Labeled("Instrumento y Valor por Punto", btInstrumentBox),
            Labeled("Fuente de datos histórica", btDataModeBox),
            TwoColumn(
                Labeled("Contratos", btQtyBox),
                TwoColumn(Labeled("SMA Ráp.", btFastBox), Labeled("SMA Lenta", btSlowBox)))
        }));

        btFromBox = MakeInput("2026-10-01", "YYYY-MM-DD");
        btToBox = MakeInput("2026-10-30", "YYYY-MM-DD");
        btSessionStartBox = MakeInput("13:30", "HH:mm");
        btSessionEndBox = MakeInput("20:59", "HH:mm");

        sidebarStack.Children.Add(MakeSidebarCard("2. PERIODO Y SESIÓN OPERATIVA", "#FFFFFF", new Control[]
        {
            TwoColumn(
                Labeled("Desde (UTC)", btFromBox),
                Labeled("Hasta (UTC)", btToBox)),
            TwoColumn(
                Labeled("Inicio Sesión UTC", btSessionStartBox),
                Labeled("Cierre Sesión UTC", btSessionEndBox))
        }));

        btSlTicksBox = MakeInput("", "Vacío = 1.5x ATR");
        btTpTicksBox = MakeInput("", "Vacío = 2.5x ATR");
        btBeTriggerBox = MakeInput("", "Ticks (opcional)");
        btBeOffsetBox = MakeInput("2", "Ticks");
        btTrailTicksBox = MakeInput("", "Ticks (opcional)");
        btCommBox = MakeInput("2.10", "USD/lado");
        btSlippageBox = MakeInput("1", "Ticks");
        btMaxDailyLossBox = MakeInput("1500", "USD");
        btMaxDailyTradesBox = MakeInput("4", "Máx/día");

        btRunBtn = new Button
        {
            Content = "▶  Ejecutar Analizador & Evaluación",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.Bold,
            FontSize = 12.5,
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8)
        };
        btRunBtn.Click += async (_, _) => await RunInteractiveBacktestAsync();

        btStatusText = new TextBlock
        {
            Text = "Configura los parámetros y pulsa 'Ejecutar Analizador'.",
            Foreground = SolidColorBrush.Parse("#A1A1AA"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };

        sidebarStack.Children.Add(MakeSidebarCard("3. GESTIÓN DE RIESGO Y COSTOS REALES", "#FFFFFF", new Control[]
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
            btRunBtn,
            new Border
            {
                Background = SolidColorBrush.Parse("#161618"),
                BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7),
                Child = btStatusText
            }
        }));

        sidebarScroll.Content = sidebarStack;
        Grid.SetColumn(sidebarScroll, 0);
        bodyGrid.Children.Add(sidebarScroll);

        // Panel Derecho: Tarjetas Cuantitativas + Curva de Equidad + Tablas (Prop Firms / Trades)
        var rightGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,10,175,10,*")
        };
        Grid.SetColumn(rightGrid, 2);

        var kpiGrid = new UniformGrid { Columns = 6 };
        kpiGrid.Children.Add(MakeDynamicKpiCard("P&L NETO TOTAL", "—", "Bruto: — | Com: —", "#00E599", out kpiNetPnlVal, out kpiNetPnlSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("WIN RATE & TRADES", "—", "Gan: — | Perd: —", "#FFFFFF", out kpiWinRateVal, out kpiWinRateSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("PROFIT FACTOR & PAYOFF", "—", "Esperanza: — / trade", "#FFFFFF", out kpiPfVal, out kpiPfSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("DRAWDOWN REAL", "—", "DD Cerrado: —", "#FF355E", out kpiDdVal, out kpiDdSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("SHARPE & SORTINO", "—", "Recovery Factor: —", "#FFFFFF", out kpiSharpeVal, out kpiSharpeSub));
        kpiGrid.Children.Add(MakeDynamicKpiCard("CONSISTENCIA DIARIA", "—", "Rachas: — | Duración: —", "#E4E4E7", out kpiDaysVal, out kpiDaysSub));
        Grid.SetRow(kpiGrid, 0);
        rightGrid.Children.Add(kpiGrid);

        btEquitySurface = new EquityCurveSurface();
        var eqBorder = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 14,
                        Margin = new Thickness(0, 0, 0, 5),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "CURVA DE EQUIDAD ACUMULADA Y PICO INTRADÍA TRAILING",
                                Foreground = SolidColorBrush.Parse("#F4F4F5"),
                                FontWeight = FontWeight.Bold,
                                FontSize = 11.5
                            },
                            new TextBlock
                            {
                                Text = "● Línea Verde/Roja: Equidad Neta Cerrada   ● Línea Punteada Gris: Pico Intradía Trailing (con MFE)",
                                Foreground = SolidColorBrush.Parse("#71717A"),
                                FontSize = 11
                            }
                        }
                    },
                    btEquitySurface
                }
            }
        };
        Grid.SetRow(btEquitySurface, 1);
        Grid.SetRow(eqBorder, 2);
        rightGrid.Children.Add(eqBorder);

        // Panel inferior con pestañas: Empresas de Fondeo vs Historial de Operaciones
        var detailsOuterGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,8,*")
        };

        var subTabsBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        btTabPropFirmsBtn = new Button
        {
            Content = "🏢  Evaluación de Empresas de Fondeo (Apex · Topstep · MFFU · TPT)",
            Background = SolidColorBrush.Parse("#FFFFFF"),
            Foreground = SolidColorBrush.Parse("#09090B"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11.5,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(6)
        };
        btTabPropFirmsBtn.Click += (_, _) => SwitchAnalyzerSubTab(0);

        btTabTradesBtn = new Button
        {
            Content = "📑  Desglose Operación por Operación (MAE · MFE · R-Múltiplo · Comisiones)",
            Background = SolidColorBrush.Parse("#18181B"),
            Foreground = SolidColorBrush.Parse("#71717A"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11.5,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(6)
        };
        btTabTradesBtn.Click += (_, _) => SwitchAnalyzerSubTab(1);

        subTabsBar.Children.Add(btTabPropFirmsBtn);
        subTabsBar.Children.Add(btTabTradesBtn);
        Grid.SetRow(subTabsBar, 0);
        detailsOuterGrid.Children.Add(subTabsBar);

        // Tabla 1: Empresas de Fondeo
        var pfGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var pfHeader = new Border
        {
            Background = SolidColorBrush.Parse("#161618"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7),
            Margin = new Thickness(0, 0, 0, 5)
        };
        var pfHeaderCols = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("115,175,115,115,120,80,95,*")
        };
        AddTableHeader(pfHeaderCols, 0, "Estado");
        AddTableHeader(pfHeaderCols, 1, "Plan de Fondeo");
        AddTableHeader(pfHeaderCols, 2, "P&L Neto Final");
        AddTableHeader(pfHeaderCols, 3, "Max DD Real");
        AddTableHeader(pfHeaderCols, 4, "Colchón Mín.");
        AddTableHeader(pfHeaderCols, 5, "Días Op.");
        AddTableHeader(pfHeaderCols, 6, "% Mejor Día");
        AddTableHeader(pfHeaderCols, 7, "Diagnóstico de Reglas");
        pfHeader.Child = pfHeaderCols;
        Grid.SetRow(pfHeader, 0);
        pfGrid.Children.Add(pfHeader);

        btPropFirmsRows = new StackPanel { Spacing = 4 };
        var pfScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = btPropFirmsRows
        };
        Grid.SetRow(pfScroll, 1);
        pfGrid.Children.Add(pfScroll);
        btPropFirmsContainer = pfGrid;

        // Tabla 2: Operaciones individuales
        var trGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), IsVisible = false };
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
            ColumnDefinitions = new ColumnDefinitions("45,65,130,90,130,90,85,100,120,75,105,*")
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
        btTradesContainer = trGrid;

        var tablesHolder = new Grid();
        tablesHolder.Children.Add(btPropFirmsContainer);
        tablesHolder.Children.Add(btTradesContainer);
        Grid.SetRow(tablesHolder, 2);
        detailsOuterGrid.Children.Add(tablesHolder);

        var detailsCard = new Border
        {
            Background = SolidColorBrush.Parse("#121214"),
            BorderBrush = SolidColorBrush.Parse("#FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Child = detailsOuterGrid
        };
        Grid.SetRow(detailsCard, 4);
        rightGrid.Children.Add(detailsCard);

        bodyGrid.Children.Add(rightGrid);
        return bodyGrid;
    }

    private void SwitchAnalyzerSubTab(int index)
    {
        if (btPropFirmsContainer is null || btTradesContainer is null || btTabPropFirmsBtn is null || btTabTradesBtn is null)
            return;

        bool showProp = index == 0;
        btPropFirmsContainer.IsVisible = showProp;
        btTradesContainer.IsVisible = !showProp;

        btTabPropFirmsBtn.Background = SolidColorBrush.Parse(showProp ? "#FFFFFF" : "#18181B");
        btTabPropFirmsBtn.Foreground = SolidColorBrush.Parse(showProp ? "#09090B" : "#71717A");

        btTabTradesBtn.Background = SolidColorBrush.Parse(!showProp ? "#FFFFFF" : "#18181B");
        btTabTradesBtn.Foreground = SolidColorBrush.Parse(!showProp ? "#09090B" : "#71717A");
    }

    private static Border MakeDynamicKpiCard(string title, string initialVal, string initialSub, string accentHex, out TextBlock valBlock, out TextBlock subBlock)
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
        btRunBtn.IsEnabled = false;
        btRunBtn.Content = "Calculando Simulación...";
        btStatusText.Text = "Cargando datos y ejecutando motor de backtesting...";
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

            string sourcePath = dataModeIdx switch
            {
                1 => File.Exists(adjustedDatasetPath) ? adjustedDatasetPath : defaultDatasetPath,
                2 => tbboDatasetPath,
                _ => defaultDatasetPath
            };

            if (!File.Exists(sourcePath))
            {
                btStatusText.Text = $"No se encontró el archivo de datos: {Path.GetFileName(sourcePath)}";
                btStatusText.Foreground = SolidColorBrush.Parse("#FF355E");
                return;
            }

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

            bool useTicks = dataModeIdx == 2;

            var (result, evaluations) = await Task.Run(() =>
            {
                IReadOnlyList<Bar> bars = useTicks ? Array.Empty<Bar>() : new NinjaTraderMinuteImporter().Read(sourcePath, fromUtc, toUtc);
                IReadOnlyList<Tick> ticks = useTicks ? new NinjaTraderTickImporter().Read(sourcePath, fromUtc, toUtc) : Array.Empty<Tick>();

                IStrategy strategy = strategyIdx == 0
                    ? new VwapDeltaPullbackStrategy(new VwapDeltaPullbackParameters(
                        quantity: qty,
                        anchorTimeUtc: sessionStart.HasValue ? sessionStart.Value.ToTimeSpan() : new TimeSpan(13, 30, 0),
                        stopLossTicks: slTicks,
                        takeProfitTicks: tpTicks,
                        breakEvenTriggerTicks: beTrigger,
                        breakEvenOffsetTicks: beOffset,
                        trailingStopTicks: trailTicks,
                        tickSize: spec.TickSize))
                    : new SmaCrossoverStrategy(new StrategyParameters(
                        fast, slow, qty, slTicks, tpTicks, beTrigger, beOffset, trailTicks));

                var btResult = new BacktestRunner().Run(new BacktestRequest
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
                    MaxTradesPerDay = maxDailyTrades
                });

                string propRoot = Workspace.FindPropFirmsDirectory(workspaceRoot);
                var firms = PropFirmEvaluator.LoadAll(propRoot);
                var evaluator = new PropFirmEvaluator();
                var evals = firms
                    .SelectMany(f => f.Plans.Select(p => evaluator.Evaluate(btResult, f, p)))
                    .ToList();

                return (btResult, evals);
            });

            PopulateAnalyzerResults(result, evaluations);
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
            btRunBtn.IsEnabled = true;
            btRunBtn.Content = "▶  Ejecutar Analizador & Evaluación";
        }
    }

    private void PopulateAnalyzerResults(BacktestResult result, List<PropFirmEvaluationResult> evaluations)
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

        btEquitySurface?.SetTrades(result.Trades);

        if (btPropFirmsRows is not null)
        {
            btPropFirmsRows.Children.Clear();
            foreach (var ev in evaluations)
            {
                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("115,175,115,115,120,80,95,*"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                string badgeText = ev.Passed ? "APROBADA" : ev.AccountBlown ? "QUEMADA" : "PENDIENTE";
                string badgeBg = ev.Passed ? "#14291F" : ev.AccountBlown ? "#2A1417" : "#18181B";
                string badgeFg = ev.Passed ? "#00E599" : ev.AccountBlown ? "#FF355E" : "#A1A1AA";
                var statusBadge = MakeBadge(badgeText, badgeBg, badgeFg);
                statusBadge.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetColumn(statusBadge, 0);
                rowGrid.Children.Add(statusBadge);

                AddTableCell(rowGrid, 1, ev.PlanName, "#FFFFFF", true);
                AddTableCell(rowGrid, 2, $"{ev.FinalNetProfitUsd:+$#,##0.00;-$#,##0.00;$0.00}", ev.FinalNetProfitUsd >= 0 ? "#00E599" : "#FF355E", true);
                AddTableCell(rowGrid, 3, $"${ev.MaxObservedDrawdownUsd:#,##0.00}", "#FF355E");
                AddTableCell(rowGrid, 4, $"${ev.MinRemainingBufferUsd:#,##0.00}", ev.MinRemainingBufferUsd > 0 ? "#FFFFFF" : "#FF355E", true);
                AddTableCell(rowGrid, 5, $"{ev.TradingDays} días", "#A1A1AA");
                AddTableCell(rowGrid, 6, $"{ev.BestDayProfitSharePercent:F1}%", ev.ConsistencySatisfied ? "#71717A" : "#FF355E");
                AddTableCell(rowGrid, 7, ev.SummaryReason, "#E4E4E7");

                btPropFirmsRows.Children.Add(new Border
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

        if (btTradesRows is not null)
        {
            btTradesRows.Children.Clear();
            foreach (var tr in result.Trades.Take(400))
            {
                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("45,65,130,90,130,90,85,100,120,75,105,*"),
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
                    CornerRadius = new CornerRadius(4)
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
                    Background = SolidColorBrush.Parse("#141416"),
                    BorderBrush = SolidColorBrush.Parse("#1F1F23"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 5),
                    Child = rowGrid
                });
            }
        }
    }

    private static string GetSpanishMonthAbbrev(int month) => month switch
    {
        3 => "Mar",
        6 => "Jun",
        9 => "Sep",
        12 => "Dic",
        _ => month.ToString("00", CultureInfo.InvariantCulture)
    };

    private static void AddTableHeader(Grid g, int col, string text)
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

    private static void AddTableCell(Grid g, int col, string text, string fgHex, bool bold = false)
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
    private static readonly DateTime MaxTbboDatasetDate = new(2016, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private DateTime CurrentMaxDatasetDate => IsTbboMode ? MaxTbboDatasetDate : MaxOhlcvDatasetDate;

    private async Task OnSchemaChangedAsync()
    {
        suppressTimeframeEvents = true;
        try
        {
            if (IsTbboMode)
            {
                schemaCoverageText.Text = "Cobertura TBBO: 2010-06-07 a 2016-09-21 UTC (229.8M ticks)";
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

        if (!File.Exists(activePath))
        {
            statusBanner.Text = $"No se encontró el archivo del esquema: {Path.GetFileName(activePath)}";
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
            infoRangeText.Text = "Sin ticks TBBO en el rango (Cobertura: 2010-06-07 a 2016-09-21)";
            infoPriceBoundsText.Text = "—";
            infoOrderFlowText.Text = "—";
            statusBanner.Text = "No hay ticks TBBO en ese rango. Selecciona fechas entre 2010-06-07 y 2016-09-21.";
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

    public void SetTrades(IReadOnlyList<TradeRecord> closedTrades)
    {
        trades = closedTrades;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 100 || bounds.Height < 50) return;

        context.DrawRectangle(
            SolidColorBrush.Parse("#09090B"),
            new Pen(SolidColorBrush.Parse("#222225"), 1),
            bounds, 8, 8);

        if (trades.Count == 0)
        {
            var ft = new FormattedText(
                "Ejecuta un backtest para visualizar la curva de equidad y el pico intradía trailing.",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, Segoe UI, Sans-Serif"),
                11.5,
                SolidColorBrush.Parse("#71717A"));
            context.DrawText(ft, new Point(18, bounds.Height / 2 - 8));
            return;
        }

        var plot = new Rect(16, 12, Math.Max(60, bounds.Width - 104), Math.Max(26, bounds.Height - 28));

        double minEq = 0d;
        double maxEq = 100d;
        double runningPeak = 0d;
        var peaks = new double[trades.Count];

        for (int i = 0; i < trades.Count; i++)
        {
            double prevEq = i == 0 ? 0d : trades[i - 1].CumulativeNetProfitUsd;
            double tradePeak = prevEq + trades[i].MaxFavorableExcursionUsd - trades[i].Commission * 0.5;
            runningPeak = Math.Max(runningPeak, Math.Max(tradePeak, trades[i].CumulativeNetProfitUsd));
            peaks[i] = runningPeak;

            minEq = Math.Min(minEq, Math.Min(trades[i].CumulativeNetProfitUsd, prevEq - trades[i].MaxAdverseExcursionUsd));
            maxEq = Math.Max(maxEq, runningPeak);
        }

        double span = Math.Max(50d, maxEq - minEq);
        double zeroY = plot.Bottom - ((0d - minEq) / span) * plot.Height;
        context.DrawLine(new Pen(SolidColorBrush.Parse("#222225"), 1), new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));

        double finalNet = trades[trades.Count - 1].CumulativeNetProfitUsd;
        var eqPen = new Pen(SolidColorBrush.Parse(finalNet >= 0 ? "#00E599" : "#FF355E"), 2.0);
        var peakPen = new Pen(SolidColorBrush.Parse("#71717A"), 1.2, new DashStyle(new double[] { 4, 4 }, 0));

        Point prevEqPt = new(plot.Left, zeroY);
        Point prevPeakPt = new(plot.Left, zeroY);

        for (int i = 0; i < trades.Count; i++)
        {
            double x = plot.Left + ((i + 1) / (double)trades.Count) * plot.Width;
            double eqY = plot.Bottom - ((trades[i].CumulativeNetProfitUsd - minEq) / span) * plot.Height;
            double pkY = plot.Bottom - ((peaks[i] - minEq) / span) * plot.Height;

            var eqPt = new Point(x, eqY);
            var pkPt = new Point(x, pkY);

            context.DrawLine(peakPen, prevPeakPt, pkPt);
            context.DrawLine(eqPen, prevEqPt, eqPt);

            prevEqPt = eqPt;
            prevPeakPt = pkPt;
        }

        DrawLabel(context, $"{maxEq:+$#,##0;-$#,##0;$0}", new Point(plot.Right + 8, plot.Top - 2), "#FFFFFF");
        DrawLabel(context, "$0", new Point(plot.Right + 8, Math.Clamp(zeroY - 7, plot.Top, plot.Bottom - 12)), "#71717A");
        if (minEq < 0)
            DrawLabel(context, $"{minEq:+$#,##0;-$#,##0;$0}", new Point(plot.Right + 8, plot.Bottom - 12), "#FF355E");
    }

    private static void DrawLabel(DrawingContext context, string text, Point pt, string hex)
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
