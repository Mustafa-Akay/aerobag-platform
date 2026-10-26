import sys, pathlib
import numpy as np
import pandas as pd
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from aerobag_ml.metrics import pinball, report
from aerobag_ml.adapt import rolling_recalibrate
from aerobag_ml.models import MedianBaseline, QuantileGBM


def test_pinball_known_values():
    assert pinball(np.array([10.0]), np.array([8.0]), 0.9) == 1.8   # eksik tahmin P90'da pahali
    assert abs(pinball(np.array([10.0]), np.array([12.0]), 0.9) - 0.2) < 1e-9


def _synthetic(n=300, seed=0):
    r = np.random.default_rng(seed)
    wide = r.integers(0, 2, n)
    y = 10 + 5 * wide + r.normal(0, 1, n)
    return pd.DataFrame({"wide": wide, "x": r.normal(size=n), "y": y})


def test_quantiles_are_ordered_and_cover_roughly():
    d = _synthetic()
    p = QuantileGBM(["wide", "x"]).fit(d, "y").predict(d)
    assert (p[0.1] <= p[0.5]).all() and (p[0.5] <= p[0.9]).all()
    assert 0.6 < report(d.y, p)["coverage_p10_p90"] < 0.95


def test_recalibration_never_uses_the_current_or_lagged_target():
    d = _synthetic(120)
    p = MedianBaseline().fit(d, "y").predict(d)
    y1 = d.y.to_numpy().copy()
    a = rolling_recalibrate(y1, p, window=20, min_n=5, lag=2)
    y2 = y1.copy(); y2[60] += 1000      # tek bir gelecek/ayni-an degeri bozulur
    b = rolling_recalibrate(y2, p, window=20, min_n=5, lag=2)
    for q in a:
        assert np.allclose(a[q][:62], b[q][:62])   # 60 ve 61 dahil, onceki tahminler etkilenmez


def test_recalibration_fixes_a_constant_shift():
    d = _synthetic(300)
    p = MedianBaseline().fit(d, "y").predict(d)
    shifted = d.y.to_numpy() + 4
    before = report(shifted, p)["p50_mae"]
    after = report(shifted, rolling_recalibrate(shifted, p))["p50_mae"]
    assert after < before / 2
