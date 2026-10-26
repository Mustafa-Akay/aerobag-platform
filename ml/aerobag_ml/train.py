"""Egitilmis modelleri duz metin olarak kaydeder (pickle YOK: yuklerken kod calistirmaz).
Kullanim: python -m aerobag_ml.train ../data/synthetic/train_A.csv models/"""
import hashlib, json, sys, pathlib
from .data import flights_from_csv, FEATURES
from .models import QuantileGBM, QUANTILES

TARGETS = ["first_min", "span_min", "last_min"]


def main(csv_path, out_dir):
    out = pathlib.Path(out_dir); out.mkdir(parents=True, exist_ok=True)
    df = flights_from_csv(csv_path)
    for t in TARGETS:
        m = QuantileGBM(FEATURES).fit(df, t)
        for q in QUANTILES:
            m.m[q].booster_.save_model(str(out / f"{t}_q{int(q*100)}.txt"))
    meta = {"features": FEATURES, "targets": TARGETS, "quantiles": list(QUANTILES), "n_flights": len(df),
            "train_csv_sha256": hashlib.sha256(open(csv_path, "rb").read()).hexdigest(),
            "note": "Sentetik veriyle egitildi (rejim A). Ayrinti: docs/EVALUATION.md"}
    (out / "meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    print("kaydedildi:", out, len(df), "ucus")


if __name__ == "__main__":
    main(*sys.argv[1:3])
