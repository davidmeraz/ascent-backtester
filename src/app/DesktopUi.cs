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
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
    private readonly string defaultDatasetPath;
    private readonly string tbboDatasetPath;
    private readonly string? rolloversFilePath;

    // Controles de navegación entre páginas
    private readonly Button navChartBtn;
    private readonly Button navRolloversBtn;
    private readonly Control chartPageView;
    private readonly Control rolloversPageView;

    // Controles Página 1: Gráfico
    private Grid? chartPageBodyGrid;
    private ScrollViewer? chartSidebarScroll;
    private Button? toggleSidebarHeaderBtn;
    private Button? toggleSidebarBottomBtn;
    private bool isSidebarVisible = true;
    private readonly ComboBox schemaBox;
    private readonly TextBlock schemaCoverageText;
    private readonly ComboBox yearJumpBox;
    private readonly ComboBox monthJumpBox;
    private readonly TextBox fromBox;
    private readonly TextBox toBox;
    private readonly ComboBox timeframeBox;
    private readonly Button loadBtn;
    private readonly TextBlock statusBanner;
    private readonly TextBlock headerSubtitle;
    private readonly TextBlock infoBarsText;
    private readonly TextBlock infoRangeText;
    private readonly TextBlock infoPriceBoundsText;
    private readonly TextBlock infoOrderFlowText;
    private readonly Slider timelineSlider;
    private readonly ChartSurface chartSurface;
    private readonly List<(Button Button, int Mode)> presetButtons = new();
    private bool suppressDateComboEvents;
    private bool suppressTimeframeEvents;

    // Controles Página 2: Historial de Vencimientos
    private readonly StackPanel rolloverTableRows;
    private readonly RolloverAccumulationSurface rolloverChartSurface;
    private readonly Button sortToggleBtn;
    private List<RolloverComputedRow> rolloverRowsData = new();
    private bool sortDescending;

    private IReadOnlyList<Bar> rawMinuteBars = Array.Empty<Bar>();
    private IReadOnlyList<Tick> rawTicks = Array.Empty<Tick>();
    private bool suppressSliderEvents;
    private bool isLoading;

    private bool IsTbboMode => schemaBox.SelectedIndex == 1;

    public MainWindow(string workspacePath)
    {
        defaultDatasetPath = Workspace.FindDatabentoMinuteFile(workspacePath, adjusted: false)
            ?? Path.Combine(workspacePath, "data", "databento", "ohlcv-1m", "NQ_1min_UTC_cierre.txt");
        tbboDatasetPath = Workspace.FindDatabentoTbboFile(workspacePath)
            ?? Path.Combine(workspacePath, "data", "databento", "tbbo", "NQ_ticks_UTC_L1.txt");
        rolloversFilePath = Workspace.FindDatabentoRolloversFile(workspacePath);

        Title = "Ascent Futures Lab · Visualizador de Mercado & Vencimientos (NQ)";
        Width = 1460;
        Height = 900;
        MinWidth = 1080;
        MinHeight = 680;
        Background = SolidColorBrush.Parse("#070B14");

        var outerGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("54,*,54"),
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
            out fromBox,
            out toBox,
            out timeframeBox,
            out loadBtn,
            out statusBanner,
            out infoBarsText,
            out infoRangeText,
            out infoPriceBoundsText,
            out infoOrderFlowText,
            out timelineSlider,
            out chartSurface);

        rolloversPageView = BuildRolloversPage(
            out rolloverTableRows,
            out rolloverChartSurface,
            out sortToggleBtn);

        var pageContainer = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12)
        };
        pageContainer.Children.Add(chartPageView);
        pageContainer.Children.Add(rolloversPageView);
        Grid.SetRow(pageContainer, 1);
        outerGrid.Children.Add(pageContainer);

        // === 3. MENÚ INFERIOR CON SU PROPIO BORDER ===
        var bottomMenu = BuildBottomMenuBar(out navChartBtn, out navRolloversBtn);
        Grid.SetRow(bottomMenu, 2);
        outerGrid.Children.Add(bottomMenu);

        Content = outerGrid;

        LoadRolloverHistory();
        SwitchPage(0);

        Opened += async (_, _) => await LoadMarketDataAsync();
    }

    private void SwitchPage(int pageIndex)
    {
        bool showChart = pageIndex == 0;
        chartPageView.IsVisible = showChart;
        rolloversPageView.IsVisible = !showChart;

        if (toggleSidebarBottomBtn is not null)
            toggleSidebarBottomBtn.IsVisible = showChart;

        navChartBtn.Background = SolidColorBrush.Parse(showChart ? "#0284C7" : "#131E33");
        navChartBtn.Foreground = SolidColorBrush.Parse(showChart ? "#FFFFFF" : "#94A3B8");

        navRolloversBtn.Background = SolidColorBrush.Parse(!showChart ? "#0284C7" : "#131E33");
        navRolloversBtn.Foreground = SolidColorBrush.Parse(!showChart ? "#FFFFFF" : "#94A3B8");
    }

    private void ToggleSidebar()
    {
        if (chartPageBodyGrid is null || chartSidebarScroll is null)
            return;

        isSidebarVisible = !isSidebarVisible;
        chartSidebarScroll.IsVisible = isSidebarVisible;
        chartPageBodyGrid.ColumnDefinitions = isSidebarVisible
            ? new ColumnDefinitions("305,14,*")
            : new ColumnDefinitions("0,0,*");

        string label = isSidebarVisible ? "◀ Ocultar Panel" : "▶ Mostrar Panel";
        if (toggleSidebarHeaderBtn is not null)
            toggleSidebarHeaderBtn.Content = label;
        if (toggleSidebarBottomBtn is not null)
            toggleSidebarBottomBtn.Content = label;
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
            Background = SolidColorBrush.Parse("#0284C7"),
            CornerRadius = new CornerRadius(8),
            Width = 34,
            Height = 34,
            Child = new TextBlock
            {
                Text = "AF",
                Foreground = Brushes.White,
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
            Foreground = SolidColorBrush.Parse("#F8FAFC"),
            FontWeight = FontWeight.Bold,
            FontSize = 14
        });
        subtitleBlock = new TextBlock
        {
            Text = "CME Globex · NQ Continuo (OHLCV)",
            Foreground = SolidColorBrush.Parse("#64748B"),
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
        rightBadges.Children.Add(MakeBadge("CME Globex · NQ", "#0C4A6E", "#38BDF8"));
        rightBadges.Children.Add(MakeBadge("Arrastrar: Mover · Rueda: Zoom", "#1E293B", "#94A3B8"));
        rightBadges.Children.Add(MakeBadge("v0.4.0", "#064E3B", "#34D399"));

        Grid.SetColumn(rightBadges, 2);
        grid.Children.Add(rightBadges);

        return new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8),
            Child = grid
        };
    }

    private Control BuildBottomMenuBar(out Button chartTabBtn, out Button rolloversTabBtn)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        toggleSidebarBottomBtn = new Button
        {
            Content = "◀ Ocultar Panel",
            Background = SolidColorBrush.Parse("#162033"),
            Foreground = SolidColorBrush.Parse("#38BDF8"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11.5,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleSidebarBottomBtn.Click += (_, _) => ToggleSidebar();
        Grid.SetColumn(toggleSidebarBottomBtn, 0);
        grid.Children.Add(toggleSidebarBottomBtn);

        var menuButtonsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        chartTabBtn = new Button
        {
            Content = "📈  Gráfico de Mercado (OHLCV)",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            Padding = new Thickness(18, 7),
            CornerRadius = new CornerRadius(7)
        };
        chartTabBtn.Click += (_, _) => SwitchPage(0);

        rolloversTabBtn = new Button
        {
            Content = "🔄  Historial de Vencimientos (66 Rollovers)",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            Padding = new Thickness(18, 7),
            CornerRadius = new CornerRadius(7)
        };
        rolloversTabBtn.Click += (_, _) => SwitchPage(1);

        menuButtonsStack.Children.Add(chartTabBtn);
        menuButtonsStack.Children.Add(rolloversTabBtn);
        Grid.SetColumn(menuButtonsStack, 1);
        grid.Children.Add(menuButtonsStack);

        var rightHint = new TextBlock
        {
            Text = "Dataset: NQ_1min_UTC_cierre.txt (2010–2026)",
            Foreground = SolidColorBrush.Parse("#64748B"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(rightHint, 2);
        grid.Children.Add(rightHint);

        return new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 7),
            Child = grid
        };
    }

    private Control BuildChartPage(
        out ComboBox schemaCombo,
        out TextBlock coverageText,
        out ComboBox yearCombo,
        out ComboBox monthCombo,
        out TextBox fromInput,
        out TextBox toInput,
        out ComboBox tfCombo,
        out Button reloadButton,
        out TextBlock statusText,
        out TextBlock barsInfo,
        out TextBlock rangeInfo,
        out TextBlock boundsInfo,
        out TextBlock orderFlowInfo,
        out Slider scrubber,
        out ChartSurface surface)
    {
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("305,14,*")
        };
        chartPageBodyGrid = bodyGrid;

        var sidebarScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        chartSidebarScroll = sidebarScroll;
        var sidebarStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 4, 0) };

        schemaCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "ohlcv-1m · Velas 1 Minuto (2010–2026)",
                "tbbo · Ticks L1 Bid/Ask (2010–2011)"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#09101E"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            FontSize = 12
        };
        schemaCombo.SelectionChanged += async (_, _) => await OnSchemaChangedAsync();

        coverageText = new TextBlock
        {
            Text = "Cobertura: 2010-06-07 a 2026-10-30 UTC (5.56M velas)",
            Foreground = SolidColorBrush.Parse("#64748B"),
            FontSize = 10.5
        };

        sidebarStack.Children.Add(MakeSidebarCard("INSTRUMENTO Y ESQUEMA", "#38BDF8", new Control[]
        {
            new Border
            {
                Background = SolidColorBrush.Parse("#09101E"),
                BorderBrush = SolidColorBrush.Parse("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8),
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "NQ · E-mini Nasdaq-100 (GLBX.MDP3)",
                            Foreground = SolidColorBrush.Parse("#F8FAFC"),
                            FontWeight = FontWeight.Bold,
                            FontSize = 12.5
                        },
                        coverageText
                    }
                }
            },
            Labeled("Esquema Databento activo", schemaCombo)
        }));

        // Selector dinámico de Año (2026 .. 2010)
        var yearItems = new List<string> { "Todos (2010–2026)" };
        for (int y = 2026; y >= 2010; y--)
            yearItems.Add(y.ToString(CultureInfo.InvariantCulture));

        yearCombo = new ComboBox
        {
            ItemsSource = yearItems,
            SelectedIndex = 1, // 2026 por defecto
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#09101E"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            FontSize = 12
        };
        yearCombo.SelectionChanged += async (_, _) => await OnYearOrMonthChangedAsync();

        monthCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "Todo el año",
                "T1 (Ene – Mar)",
                "T2 (Abr – Jun)",
                "T3 (Jul – Sep)",
                "T4 (Oct – Dic)",
                "01 · Enero",
                "02 · Febrero",
                "03 · Marzo",
                "04 · Abril",
                "05 · Mayo",
                "06 · Junio",
                "07 · Julio",
                "08 · Agosto",
                "09 · Septiembre",
                "10 · Octubre",
                "11 · Noviembre",
                "12 · Diciembre"
            },
            SelectedIndex = 14, // Octubre por defecto
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#09101E"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            FontSize = 12
        };
        monthCombo.SelectionChanged += async (_, _) => await OnYearOrMonthChangedAsync();

        fromInput = MakeInput("2026-10-01", "YYYY-MM-DD");
        toInput = MakeInput("2026-10-30", "YYYY-MM-DD");

        fromInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await LoadMarketDataAsync(); };
        toInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await LoadMarketDataAsync(); };

        presetButtons.Clear();
        var quickRangeRow1 = new UniformGrid { Columns = 4, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (label, mode) in new[] { ("1ª Sem", -1), ("1D", 1), ("1S", 7), ("1M", 30) })
        {
            var pillBtn = MakeQuickPillButton(label);
            int capturedMode = mode;
            pillBtn.Click += async (_, _) => await ApplyDatePresetAndLoadAsync(capturedMode);
            presetButtons.Add((pillBtn, capturedMode));
            quickRangeRow1.Children.Add(pillBtn);
        }

        var quickRangeRow2 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (label, mode) in new[] { ("3M", 90), ("1A", 365), ("Todo", 0) })
        {
            var pillBtn = MakeQuickPillButton(label);
            int capturedMode = mode;
            pillBtn.Click += async (_, _) => await ApplyDatePresetAndLoadAsync(capturedMode);
            presetButtons.Add((pillBtn, capturedMode));
            quickRangeRow2.Children.Add(pillBtn);
        }

        var quickRangesStack = new StackPanel
        {
            Spacing = 3,
            Children = { quickRangeRow1, quickRangeRow2 }
        };

        var prevWindowBtn = MakeQuickPillButton("◀ Periodo Ant.");
        prevWindowBtn.Foreground = SolidColorBrush.Parse("#38BDF8");
        prevWindowBtn.Click += async (_, _) => await ShiftDateWindowAsync(-1);

        var nextWindowBtn = MakeQuickPillButton("Periodo Sig. ▶");
        nextWindowBtn.Foreground = SolidColorBrush.Parse("#38BDF8");
        nextWindowBtn.Click += async (_, _) => await ShiftDateWindowAsync(1);

        var stepWindowGrid = TwoColumn(prevWindowBtn, nextWindowBtn);

        sidebarStack.Children.Add(MakeSidebarCard("RANGO DE FECHAS DINÁMICO", "#818CF8", new Control[]
        {
            TwoColumn(
                Labeled("Año rápido", yearCombo),
                Labeled("Mes / Trimestre", monthCombo)),
            Labeled("Ventanas rápidas", quickRangesStack),
            Labeled("Desplazar ventana temporal", stepWindowGrid),
            TwoColumn(
                Labeled("Desde (UTC)", fromInput),
                Labeled("Hasta (UTC)", toInput))
        }));

        tfCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "1 minuto (Original)",
                "5 minutos",
                "15 minutos",
                "1 hora",
                "4 horas",
                "1 día (Diario)"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SolidColorBrush.Parse("#0B1325"),
            BorderBrush = SolidColorBrush.Parse("#1E293B")
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
            Background = SolidColorBrush.Parse("#0284C7"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8)
        };
        reloadButton.Click += async (_, _) => await LoadMarketDataAsync();

        statusText = new TextBlock
        {
            Text = "Listo para visualizar datos.",
            Foreground = SolidColorBrush.Parse("#38BDF8"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5
        };

        sidebarStack.Children.Add(MakeSidebarCard("TEMPORALIDAD DE VELAS", "#10B981", new Control[]
        {
            Labeled("Agrupar velas en", tfCombo),
            reloadButton,
            new Border
            {
                Background = SolidColorBrush.Parse("#09101E"),
                BorderBrush = SolidColorBrush.Parse("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = statusText
            }
        }));

        barsInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#F8FAFC"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        rangeInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#94A3B8"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        boundsInfo = new TextBlock
        {
            Text = "—",
            Foreground = SolidColorBrush.Parse("#38BDF8"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        orderFlowInfo = new TextBlock
        {
            Text = "Disponible en esquema TBBO (Ticks L1)",
            Foreground = SolidColorBrush.Parse("#64748B"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };

        sidebarStack.Children.Add(MakeSidebarCard("RESUMEN DEL PERIODO", "#F59E0B", new Control[]
        {
            Labeled("Cantidad de velas / ticks", barsInfo),
            Labeled("Periodo en pantalla (UTC)", rangeInfo),
            Labeled("Rango de precios (Mín / Máx)", boundsInfo),
            Labeled("Order Flow TBBO (Bid / Ask Delta)", orderFlowInfo)
        }));

        sidebarScroll.Content = sidebarStack;
        Grid.SetColumn(sidebarScroll, 0);
        bodyGrid.Children.Add(sidebarScroll);

        // Área derecha: Gráfico + Scrubber
        var chartPanelGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        Grid.SetColumn(chartPanelGrid, 2);

        surface = new ChartSurface();
        var capturedSurface = surface;
        surface.ViewportChanged += OnChartViewportChanged;

        var chartContainer = new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = BuildChartContainerContent(capturedSurface)
        };
        Grid.SetRow(chartContainer, 0);
        chartPanelGrid.Children.Add(chartContainer);

        scrubber = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var capturedScrubber = scrubber;
        scrubber.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !suppressSliderEvents)
            {
                capturedSurface.SetRightEdgeIndex((int)Math.Round(capturedScrubber.Value));
            }
        };

        var navBar = BuildNavigationFooter(capturedSurface, capturedScrubber);
        Grid.SetRow(navBar, 1);
        chartPanelGrid.Children.Add(navBar);

        bodyGrid.Children.Add(chartPanelGrid);
        return bodyGrid;
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
            "#38BDF8"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "PRECIO INICIAL (JUN 2010)",
            "1,826.25 pts",
            "Primer contrato registrado (NQM0)",
            "#818CF8"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "ACUMULADO POR VENCIMIENTOS",
            "+3,769.75 pts",
            "Suma neta de los 66 cambios de contrato ($75,395 / contrato)",
            "#10B981"));
        summaryGrid.Children.Add(MakeSummaryCard(
            "PRECIO FINAL (OCT 2026)",
            "29,110.25 pts",
            "1,826.25 (Inicial) + 23,514.25 (Mercado) + 3,769.75 (Rollovers)",
            "#F59E0B"));

        Grid.SetRow(summaryGrid, 0);
        pageGrid.Children.Add(summaryGrid);

        // 2. Gráfico visual de la curva de acumulación por vencimiento
        accumChart = new RolloverAccumulationSurface();
        var chartBorder = new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
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
                        Foreground = SolidColorBrush.Parse("#E2E8F0"),
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
                    Foreground = SolidColorBrush.Parse("#F8FAFC"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 13
                },
                new TextBlock
                {
                    Text = "Haz clic en 'Ver en Gráfico' para inspeccionar las velas de cualquier vencimiento",
                    Foreground = SolidColorBrush.Parse("#64748B"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });

        orderBtn = new Button
        {
            Content = "Orden: 2010 → 2026 (Cronológico)",
            Background = SolidColorBrush.Parse("#162033"),
            Foreground = SolidColorBrush.Parse("#38BDF8"),
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
            Background = SolidColorBrush.Parse("#09101E"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
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
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
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

            AddTableCell(rowGrid, 0, $"#{row.SequenceNumber}", "#64748B", true);
            AddTableCell(rowGrid, 1, row.CloseTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "#E2E8F0");
            AddTableCell(rowGrid, 2, row.QuarterName, "#38BDF8", true);
            AddTableCell(rowGrid, 3, $"{row.PreviousClose:N2}", "#CBD5E1");
            AddTableCell(rowGrid, 4, $"{row.NewOpen:N2}", "#F8FAFC", true);

            bool posStep = row.AdjustmentPoints >= 0;
            string stepSign = posStep ? "+" : "";
            var stepBadge = MakeBadge(
                $"{stepSign}{row.AdjustmentPoints:N2} pts",
                posStep ? "#064E3B" : "#7F1D1D",
                posStep ? "#34D399" : "#FCA5A5");
            stepBadge.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(stepBadge, 5);
            rowGrid.Children.Add(stepBadge);

            bool posCum = row.CumulativeFrom2010 >= 0;
            string cumSign = posCum ? "+" : "";
            AddTableCell(rowGrid, 6, $"{cumSign}{row.CumulativeFrom2010:N2} pts", posCum ? "#34D399" : "#F87171", true);

            AddTableCell(rowGrid, 7, $"{(row.CumulativeBackFrom2026 >= 0 ? "+" : "")}{row.CumulativeBackFrom2026:N2} pts", "#94A3B8");

            var viewBtn = new Button
            {
                Content = "Ver en Gráfico →",
                Background = SolidColorBrush.Parse("#162033"),
                Foreground = SolidColorBrush.Parse("#38BDF8"),
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
                Background = SolidColorBrush.Parse("#09101E"),
                BorderBrush = SolidColorBrush.Parse("#152036"),
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
            Foreground = SolidColorBrush.Parse("#64748B"),
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
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
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
                        Foreground = SolidColorBrush.Parse("#64748B"),
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
                        Foreground = SolidColorBrush.Parse("#94A3B8"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Control BuildChartContainerContent(ChartSurface surface)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        toggleSidebarHeaderBtn = new Button
        {
            Content = "◀ Ocultar Panel",
            Background = SolidColorBrush.Parse("#162033"),
            Foreground = SolidColorBrush.Parse("#38BDF8"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleSidebarHeaderBtn.Click += (_, _) => ToggleSidebar();
        titleStack.Children.Add(toggleSidebarHeaderBtn);
        titleStack.Children.Add(new TextBlock
        {
            Text = "GRÁFICO DE VELAS (OHLC) Y VOLUMEN",
            Foreground = SolidColorBrush.Parse("#E2E8F0"),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "● Alcista (Verde)   ● Bajista (Rojo)   ● Línea punteada: Último cierre visible",
            Foreground = SolidColorBrush.Parse("#64748B"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        headerGrid.Children.Add(titleStack);

        var zoomStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        zoomStack.Children.Add(new TextBlock
        {
            Text = "Zoom:",
            Foreground = SolidColorBrush.Parse("#64748B"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        foreach (int count in new[] { 60, 120, 240, 450, 800 })
        {
            var btn = MakeQuickPillButton($"{count}v");
            int captured = count;
            btn.Click += (_, _) => surface.SetVisibleBarCount(captured);
            zoomStack.Children.Add(btn);
        }
        Grid.SetColumn(zoomStack, 1);
        headerGrid.Children.Add(zoomStack);

        Grid.SetRow(headerGrid, 0);
        grid.Children.Add(headerGrid);

        Grid.SetRow(surface, 1);
        grid.Children.Add(surface);

        return grid;
    }

    private static Control BuildNavigationFooter(ChartSurface surface, Slider scrubber)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var startBtn = MakeNavButton("⏮ Inicio");
        startBtn.Click += (_, _) => surface.JumpToStart();

        var stepBackBtn = MakeNavButton("◀ Anterior");
        stepBackBtn.Margin = new Thickness(6, 0, 0, 0);
        stepBackBtn.Click += (_, _) => surface.PanByBars(-surface.VisibleBarCount / 2);

        var stepFwdBtn = MakeNavButton("Siguiente ▶");
        stepFwdBtn.Margin = new Thickness(0, 0, 6, 0);
        stepFwdBtn.Click += (_, _) => surface.PanByBars(surface.VisibleBarCount / 2);

        var endBtn = MakeNavButton("Final ⏭");
        endBtn.Click += (_, _) => surface.JumpToEnd();

        Grid.SetColumn(startBtn, 0);
        Grid.SetColumn(stepBackBtn, 1);
        Grid.SetColumn(scrubber, 2);
        Grid.SetColumn(stepFwdBtn, 3);
        Grid.SetColumn(endBtn, 4);

        grid.Children.Add(startBtn);
        grid.Children.Add(stepBackBtn);
        grid.Children.Add(scrubber);
        grid.Children.Add(stepFwdBtn);
        grid.Children.Add(endBtn);

        return new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8),
            Child = grid
        };
    }

    private void OnChartViewportChanged(int rightEdgeIndex, int totalBars)
    {
        suppressSliderEvents = true;
        try
        {
            timelineSlider.Minimum = Math.Min(20, Math.Max(0, totalBars - 1));
            timelineSlider.Maximum = Math.Max(0, totalBars - 1);
            timelineSlider.Value = Math.Clamp(rightEdgeIndex, timelineSlider.Minimum, timelineSlider.Maximum);
        }
        finally
        {
            suppressSliderEvents = false;
        }
    }

    private static readonly DateTime MinDatasetDate = new(2010, 6, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxOhlcvDatasetDate = new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxTbboDatasetDate = new(2011, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    private DateTime CurrentMaxDatasetDate => IsTbboMode ? MaxTbboDatasetDate : MaxOhlcvDatasetDate;

    private async Task OnSchemaChangedAsync()
    {
        suppressTimeframeEvents = true;
        try
        {
            if (IsTbboMode)
            {
                schemaCoverageText.Text = "Cobertura TBBO: 2010-06-07 a 2011-09-13 UTC (50.17M ticks)";
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
                    toBox.Text = "2010-06-11";
                    SyncYearMonthCombos(MinDatasetDate);
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
            }
        }
        finally
        {
            suppressTimeframeEvents = false;
        }

        await LoadMarketDataAsync();
    }

    private void HighlightPresetButton(int? activeMode)
    {
        foreach (var (btn, mode) in presetButtons)
        {
            bool active = activeMode.HasValue && activeMode.Value == mode;
            btn.Background = SolidColorBrush.Parse(active ? "#0284C7" : "#162033");
            btn.Foreground = SolidColorBrush.Parse(active ? "#FFFFFF" : "#94A3B8");
        }
    }

    private async Task OnYearOrMonthChangedAsync()
    {
        if (suppressDateComboEvents) return;

        HighlightPresetButton(null);
        int yearIdx = yearJumpBox.SelectedIndex;
        if (yearIdx <= 0)
        {
            fromBox.Text = string.Empty;
            toBox.Text = string.Empty;
            await LoadMarketDataAsync();
            return;
        }

        int year = 2027 - yearIdx; // index 1 = 2026, index 17 = 2010
        int monthSel = monthJumpBox.SelectedIndex;

        DateTime start;
        DateTime end;

        if (monthSel <= 0)
        {
            start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            end = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        }
        else if (monthSel is >= 1 and <= 4)
        {
            int startMonth = (monthSel - 1) * 3 + 1;
            int endMonth = startMonth + 2;
            start = new DateTime(year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
            end = new DateTime(year, endMonth, DateTime.DaysInMonth(year, endMonth), 0, 0, 0, DateTimeKind.Utc);
        }
        else
        {
            int month = Math.Clamp(monthSel - 4, 1, 12);
            start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            end = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        }

        if (start < MinDatasetDate) start = MinDatasetDate;
        if (end > CurrentMaxDatasetDate) end = CurrentMaxDatasetDate;
        if (start > end) start = end.AddDays(-7);

        fromBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        toBox.Text = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await LoadMarketDataAsync();
    }

    private async Task ShiftDateWindowAsync(int direction)
    {
        HighlightPresetButton(null);

        DateTime currentFrom = ParseOptionalUtc(fromBox.Text, false)?.Date ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime currentTo = ParseOptionalUtc(toBox.Text, false)?.Date ?? CurrentMaxDatasetDate;
        if (currentTo < currentFrom) currentTo = currentFrom.AddDays(7);

        int spanDays = Math.Max(1, (int)Math.Round((currentTo - currentFrom).TotalDays) + 1);
        DateTime newFrom = currentFrom.AddDays(direction * spanDays);
        DateTime newTo = currentTo.AddDays(direction * spanDays);

        if (newFrom < MinDatasetDate)
        {
            newFrom = MinDatasetDate;
            newTo = MinDatasetDate.AddDays(spanDays - 1);
        }
        if (newTo > CurrentMaxDatasetDate)
        {
            newTo = CurrentMaxDatasetDate;
            newFrom = CurrentMaxDatasetDate.AddDays(-(spanDays - 1));
        }

        fromBox.Text = newFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        toBox.Text = newTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SyncYearMonthCombos(newFrom);

        await LoadMarketDataAsync();
    }

    private void SyncYearMonthCombos(DateTime date)
    {
        suppressDateComboEvents = true;
        try
        {
            int yIdx = 2027 - date.Year;
            if (yIdx is >= 1 and <= 17)
                yearJumpBox.SelectedIndex = yIdx;
            monthJumpBox.SelectedIndex = date.Month + 4;
        }
        finally
        {
            suppressDateComboEvents = false;
        }
    }

    private async Task ApplyDatePresetAndLoadAsync(int mode)
    {
        HighlightPresetButton(mode);

        if (mode == -1)
        {
            fromBox.Text = "2010-06-07";
            toBox.Text = "2010-06-14";
            SyncYearMonthCombos(MinDatasetDate);
        }
        else if (mode == 0)
        {
            suppressDateComboEvents = true;
            yearJumpBox.SelectedIndex = 0;
            monthJumpBox.SelectedIndex = 0;
            suppressDateComboEvents = false;
            fromBox.Text = string.Empty;
            toBox.Text = string.Empty;
        }
        else
        {
            DateTime anchor = ParseOptionalUtc(toBox.Text, false)?.Date ?? CurrentMaxDatasetDate;
            if (anchor > CurrentMaxDatasetDate) anchor = CurrentMaxDatasetDate;
            if (anchor < MinDatasetDate.AddDays(mode)) anchor = CurrentMaxDatasetDate;
            DateTime start = anchor.AddDays(-mode);
            if (start < MinDatasetDate) start = MinDatasetDate;

            fromBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            toBox.Text = anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            SyncYearMonthCombos(anchor);
        }

        await LoadMarketDataAsync();
    }

    private async Task LoadMarketDataAsync()
    {
        if (isLoading) return;

        bool tbbo = IsTbboMode;
        string activePath = tbbo ? tbboDatasetPath : defaultDatasetPath;

        if (!File.Exists(activePath))
        {
            statusBanner.Text = $"No se encontró el archivo del esquema: {Path.GetFileName(activePath)}";
            statusBanner.Foreground = SolidColorBrush.Parse("#F87171");
            return;
        }

        isLoading = true;
        loadBtn.IsEnabled = false;
        loadBtn.Content = "Cargando datos...";
        statusBanner.Text = tbbo
            ? "Leyendo ticks TBBO (Last, Bid, Ask, Vol) desde disco..."
            : "Leyendo barras históricas de 1 minuto desde disco...";
        statusBanner.Foreground = SolidColorBrush.Parse("#38BDF8");

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
        catch (Exception ex)
        {
            statusBanner.Text = $"Error al leer datos: {ex.Message}";
            statusBanner.Foreground = SolidColorBrush.Parse("#F87171");
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
            statusBanner.Foreground = SolidColorBrush.Parse("#FBBF24");
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
        infoOrderFlowText.Foreground = SolidColorBrush.Parse("#64748B");

        headerSubtitle.Text = $"NQ (ohlcv-1m) · {displayBars.Count:N0} velas ({tfLabel}) · {firstUtc:yyyy-MM-dd} a {lastUtc:yyyy-MM-dd} UTC";
        statusBanner.Text = $"Datos cargados: {displayBars.Count:N0} velas de {tfLabel} listas para explorar.";
        statusBanner.Foreground = SolidColorBrush.Parse("#34D399");

        chartSurface.SetBars(displayBars, tfLabel);
    }

    private void RebuildDisplayBarsFromTbbo()
    {
        if (rawTicks.Count == 0)
        {
            chartSurface.SetBars(Array.Empty<Bar>(), "TBBO");
            infoBarsText.Text = "0 ticks";
            infoRangeText.Text = "Sin ticks TBBO en el rango (Cobertura: 2010-06-07 a 2011-09-13)";
            infoPriceBoundsText.Text = "—";
            infoOrderFlowText.Text = "—";
            statusBanner.Text = "No hay ticks TBBO en ese rango. Selecciona fechas entre 2010-06-07 y 2011-09-13.";
            statusBanner.Foreground = SolidColorBrush.Parse("#FBBF24");
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
        infoOrderFlowText.Foreground = SolidColorBrush.Parse(netDelta >= 0 ? "#34D399" : "#F87171");

        headerSubtitle.Text = $"NQ (TBBO L1) · {rawTicks.Count:N0} ticks → {displayBars.Count:N0} velas ({tfLabel}) · Delta {deltaSign}{netDelta:N0}";
        statusBanner.Text = $"TBBO cargado: {rawTicks.Count:N0} ticks con Bid/Ask agrupados en {displayBars.Count:N0} velas ({tfLabel}).";
        statusBanner.Foreground = SolidColorBrush.Parse("#34D399");

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
            Foreground = SolidColorBrush.Parse("#E2E8F0"),
            FontWeight = FontWeight.Bold,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        foreach (var child in children)
            stack.Children.Add(child);

        return new Border
        {
            Background = SolidColorBrush.Parse("#0D1526"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
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

    private static TextBox MakeInput(string value, string? watermark = null) => new()
    {
        Text = value,
        Watermark = watermark,
        Background = SolidColorBrush.Parse("#09101E"),
        Foreground = SolidColorBrush.Parse("#F1F5F9"),
        BorderBrush = SolidColorBrush.Parse("#1E293B"),
        CornerRadius = new CornerRadius(6),
        FontSize = 12,
        Padding = new Thickness(8, 6)
    };

    private static Button MakeNavButton(string text) => new()
    {
        Content = text,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = SolidColorBrush.Parse("#162033"),
        Foreground = SolidColorBrush.Parse("#E2E8F0"),
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
        Background = SolidColorBrush.Parse("#162033"),
        Foreground = SolidColorBrush.Parse("#94A3B8"),
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
                Foreground = SolidColorBrush.Parse("#94A3B8"),
                FontSize = 11
            },
            input
        }
    };
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
            SolidColorBrush.Parse("#080D1A"),
            new Pen(SolidColorBrush.Parse("#1E293B"), 1),
            bounds, 8, 8);

        if (rows.Count == 0) return;

        var plot = new Rect(16, 14, Math.Max(60, bounds.Width - 96), Math.Max(30, bounds.Height - 36));
        double minVal = Math.Min(0, rows.Min(r => r.CumulativeFrom2010));
        double maxVal = Math.Max(100, rows.Max(r => r.CumulativeFrom2010));
        double span = Math.Max(1, maxVal - minVal);

        var gridPen = new Pen(SolidColorBrush.Parse("#111C30"), 1);
        double zeroY = plot.Bottom - ((0 - minVal) / span) * plot.Height;
        context.DrawLine(gridPen, new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));

        var barBrushPos = SolidColorBrush.Parse("#38BDF8");
        var barBrushNeg = SolidColorBrush.Parse("#F87171");
        var linePen = new Pen(SolidColorBrush.Parse("#10B981"), 2);

        double stepX = plot.Width / rows.Count;
        double barW = Math.Clamp(stepX * 0.55, 2, 12);

        Point? prevPoint = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double cx = plot.Left + (i + 0.5) * stepX;
            double cy = plot.Bottom - ((r.CumulativeFrom2010 - minVal) / span) * plot.Height;

            // Barra de salto individual escalada
            double barTop = Math.Min(cy, zeroY);
            double barH = Math.Max(1.5, Math.Abs( zeroY - cy ));
            context.FillRectangle(
                r.CumulativeFrom2010 >= 0 ? SolidColorBrush.Parse("#2210B981") : SolidColorBrush.Parse("#22EF4444"),
                new Rect(cx - barW / 2, barTop, barW, barH));

            var pt = new Point(cx, cy);
            if (prevPoint.HasValue)
            {
                context.DrawLine(linePen, prevPoint.Value, pt);
            }
            prevPoint = pt;

            if (i == 0 || i == rows.Count - 1 || i % 12 == 0)
            {
                DrawSmallText(context, r.CloseTimeUtc.ToString("yyyy", CultureInfo.InvariantCulture), new Point(cx - 12, plot.Bottom + 4), "#64748B");
            }
        }

        DrawSmallText(context, $"+{maxVal:N0} pts", new Point(plot.Right + 8, plot.Top - 2), "#34D399");
        DrawSmallText(context, "0 pts", new Point(plot.Right + 8, Math.Clamp(zeroY - 7, plot.Top, plot.Bottom - 12)), "#64748B");
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
    private Point dragStartPoint;
    private int dragStartRightEdgeIndex;

    public int VisibleBarCount => visibleBarCount;

    public event Action<int, int>? ViewportChanged;

    public ChartSurface()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void SetBars(IReadOnlyList<Bar> loadedBars, string tfLabel)
    {
        bars = loadedBars;
        timeframeLabel = tfLabel;
        rightEdgeIndex = Math.Max(0, bars.Count - 1);
        NotifyViewport();
        InvalidateVisual();
    }

    public void SetVisibleBarCount(int count)
    {
        visibleBarCount = Math.Clamp(count, 20, 1500);
        NotifyViewport();
        InvalidateVisual();
    }

    public void SetRightEdgeIndex(int index)
    {
        if (bars.Count == 0) return;
        int minRight = Math.Min(bars.Count - 1, Math.Max(10, visibleBarCount / 2));
        rightEdgeIndex = Math.Clamp(index, minRight, bars.Count - 1);
        InvalidateVisual();
    }

    public void JumpToStart()
    {
        if (bars.Count == 0) return;
        rightEdgeIndex = Math.Min(bars.Count - 1, visibleBarCount - 1);
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
        int minRight = Math.Min(bars.Count - 1, Math.Max(10, visibleBarCount / 2));
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
        isDragging = true;
        dragStartPoint = e.GetPosition(this);
        dragStartRightEdgeIndex = rightEdgeIndex;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (isDragging)
        {
            isDragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        hoverPoint = pos;

        if (isDragging && bars.Count > 0)
        {
            double plotWidth = Math.Max(100, Bounds.Width - 76);
            double step = plotWidth / Math.Max(1, visibleBarCount);
            double dx = pos.X - dragStartPoint.X;
            int barDelta = (int)Math.Round(dx / step);
            int minRight = Math.Min(bars.Count - 1, Math.Max(10, visibleBarCount / 2));
            rightEdgeIndex = Math.Clamp(dragStartRightEdgeIndex - barDelta, minRight, bars.Count - 1);
            NotifyViewport();
        }

        InvalidateVisual();
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

        double factor = e.Delta.Y > 0 ? 0.82 : 1.22;
        visibleBarCount = Math.Clamp((int)Math.Round(visibleBarCount * factor), 25, 1500);
        NotifyViewport();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 120 || bounds.Height < 120) return;

        var bgBrush = SolidColorBrush.Parse("#080D1A");
        var borderPen = new Pen(SolidColorBrush.Parse("#1E293B"), 1);
        context.DrawRectangle(bgBrush, borderPen, bounds, 8, 8);

        if (bars.Count == 0)
        {
            DrawLabel(context, "Sin datos para mostrar en el gráfico.", new Point(24, 24), "#94A3B8", 13);
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
            Bar b = bars[i];
            if (b.Low < minPrice) minPrice = b.Low;
            if (b.High > maxPrice) maxPrice = b.High;
            if (b.Volume > maxVolume) maxVolume = b.Volume;
        }

        double pricePadding = Math.Max(1.0, (maxPrice - minPrice) * 0.06);
        minPrice -= pricePadding;
        maxPrice += pricePadding;
        double priceSpan = Math.Max(0.25, maxPrice - minPrice);

        var gridPen = new Pen(SolidColorBrush.Parse("#111C30"), 1);

        // Cuadrícula horizontal y eje derecho de precios
        const int priceSteps = 7;
        for (int i = 0; i <= priceSteps; i++)
        {
            double ratio = i / (double)priceSteps;
            double y = candleRect.Top + ratio * candleRect.Height;
            double priceVal = maxPrice - ratio * priceSpan;
            context.DrawLine(gridPen, new Point(candleRect.Left, y), new Point(candleRect.Right, y));
            DrawLabel(context, priceVal.ToString("F2", CultureInfo.InvariantCulture), new Point(candleRect.Right + 8, y - 7), "#64748B", 10.5);
        }

        // Cuadrícula vertical y eje inferior de tiempo UTC
        int timeSteps = Math.Min(7, count);
        for (int i = 0; i < timeSteps; i++)
        {
            int barOffset = (int)Math.Round(i * (count - 1) / (double)Math.Max(1, timeSteps - 1));
            int barIdx = startIdx + barOffset;
            double x = candleRect.Left + (barOffset + 0.5) * (candleRect.Width / count);
            context.DrawLine(gridPen, new Point(x, candleRect.Top), new Point(x, volumeRect.Bottom));

            if (barIdx >= 0 && barIdx < bars.Count)
            {
                string timeLabel = bars[barIdx].CloseTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                DrawLabel(context, timeLabel, new Point(Math.Max(10, x - 44), volumeRect.Bottom + 6), "#64748B", 10);
            }
        }

        // Separador entre velas y volumen
        context.DrawLine(borderPen, new Point(candleRect.Left, volumeRect.Top - 4), new Point(candleRect.Right, volumeRect.Top - 4));
        DrawLabel(context, "VOLUMEN", new Point(candleRect.Left + 6, volumeRect.Top + 2), "#475569", 9.5, true);

        double step = candleRect.Width / count;
        double bodyWidth = Math.Clamp(step * 0.68, 1.5, 16);

        var bullBrush = SolidColorBrush.Parse("#10B981");
        var bearBrush = SolidColorBrush.Parse("#EF4444");
        var bullVolBrush = SolidColorBrush.Parse("#3510B981");
        var bearVolBrush = SolidColorBrush.Parse("#35EF4444");
        var bullPen = new Pen(bullBrush, 1.15);
        var bearPen = new Pen(bearBrush, 1.15);

        // Dibujar Velas OHLC y Barras de Volumen
        for (int i = startIdx; i <= endIdx; i++)
        {
            Bar bar = bars[i];
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
            context.FillRectangle(bullish ? bullBrush : bearBrush, new Rect(centerX - bodyWidth / 2, top, bodyWidth, height));

            // Barra de Volumen
            double volRatio = bar.Volume / (double)maxVolume;
            double volBarHeight = Math.Max(1.0, volRatio * (volumeRect.Height - 4));
            double volTop = volumeRect.Bottom - volBarHeight;
            context.FillRectangle(bullish ? bullVolBrush : bearVolBrush, new Rect(centerX - bodyWidth / 2, volTop, bodyWidth, volBarHeight));
        }

        // Línea punteada del último precio visible
        Bar lastVisibleBar = bars[endIdx];
        double lastCloseY = PriceToY(lastVisibleBar.Close, minPrice, priceSpan, candleRect);
        bool lastBull = lastVisibleBar.Close >= lastVisibleBar.Open;
        var lastPricePen = new Pen(SolidColorBrush.Parse(lastBull ? "#10B981" : "#EF4444"), 1, new DashStyle(new double[] { 4, 4 }, 0));
        context.DrawLine(lastPricePen, new Point(candleRect.Left, lastCloseY), new Point(candleRect.Right, lastCloseY));

        var lastTagRect = new Rect(candleRect.Right + 2, Math.Clamp(lastCloseY - 9, candleRect.Top, candleRect.Bottom - 18), rightAxisWidth - 6, 18);
        context.FillRectangle(SolidColorBrush.Parse(lastBull ? "#065F46" : "#7F1D1D"), lastTagRect, 4);
        DrawLabel(context, lastVisibleBar.Close.ToString("F2", CultureInfo.InvariantCulture), new Point(lastTagRect.Left + 6, lastTagRect.Top + 2), "#F8FAFC", 10.5, true);

        // Mira interactiva (Crosshair) y barra HUD superior con datos OHLCV
        Bar inspectedBar = lastVisibleBar;
        if (hoverPoint.HasValue && candleRect.WithHeight(totalPlotHeight).Contains(hoverPoint.Value))
        {
            Point hp = hoverPoint.Value;
            int hoveredLocal = Math.Clamp((int)((hp.X - candleRect.Left) / step), 0, count - 1);
            inspectedBar = bars[startIdx + hoveredLocal];
            double snapX = candleRect.Left + (hoveredLocal + 0.5) * step;

            var crossPen = new Pen(SolidColorBrush.Parse("#475569"), 1, new DashStyle(new double[] { 4, 4 }, 0));
            context.DrawLine(crossPen, new Point(snapX, candleRect.Top), new Point(snapX, volumeRect.Bottom));
            context.DrawLine(crossPen, new Point(candleRect.Left, hp.Y), new Point(candleRect.Right, hp.Y));

            if (candleRect.Contains(hp))
            {
                double hoverPrice = maxPrice - ((hp.Y - candleRect.Top) / candleRect.Height) * priceSpan;
                var priceTagRect = new Rect(candleRect.Right + 2, Math.Clamp(hp.Y - 9, candleRect.Top, candleRect.Bottom - 18), rightAxisWidth - 6, 18);
                context.FillRectangle(SolidColorBrush.Parse("#0284C7"), priceTagRect, 4);
                DrawLabel(context, hoverPrice.ToString("F2", CultureInfo.InvariantCulture), new Point(priceTagRect.Left + 6, priceTagRect.Top + 2), "#FFFFFF", 10.5, true);
            }

            // Etiqueta flotante de fecha en el eje inferior
            string hoverTimeText = inspectedBar.CloseTimeUtc.ToString("yyyy-MM-dd HH:mm UTC", CultureInfo.InvariantCulture);
            var timeTagRect = new Rect(Math.Clamp(snapX - 62, candleRect.Left, candleRect.Right - 124), volumeRect.Bottom + 3, 124, 18);
            context.FillRectangle(SolidColorBrush.Parse("#1E293B"), timeTagRect, 4);
            DrawLabel(context, hoverTimeText, new Point(timeTagRect.Left + 8, timeTagRect.Top + 2), "#F8FAFC", 10, true);
        }

        // HUD Superior con valores exactos de la vela bajo el cursor
        double changePts = inspectedBar.Close - inspectedBar.Open;
        double changePct = inspectedBar.Open != 0 ? (changePts / inspectedBar.Open) * 100.0 : 0.0;
        string sign = changePts >= 0 ? "+" : "";
        string hudText =
            $"NQ ({timeframeLabel})  ·  {inspectedBar.CloseTimeUtc:yyyy-MM-dd HH:mm} UTC   " +
            $"O: {inspectedBar.Open:F2}   H: {inspectedBar.High:F2}   L: {inspectedBar.Low:F2}   C: {inspectedBar.Close:F2}   " +
            $"Cambio: {sign}{changePts:F2} ({sign}{changePct:F2}%)   Vol: {inspectedBar.Volume:N0}";

        string hudColor = inspectedBar.Close >= inspectedBar.Open ? "#34D399" : "#F87171";
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
