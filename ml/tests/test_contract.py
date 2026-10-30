"""Sozlesme testi (W6): servis, contracts/ml altindaki ornek istekleri kabul etmeli ve yanitlari ornek yanitlarla
ayni alanlari ve JSON tiplerini tasimali. Degerler karsilastirilmaz. .NET tarafi ayni dosyalara karsi test edilir
(tests/AeroBag.UnitTests/MlContractTests.cs)."""
import json, os, pathlib, sys
import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
os.environ["AEROBAG_MODEL_DIR"] = str(ROOT / "models")
from fastapi.testclient import TestClient
from service import app as appmod

CONTRACTS = ROOT.parent / "contracts" / "ml"


def load(name):
    return json.loads((CONTRACTS / name).read_text(encoding="utf-8"))


def shape(v):
    """JSON degerinin tipi: alan adlari ve tipler; tam sayi ile ondalik ayni ('number')."""
    if isinstance(v, bool):
        return "bool"
    if isinstance(v, (int, float)):
        return "number"
    if isinstance(v, str):
        return "string"
    if v is None:
        return "null"
    if isinstance(v, list):
        return ["list", shape(v[0]) if v else None]
    if isinstance(v, dict):
        return {k: shape(x) for k, x in sorted(v.items())}
    raise TypeError(type(v))


@pytest.fixture()
def client():
    appmod._pred = None
    return TestClient(appmod.app)


@pytest.mark.parametrize("path,name", [
    ("/v1/predict", "predict"),
    ("/v1/predict-bag", "predict-bag"),
    ("/v1/outcome", "outcome"),
    ("/v1/outcome-bags", "outcome-bags"),
])
def test_service_accepts_the_example_request_and_answers_in_the_example_shape(client, path, name):
    r = client.post(path, json=load(f"{name}.request.json"))
    assert r.status_code == 200, r.text
    assert shape(r.json()) == shape(load(f"{name}.response.json"))


def test_health_answers_in_the_example_shape(client):
    assert shape(client.get("/health").json()) == shape(load("health.response.json"))


def test_every_contract_file_is_used():
    used = {f"{n}.{k}.json" for n in ("predict", "predict-bag", "outcome", "outcome-bags") for k in ("request", "response")}
    used.add("health.response.json")
    assert {p.name for p in CONTRACTS.glob("*.json")} == used


def test_shape_treats_int_and_float_alike_but_not_bool():
    assert shape({"a": 1}) == shape({"a": 1.5})
    assert shape({"a": True}) != shape({"a": 1})
    assert shape({"a": {"b": 1}}) != shape({"a": {"c": 1}})
