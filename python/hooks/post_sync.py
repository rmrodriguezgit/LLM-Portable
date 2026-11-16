"""Hook post-sincronización Git (Nivel 1, hot-reload).

El Gateway lo ejecuta con:  bin\\uv\\uv.exe run --project repo\\python python hooks\\post_sync.py <from> <to>
dentro del entorno aislado env\\.venv. Úsalo para validaciones o migraciones ligeras de datos.
Un código de salida distinto de 0 se registra en data\\logs pero no revierte la sincronización.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def main() -> int:
    from_commit, to_commit = (sys.argv[1:3] + ["?", "?"])[:2]
    repo = Path(__file__).resolve().parents[2]
    profiles = repo / "profiles.json"

    # Validación mínima de que profiles.json sigue siendo legible (admite comentarios // de línea completa).
    text = "\n".join(
        line for line in profiles.read_text(encoding="utf-8").splitlines()
        if not line.lstrip().startswith("//")
    )
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:  # comentarios al final de línea no se filtran aquí
        print(f"[post_sync] aviso: no se pudo validar profiles.json estrictamente: {exc}")
        return 0

    ids = [p.get("id") for p in data.get("profiles", [])]
    print(f"[post_sync] {from_commit[:7]} -> {to_commit[:7]}: {len(ids)} perfiles: {', '.join(map(str, ids))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
