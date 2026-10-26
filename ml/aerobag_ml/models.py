import numpy as np
import lightgbm as lgb

QUANTILES = (0.1, 0.5, 0.9)


class MedianBaseline:
    """Referans: genis/dar govdeye gore gecmis medyan ve ampirik P10/P90."""

    def fit(self, df, target):
        self.q = {w: df.loc[df.wide == w, target].quantile(QUANTILES).to_dict() for w in (0, 1)}
        return self

    def predict(self, df):
        return {q: df.wide.map(lambda w: self.q[w][q]).to_numpy() for q in QUANTILES}


class QuantileGBM:
    """Her kuantil icin ayri LightGBM; kuantil capraz gecisi sirala ile duzeltilir."""

    def __init__(self, features, seed=0):
        self.features, self.seed = features, seed

    def fit(self, df, target):
        self.m = {}
        for q in QUANTILES:
            self.m[q] = lgb.LGBMRegressor(
                objective="quantile", alpha=q, n_estimators=300, learning_rate=0.03,
                num_leaves=8, min_child_samples=20, subsample=0.8, subsample_freq=1,
                colsample_bytree=0.8, random_state=self.seed, verbose=-1,
            ).fit(df[self.features], df[target])
        return self

    def predict(self, df):
        raw = np.sort(np.column_stack([self.m[q].predict(df[self.features]) for q in QUANTILES]), axis=1)
        return {q: raw[:, i] for i, q in enumerate(QUANTILES)}
