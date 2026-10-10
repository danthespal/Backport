"""Local, read-only Phase 8 prototypes. SQLite is in memory; no production importer is changed."""
import hashlib
import importlib.metadata
import json
import pathlib
import sqlite3
import sys
import time
from jsonschema import Draft202012Validator

root = pathlib.Path.cwd()
report = pathlib.Path(sys.argv[1])
files = sorted((root / "firmware_db").glob("*.json"))
hashes = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
samples = []
for iteration in range(4):
    start = time.perf_counter()
    documents = [(p.name, json.loads(p.read_bytes())) for p in files]
    # Same query semantics in both prototypes: NID membership per firmware, ignoring module duplication.
    sets = {name: {nid for nids in d["modules"].values() for nid in nids} for name, d in documents}
    samples.append({"iteration": iteration, "json_load_ms": (time.perf_counter() - start) * 1000})
connection = sqlite3.connect(":memory:")
connection.executescript("""
CREATE TABLE metadata(name TEXT PRIMARY KEY, document TEXT NOT NULL);
CREATE TABLE modules(name TEXT, module TEXT, PRIMARY KEY(name,module));
CREATE TABLE exports(name TEXT, module TEXT, nid TEXT, PRIMARY KEY(name,module,nid));
CREATE INDEX membership ON exports(name,nid);
""")
start = time.perf_counter()
with connection:
    for name, document in documents:
        connection.execute("INSERT INTO metadata VALUES (?,?)", (name, json.dumps({k: v for k, v in document.items() if k != "modules"})))
        connection.executemany("INSERT INTO modules VALUES (?,?)", ((name, module) for module in document["modules"]))
        connection.executemany("INSERT INTO exports VALUES (?,?,?)", ((name, module, nid) for module, nids in document["modules"].items() for nid in sorted(set(nids))))
import_ms = (time.perf_counter() - start) * 1000
connection.execute("INSERT INTO metadata VALUES (?,?)", ("__synthetic_empty__", json.dumps({"firmware": "0", "source": "synthetic", "data_symbols": False})))
connection.execute("INSERT INTO modules VALUES (?,?)", ("__synthetic_empty__", "empty-module"))
restored = {}
for name, metadata in connection.execute("SELECT name,document FROM metadata ORDER BY name"):
    restored[name] = json.loads(metadata)
    restored[name]["modules"] = {}
for name, module in connection.execute("SELECT name,module FROM modules ORDER BY name,module"):
    restored[name]["modules"][module] = []
for name, module, nid in connection.execute("SELECT name,module,nid FROM exports ORDER BY name,module,nid"):
    restored[name]["modules"][module].append(nid)
normalized = {name: {**d, "modules": {m: sorted(set(nids)) for m, nids in d["modules"].items()}} for name, d in documents}
assert restored.pop("__synthetic_empty__") == {"firmware": "0", "source": "synthetic", "data_symbols": False, "modules": {"empty-module": []}}
assert restored == normalized, "SQLite semantic round-trip changed catalog data"
needles = sorted({nid for nids in sets.values() for nid in nids})[:1000]
query_samples = []
for iteration in range(3):
    start = time.perf_counter()
    json_hits = sum(nid in nids for nid in needles for nids in sets.values())
    json_ms = (time.perf_counter() - start) * 1000
    start = time.perf_counter()
    sqlite_hits = sum(connection.execute("SELECT EXISTS(SELECT 1 FROM exports WHERE name=? AND nid=?)", (name, nid)).fetchone()[0] for nid in needles for name in sets)
    sqlite_ms = (time.perf_counter() - start) * 1000
    assert sqlite_hits == json_hits, "SQLite membership disagreement"
    query_samples.append({"iteration": iteration, "queries": len(needles) * len(sets), "hits": json_hits, "json_set_ms": json_ms, "sqlite_ms": sqlite_ms})
schema = json.loads((root / "verification/Phase8Evaluation/fix-record.prototype.schema.json").read_text())
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema)
rules = json.loads((root / "data/fixes.json").read_bytes())
records = list(rules["fixes"].values()) + [f for option in rules["options"].values() for f in option["fixes"].values()]
for record in records:
    validator.validate(record)
examples = [
    ("typed-record", {"kind": "stub", "stub": "ret0", "sources": [], "proven": False}, True),
    ("misspelled-proven", {"kind": "stub", "stub": "ret0", "sources": [], "proovn": False}, False),
    ("wrong-boolean", {"kind": "stub", "sources": [], "proven": "false"}, False),
    ("invalid-condition", {"kind": "stub", "sources": [], "when": "sometimes"}, False),
    ("unresolved-stub-needs-core", {"kind": "stub", "sources": [], "stub": "does-not-exist"}, True),
    ("empty-adapter-needs-core", {"kind": "adapter", "sources": [], "insns": []}, True),
]
checks = []
for name, record, expected in examples:
    errors = [e.message for e in validator.iter_errors(record)]
    assert (not errors) == expected, name
    checks.append({"name": name, "schema_accepts": not errors, "errors": errors})
assert all(hashlib.sha256((root / p).read_bytes()).hexdigest() == value for p, value in hashes.items()), "Source catalog changed"
connection.close()
report.parent.mkdir(parents=True, exist_ok=True)
report.write_text(json.dumps({"python": sys.version, "sqlite": sqlite3.sqlite_version, "jsonschema": importlib.metadata.version("jsonschema"),
    "catalog_files": len(files), "json_samples": samples, "sqlite_import_ms": import_ms, "queries": query_samples,
    "python_dependencies": {name: importlib.metadata.version(name) for name in ("attrs", "jsonschema-specifications", "referencing", "rpds-py")},
    "semantic_roundtrip": True, "schema_records": len(records), "schema_checks": checks,
    "limits": "In-memory SQLite/Python prototype, not a .NET performance or on-disk durability comparison. Schema covers individual records only, not full documents, cross-record semantics or duplicate JSON keys."}, indent=2), encoding="utf-8")
print(f"Passed SQLite semantic round-trip/query equivalence and {len(records)} schema records + {len(checks)} specified cases: {report}")
