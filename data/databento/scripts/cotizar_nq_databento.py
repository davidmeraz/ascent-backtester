#!/usr/bin/env python3
"""Cotiza datos NQ sin descargarlos ni realizar compras.

Por defecto usa la calculadora pública del proveedor. Con --cuenta consulta
la API autenticada: la clave se lee de DATABENTO_API_KEY o desde el teclado,
sin mostrarla, guardarla ni imprimirla.
"""

import argparse
import base64
import getpass
import json
import math
import os
import sys
from datetime import date
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


DATASET = "GLBX.MDP3"
SYMBOL = "NQ.v.0"
API_ROOT = "https://hist.databento.com/v0/"
PUBLIC_QUOTE_URL = "https://api.databento.com/v0/batch/quote_job"


def request_metadata(endpoint, api_key, parameters):
    credentials = base64.b64encode((api_key + ":").encode("utf-8")).decode("ascii")
    url = API_ROOT + endpoint + "?" + urlencode(parameters)
    request = Request(url, headers={"Authorization": "Basic " + credentials, "Accept": "application/json"})
    with urlopen(request, timeout=90) as response:
        return json.load(response)


def request_public_quote(parameters):
    url = PUBLIC_QUOTE_URL + "?" + urlencode({**parameters, "encoding": "dbn"})
    request = Request(
        url,
        data=b"{}",
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json",
            "Origin": "https://databento.com",
            "Referer": "https://databento.com/pricing/",
        },
        method="POST",
    )
    with urlopen(request, timeout=90) as response:
        result = json.load(response)
    if result.get("errors"):
        raise ValueError("La calculadora pública no pudo cotizar el intervalo")
    return float(result["estimated_cost_usd"]), int(result["estimated_size"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inicio", default="2016-01-01", help="Primer día UTC, incluido (AAAA-MM-DD).")
    parser.add_argument("--fin", default="2026-01-01", help="Último día UTC, excluido (AAAA-MM-DD).")
    parser.add_argument("--cuenta", action="store_true", help="Usar la API autenticada para obtener una cotización de la cuenta.")
    args = parser.parse_args()
    try:
        start, end = date.fromisoformat(args.inicio), date.fromisoformat(args.fin)
        if start >= end:
            parser.error("--fin debe ser posterior a --inicio")
    except ValueError:
        parser.error("Las fechas deben tener el formato AAAA-MM-DD")

    api_key = None
    if args.cuenta:
        api_key = os.environ.get("DATABENTO_API_KEY") or getpass.getpass("Clave de Databento (no se mostrará): ")
        if not api_key.strip():
            print("No se proporcionó una clave.", file=sys.stderr)
            return 2

    common = {
        "dataset": DATASET,
        "symbols": SYMBOL,
        "stype_in": "continuous",
        "start": start.isoformat(),
        "end": end.isoformat(),
    }
    print(f"NQ continuo por volumen | UTC [{start}, {end}) | {DATASET}")
    print("Solo consulta cotizaciones: no descarga datos ni compra nada.")
    print("Fuente:", "API autenticada" if args.cuenta else "calculadora pública", "\n")
    try:
        for schema, label in (
            ("ohlcv-1m", "Velas de un minuto"),
            ("tbbo", "Ticks con mejor bid/ask en cada operación"),
            ("mbp-1", "Ticks y todas las actualizaciones del mejor bid/ask"),
        ):
            params = {**common, "schema": schema}
            if api_key:
                cost = float(request_metadata("metadata.get_cost", api_key, params))
                size = int(request_metadata("metadata.get_billable_size", api_key, params))
            else:
                cost, size = request_public_quote(params)
            if not math.isfinite(cost) or cost < 0 or size < 0:
                raise ValueError("La API devolvió una cifra no válida")
            print(f"{schema:10s} | {label}: USD {cost:.2f}; {size / (1024 ** 3):.2f} GiB facturables sin comprimir")
    except HTTPError as error:
        print(f"La API rechazó la consulta (HTTP {error.code}). Revisa acceso, fechas y disponibilidad.", file=sys.stderr)
        return 1
    except (URLError, TimeoutError, ValueError, KeyError, OSError) as error:
        print(f"No se pudo obtener la cotización: {type(error).__name__}.", file=sys.stderr)
        return 1

    print("\nLos importes son cotizaciones brutas; consulta tu crédito y posibles impuestos en la cuenta.")
    print("TBBO permite reconstruir velas desde operaciones, sujeto a convenciones y validación.")
    print("El tamaño facturable no necesariamente coincide con el tamaño descargado.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
