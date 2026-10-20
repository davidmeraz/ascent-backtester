using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

internal sealed class MainWindow : Window
{
    private readonly string workspace;
    private readonly TextBox fileBox;
    private readonly CheckBox ticksCheck;
    private readonly ComboBox instrumentBox;
    private readonly TextBox fastBox;
    private readonly TextBox slowBox;
    private readonly TextBox qtyBox;
    private readonly TextBox stopBox;
    private readonly TextBox targetBox;
    private readonly TextBox commBox;
    private readonly TextBox slipBox;
    private readonly TextBlock pnlMetric;
    private readonly TextBlock winRateMetric;
    private readonly TextBlock closedDdMetric;
    private readonly TextBlock intradayDdMetric;
    private readonly TextBlock statusBanner;
    private readonly ChartSurface chartSurface;
    private readonly StackPanel propFirmRows;
    private readonly StackPanel tradeRows;

    public MainWindow(string workspacePath)
    {
        workspace = workspacePath;
        Title = "Ascent Futures Lab · Backtester & Evaluador de Empresas de Fondeo (ES / NQ)";
        Width = 1360;
        Height = 860;
        MinWidth = 1024;
        MinHeight = 680;
        Background = SolidColorBrush.Parse("#0B0F19");

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("310,*"),
            Margin = new Thickness(14)
        };

