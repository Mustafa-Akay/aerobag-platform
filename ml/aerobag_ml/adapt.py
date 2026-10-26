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


def completed_bag_recalibrate(y, pred, unload_s, carousel_s, window=1000, min_n=50):
    """Bavul duzeyi canli duzeltme (cevrimdisi tekrar oynatma): i. bavulun tahmini, yalnizca i. bavul indirildigi ana kadar
    BANTA GELMIS bavullarin artiklarini kullanir (carousel_s[j] <= unload_s[i]). Son `window` bavulun artik kuantilleri eklenir.
    Girdilerin unload_s'e gore sirali olmasi gerekmez; sonuc girdi sirasindadir."""
    y = np.asarray(y, float)
    unload_s = np.asarray(unload_s); carousel_s = np.asarray(carousel_s)
    order = np.argsort(carousel_s, kind="stable")
    car_sorted = carousel_s[order]
    res = {q: (y - pred[q])[order] for q in QUANTILES}
    out = {q: pred[q].astype(float).copy() for q in QUANTILES}
    for i in range(len(y)):
        k = int(np.searchsorted(car_sorted, unload_s[i], side="right"))
        lo = max(0, k - window)
        if k - lo < min_n:
            continue
        for q in QUANTILES:
            out[q][i] = pred[q][i] + np.quantile(res[q][lo:k], q)
    mid = out[0.5]
    return {0.1: np.minimum(out[0.1], mid), 0.5: mid, 0.9: np.maximum(out[0.9], mid)}
