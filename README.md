# Ascent Backtester

Motor de backtesting y simulación en C# (.NET 10) para estrategias de futuros (**ES** y **NQ**) y evaluación realista de reglas de empresas de fondeo.

## Capacidades principales

1. **Instrumentos (`ES` y `NQ`)**: Especificación de tick y valor por punto en [`InstrumentSpec`](src/core/Models/InstrumentSpec.cs).
2. **Contrato genérico `IStrategy`**: Desacopla el motor de cualquier estrategia específica en [`StrategyContracts.cs`](src/core/Models/StrategyContracts.cs).
3. **Ejecución por Minuto y Tick-by-Tick**:
   - **Minuto (`NinjaTraderMinuteImporter`)**: Evalúa Stop Loss y Take Profit intravela aplicando la regla pesimista **"Stop primero"** si una misma vela toca ambos niveles.
   - **Tick-by-Tick (`NinjaTraderTickImporter` + `Tick`)**: Lee flujos de ticks (con precios `Last`, `Bid`, `Ask` y cálculo de Delta Bid/Ask), llena órdenes contra `Bid`/`Ask` más *slippage* y evalúa SL/TP al milisegundo.
4. **Seguimiento de Excursión (`MAE` y `MFE`) y Trailing Drawdown Intradía**:
   - Cada operación registra su máxima pérdida flotante (`MAE_USD`) y máxima ganancia flotante (`MFE_USD`), permitiendo a [`PropFirmEvaluator`](src/analyzer/PropFirmEvaluator.cs) calcular el *Trailing Drawdown Intradía* real (ej. Apex).
5. **Guardián de Sesión y Riesgo Diario**:
   - Ventana operativa (`--session-start`, `--session-end`) con cierre automático al terminar la sesión.
   - Límites diarios (`--max-daily-loss`, `--max-daily-trades`).
