#!/usr/bin/env python3
"""Descarga y convierte históricos de futuros (Databento GLBX.MDP3) a formato Ascent Backtester / NinjaTrader 8.

Organización por instrumento bajo `data/databento/<INSTRUMENTO>/<esquema>/`:
- `ohlcv-1m`:
  - `raw/<inicio>-<fin>.ohlcv-1m.csv.zst`
  - `<INST>_1min_UTC_cierre.txt` (cierre exclusivo UTC +1 min, sin ajuste de rollover)
  - `<INST>_1min_UTC_cierre_ajustado.txt` (back-adjusted al tick en cada rollover)
  - `rollovers.json`, `dates_cache.json` y `manifest.json`
- `tbbo` (incremental, particionado por año/mes y sin duplicación):
  - `raw/<inicio>-<fin>.tbbo.csv.zst`
  - `ticks/YYYY/YYYY-MM.txt` (5 columnas: `yyyyMMdd HHmmss fffffff;Last;Bid;Ask;Volume`)
  - `<INST>_1min_UTC_cierre.txt` (velas de 1m derivadas de ticks, actualizadas incrementalmente)
  - `dates_cache.json` y `catalog.json`

Soporta dos modos:
1. Descarga directa autenticada desde Databento (`timeseries.get_range`), verificando
   primero el costo exacto con `metadata.get_cost` y bloqueando si supera `--max-usd`.
2. Conversión local (`--desde-archivo`) a partir de un archivo `.csv` o `.csv.zst` ya
   descargado del portal Batch de Databento.
"""

from __future__ import annotations

import argparse
import base64
from compression import zstd
import csv
from datetime import date, datetime, timedelta, timezone
import getpass
import hashlib
import io
import json
import math
import os
from pathlib import Path
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


DATASET = "GLBX.MDP3"
DEFAULT_SYMBOL = "NQ.v.0"
TICK_SIZE_BY_INSTRUMENT = {
    "NQ": 0.25,
    "MNQ": 0.25,
    "ES": 0.25,
    "MES": 0.25,
}
UNDEF_PRICE_RAW = 9223372036854775807
API_ROOT = "https://hist.databento.com/v0/"


def infer_instrument(symbol: str, override: str | None = None) -> str:
    if override and override.strip():
        return override.strip().upper()
    head = symbol.strip().split(".", 1)[0].upper()
    if not head:
        raise ValueError(f"No se pudo deducir el instrumento desde '{symbol}'.")
    return head


def auth_header(api_key: str) -> dict[str, str]:
    token = base64.b64encode(f"{api_key}:".encode("utf-8")).decode("ascii")
    return {"Authorization": f"Basic {token}"}


def request_json(endpoint: str, api_key: str, parameters: dict[str, str]) -> object:
    url = API_ROOT + endpoint + "?" + urlencode(parameters)
    req = Request(url, headers={**auth_header(api_key), "Accept": "application/json"})
    with urlopen(req, timeout=120) as resp:
        return json.load(resp)


def download_stream_to_file(api_key: str, parameters: dict[str, str], dest_path: Path) -> None:
    dest_path.parent.mkdir(parents=True, exist_ok=True)
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
        while chunk := f.read(4 * 1024 * 1024):
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


def round_tick(price: float, tick_size: float = 0.25) -> float:
    return round(round(price / tick_size) * tick_size, 6)


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


def parse_tick_iso(ts_prefix: bytes) -> str:
    s = ts_prefix.decode("ascii")
    y, m, d = s[0:4], s[4:6], s[6:8]
    hh, mm, ss = s[9:11], s[11:13], s[13:15]
    frac = s[16:23].rstrip("0")
    if frac:
        return f"{y}-{m}-{d}T{hh}:{mm}:{ss}.{frac}+00:00"
    return f"{y}-{m}-{d}T{hh}:{mm}:{ss}+00:00"


def open_csv_reader(source_path: Path):
    if source_path.suffix == ".zst":
        raw_stream = zstd.open(source_path, mode="rb")
        return io.TextIOWrapper(raw_stream, encoding="utf-8", newline="")
    return open(source_path, mode="r", encoding="utf-8", newline="")


