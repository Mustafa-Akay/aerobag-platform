"""Bavul duzeyi ETA (indirmeden banta) degerlendirmesi.
Kullanim: python run_bag_eval.py ../data/synthetic/train_A.csv ../data/synthetic/test_B.csv [cikti.json]
Karsilastirma: (1) ampirik taban cizgisi (mevcut sistem: tum gecmis bavullarin kuantilleri), (2) kuantil LightGBM,
(3) LightGBM + bavul duzeyi canli duzeltme. Sut asamasi icin: ampirik vs fizik formulu (mesafe / bant hizi)."""
import json, sys
import numpy as np
from sklearn.model_selection import GroupKFold
from aerobag_ml.data import bags_from_csv, FEATURES
from aerobag_ml.models import QuantileGBM, QUANTILES
from aerobag_ml.metrics import report_minutes
from aerobag_ml.adapt import completed_bag_recalibrate

PHYSICS_TOLERANCE_S = 15  # tasarim dokumani 5.4: +-15 sn


def empirical(train, target):
    q = {x: float(train[target].quantile(x)) for x in QUANTILES}
    return lambda df: {x: np.full(len(df), q[x]) for x in QUANTILES}


def physics(df):
    d = (df["chute_m"] / df["speed"]).to_numpy()
    return {0.1: d - PHYSICS_TOLERANCE_S, 0.5: d, 0.9: d + PHYSICS_TOLERANCE_S}


def main(train_p, test_p, out=None):
    A, B = bags_from_csv(train_p), bags_from_csv(test_p)
    res = {"n_train_bags": len(A), "n_test_bags": len(B), "target": "unload_to_carousel_seconds"}

    # A icinde 5 katli, UCUS bazli CV
    cv = {"baseline": [], "lgbm": []}
    for tr, te in GroupKFold(5).split(A, groups=A["flight_id"]):
        trd, ted = A.iloc[tr], A.iloc[te]
        cv["baseline"].append(report_minutes(ted["dur_s"], empirical(trd, "dur_s")(ted)))
        cv["lgbm"].append(report_minutes(ted["dur_s"], QuantileGBM(FEATURES).fit(trd, "dur_s").predict(ted)))
    avg = lambda L: {k: float(np.mean([x[k] for x in L])) for k in L[0]}
    res["in_regime_cv"] = {k: avg(v) for k, v in cv.items()}

    # A -> B kaymasi
    base = empirical(A, "dur_s")(B)
    model = QuantileGBM(FEATURES).fit(A, "dur_s")
    raw = model.predict(B)
    adapted = completed_bag_recalibrate(B["dur_s"], raw, B["unload_s"], B["carousel_s"])
    res["shift_A_to_B"] = {
        "baseline": report_minutes(B["dur_s"], base),
        "lgbm": report_minutes(B["dur_s"], raw),
        "lgbm_recalibrated": report_minutes(B["dur_s"], adapted),
    }

    # Ablasyon (kayma testi, duzeltmesiz): bavula ozgu ozellikler katki sagliyor mu?
    A2, B2 = A.copy(), B.copy()
    for D in (A2, B2):
        D["idx_frac"] = D["unload_index"] / D["bags"]
        D["since_onblock"] = D["unload_s"] - D["on_block_s"]
    sets = {
        "ucus_ozellikleri": FEATURES,
        "+indirme_sirasi": FEATURES + ["unload_index", "idx_frac"],
        "+onblock_sonrasi_sure": FEATURES + ["unload_index", "idx_frac", "since_onblock"],
        "yalniz_govde_yagmur": ["wide", "rain"],
        "fizik_yok": [f for f in FEATURES if f not in ("stand_m", "chute_m", "speed")],
    }
    res["ablation_shift_p50_mae_min"] = {
        n: report_minutes(B2["dur_s"], QuantileGBM(f).fit(A2, "dur_s").predict(B2))["p50_mae"] for n, f in sets.items()}

    # Sut -> bant: ampirik vs fizik
    cb = empirical(A, "chute_dur_s")(B)
    res["chute_stage_shift"] = {"empirical": report_minutes(B["chute_dur_s"], cb), "physics": report_minutes(B["chute_dur_s"], physics(B))}
    res["chute_stage_note"] = ("Simulatorde sut->bant suresi tam olarak mesafe/hiz (+%3 gurultu) ile uretilir; fizik formulunun "
                               "burada ustun cikmasi bu yuzden beklenendir ve gercek donanim hakkinda kanit degildir.")

    res["seeds_shift_p50_mae_min"] = [
        report_minutes(B["dur_s"], QuantileGBM(FEATURES, s).fit(A, "dur_s").predict(B))["p50_mae"] for s in range(5)]
    txt = json.dumps(res, indent=2, ensure_ascii=False)
    if out:
        open(out, "w", encoding="utf-8").write(txt)
    print(txt)


if __name__ == "__main__":
    main(*sys.argv[1:4])
