# Datos Históricos de Databento (`GLBX.MDP3`)

Los datos están organizados **por instrumento** y **por esquema**, sin copias duplicadas y con los ticks TBBO particionados por año/mes para cargas rápidas e ingestión incremental.

## Estructura de carpetas

```text
data/databento/
├── README.md
├── scripts/
│   ├── cotizar_nq_databento.py       # Cotiza costos en Databento (--simbolo NQ.v.0 / ES.v.0)
│   └── descargar_nq_databento.py     # Descarga e ingesta incrementalmente en esta estructura
└── NQ/                               # Un directorio por instrumento (mañana: ES/, etc.)
    ├── ohlcv-1m/
    │   ├── NQ_1min_UTC_cierre.txt
    │   ├── NQ_1min_UTC_cierre_ajustado.txt
    │   ├── rollovers.json
    │   ├── dates_cache.json
    │   ├── manifest.json
    │   └── raw/
    │       └── 20100606-20261030.ohlcv-1m.csv.zst
    └── tbbo/
        ├── catalog.json              # Fuente única de verdad: cobertura, meses, hashes y lotes raw
        ├── dates_cache.json          # Índice de fechas UTC con operaciones (2,762 días)
        ├── NQ_1min_UTC_cierre.txt    # Velas de 1m derivadas de los ticks TBBO (~137.8 MB)
        ├── ticks/                    # Ticks L1 particionados por año/mes (sin duplicados)
        │   ├── 2010/
        │   │   ├── 2010-06.txt
        │   │   └── ...
        │   └── 2019/
        │       └── 2019-05.txt
        └── raw/                      # Respaldos originales comprimidos (.csv.zst) + auditoría
            ├── 20100606-20110914.tbbo.csv.zst
            ├── ...
            ├── 20180627-20190513.tbbo.csv.zst
            └── provider/
                └── 20180627-20190513/
```

## Cobertura actual de NQ (`NQ.v.0`)

| Esquema | Rango UTC | Cantidad | Ubicación activa |
|---|---|---:|---|
| **TBBO (Ticks L1)** | `2010-06-07` → `2019-05-13` | 349,071,522 ticks (108 meses) | `NQ/tbbo/ticks/` |
| **TBBO (Velas 1m derivadas)** | `2010-06-07` → `2019-05-13` | 2,922,762 velas | `NQ/tbbo/NQ_1min_UTC_cierre.txt` |
| **OHLCV 1m** | `2010-06-07` → `2026-10-30` | 5,559,143 velas (66 rollovers) | `NQ/ohlcv-1m/NQ_1min_UTC_cierre.txt` |

- Todos los archivos `.txt` usan formato NinjaTrader UTC delimitado por `;`:
  - **Ticks (`ticks/YYYY/YYYY-MM.txt`):** `yyyyMMdd HHmmss fffffff;Last;Bid;Ask;Volumen`
  - **Velas 1m (`*_1min_UTC_cierre.txt`):** `yyyyMMdd HHmmss;Open;High;Low;Close;Volumen` (timestamp de cierre exclusivo UTC).
- En el tramo `2018-2019` de NQ TBBO, Databento marca 5 fechas CME como `degraded`: `2018-10-21`, `2019-01-15`, `2019-02-22`, `2019-03-13` y `2019-03-26` (respaldadas en `NQ/tbbo/raw/provider/20180627-20190513/condition.json`).

## Uso en la aplicación

La CLI y la interfaz gráfica detectan automáticamente `data/databento/<INSTRUMENTO>/tbbo/ticks/` y abren **únicamente los archivos mensuales que intersectan el rango `--from`..`--to`**:

```bash
~/.dotnet/dotnet run --project src/app -- backtest run \
  --ticks --instrument NQ --strategy RandomEntry --seed 42 \
  --from 2019-05-01 --to 2019-05-13
```

Los contratos micro (`MNQ`, `MES`) reutilizan automáticamente la serie de su contrato padre (`NQ`, `ES`) si no tienen carpeta propia.

## Cómo añadir nuevos tramos u otros instrumentos (ej. ES)

El script [descargar_nq_databento.py](scripts/descargar_nq_databento.py) guarda el respaldo `.csv.zst` en `raw/`, escribe o fusiona los meses afectados en `ticks/YYYY/YYYY-MM.txt` (incluidos meses parciales como `2019-05`) y actualiza `NQ_1min_UTC_cierre.txt`, `dates_cache.json` y `catalog.json`:

```bash
# Continuar NQ TBBO desde el 14 de mayo de 2019
python3 data/databento/scripts/descargar_nq_databento.py \
  --esquema tbbo --simbolo NQ.v.0 --inicio 2019-05-14 --fin 2020-01-01 --max-usd 125

# Cotizar o descargar otro instrumento (creará automáticamente data/databento/ES/tbbo/)
python3 data/databento/scripts/cotizar_nq_databento.py \
  --simbolo ES.v.0 --inicio 2024-01-01 --fin 2025-01-01
```
