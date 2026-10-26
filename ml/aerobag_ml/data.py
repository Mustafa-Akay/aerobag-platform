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


def bags_from_csv(path: str) -> pd.DataFrame:
    """Bavul duzeyinde tablo (indirmeden bantta gorunmeye gecen sure). Ozellikler uçus ozellikleridir; indirme sirasi gibi
    bavula ozgu ozelliklerin ise katki saglamadigi deneyle gosterildi (docs/EVALUATION.md)."""
    d = pd.read_csv(path)
    for c in ("on_block_utc", "unload_utc", "chute_utc", "carousel_utc"):
        d[c] = pd.to_datetime(d[c], utc=True).dt.tz_localize(None).astype("datetime64[s]").astype("int64")
    b = pd.DataFrame({
        "flight_id": d["flight_id"], "wide": d["wide_body"], "rain": (d["weather"] == "RAIN").astype(int),
        "bags": d["total_bags"], "stand_m": d["stand_distance_m"], "chute_m": d["chute_to_carousel_m"],
        "speed": d["conveyor_speed_mps"], "arrivals_30": d["arrivals_last_30min"], "handler": d["ground_handler"],
        "on_block_s": d["on_block_utc"], "unload_s": d["unload_utc"], "chute_s": d["chute_utc"], "carousel_s": d["carousel_utc"],
        "unload_index": d["unload_index"],
    })
    for h in HANDLERS:
        b[f"h_{h[-1]}"] = (b["handler"] == h).astype(int)
    b["dur_s"] = b["carousel_s"] - b["unload_s"]          # indirme -> bant
    b["chute_dur_s"] = b["carousel_s"] - b["chute_s"]     # sut -> bant
    return b.drop(columns=["handler"]).sort_values("unload_s", kind="stable").reset_index(drop=True)
