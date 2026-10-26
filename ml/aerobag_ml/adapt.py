import numpy as np
from .models import QUANTILES


def rolling_recalibrate(y, pred, window=40, min_n=10, lag=2):
    """Canli kullanimda uygulanabilir uyarlama: her tahmin yalniz kendinden ONCE gerceklesmis ucuslarin
    artiklarini kullanir (y[i] kullanilmaz). `lag`: son bavul ~20 dk sonra bilindigi icin (ucuslar 15 dk arayla)
    en yeni `lag` ucus kullanilmaz. Son `window` ucusun artik kuantilleri tahmine eklenir."""
    y = np.asarray(y)
    out = {q: pred[q].copy() for q in QUANTILES}
    for i in range(len(y)):
        hi = i - lag
        lo = max(0, hi - window)
        if hi - lo < min_n:
            continue
        for q in QUANTILES:
            res = y[lo:hi] - pred[q][lo:hi]
            out[q][i] = pred[q][i] + np.quantile(res, q)
    mid = out[0.5]
    return {0.1: np.minimum(out[0.1], mid), 0.5: mid, 0.9: np.maximum(out[0.9], mid)}
