# ML servis sözleşmesi

API (.NET, `MlHttpPredictor`) ile ML servisi (Python, `ml/service/app.py`) arasındaki istek ve yanıt biçimi. Her uç için bir örnek istek ve bir örnek yanıt vardır:

| Uç | İstek | Yanıt |
|---|---|---|
| `POST /v1/predict` | `predict.request.json` | `predict.response.json` |
| `POST /v1/predict-bag` | `predict-bag.request.json` | `predict-bag.response.json` |
| `POST /v1/outcome` | `outcome.request.json` | `outcome.response.json` |
| `POST /v1/outcome-bags` | `outcome-bags.request.json` | `outcome-bags.response.json` |
| `GET /health` | – | `health.response.json` |

Sözleşme **alan adları ve JSON tipleridir** (metin, sayı, mantıksal, nesne, dizi), değerler değildir. Tam sayı ile ondalık sayı aynı tip ("sayı") sayılır.

İki taraf da bu dosyalara karşı test edilir:
- **Python** (`ml/tests/test_contract.py`): her örnek istek servise gönderilir. Servis kabul etmeli ve yanıtı örnek yanıtla aynı alanları ve tipleri taşımalı.
- **.NET** (`tests/AeroBag.UnitTests/MlContractTests.cs`): `MlHttpPredictor` sahte bir HTTP işleyiciye karşı çalıştırılır. Gönderdiği istek örnek istekle aynı alanları ve tipleri taşımalı, örnek yanıtı da doğru okumalı.

Bir alan eklenir, kaldırılır ya da yeniden adlandırılırsa önce bu dosyalar değiştirilir. Diğer taraf güncellenmeden testlerden biri kırmızıya döner.
