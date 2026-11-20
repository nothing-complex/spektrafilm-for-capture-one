"""Make a closed catalog and private RAW copies for licensed metadata tests."""
from argparse import ArgumentParser
from hashlib import file_digest
import json
from pathlib import Path
import shutil
import sqlite3


def sha(path):
    with path.open("rb") as stream:
        return file_digest(stream, "sha256").hexdigest()


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("raw_samples", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    catalog = args.catalog.resolve(strict=True)
    raw_samples = args.raw_samples.resolve(strict=True)
    destination = args.destination.resolve()
    if catalog.suffix.lower() != ".cocatalogdb" or not raw_samples.is_dir():
        parser.error("Supply a closed Capture One catalog and a directory of test RAW samples.")
    if any(catalog.with_name(catalog.name + suffix).exists() for suffix in ("-wal", "-shm", "-journal")):
        parser.error("The source catalog has active transaction files.")
    source_hash = sha(catalog)
    destination.mkdir()
    raw = destination / "Raw"
    raw.mkdir()
    copy = destination / "Metadata Fixture.cocatalogdb"
    shutil.copyfile(catalog, copy)
    if sha(copy) != source_hash:
        raise ValueError("Catalog fixture copy did not match the source.")
    copied = {}
    raw_formats = {".arw", ".cr2", ".cr3", ".dng", ".iiq", ".nef", ".orf", ".raf", ".rw2"}
    with sqlite3.connect(copy) as db:
        locations = db.execute("SELECT DISTINCT ZIMAGELOCATION, ZIMAGEFILENAME FROM ZIMAGE").fetchall()
        for location, name in locations:
            if not isinstance(location, int) or not name or Path(name).name != name:
                raise ValueError("Unexpected image path in the closed fixture catalog.")
            if Path(name).suffix.lower() in raw_formats:
                source = (raw_samples / name).resolve(strict=True)
                if source.parent != raw_samples:
                    raise ValueError("RAW sample resolved outside its designated source directory.")
                target = raw / name
                if name not in copied:
                    shutil.copyfile(source, target)
                    copied[name] = sha(source)
                    if sha(target) != copied[name]:
                        raise ValueError("RAW fixture copy did not match the source.")
            db.execute("UPDATE ZPATHLOCATION SET ZWINROOT=?,ZRELATIVEPATH=?,ZISRELATIVE=0 WHERE Z_PK=?",
                       (raw.anchor, str(raw.relative_to(raw.anchor)), location))
        if not copied or db.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError("Private metadata test catalog failed integrity or had no RAW samples.")
    if sha(catalog) != source_hash:
        raise ValueError("The original catalog changed during fixture preparation.")
    (destination / "metadata-fixture.marker").write_text(json.dumps({"catalog_sha256": sha(copy), "raw_sha256": copied}), encoding="utf-8")
    print(copy)


if __name__ == "__main__":
    main()
