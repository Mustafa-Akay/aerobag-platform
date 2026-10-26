"""Kullanim: python run_eval.py data/synthetic/train_A.csv data/synthetic/test_B.csv [cikti.json]
Egitim: rejim A. Test: rejim B (dagilim kaymasi). Bolme uçuş duzeyindedir (ayni ucus iki tarafta olamaz)."""
import json, sys
import numpy as np
from sklearn.model_selection import KFold
from aerobag_ml.data import flights_from_csv, FEATURES
from aerobag_ml.models import MedianBaseline, QuantileGBM
from aerobag_ml.metrics import report
from aerobag_ml.adapt import rolling_recalibrate

TARGETS = ["first_min", "span_min", "last_min"]
ABL = {
    "tum_ozellikler": FEATURES,
    "handler_yok": [f for f in FEATURES if not f.startswith("h_")],
    "yogunluk_yok": [f for f in FEATURES if f != "arrivals_30"],
    "fizik_yok": [f for f in FEATURES if f not in ("stand_m", "chute_m", "speed")],
    "yalniz_govde_yagmur": ["wide", "rain"],
}


def evaluate(train, test, features, target, seed=0):
    base = report(test[target], MedianBaseline().fit(train, target).predict(test))
    ml = report(test[target], QuantileGBM(features, seed).fit(train, target).predict(test))
    return base, ml


def main(train_p, test_p, out=None):
    A, B = flights_from_csv(train_p), flights_from_csv(test_p)
    res = {"n_train": len(A), "n_test": len(B), "in_regime_cv": {}, "shift_A_to_B": {}, "shift_A_to_B_adapted": {}, "ablation_shift": {}, "seeds": {}}
    for t in TARGETS:
        # A icinde 5 katli uçuş bazli CV
        acc_b, acc_m = [], []
        for tr, te in KFold(5, shuffle=True, random_state=0).split(A):
            b, m = evaluate(A.iloc[tr], A.iloc[te], FEATURES, t)
            acc_b.append(b); acc_m.append(m)
        avg = lambda L: {k: float(np.mean([x[k] for x in L])) for k in L[0]}
        res["in_regime_cv"][t] = {"baseline": avg(acc_b), "lgbm": avg(acc_m)}
        b, m = evaluate(A, B, FEATURES, t)
        res["shift_A_to_B"][t] = {"baseline": b, "lgbm": m}
        pb = MedianBaseline().fit(A, t).predict(B)
        pm = QuantileGBM(FEATURES).fit(A, t).predict(B)
        res["shift_A_to_B_adapted"][t] = {"baseline": report(B[t], rolling_recalibrate(B[t], pb)),
                                          "lgbm": report(B[t], rolling_recalibrate(B[t], pm))}
        res["ablation_shift"][t] = {n: evaluate(A, B, f, t)[1]["p50_mae"] for n, f in ABL.items()}
        res["seeds"][t] = [evaluate(A, B, FEATURES, t, s)[1]["p50_mae"] for s in range(5)]
    txt = json.dumps(res, indent=2, ensure_ascii=False)
    if out:
        open(out, "w", encoding="utf-8").write(txt)
    print(txt)


if __name__ == "__main__":
    main(*sys.argv[1:4])