        // === PANEL IZQUIERDO: CONFIGURACIÓN ===
        fileBox = CreateInput("(Muestra integrada de ES)", 200);
        ticksCheck = new CheckBox { Content = "Archivo Tick-by-Tick (Bid/Ask)", Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 8) };
        instrumentBox = new ComboBox
        {
            ItemsSource = new[] { "ES", "NQ" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        fastBox = CreateInput("5");
        slowBox = CreateInput("12");
        qtyBox = CreateInput("1");
        stopBox = CreateInput("20");
        targetBox = CreateInput("48");
        commBox = CreateInput("2.50");
        slipBox = CreateInput("1");

        var browseBtn = new Button
        {
            Content = "Elegir archivo...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        browseBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Seleccionar histórico de Minuto o Tick-by-Tick (.txt / .csv)",
                AllowMultiple = false
            });
            if (files.Count > 0)
            {
                fileBox.Text = files[0].Path.LocalPath;
            }
        };

        var runBtn = new Button
        {
            Content = "Ejecutar Backtest y Evaluar Fondeo",
            Background = SolidColorBrush.Parse("#2563EB"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 6)
        };
        runBtn.Click += (_, _) => ExecuteSimulation();

        var sidebarStack = new StackPanel { Spacing = 4 };
        sidebarStack.Children.Add(new TextBlock
        {
            Text = "ASCENT BACKTESTER",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = SolidColorBrush.Parse("#38BDF8"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        sidebarStack.Children.Add(SectionLabel("1. Datos Históricos"));
        sidebarStack.Children.Add(fileBox);
        sidebarStack.Children.Add(browseBtn);
        sidebarStack.Children.Add(ticksCheck);

        sidebarStack.Children.Add(SectionLabel("2. Instrumento y Estrategia SMA"));
        sidebarStack.Children.Add(FieldRow("Instrumento", instrumentBox));
        sidebarStack.Children.Add(FieldRow("SMA Rápida", fastBox));
        sidebarStack.Children.Add(FieldRow("SMA Lenta", slowBox));
        sidebarStack.Children.Add(FieldRow("Contratos", qtyBox));

        sidebarStack.Children.Add(SectionLabel("3. Gestión de Riesgo y Costos"));
        sidebarStack.Children.Add(FieldRow("Stop Loss (ticks)", stopBox));
        sidebarStack.Children.Add(FieldRow("Take Profit (ticks)", targetBox));
        sidebarStack.Children.Add(FieldRow("Comisión ($/lado)", commBox));
        sidebarStack.Children.Add(FieldRow("Slippage (ticks)", slipBox));
        sidebarStack.Children.Add(runBtn);

        statusBanner = new TextBlock
        {
            Text = "Listo para simular.",
            Foreground = SolidColorBrush.Parse("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };
        sidebarStack.Children.Add(statusBanner);

        var sidebarBorder = new Border
        {
            Background = SolidColorBrush.Parse("#111827"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new ScrollViewer { Content = sidebarStack }
        };
        Grid.SetColumn(sidebarBorder, 0);
        root.Children.Add(sidebarBorder);

        // === PANEL DERECHO: MÉTRICAS, GRÁFICO Y TABLAS ===
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,260")
        };

        // 1. Tarjetas de Métricas
        var metricsGrid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
        metricsGrid.Children.Add(CreateMetricCard("P&L Neto Cerrado", out pnlMetric));
        metricsGrid.Children.Add(CreateMetricCard("Win Rate / Trades", out winRateMetric));
        metricsGrid.Children.Add(CreateMetricCard("Drawdown EOD / Cerrado", out closedDdMetric));
        metricsGrid.Children.Add(CreateMetricCard("Drawdown Intradía (Apex)", out intradayDdMetric));
        Grid.SetRow(metricsGrid, 0);
        mainGrid.Children.Add(metricsGrid);

        // 2. Gráfico Nativo de Velas + SMA + Curva de Capital
        chartSurface = new ChartSurface();
        var chartBorder = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = chartSurface
        };
        Grid.SetRow(chartBorder, 1);
        mainGrid.Children.Add(chartBorder);

        // 3. Paneles Inferiores: Evaluación de Empresas de Fondeo y Operaciones
        var bottomGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,0.85*") };

        propFirmRows = new StackPanel { Spacing = 4 };
        var propFirmBox = CreateTableBox("Evaluación en Empresas de Fondeo (Prop Firms)", propFirmRows, new Thickness(0, 0, 6, 0));
        Grid.SetColumn(propFirmBox, 0);
        bottomGrid.Children.Add(propFirmBox);

        tradeRows = new StackPanel { Spacing = 4 };
        var tradesBox = CreateTableBox("Operaciones Ejecutadas (con MAE y MFE intravela)", tradeRows, new Thickness(6, 0, 0, 0));
        Grid.SetColumn(tradesBox, 1);
        bottomGrid.Children.Add(tradesBox);

        Grid.SetRow(bottomGrid, 2);
        mainGrid.Children.Add(bottomGrid);

        Grid.SetColumn(mainGrid, 1);
        root.Children.Add(mainGrid);

        Content = root;
        ExecuteSimulation();
    }

    private void ExecuteSimulation()
    {
        try
        {
            string symbol = (instrumentBox.SelectedItem as string) ?? "ES";
            InstrumentSpec spec = symbol == "NQ" ? InstrumentSpec.NQ : InstrumentSpec.ES;
            int fast = int.Parse(fastBox.Text ?? "5", CultureInfo.InvariantCulture);
            int slow = int.Parse(slowBox.Text ?? "12", CultureInfo.InvariantCulture);
            int qty = int.Parse(qtyBox.Text ?? "1", CultureInfo.InvariantCulture);
            int? slTicks = int.TryParse(stopBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sl) && sl > 0 ? sl : null;
            int? tpTicks = int.TryParse(targetBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tp) && tp > 0 ? tp : null;
            double comm = double.Parse(commBox.Text ?? "2.5", CultureInfo.InvariantCulture);
            int slip = int.Parse(slipBox.Text ?? "1", CultureInfo.InvariantCulture);

            IReadOnlyList<Bar> bars = Array.Empty<Bar>();
            IReadOnlyList<Tick> ticks = Array.Empty<Tick>();
            string inputText = (fileBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(inputText) || inputText.StartsWith("(", StringComparison.Ordinal))
            {
                bars = GenerateBuiltInSampleBars();
            }
            else if (ticksCheck.IsChecked == true)
            {
                ticks = new NinjaTraderTickImporter().Read(inputText);
            }
            else
            {
                bars = new NinjaTraderMinuteImporter().Read(inputText);
            }

            var result = new BacktestRunner().Run(new BacktestRequest
            {
                Bars = bars,
                Ticks = ticks,
                Instrument = spec,
                FastPeriod = fast,
                SlowPeriod = slow,
                Quantity = qty,
                StopLossTicks = slTicks,
                TakeProfitTicks = tpTicks,
                CommissionPerContractPerSide = comm,
                SlippageTicks = slip
            });

            pnlMetric.Text = $"{result.Summary.NetProfit:+0.00;-0.00;0.00} USD";
            pnlMetric.Foreground = result.Summary.NetProfit >= 0 ? SolidColorBrush.Parse("#34D399") : SolidColorBrush.Parse("#F87171");
            winRateMetric.Text = $"{result.Summary.WinRate * 100:F1}% ({result.Summary.WinningTrades}/{result.Summary.ClosedTrades})";
            closedDdMetric.Text = $"-{result.Summary.MaxClosedTradeDrawdown:F2} USD";
            intradayDdMetric.Text = $"-{result.Summary.MaxIntradayTrailingDrawdown:F2} USD";
            intradayDdMetric.Foreground = SolidColorBrush.Parse("#FBBF24");

            chartSurface.SetData(result);
            PopulatePropFirms(result);
            PopulateTrades(result);

            statusBanner.Text = $"Simulación completada: {result.Summary.Bars} velas procesadas, {result.Summary.ClosedTrades} trades cerrados ({spec.Symbol}).";
            statusBanner.Foreground = SolidColorBrush.Parse("#34D399");
        }
        catch (Exception ex)
        {
            statusBanner.Text = "Error: " + ex.Message;
            statusBanner.Foreground = SolidColorBrush.Parse("#F87171");
        }
    }

    private void PopulatePropFirms(BacktestResult result)
    {
        propFirmRows.Children.Clear();
        string propRoot = Path.Combine(workspace, "prop-firms");
        var firms = PropFirmEvaluator.LoadAll(propRoot);
        var evaluator = new PropFirmEvaluator();

        foreach (var firm in firms)
        {
            foreach (var plan in firm.Plans)
            {
                var eval = evaluator.Evaluate(result, firm, plan);
                string badge = eval.Passed ? "APROBADA" : eval.AccountBlown ? "QUEMADA" : "EN CURSO";
                string color = eval.Passed ? "#10B981" : eval.AccountBlown ? "#EF4444" : "#38BDF8";

                var row = new Border
                {
                    Background = SolidColorBrush.Parse("#1E293B"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 5)
                };
                var text = new TextBlock
                {
                    Text = $"[{badge}] {plan.Name,-18} | Meta: ${plan.ProfitTargetUsd:F0} | MaxDD obs: ${eval.MaxObservedDrawdownUsd:F0}/${plan.MaxDrawdownUsd:F0} ({plan.DrawdownType}) | {eval.SummaryReason}",
                    Foreground = SolidColorBrush.Parse(color),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                row.Child = text;
                propFirmRows.Children.Add(row);
            }
        }
    }

    private void PopulateTrades(BacktestResult result)
    {
        tradeRows.Children.Clear();
        foreach (var t in result.Trades)
        {
            string color = t.NetProfit >= 0 ? "#34D399" : "#F87171";
            var row = new Border
            {
                Background = SolidColorBrush.Parse("#1E293B"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5)
            };
            row.Child = new TextBlock
            {
                Text = $"#{t.TradeNumber} {t.Side} x{t.Quantity} | {t.EntryPrice:F2} -> {t.ExitPrice:F2} | Neto: {t.NetProfit:+0.00;-0.00} USD | MAE: ${t.MaxAdverseExcursionUsd:F0} | MFE: ${t.MaxFavorableExcursionUsd:F0} | {t.ExitReason}",
                Foreground = SolidColorBrush.Parse(color),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tradeRows.Children.Add(row);
        }
    }

    private static IReadOnlyList<Bar> GenerateBuiltInSampleBars()
    {
        var list = new List<Bar>(180);
        var start = new DateTime(2026, 9, 2, 13, 30, 0, DateTimeKind.Utc);
        double price = 5600.0;
        for (int i = 1; i <= 180; i++)
        {
            double wave = Math.Sin(i * 0.14) * 3.25 + Math.Cos(i * 0.05) * 1.75 + (i * 0.08);
            double open = Math.Round((price) * 4.0) / 4.0;
            double close = Math.Round((5600.0 + wave) * 4.0) / 4.0;
            double high = Math.Max(open, close) + 1.50;
            double low = Math.Min(open, close) - 1.25;
            list.Add(new Bar(start.AddMinutes(i), open, high, low, close, 1200 + (i % 15) * 110));
            price = close;
        }
        return list;
    }

    private static TextBox CreateInput(string text, double width = 110) => new()
    {
        Text = text,
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBlock SectionLabel(string title) => new()
    {
        Text = title,
        FontWeight = FontWeight.SemiBold,
        Foreground = SolidColorBrush.Parse("#E2E8F0"),
        Margin = new Thickness(0, 10, 0, 4)
    };

    private static Control FieldRow(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), Margin = new Thickness(0, 2) };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#94A3B8"),
            FontSize = 13
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
        return grid;
    }

    private static Border CreateMetricCard(string label, out TextBlock valueBlock)
    {
        valueBlock = new TextBlock
        {
            Text = "0.00 USD",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = SolidColorBrush.Parse("#94A3B8") });
        stack.Children.Add(valueBlock);
        return new Border
        {
            Background = SolidColorBrush.Parse("#111827"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(4, 0),
            Child = stack
        };
    }

    private static Border CreateTableBox(string title, Control content, Thickness margin)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            Foreground = SolidColorBrush.Parse("#E2E8F0"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        var scroll = new ScrollViewer { Content = content };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return new Border
        {
            Background = SolidColorBrush.Parse("#111827"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = margin,
            Child = grid
        };
    }
}

internal sealed class ChartSurface : Control
{
    private BacktestResult? current;

    public void SetData(BacktestResult result)
    {
        current = result;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 50 || bounds.Height < 50 || current is null || current.Signals.Count == 0)
            return;

        double priceHeight = bounds.Height * 0.68;
        double equityTop = priceHeight + 14;
        double equityHeight = Math.Max(40, bounds.Height - equityTop - 8);

        var bars = current.Signals;
        double minPrice = bars.Min(b => b.Low);
        double maxPrice = bars.Max(b => b.High);
        if (Math.Abs(maxPrice - minPrice) < 1e-6) maxPrice = minPrice + 1;

        var gridPen = new Pen(SolidColorBrush.Parse("#1E293B"), 1);
        for (int g = 1; g <= 4; g++)
        {
            double y = priceHeight * g / 5.0;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        var bullBrush = SolidColorBrush.Parse("#10B981");
        var bearBrush = SolidColorBrush.Parse("#EF4444");
        var bullPen = new Pen(bullBrush, 1);
        var bearPen = new Pen(bearBrush, 1);
        var fastPen = new Pen(SolidColorBrush.Parse("#38BDF8"), 1.8);
        var slowPen = new Pen(SolidColorBrush.Parse("#F59E0B"), 1.8);

        double stepX = bounds.Width / Math.Max(1, bars.Count);
        double candleWidth = Math.Clamp(stepX * 0.65, 2.0, 10.0);

        Point? prevFast = null;
        Point? prevSlow = null;

        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            double x = i * stepX + stepX * 0.5;
            double yHigh = MapPrice(b.High);
            double yLow = MapPrice(b.Low);
            double yOpen = MapPrice(b.Open);
            double yClose = MapPrice(b.Close);
            bool bull = b.Close >= b.Open;

            context.DrawLine(bull ? bullPen : bearPen, new Point(x, yHigh), new Point(x, yLow));
            double top = Math.Min(yOpen, yClose);
            double bodyH = Math.Max(1.5, Math.Abs(yClose - yOpen));
            context.FillRectangle(bull ? bullBrush : bearBrush, new Rect(x - candleWidth / 2, top, candleWidth, bodyH));

            if (b.Fast.HasValue)
            {
                var pt = new Point(x, MapPrice(b.Fast.Value));
                if (prevFast.HasValue) context.DrawLine(fastPen, prevFast.Value, pt);
                prevFast = pt;
            }
            if (b.Slow.HasValue)
            {
                var pt = new Point(x, MapPrice(b.Slow.Value));
                if (prevSlow.HasValue) context.DrawLine(slowPen, prevSlow.Value, pt);
                prevSlow = pt;
            }
        }

        // Subgráfico de Curva de Capital Cerrada
        context.DrawLine(gridPen, new Point(0, equityTop), new Point(bounds.Width, equityTop));
        if (current.Trades.Count > 0)
        {
            var points = new List<double> { 0d };
            double eq = 0d;
            foreach (var t in current.Trades)
            {
                eq += t.NetProfit;
                points.Add(eq);
            }
            double minEq = Math.Min(0, points.Min());
            double maxEq = Math.Max(1, points.Max());
            var eqPen = new Pen(SolidColorBrush.Parse("#A855F7"), 2.2);
            for (int i = 1; i < points.Count; i++)
            {
                double x1 = (i - 1) * (bounds.Width / Math.Max(1, points.Count - 1));
                double x2 = i * (bounds.Width / Math.Max(1, points.Count - 1));
                double y1 = equityTop + equityHeight - ((points[i - 1] - minEq) / (maxEq - minEq)) * (equityHeight - 8);
                double y2 = equityTop + equityHeight - ((points[i] - minEq) / (maxEq - minEq)) * (equityHeight - 8);
                context.DrawLine(eqPen, new Point(x1, y1), new Point(x2, y2));
            }
        }

        double MapPrice(double price) =>
            8 + (1.0 - (price - minPrice) / (maxPrice - minPrice)) * (priceHeight - 16);
    }
}
