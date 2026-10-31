#!/usr/bin/env python3
"""Descarga y convierte históricos de NQ (Databento GLBX.MDP3) a formato NinjaTrader 8 / Ascent Backtester.

Soporta dos modos:
1. Descarga directa autenticada desde Databento (`timeseries.get_range`), verificando
   primero el costo exacto con `metadata.get_cost` y bloqueando si supera `--max-usd`.
2. Conversión local (`--desde-archivo`) a partir de un archivo `.csv` o `.csv.zst` ya
   descargado del portal Batch de Databento.

Para `ohlcv-1m` genera:
- `fuente-databento.csv.zst` (respaldo íntegro comprimido con Zstandard)
- `NQ_1min_UTC_cierre.txt` (cierre exclusivo UTC +1 min, sin ajuste de rollover)
- `NQ_1min_UTC_cierre_ajustado.txt` (cierre exclusivo UTC +1 min, back-adjusted al tick 0.25 en cada cambio de instrument_id)
- `rollovers.json` y `manifest.json` con hashes SHA-256.

Para `tbbo` genera:
- `fuente-databento.csv.zst`
- `NQ_ticks_UTC_L1.txt` (5 columnas: `yyyyMMdd HHmmss fffffff;Last;Bid;Ask;Volume`)
- `manifest.json` con hashes SHA-256.
"""

from __future__ import annotations

import argparse
import base64
from compression import zstd
import csv
from datetime import date, datetime, timezone
import getpass
import hashlib
import io
import json
import math
import os
from pathlib import Path
import shutil
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


DATASET = "GLBX.MDP3"
DEFAULT_SYMBOL = "NQ.v.0"
TICK_SIZE = 0.25
UNDEF_PRICE_RAW = 9223372036854775807
API_ROOT = "https://hist.databento.com/v0/"


def auth_header(api_key: str) -> dict[str, str]:
    token = base64.b64encode(f"{api_key}:".encode("utf-8")).decode("ascii")
    return {"Authorization": f"Basic {token}"}


def request_json(endpoint: str, api_key: str, parameters: dict[str, str]) -> object:
    url = API_ROOT + endpoint + "?" + urlencode(parameters)
    req = Request(url, headers={**auth_header(api_key), "Accept": "application/json"})
    with urlopen(req, timeout=120) as resp:
        return json.load(resp)


def download_stream_to_file(api_key: str, parameters: dict[str, str], dest_path: Path) -> None:
    data = urlencode(parameters).encode("utf-8")
    req = Request(
        API_ROOT + "timeseries.get_range",
        data=data,
        headers={
            **auth_header(api_key),
            "Content-Type": "application/x-www-form-urlencoded",
        },
        method="POST",
    )
    tmp_path = dest_path.with_suffix(dest_path.suffix + ".part")
    downloaded = 0
    next_report = 5 * 1024 * 1024
    with urlopen(req, timeout=900) as resp, open(tmp_path, "wb") as out_file:
        while chunk := resp.read(1024 * 1024):
            out_file.write(chunk)
            downloaded += len(chunk)
            if downloaded >= next_report:
                print(f"  Descargados {downloaded / (1024 * 1024):.1f} MiB comprimidos...", flush=True)
                next_report += 5 * 1024 * 1024
    os.replace(tmp_path, dest_path)
    print(f"  Descarga finalizada: {downloaded / (1024 * 1024):.2f} MiB comprimidos en disco.", flush=True)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


def parse_ns_timestamp(raw: str) -> int:
    text = raw.strip()
    if text.isdigit() or (text.startswith("-") and text[1:].isdigit()):
        return int(text)
    if text.endswith("Z"):
        text = text[:-1]
    if "." in text:
        head, frac = text.split(".", 1)
        frac_ns = int((frac + "000000000")[:9])
    else:
        head, frac_ns = text, 0
    dt = datetime.fromisoformat(head).replace(tzinfo=timezone.utc)
    return int(dt.timestamp()) * 1_000_000_000 + frac_ns


