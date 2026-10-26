import numpy as np


def pinball(y, p, q):
    d = y - p
    return float(np.mean(np.maximum(q * d, (q - 1) * d)))


def report(y, pred):
    y = np.asarray(y)
    lo, mid, hi = pred[0.1], pred[0.5], pred[0.9]
    return {
        "p50_mae": float(np.mean(np.abs(y - mid))),
        "pinball_avg": float(np.mean([pinball(y, pred[q], q) for q in pred])),
        "coverage_p10_p90": float(np.mean((y >= lo) & (y <= hi))),   # hedef ~0.80
        "late_rate_p90": float(np.mean(y > hi)),                      # hedef ~0.10
        "late_rate_p50": float(np.mean(y > mid)),                     # hedef ~0.50
        "width_p10_p90": float(np.mean(hi - lo)),
    }


def report_minutes(y_seconds, pred_seconds):
    """Saniye cinsinden tahminleri dakika olarak raporlar."""
    return report(np.asarray(y_seconds) / 60, {q: np.asarray(v) / 60 for q, v in pred_seconds.items()})