def build_dates_cache_from_bars(bars_path: Path, is_tbbo: bool) -> list[int]:
    dates: set[int] = set()
    with open(bars_path, "r", encoding="utf-8") as f:
        for line in f:
            if len(line) < 15:
                continue
            dt = datetime.strptime(line[:15], "%Y%m%d %H%M%S")
            if is_tbbo:
                dt -= timedelta(minutes=1)
            dates.add(dt.year * 10000 + dt.month * 100 + dt.day)
    return sorted(dates)


def inspect_tick_partition(path: Path, rel_path: str, month_key: str) -> dict[str, object]:
    h = hashlib.sha256()
    count = 0
    size_bytes = 0
    first_ts: bytes | None = None
    last_ts: bytes | None = None
    with open(path, "rb", buffering=4 * 1024 * 1024) as f:
        for line in f:
            if not line.strip():
                continue
            h.update(line)
            count += 1
            size_bytes += len(line)
            ts = line[:23]
            if first_ts is None:
                first_ts = ts
            last_ts = ts
    if count == 0 or first_ts is None or last_ts is None:
        raise ValueError(f"Partición vacía inesperada: {path}")
    return {
        "month": month_key,
        "file": rel_path,
        "tick_count": count,
        "size_bytes": size_bytes,
        "first_tick_utc": parse_tick_iso(first_ts),
        "last_tick_utc": parse_tick_iso(last_ts),
        "sha256": h.hexdigest(),
    }


def merge_sorted_line_files(existing_path: Path, incoming_path: Path, key_len: int, dedup_by_key: bool = False) -> None:
    """Fusiona dos archivos ordenados cronológicamente de forma atómica sin cargar todo en RAM."""
    merged_tmp = existing_path.with_suffix(existing_path.suffix + ".merging")
    with (
        open(existing_path, "rb", buffering=4 * 1024 * 1024) as fa,
        open(incoming_path, "rb", buffering=4 * 1024 * 1024) as fb,
        open(merged_tmp, "wb", buffering=4 * 1024 * 1024) as out,
    ):
        la = fa.readline()
        lb = fb.readline()
        last_written: bytes | None = None
        last_key: bytes | None = None

        def emit(line: bytes) -> None:
            nonlocal last_written, last_key
            key = line[:key_len]
            if dedup_by_key:
                if key == last_key:
                    return
                last_key = key
            elif line == last_written:
                return
            last_written = line
            out.write(line)

        while la and lb:
            ka = la[:key_len]
            kb = lb[:key_len]
            if ka < kb:
                emit(la)
                la = fa.readline()
            elif kb < ka:
                emit(lb)
                lb = fb.readline()
            else:
                if dedup_by_key:
                    # En barras de 1m con el mismo cierre, preferir la línea entrante más reciente
                    emit(lb)
                    la = fa.readline()
                    lb = fb.readline()
                elif la == lb:
                    emit(la)
                    la = fa.readline()
                    lb = fb.readline()
                else:
                    emit(la)
                    la = fa.readline()
        while la:
            emit(la)
            la = fa.readline()
        while lb:
            emit(lb)
            lb = fb.readline()

    os.replace(merged_tmp, existing_path)
    incoming_path.unlink(missing_ok=True)