def parse_price(raw: str) -> float | None:
    text = raw.strip()
    if not text:
        return None
    if "." not in text and "e" not in text.lower():
        val_int = int(text)
        if val_int == UNDEF_PRICE_RAW:
            return None
        val = val_int / 1e9
    else:
        val = float(text)
    if not math.isfinite(val):
        return None
    return val


def round_tick(price: float, tick_size: float = TICK_SIZE) -> float:
    return round(round(price / tick_size) * tick_size, 2)


def format_price(price: float) -> str:
    rounded = round(price, 2)
    text = f"{rounded:.2f}"
    if text.endswith(".00"):
        return text[:-3]
    if text.endswith("0"):
        return text[:-1]
    return text


def format_minute_utc(ns: int) -> str:
    sec = ns // 1_000_000_000
    return datetime.fromtimestamp(sec, tz=timezone.utc).strftime("%Y%m%d %H%M%S")


def format_tick_utc(ns: int) -> str:
    sec, rem_ns = divmod(ns, 1_000_000_000)
    ticks_100ns = rem_ns // 100
    base = datetime.fromtimestamp(sec, tz=timezone.utc).strftime("%Y%m%d %H%M%S")
    return f"{base} {ticks_100ns:07d}"


def open_csv_reader(source_path: Path):
    if source_path.suffix == ".zst":
        raw_stream = zstd.open(source_path, mode="rb")
        return io.TextIOWrapper(raw_stream, encoding="utf-8", newline="")
    return open(source_path, mode="r", encoding="utf-8", newline="")


