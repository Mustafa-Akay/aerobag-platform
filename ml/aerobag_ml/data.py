"""Bavul satırlarından uçuş düzeyinde tablo üretir. Hedefler: ilk bavul ve ilk->son bavul süresi (dakika)."""
import pandas as pd

# 'regime' bilerek yok: simülatörün gizli etiketidir, gerçekte bilinmez (sızıntı olurdu).
NUMERIC = ["wide", "rain", "bags", "stand_m", "chute_m", "speed", "arrivals_30"]
HANDLERS = ["HANDLER_A", "HANDLER_B", "HANDLER_C"]
FEATURES = NUMERIC + [f"h_{h[-1]}" for h in HANDLERS]


def flights_from_csv(path: str) -> pd.DataFrame:
    d = pd.read_csv(path)
    g = d.groupby("flight_id", sort=True)
    f = g.agg(
        wide=("wide_body", "first"),
        rain=("weather", lambda x: int(x.iloc[0] == "RAIN")),
        bags=("total_bags", "first"),
        stand_m=("stand_distance_m", "first"),
        chute_m=("chute_to_carousel_m", "first"),
        speed=("conveyor_speed_mps", "first"),
        arrivals_30=("arrivals_last_30min", "first"),
        handler=("ground_handler", "first"),
        first_s=("sec_onblock_to_carousel", "min"),
        last_s=("sec_onblock_to_carousel", "max"),
    )
    for h in HANDLERS:
        f[f"h_{h[-1]}"] = (f["handler"] == h).astype(int)
    f["first_min"] = f["first_s"] / 60
    f["span_min"] = (f["last_s"] - f["first_s"]) / 60
    f["last_min"] = f["last_s"] / 60
    return f.drop(columns=["handler", "first_s", "last_s"]).reset_index()