def convert_ohlcv_1m(source_path: Path, out_dir: Path, instrument: str, tick_size: float) -> dict[str, object]:
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

    rollovers = []
    deltas = [0.0] * len(raw_rows)
    for i in range(1, len(raw_rows)):
        prev_inst = raw_rows[i - 1][1]
        curr_inst = raw_rows[i][1]
        if prev_inst != 0 and curr_inst != 0 and curr_inst != prev_inst:
            gap = round_tick(raw_rows[i][2] - raw_rows[i - 1][5], tick_size)
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

    cumulative_adj = [0.0] * len(raw_rows)
    running = 0.0
    for i in range(len(raw_rows) - 1, 0, -1):
        running = round_tick(running + deltas[i], tick_size)
        cumulative_adj[i - 1] = running

    unadjusted_path = out_dir / f"{instrument}_1min_UTC_cierre.txt"
    adjusted_path = out_dir / f"{instrument}_1min_UTC_cierre_ajustado.txt"
    rollovers_path = out_dir / "rollovers.json"
    dates_cache_path = out_dir / "dates_cache.json"

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
            ao = round_tick(o + adj, tick_size)
            ah = round_tick(h + adj, tick_size)
            al = round_tick(l + adj, tick_size)
            ac = round_tick(c + adj, tick_size)
            f_adj.write(
                f"{ts_str};{format_price(ao)};{format_price(ah)};{format_price(al)};{format_price(ac)};{vol}\n"
            )

    rollovers_path.write_text(json.dumps(rollovers, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    dates = build_dates_cache_from_bars(unadjusted_path, is_tbbo=False)
    dates_cache_path.write_text(json.dumps(dates), encoding="utf-8")

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


def convert_tbbo_partitioned(
    source_path: Path,
    out_dir: Path,
    instrument: str,
    symbol: str,
    start_iso: str,
    end_iso: str,
    cost_usd: float | None,
    billable_bytes: int | None,
) -> dict[str, object]:
    """Convierte TBBO incrementalmente en ticks/YYYY/YYYY-MM.txt y actualiza velas 1m + catalog.json."""
    ticks_root = out_dir / "ticks"
    ticks_root.mkdir(parents=True, exist_ok=True)
    bars_path = out_dir / f"{instrument}_1min_UTC_cierre.txt"
    bars_incoming_path = out_dir / f".{instrument}_1min_UTC_cierre.incoming.txt"
    dates_cache_path = out_dir / "dates_cache.json"
    catalog_path = out_dir / "catalog.json"

    tick_count = 0
    bar_count = 0
    skipped_crossed_or_empty_book = 0
    skipped_out_of_order = 0
    first_ns: int | None = None
    last_ns: int | None = None

    bucket_close_ns: int | None = None
    b_open = b_high = b_low = b_close = 0.0
    b_vol = 0

    current_ym: str | None = None
    current_tmp_path: Path | None = None
    out_f = None
    touched_months: dict[str, Path] = {}

    def switch_month(ym: str) -> None:
        nonlocal current_ym, current_tmp_path, out_f
        if out_f is not None:
            out_f.close()
        year_str, mon_str = ym[:4], ym[4:6]
        year_dir = ticks_root / year_str
        year_dir.mkdir(parents=True, exist_ok=True)
        month_key = f"{year_str}-{mon_str}"
        tmp_path = year_dir / f".{month_key}.incoming.txt"
        mode = "a" if month_key in touched_months else "w"
        touched_months[month_key] = tmp_path
        current_ym = ym
        current_tmp_path = tmp_path
        out_f = open(tmp_path, mode, encoding="utf-8", newline="\n", buffering=4 * 1024 * 1024)

    try:
        with (
            open_csv_reader(source_path) as text_stream,
            open(bars_incoming_path, "w", encoding="utf-8", newline="\n", buffering=1024 * 1024) as bars_f,
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
                ts_100ns = (ts_ns // 100) * 100
                if last_ns is not None and ts_100ns < last_ns:
                    skipped_out_of_order += 1
                    continue

                if first_ns is None:
                    first_ns = ts_100ns
                last_ns = ts_100ns
                tick_count += 1
                if tick_count % 5_000_000 == 0:
                    print(f"  Convertidos {tick_count:,} ticks...", flush=True)

                ts_formatted = format_tick_utc(ts_100ns)
                ym = ts_formatted[:6]
                if ym != current_ym:
                    switch_month(ym)
                assert out_f is not None
                out_f.write(
                    f"{ts_formatted};{format_price(price)};{format_price(bid)};{format_price(ask)};{vol}\n"
                )

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
    finally:
        if out_f is not None:
            out_f.close()

    if tick_count == 0 or first_ns is None or last_ns is None:
        bars_incoming_path.unlink(missing_ok=True)
        for tmp in touched_months.values():
            tmp.unlink(missing_ok=True)
        raise ValueError("No se encontraron ticks válidos en el archivo TBBO.")

    # Publicar o fusionar cada partición mensual afectada
    updated_month_meta: dict[str, dict[str, object]] = {}
    for month_key, tmp_path in sorted(touched_months.items()):
        year_str = month_key[:4]
        dest_path = ticks_root / year_str / f"{month_key}.txt"
        if dest_path.is_file():
            print(f"  Fusionando mes existente {month_key}...", flush=True)
            merge_sorted_line_files(dest_path, tmp_path, key_len=23, dedup_by_key=False)
        else:
            os.replace(tmp_path, dest_path)
        rel_path = f"ticks/{year_str}/{month_key}.txt"
        updated_month_meta[month_key] = inspect_tick_partition(dest_path, rel_path, month_key)

    # Publicar o fusionar las velas de 1m derivadas
    if bars_path.is_file():
        merge_sorted_line_files(bars_path, bars_incoming_path, key_len=15, dedup_by_key=True)
    else:
        os.replace(bars_incoming_path, bars_path)

    total_bars_1m = 0
    with open(bars_path, "rb") as bf:
        for _ in bf:
            total_bars_1m += 1

    dates = build_dates_cache_from_bars(bars_path, is_tbbo=True)
    dates_cache_path.write_text(json.dumps(dates), encoding="utf-8")

    # Cargar catálogo existente (si hay) y actualizarlo incrementalmente
    existing_catalog = json.loads(catalog_path.read_text(encoding="utf-8")) if catalog_path.is_file() else {}
    months_by_key: dict[str, dict[str, object]] = {
        m["month"]: m for m in existing_catalog.get("months", [])
    }
    months_by_key.update(updated_month_meta)
    sorted_months = [months_by_key[k] for k in sorted(months_by_key)]

    try:
        raw_rel = source_path.relative_to(out_dir).as_posix()
    except ValueError:
        raw_rel = str(source_path)

    raw_batches = [
        b for b in existing_catalog.get("raw_batches", []) if b.get("raw_file") != raw_rel
    ]
    raw_batches.append(
        {
            "start_inclusive_utc": start_iso,
            "end_exclusive_utc": end_iso,
            "raw_file": raw_rel,
            "raw_sha256": sha256_file(source_path),
            "raw_size_bytes": source_path.stat().st_size,
            "billable_size_bytes": billable_bytes,
            "quoted_cost_usd": cost_usd,
            "tick_count": tick_count,
            "derived_bar_count": bar_count,
            "skipped_crossed_or_empty_book": skipped_crossed_or_empty_book,
            "skipped_out_of_order": skipped_out_of_order,
            "ingested_utc": datetime.now(tz=timezone.utc).isoformat(),
        }
    )
    raw_batches.sort(key=lambda b: (b.get("start_inclusive_utc", ""), b.get("end_exclusive_utc", "")))

    prev_start = existing_catalog.get("start_inclusive_utc")
    prev_end = existing_catalog.get("end_exclusive_utc")
    overall_start = min(prev_start, start_iso) if prev_start else start_iso
    overall_end = max(prev_end, end_iso) if prev_end else end_iso

    catalog = {
        "format_version": 2,
        "provider": "Databento",
        "dataset": DATASET,
        "instrument": instrument,
        "symbol": symbol,
        "stype_in": "continuous",
        "schema": "tbbo",
        "timezone": "UTC",
        "timestamp_basis": "ts_event",
        "timestamp_precision": "100ns",
        "layout": "monthly_partitions",
        "ticks_dir": "ticks",
        "bars_1m_file": bars_path.name,
        "bars_1m_sha256": sha256_file(bars_path),
        "bars_1m_size_bytes": bars_path.stat().st_size,
        "dates_cache_file": dates_cache_path.name,
        "start_inclusive_utc": overall_start,
        "end_exclusive_utc": overall_end,
        "first_tick_utc": sorted_months[0]["first_tick_utc"],
        "last_tick_utc": sorted_months[-1]["last_tick_utc"],
        "tick_count": sum(int(m["tick_count"]) for m in sorted_months),
        "bar_count_1m": total_bars_1m,
        "month_count": len(sorted_months),
        "total_ticks_size_bytes": sum(int(m["size_bytes"]) for m in sorted_months),
        "degraded_dates_provider": existing_catalog.get("degraded_dates_provider", []),
        "raw_batches": raw_batches,
        "months": sorted_months,
    }
    catalog_path.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    return {
        "batch_ticks": tick_count,
        "batch_derived_bars": bar_count,
        "touched_months": sorted(touched_months),
        "catalog_file": str(catalog_path),
        "total_ticks": catalog["tick_count"],
        "total_months": catalog["month_count"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--esquema",
        choices=("ohlcv-1m", "tbbo"),
        default="ohlcv-1m",
        help="Esquema a descargar o convertir: 'ohlcv-1m' (1 minuto) o 'tbbo' (ticks con Bid/Ask).",
    )
    parser.add_argument("--simbolo", default=DEFAULT_SYMBOL, help="Símbolo continuo en Databento (por defecto NQ.v.0; ej. ES.v.0).")
    parser.add_argument("--instrumento", help="Nombre de carpeta de instrumento (por defecto se deduce de --simbolo, ej. NQ o ES).")
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
        help="Carpeta de salida (por defecto: data/databento/<INSTRUMENTO>/<esquema>).",
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

    instrument = infer_instrument(args.simbolo, args.instrumento)
    tick_size = TICK_SIZE_BY_INSTRUMENT.get(instrument, 0.25)

    script_dir = Path(__file__).resolve().parent
    data_root = script_dir.parent if script_dir.name == "scripts" else script_dir
    out_dir = (
        Path(args.destino).resolve()
        if args.destino
        else data_root / instrument / args.esquema
    )
    out_dir.mkdir(parents=True, exist_ok=True)
    raw_dir = out_dir / "raw"

    tag = f"{start_d.strftime('%Y%m%d')}-{end_d.strftime('%Y%m%d')}"
    default_raw_path = raw_dir / f"{tag}.{args.esquema}.csv.zst"

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

        raw_path = default_raw_path
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

    print(f"Convirtiendo {raw_path.name} para {instrument} ({args.esquema}) ...")
    if args.esquema == "tbbo":
        summary = convert_tbbo_partitioned(
            source_path=raw_path,
            out_dir=out_dir,
            instrument=instrument,
            symbol=args.simbolo,
            start_iso=start_d.isoformat(),
            end_iso=end_d.isoformat(),
            cost_usd=cost_usd,
            billable_bytes=billable_bytes,
        )
        print(f"\nProceso TBBO completado con éxito en: {out_dir}")
        print(json.dumps(summary, indent=2, ensure_ascii=False))
        return 0

    conversion_summary = convert_ohlcv_1m(raw_path, out_dir, instrument=instrument, tick_size=tick_size)
    try:
        source_rel = raw_path.relative_to(out_dir).as_posix()
    except ValueError:
        source_rel = raw_path.name

    manifest = {
        "provider": "Databento",
        "dataset": DATASET,
        "instrument": instrument,
        "symbol": args.simbolo,
        "stype_in": "continuous",
        "schema": args.esquema,
        "start_inclusive_utc": start_d.isoformat(),
        "end_exclusive_utc": end_d.isoformat(),
        "quoted_cost_usd": cost_usd,
        "billable_size_bytes": billable_bytes,
        "created_utc": datetime.now(tz=timezone.utc).isoformat(),
        "source_file": source_rel,
        "source_sha256": sha256_file(raw_path),
        **conversion_summary,
    }
    manifest_path = out_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"\nProceso completado con éxito en: {out_dir}")
    print(json.dumps(manifest, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
