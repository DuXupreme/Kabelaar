from __future__ import annotations

import shutil
import tempfile
from pathlib import Path


def write_text_atomic(path: Path, text: str, *, encoding: str = "utf-8", backup: bool = True) -> Path | None:
    """Write text through a temp file, optionally keeping the previous file as .bak."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    backup_path = path.with_name(f"{path.name}.bak") if backup and path.exists() else None
    temp_path: Path | None = None

    try:
        with tempfile.NamedTemporaryFile(
            "w",
            encoding=encoding,
            dir=path.parent,
            prefix=f".{path.name}.",
            suffix=".tmp",
            delete=False,
        ) as tmp:
            tmp.write(text)
            temp_path = Path(tmp.name)

        if backup_path is not None:
            shutil.copy2(path, backup_path)
        temp_path.replace(path)
        return backup_path
    except Exception:
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)
        raise
