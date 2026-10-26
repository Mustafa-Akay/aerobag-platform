import os
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
from .predictor import Predictor

app = FastAPI(title="AeroBag ML", version="0.1.0")
_pred = None


def predictor() -> Predictor:
    global _pred
    if _pred is None:
        _pred = Predictor(os.environ.get("AEROBAG_MODEL_DIR", "models"))
    return _pred


class PredictIn(BaseModel):
    flight_id: str | None = Field(default=None, max_length=40, pattern=r"^[A-Z0-9\-]+$")
    wide: int = Field(ge=0, le=1)
    rain: int = Field(ge=0, le=1)
    bags: int = Field(ge=1, le=800)
    stand_m: float = Field(ge=0, le=10000)
    chute_m: float = Field(ge=0, le=500)
    speed: float = Field(gt=0, le=3)
    arrivals_30: int = Field(ge=0, le=30)
    handler: str = Field(pattern=r"^HANDLER_[ABC]$")


class FlightFeatures(BaseModel):
    wide: int = Field(ge=0, le=1)
    rain: int = Field(ge=0, le=1)
    bags: int = Field(ge=1, le=800)
    stand_m: float = Field(ge=0, le=10000)
    chute_m: float = Field(ge=0, le=500)
    speed: float = Field(gt=0, le=3)
    arrivals_30: int = Field(ge=0, le=30)
    handler: str = Field(pattern=r"^HANDLER_[ABC]$")


class BagOutcomeIn(BaseModel):
    features: FlightFeatures
    duration_s: float = Field(gt=0, le=7200)


class BagOutcomesIn(BaseModel):
    items: list[BagOutcomeIn] = Field(max_length=500)


def _x(f):
    x = f.model_dump()
    for h in ("A", "B", "C"):
        x[f"h_{h}"] = int(x["handler"] == f"HANDLER_{h}")
    return x


class OutcomeIn(BaseModel):
    flight_id: str = Field(max_length=40, pattern=r"^[A-Z0-9\-]+$")
    first_min: float = Field(ge=0, le=600)
    last_min: float = Field(ge=0, le=600)


@app.get("/health")
def health():
    p = predictor()
    return {"status": "ok", "n_train_flights": p.meta["n_flights"]}


@app.post("/v1/predict")
def predict(body: PredictIn):
    p = predictor()
    x = body.model_dump()
    for h in ("A", "B", "C"):
        x[f"h_{h}"] = int(x["handler"] == f"HANDLER_{h}")
    out, adapted, n = p.predict(body.flight_id, x)
    return {"basis": "ML", "adapted": adapted, "adaptation_samples": n, "minutes_from_on_block": out}


@app.post("/v1/outcome")
def outcome(body: OutcomeIn):
    if body.last_min < body.first_min:
        raise HTTPException(422, "last_min must be >= first_min")
    return {"recorded": predictor().outcome(body.flight_id, body.first_min, body.last_min)}


@app.post("/v1/predict-bag")
def predict_bag(body: FlightFeatures):
    """Indirilmis bir bavulun banta gelmesine kalan sure (saniye, indirme anindan itibaren)."""
    out, adapted, n = predictor().predict_bag(_x(body))
    return {"basis": "ML", "adapted": adapted, "adaptation_samples": n, "seconds_after_unload": out}


@app.post("/v1/outcome-bags")
def outcome_bags(body: BagOutcomesIn):
    return {"recorded": predictor().outcome_bags([(_x(i.features), i.duration_s) for i in body.items])}
