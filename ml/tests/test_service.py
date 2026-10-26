import os, pathlib, sys
import pytest
ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
os.environ["AEROBAG_MODEL_DIR"] = str(ROOT / "models")
from fastapi.testclient import TestClient
from service import app as appmod

BODY = dict(flight_id="ZZ0100-20261026", wide=0, rain=0, bags=120, stand_m=1000, chute_m=50,
            speed=0.55, arrivals_30=2, handler="HANDLER_B")


@pytest.fixture()
def client():
    appmod._pred = None   # her testte temiz durum
    return TestClient(appmod.app)


def test_health(client):
    assert client.get("/health").json()["status"] == "ok"


def test_predict_shape_and_order(client):
    r = client.post("/v1/predict", json=BODY).json()
    assert r["basis"] == "ML" and r["adapted"] is False
    for t in ("first_min", "span_min", "last_min"):
        v = r["minutes_from_on_block"][t]
        assert 0 <= v["p10"] <= v["p50"] <= v["p90"]
    f, l = r["minutes_from_on_block"]["first_min"], r["minutes_from_on_block"]["last_min"]
    assert f["p50"] < l["p50"]


def test_wide_body_arrives_later(client):
    a = client.post("/v1/predict", json=BODY).json()["minutes_from_on_block"]["last_min"]["p50"]
    b = client.post("/v1/predict", json={**BODY, "wide": 1, "bags": 216}).json()["minutes_from_on_block"]["last_min"]["p50"]
    assert b > a


@pytest.mark.parametrize("patch", [{"wide": 2}, {"bags": 0}, {"handler": "X"}, {"speed": 0},
                                   {"flight_id": "zz; drop"}, {"stand_m": -5}])
def test_invalid_input_rejected(client, patch):
    assert client.post("/v1/predict", json={**BODY, **patch}).status_code == 422


def test_outcomes_shift_predictions_after_enough_samples(client):
    base = client.post("/v1/predict", json=BODY).json()["minutes_from_on_block"]["first_min"]["p50"]
    for i in range(12):
        fid = f"ZZ{i:04d}-20261026"
        client.post("/v1/predict", json={**BODY, "flight_id": fid})
        # gercek her seferinde tahminden ~6 dk gec
        assert client.post("/v1/outcome", json={"flight_id": fid, "first_min": base + 6, "last_min": base + 20}).json()["recorded"]
    r = client.post("/v1/predict", json={**BODY, "flight_id": "ZZ9999-20261026"}).json()
    assert r["adapted"] is True and r["adaptation_samples"] == 12
    assert r["minutes_from_on_block"]["first_min"]["p50"] > base + 4


def test_unknown_outcome_is_not_recorded_and_bad_order_rejected(client):
    assert client.post("/v1/outcome", json={"flight_id": "NOPE-1", "first_min": 5, "last_min": 9}).json()["recorded"] is False
    assert client.post("/v1/outcome", json={"flight_id": "NOPE-1", "first_min": 9, "last_min": 5}).status_code == 422


FEAT = dict(wide=0, rain=0, bags=120, stand_m=1000, chute_m=50, speed=0.55, arrivals_30=2, handler="HANDLER_B")


def test_predict_bag_shape_and_order(client):
    r = client.post("/v1/predict-bag", json=FEAT).json()
    v = r["seconds_after_unload"]
    assert r["basis"] == "ML" and r["adapted"] is False
    assert 60 < v["p10"] <= v["p50"] <= v["p90"] < 1800


def test_predict_bag_rejects_invalid_input(client):
    assert client.post("/v1/predict-bag", json={**FEAT, "handler": "X"}).status_code == 422
    assert client.post("/v1/predict-bag", json={**FEAT, "bags": 0}).status_code == 422


def test_bag_outcomes_shift_predictions_after_enough_samples(client):
    base = client.post("/v1/predict-bag", json=FEAT).json()["seconds_after_unload"]["p50"]
    items = [{"features": FEAT, "duration_s": base + 120} for _ in range(60)]
    assert client.post("/v1/outcome-bags", json={"items": items}).json()["recorded"] == 60
    r = client.post("/v1/predict-bag", json=FEAT).json()
    assert r["adapted"] is True and r["adaptation_samples"] == 60
    assert r["seconds_after_unload"]["p50"] > base + 100


def test_bag_outcomes_batch_is_bounded(client):
    items = [{"features": FEAT, "duration_s": 300}] * 501
    assert client.post("/v1/outcome-bags", json={"items": items}).status_code == 422
