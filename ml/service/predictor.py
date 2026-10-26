import json, pathlib, threading
from collections import deque
import numpy as np
import lightgbm as lgb

QS = (0.1, 0.5, 0.9)
TARGETS = ("first_min", "span_min", "last_min")


class Predictor:
    """Model + canli artik duzeltmesi. Duzeltme yalnizca /outcome ile bildirilmis (yani gercekten bitmis)
    ucuslarin artiklarindan hesaplanir; bu yuzden gecikme dogal olarak uygulanir. Durum bellekte tutulur (tek ornek)."""

    def __init__(self, model_dir, window=40, min_n=10, max_pending=5000):
        d = pathlib.Path(model_dir)
        self.meta = json.loads((d / "meta.json").read_text(encoding="utf-8"))
        self.features = self.meta["features"]
        self.models = {t: {q: lgb.Booster(model_file=str(d / f"{t}_q{int(q*100)}.txt")) for q in QS} for t in TARGETS}
        self.min_n = min_n
        self.res = {t: {q: deque(maxlen=window) for q in QS} for t in TARGETS}
        self.pending = {}          # flight_id -> ham tahminler
        self.max_pending = max_pending
        self.lock = threading.Lock()

    def _raw(self, x):
        row = np.array([[x[f] for f in self.features]], dtype=float)
        return {t: np.sort([self.models[t][q].predict(row)[0] for q in QS]) for t in TARGETS}

    def predict(self, flight_id, x):
        raw = self._raw(x)
        with self.lock:
            out, n = {}, min(len(self.res[t][0.5]) for t in TARGETS)
            adapted = n >= self.min_n
            for t in TARGETS:
                v = list(raw[t])
                if adapted:
                    v = [v[i] + float(np.quantile(self.res[t][q], q)) for i, q in enumerate(QS)]
                    v[0], v[2] = min(v[0], v[1]), max(v[2], v[1])
                out[t] = {"p10": max(v[0], 0.0), "p50": max(v[1], 0.0), "p90": max(v[2], 0.0)}
            if flight_id:
                if len(self.pending) >= self.max_pending:
                    self.pending.pop(next(iter(self.pending)))
                self.pending[flight_id] = raw
        return out, adapted, n

    def outcome(self, flight_id, first_min, last_min):
        actual = {"first_min": first_min, "last_min": last_min, "span_min": last_min - first_min}
        with self.lock:
            raw = self.pending.pop(flight_id, None)
            if raw is None:
                return False
            for t in TARGETS:
                for i, q in enumerate(QS):
                    self.res[t][q].append(actual[t] - raw[t][i])
        return True