def convert_ohlcv_1m(source_path: Path, out_dir: Path) -> dict[str, object]:
    raw_rows: list[tuple[int, int, float, float, float, float, int]] = []
    skipped_invalid = 0
    duplicates_merged = 0

    with open_csv_reader(source_path) as text_stream:
        reader = csv.DictReader(text_stream)
        for row in reader:
            ts_ns = parse_ns_timestamp(row["ts_event"])
            close_ns = ts_ns + 60_000_000_000
            inst_id = int(row.get("instrument_id") or 0)
            o = parse_price(row["open"])
            h = parse_price(row["high"])
            l = parse_price(row["low"])
            c = parse_price(row["close"])
            vol = int(row["volume"])
            if o is None or h is None or l is None or c is None or vol < 0:
                skipped_invalid += 1
                continue
            if h < l or o < l or o > h or c < l or c > h:
                skipped_invalid += 1
                continue
            if raw_rows and close_ns <= raw_rows[-1][0]:
                if close_ns == raw_rows[-1][0]:
                    duplicates_merged += 1
                    if vol > raw_rows[-1][6]:
                        raw_rows[-1] = (close_ns, inst_id, o, h, l, c, vol)
                else:
                    skipped_invalid += 1
                continue
            raw_rows.append((close_ns, inst_id, o, h, l, c, vol))

    if not raw_rows:
        raise ValueError("No se encontraron barras válidas de 1 minuto para convertir.")

    # Calcular rollovers (cambios de instrument_id) y ajuste hacia atrás (back-adjustment Panamá)
    rollovers = []
    deltas = [0.0] * len(raw_rows)
    for i in range(1, len(raw_rows)):
        prev_inst = raw_rows[i - 1][1]
        curr_inst = raw_rows[i][1]
        if prev_inst != 0 and curr_inst != 0 and curr_inst != prev_inst:
            # Diferencia entre la apertura del nuevo contrato y el cierre del contrato anterior, redondeada al tick
            gap = round_tick(raw_rows[i][2] - raw_rows[i - 1][5])
            deltas[i] = gap
            rollovers.append(
                {
                    "bar_index": i,
                    "close_time_utc": datetime.fromtimestamp(raw_rows[i][0] // 1_000_000_000, tz=timezone.utc).isoformat(),
                    "from_instrument_id": prev_inst,
                    "to_instrument_id": curr_inst,
                    "previous_close": raw_rows[i - 1][5],
                    "new_open": raw_rows[i][2],
                    "adjustment_points": gap,
                }
            )

    # Acumular ajuste desde el contrato más reciente hacia atrás (el contrato actual queda en offset 0.0)
    cumulative_adj = [0.0] * len(raw_rows)
    running = 0.0
    for i in range(len(raw_rows) - 1, 0, -1):
        running = round_tick(running + deltas[i])
        cumulative_adj[i - 1] = running

    unadjusted_path = out_dir / "NQ_1min_UTC_cierre.txt"
    adjusted_path = out_dir / "NQ_1min_UTC_cierre_ajustado.txt"
    rollovers_path = out_dir / "rollovers.json"

    with (
        open(unadjusted_path, "w", encoding="utf-8", newline="\n") as f_unadj,
        open(adjusted_path, "w", encoding="utf-8", newline="\n") as f_adj,
    ):
        for idx, (close_ns, _, o, h, l, c, vol) in enumerate(raw_rows):
            ts_str = format_minute_utc(close_ns)
            f_unadj.write(
                f"{ts_str};{format_price(o)};{format_price(h)};{format_price(l)};{format_price(c)};{vol}\n"
            )
            adj = cumulative_adj[idx]
            ao = round_tick(o + adj)
            ah = round_tick(h + adj)
            al = round_tick(l + adj)
            ac = round_tick(c + adj)
            f_adj.write(
                f"{ts_str};{format_price(ao)};{format_price(ah)};{format_price(al)};{format_price(ac)};{vol}\n"
            )

    with open(rollovers_path, "w", encoding="utf-8") as f_roll:
        json.dump(rollovers, f_roll, indent=2, ensure_ascii=False)

    return {
        "schema": "ohlcv-1m",
        "bar_count": len(raw_rows),
        "skipped_invalid_rows": skipped_invalid,
        "duplicates_merged": duplicates_merged,
        "rollover_count": len(rollovers),
        "first_close_utc": datetime.fromtimestamp(raw_rows[0][0] // 1_000_000_000, tz=timezone.utc).isoformat(),
        "last_close_utc": datetime.fromtimestamp(raw_rows[-1][0] // 1_000_000_000, tz=timezone.utc).isoformat(),
        "unadjusted_file": unadjusted_path.name,
        "unadjusted_sha256": sha256_file(unadjusted_path),
        "adjusted_file": adjusted_path.name,
        "adjusted_sha256": sha256_file(adjusted_path),
        "rollovers_file": rollovers_path.name,
    }


def convert_tbbo(source_path: Path, out_dir: Path) -> dict[str, object]:
    ticks_path = out_dir / "NQ_ticks_UTC_L1.txt"
    bars_path = out_dir / "NQ_1min_UTC_cierre.txt"
    tick_count = 0
    bar_count = 0
    skipped_crossed_or_empty_book = 0
    skipped_out_of_order = 0
    first_ns: int | None = None
    last_ns: int | None = None

    bucket_close_ns: int | None = None
    b_open = b_high = b_low = b_close = 0.0
    b_vol = 0

    with (
        open_csv_reader(source_path) as text_stream,
        open(ticks_path, "w", encoding="utf-8", newline="\n") as out_f,
        open(bars_path, "w", encoding="utf-8", newline="\n") as bars_f,
    ):
        reader = csv.DictReader(text_stream)
        for row in reader:
            ts_ns = parse_ns_timestamp(row["ts_event"])
            price = parse_price(row["price"])
            bid = parse_price(row.get("bid_px_00", ""))
            ask = parse_price(row.get("ask_px_00", ""))
            vol = int(row["size"])

            if price is None or vol < 0:
                skipped_crossed_or_empty_book += 1
                continue
            if bid is None or ask is None or ask < bid:
                skipped_crossed_or_empty_book += 1
                continue
            # Truncar a resolución de 100ns (7 decimales de segundo) para comparar orden
            ts_100ns = (ts_ns // 100) * 100
            if last_ns is not None and ts_100ns < last_ns:
                skipped_out_of_order += 1
                continue

            if first_ns is None:
                first_ns = ts_100ns
            last_ns = ts_100ns
            tick_count += 1
            out_f.write(
                f"{format_tick_utc(ts_100ns)};{format_price(price)};{format_price(bid)};{format_price(ask)};{vol}\n"
            )

            # Reconstruir vela de 1 minuto (cierre exclusivo UTC)
            minute_close_ns = ((ts_100ns // 60_000_000_000) + 1) * 60_000_000_000
            if bucket_close_ns is not None and minute_close_ns > bucket_close_ns:
                bars_f.write(
                    f"{format_minute_utc(bucket_close_ns)};{format_price(b_open)};{format_price(b_high)};{format_price(b_low)};{format_price(b_close)};{b_vol}\n"
                )
                bar_count += 1
                bucket_close_ns = None

            if bucket_close_ns is None:
                bucket_close_ns = minute_close_ns
                b_open = b_high = b_low = b_close = price
                b_vol = vol
            else:
                if price > b_high:
                    b_high = price
                if price < b_low:
                    b_low = price
                b_close = price
                b_vol += vol

        if bucket_close_ns is not None:
            bars_f.write(
                f"{format_minute_utc(bucket_close_ns)};{format_price(b_open)};{format_price(b_high)};{format_price(b_low)};{format_price(b_close)};{b_vol}\n"
            )
            bar_count += 1

    if tick_count == 0 or first_ns is None or last_ns is None:
        raise ValueError("No se encontraron ticks válidos en el archivo TBBO.")

    return {
        "schema": "tbbo",
        "tick_count": tick_count,
        "derived_bar_count": bar_count,
        "skipped_crossed_or_empty_book": skipped_crossed_or_empty_book,
        "skipped_out_of_order": skipped_out_of_order,
        "first_tick_utc": datetime.fromtimestamp(first_ns / 1e9, tz=timezone.utc).isoformat(),
        "last_tick_utc": datetime.fromtimestamp(last_ns / 1e9, tz=timezone.utc).isoformat(),
        "derived_file": ticks_path.name,
        "derived_sha256": sha256_file(ticks_path),
        "derived_minute_bars_file": bars_path.name,
        "derived_minute_bars_sha256": sha256_file(bars_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--esquema",
        choices=("ohlcv-1m", "tbbo"),
        default="ohlcv-1m",
        help="Esquema a descargar o convertir: 'ohlcv-1m' (1 minuto) o 'tbbo' (ticks con Bid/Ask).",
    )
    parser.add_argument("--simbolo", default=DEFAULT_SYMBOL, help="Símbolo continuo en Databento (por defecto NQ.v.0).")
    parser.add_argument("--inicio", default="2010-06-06", help="Fecha inicial UTC incluida (AAAA-MM-DD).")
    parser.add_argument("--fin", default="2026-10-30", help="Fecha final UTC excluida (AAAA-MM-DD).")
    parser.add_argument(
        "--max-usd",
        type=float,
        default=25.0,
        help="Tope máximo autorizado en USD antes de abortar la descarga (por defecto: 25.0).",
    )
    parser.add_argument(
        "--destino",
        help="Carpeta de salida (por defecto: data/databento/<esquema>).",
    )
    parser.add_argument(
        "--desde-archivo",
        help="Convierte un archivo .csv o .csv.zst local ya descargado sin llamar a la API de Databento.",
    )
    parser.add_argument(
        "--solo-cotizar",
        action="store_true",
        help="Verifica el costo exacto en tu cuenta con metadata.get_cost y termina sin descargar.",
    )
    args = parser.parse_args()

    try:
        start_d, end_d = date.fromisoformat(args.inicio), date.fromisoformat(args.fin)
        if start_d >= end_d:
            parser.error("--fin debe ser posterior a --inicio")
    except ValueError:
        parser.error("Las fechas deben tener el formato AAAA-MM-DD")

    script_dir = Path(__file__).resolve().parent
    data_root = script_dir.parent if script_dir.name == "scripts" else script_dir
    out_dir = (
        Path(args.destino).resolve()
        if args.destino
        else data_root / args.esquema
    )
    out_dir.mkdir(parents=True, exist_ok=True)

    default_raw_path = out_dir / "fuente-databento.csv.zst"
    if args.desde_archivo:
        raw_path = Path(args.desde_archivo).resolve()
        if not raw_path.is_file():
            print(f"No existe el archivo: {raw_path}", file=sys.stderr)
            return 2
        cost_usd = None
        billable_bytes = None
    elif not args.solo_cotizar and default_raw_path.is_file() and default_raw_path.stat().st_size > 0:
        print(f"Ya existe descarga previa en {default_raw_path}; se reutilizará sin volver a cobrar en Databento.")
        raw_path = default_raw_path
        cost_usd = None
        billable_bytes = None
    else:
        api_key = os.environ.get("DATABENTO_API_KEY") or getpass.getpass("Clave API de Databento (no se mostrará): ")
        api_key = api_key.strip()
        if not api_key:
            print("No se proporcionó una clave API.", file=sys.stderr)
            return 2

        meta_params = {
            "dataset": DATASET,
            "symbols": args.simbolo,
            "stype_in": "continuous",
            "schema": args.esquema,
            "start": start_d.isoformat(),
            "end": end_d.isoformat(),
        }
        try:
            cost_usd = float(request_json("metadata.get_cost", api_key, meta_params))
            billable_bytes = int(request_json("metadata.get_billable_size", api_key, meta_params))
        except HTTPError as err:
            body = err.read().decode("utf-8", errors="replace")
            print(f"Error HTTP {err.code} al consultar metadata: {body}", file=sys.stderr)
            return 1
        except (URLError, TimeoutError, ValueError, OSError) as err:
            print(f"Fallo al consultar metadata: {err}", file=sys.stderr)
            return 1

        print(f"Cotización oficial de cuenta | {args.simbolo} ({args.esquema}) [{start_d}, {end_d}):")
        print(f"  Costo estimado : USD {cost_usd:.2f} (Límite autorizado --max-usd: USD {args.max_usd:.2f})")
        print(f"  Tamaño facturable: {billable_bytes / (1024 ** 3):.3f} GiB ({billable_bytes:,} bytes)")

        if args.solo_cotizar:
            return 0

        if cost_usd > args.max_usd:
            print(
                f"\nDESCARGA BLOQUEADA POR SEGURIDAD: El costo (USD {cost_usd:.2f}) supera --max-usd ({args.max_usd:.2f}).\n"
                f"Si deseas autorizarlo, vuelve a ejecutar con --max-usd {math.ceil(cost_usd + 1):.0f}",
                file=sys.stderr,
            )
            return 3

        raw_path = out_dir / "fuente-databento.csv.zst"
        dl_params = {
            **meta_params,
            "encoding": "csv",
            "compression": "zstd",
            "pretty_px": "true",
            "pretty_ts": "false",
        }
        print(f"\nDescargando stream comprimido (Zstd) hacia {raw_path} ...")
        try:
            download_stream_to_file(api_key, dl_params, raw_path)
        except HTTPError as err:
            body = err.read().decode("utf-8", errors="replace")
            print(f"Error HTTP {err.code} durante la descarga: {body}", file=sys.stderr)
            return 1

    print(f"Convirtiendo {raw_path.name} al formato NinjaTrader 8 / Ascent Backtester ...")
    conversion_summary = (
        convert_ohlcv_1m(raw_path, out_dir)
        if args.esquema == "ohlcv-1m"
        else convert_tbbo(raw_path, out_dir)
    )

    manifest = {
        "provider": "Databento",
        "dataset": DATASET,
        "symbol": args.simbolo,
        "stype_in": "continuous",
        "schema": args.esquema,
        "start_inclusive_utc": start_d.isoformat(),
        "end_exclusive_utc": end_d.isoformat(),
        "quoted_cost_usd": cost_usd,
        "billable_size_bytes": billable_bytes,
        "created_utc": datetime.now(tz=timezone.utc).isoformat(),
        "source_file": raw_path.name,
        "source_sha256": sha256_file(raw_path),
        **conversion_summary,
    }
    manifest_path = out_dir / "manifest.json"
    with open(manifest_path, "w", encoding="utf-8") as f_man:
        json.dump(manifest, f_man, indent=2, ensure_ascii=False)

    print(f"\nProceso completado con éxito en: {out_dir}")
    print(json.dumps(manifest, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
