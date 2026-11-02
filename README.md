# Ascent Backtester

Motor de backtesting cuantitativo y analizador profesional en C# (.NET 10 / .NET Framework 4.8) para estrategias de futuros (**NQ**, **MNQ**, **ES** y **MES**) y evaluación exacta de reglas de empresas de fondeo.

## Capacidades principales

1. **Instrumentos Mini y Micro (`ES`, `NQ`, `MES`, `MNQ`)**: Especificación de tick, valor por punto y detección de contratos micro en [`InstrumentSpec`](src/core/Models/InstrumentSpec.cs).
2. **Contrato genérico `IStrategy` e Indicadores con Paridad NinjaTrader (`Futures.Core`)**:
   - [`AnchoredVwap`](src/core/Indicators/AnchoredVwap.cs): VWAP anclado por sesión UTC con bandas $\sigma$, pendiente canónica (`SlopeLookbackBars`) y filtro de extensión (`ExtendedDistancePoints`), con paridad exacta respecto a `dav3e_vwap`.
   - [`WilderAtr`](src/core/Indicators/WilderAtr.cs): ATR con suavizado de Wilder continuo en barras cerradas, con paridad exacta respecto a `dav3e_hud`.
   - [`VwapDeltaPullbackStrategy`](src/core/Strategies/VwapDeltaPullbackStrategy.cs) y [`SmaCrossoverStrategy`](src/core/Strategies/SmaCrossoverStrategy.cs).
3. **Ejecución por Minuto y Tick-by-Tick Realista ([`BacktestRunner`](src/analyzer/BacktestRunner.cs))**:
   - **Minuto (`NinjaTraderMinuteImporter`)**: Evalúa Stop Loss y Take Profit intravela aplicando la regla pesimista **"Stop primero"**, llenado consciente de *gaps* en la apertura (`bar.Open`), y recorrido cronológico intravela según polaridad de vela.
   - **Tick-by-Tick (`NinjaTraderTickImporter` + [`Tick`](src/core/Models/Tick.cs))**: Lee flujos Level 1 (`Last`, `Bid`, `Ask`), agrega métricas de *Order Flow* (`DeltaVolume`, `DeltaRatio`, `AskVolume`, `BidVolume`, `TickCount`) en cada [`Bar`](src/core/Models/Bar.cs), llena órdenes contra `Bid`/`Ask` más *slippage* y aplica reglas de sesión y pérdida diaria al milisegundo.
   - **Gestión Dinámica de Posición**: Soporta *Break-Even* automático (`--breakeven-ticks`, `--breakeven-offset`), *Trailing Stop* dinámico (`--trailing-ticks`) y reversiones limpias con contabilidad exacta de comisiones por lado.
4. **Seguimiento Cronológico Real de `MAE`, `MFE` y *Intraday Trailing Drawdown***:
   - Mide en orden estrictamente cronológico el pico de equidad flotante (descontando comisión de entrada) y la caída *Peak-to-Trough* real de cada operación, evitando inflar el drawdown en operaciones ganadoras.
   - [`PropFirmEvaluator`](src/analyzer/PropFirmEvaluator.cs) aplica el congelamiento del umbral de liquidación (*Safety Net Buffer*), límites de contratos Mini/Micro, *Daily Loss Limit* (Soft/Hard Breach) y regla de consistencia.
5. **Métricas Cuantitativas Profesionales ([`BacktestSummary`](src/analyzer/BacktestModels.cs))**:
   - Esperanza matemática en USD y en múltiplos $R$ (`AverageRMultiple`), *Profit Factor*, *Payoff Ratio*, *Sharpe Ratio* anualizado, *Sortino Ratio* anualizado, *Recovery Factor*, rachas máximas, eficiencia de captura MFE y desglose diario (`daily.csv`).
6. **Interfaz Gráfica Nativa Multiplataforma ([`DesktopUi.cs`](src/app/DesktopUi.cs))**:
   - Gráfico interactivo OHLCV / TBBO, página de historial de 66 vencimientos trimestrales y pestaña dedicada **⚡ Analizador de Estrategias & Fondeo** con curva de equidad, tarjetas de métricas, evaluación de empresas de fondeo y tabla detallada de operaciones.
