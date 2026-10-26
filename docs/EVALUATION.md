# Faz 2 değerlendirme: bavul gelişi tahmini

Bu belge **sentetik** veri üzerindeki sonuçları anlatır. Sayılar bu simülatörün varsayımlarını yansıtır, gerçek bir havalimanını değil. Gerçek-veri çıpası olarak yalnızca Münih için yayımlanmış hata düzeyi (yaklaşık 4–6 dk) referans alındı; mutlak süre seviyeleri kaynaklı değildir.

## Düzen
- Veri: `train_A` = 1000 uçuş (rejim A, seed 1); `test_B` = 400 uçuş (rejim B: yağmur ağırlıklı, yavaş operasyon, seed 2). Veri `data/synthetic/` altında üretilir, depoya girmez.
- Birim: uçuş. Hedefler: ilk bavulun banda geliş süresi (on-block'tan), ilk→son bavul süresi, son bavul (= ikisinin toplamı).
- Özellikler: gövde tipi, yağmur, bavul sayısı, stand/şut mesafesi, bant hızı, son 30 dk iniş sayısı, yer hizmeti firması. `regime` etiketi **kullanılmaz** (gerçekte bilinmeyen simülatör etiketi; sızıntı olurdu).
- Baseline: gövde tipine göre geçmiş medyan ve ampirik P10/P90.
- Model: kuantil başına LightGBM (P10/P50/P90), küçük ağaçlar (8 yaprak), sıralı kuantiller.
- Bölme uçuş düzeyindedir; aynı uçuşun bavulları iki tarafa düşmez.

## Sonuçlar
### A içi, 5 katlı uçuş bazlı çapraz doğrulama

**İlk bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 3.31 | 1.14 | 79% | 10% | 9.7 |
| LightGBM kuantil | 2.89 | 1.06 | 73% | 13% | 7.4 |

**İlk→son bavul süresi**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 1.79 | 0.57 | 80% | 10% | 5.7 |
| LightGBM kuantil | 1.44 | 0.48 | 72% | 15% | 3.8 |

**Son bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 4.19 | 1.40 | 80% | 10% | 12.6 |
| LightGBM kuantil | 3.66 | 1.29 | 72% | 14% | 9.3 |

### Kayma: A'da eğit, B'de test (düzeltmesiz)

**İlk bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 6.34 | 2.06 | 61% | 38% | 9.8 |
| LightGBM kuantil | 5.73 | 2.00 | 52% | 48% | 7.6 |

**İlk→son bavul süresi**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 2.30 | 0.73 | 73% | 25% | 5.7 |
| LightGBM kuantil | 1.87 | 0.63 | 65% | 32% | 4.0 |

**Son bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 8.03 | 2.63 | 56% | 42% | 12.6 |
| LightGBM kuantil | 7.23 | 2.54 | 50% | 50% | 9.9 |

### Kayma + canlı artık düzeltmesi (B'de test)

**İlk bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 4.34 | 1.47 | 76% | 12% | 13.6 |
| LightGBM kuantil | 3.81 | 1.34 | 78% | 10% | 12.1 |

**İlk→son bavul süresi**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 1.98 | 0.63 | 76% | 12% | 5.9 |
| LightGBM kuantil | 1.53 | 0.51 | 76% | 12% | 4.6 |

**Son bavul**

| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Medyan baseline | 5.47 | 1.80 | 75% | 12% | 16.7 |
| LightGBM kuantil | 4.76 | 1.60 | 76% | 11% | 14.5 |

### Özellik çıkarma (kayma testi, düzeltmesiz, P50 MAE dk)
| Özellik kümesi | İlk bavul | İlk→son bavul süresi | Son bavul |
|---|---|---|---|
| tum_ozellikler | 5.73 | 1.87 | 7.23 |
| handler_yok | 5.83 | 1.93 | 7.27 |
| yogunluk_yok | 5.73 | 1.87 | 7.17 |
| fizik_yok | 6.20 | 1.82 | 7.47 |
| yalniz_govde_yagmur | 6.18 | 2.12 | 7.27 |

Tohum değişkenliği (5 tohum, kayma testi, P50 MAE): İlk bavul: 5.69–5.76; İlk→son bavul süresi: 1.87–1.91; Son bavul: 7.16–7.23.

## Okuma
1. **Aynı rejimde** LightGBM, baseline'ı P50 hatasında ve pinball'da yener (yaklaşık %13–19) ve aralığı daraltır. Ancak aralığı **fazla dar** kurar: [P10,P90] kapsaması hedef %80 yerine ~%71–73. Aralık güvenilirliği için ek kalibrasyon gerekir.
2. **Rejim kaymasında** ikisi de bozulur. Model P50 hatasında baseline'dan ~%10–18 iyi kalır ama kapsama %50–65'e, P90'ı aşma oranı %32–50'ye çıkar. Yani yağmurlu/yavaş günlerde sistem sistematik olarak erken tahmin eder. Nedeni: B'deki yavaşlık (yük çarpanı) bir özellik olarak verilmiyor; yalnızca `rain` onu kısmen taşıyor.
3. **Canlı artık düzeltmesi** (`aerobag_ml/adapt.py`): her tahmine, yalnızca bitmiş uçuşların son 40 artığından hesaplanan kuantil kayması eklenir. Son bavul ~20 dk sonra bilindiği için en yeni 2 uçuş hariç tutulur (testle doğrulandı: hedef sızmaz). Sonuç: kapsama ~%76–78'e, P90'ı aşma oranı ~%10–12'ye döner; P50 MAE ilk bavulda 5,73 → 3,81, son bavulda 7,23 → 4,76 dk. Bedel: aralık genişler.
4. **Düzeltme tek başına da çalışır**: baseline + düzeltme de kapsamayı toparlar; LightGBM'in düzeltme sonrası kalan üstünlüğü P50 MAE'de ~%12–23 civarındadır. Kayma durumunda asıl kazancı özelliklerden çok bu geri besleme getirir.
5. **Özellik çıkarma**: yalnızca gövde tipi ve yağmur yeterli değil; mesafe ve bant hızı (fizik) çıkarılınca ilk bavul 5,73 → 6,20 dk. Yer hizmeti firması ve yoğunluk özelliği kayma testinde ihmal edilebilir fark yaratıyor. (Not: yoğunluk özelliği simülatörde hiçbir şeyi etkilemiyor, bu yüzden beklenen sonuç budur.)

## Sınırlar
- Veri sentetik; gürültü parametreleri bu belge için bilinçli seçildi (`FlightGenerator.cs`). Eğitim ve test aynı üretici ailesinden geldiği için sonuçlar iyimser olabilir.
- Rejim B yalnızca bir kayma türünü (daha yüksek yük çarpanı + yağmur) temsil eder.
- Düzeltme uçuşların zaman sırasına ve düzgün akışına dayanır; gerçekte geç veri veya kesintiler bunu zorlaştırır.
- Münih sonuçlarıyla birebir karşılaştırma yapılmadı; veri ve protokol farklı.

## Yeniden üretme
```
pip install -r ml/requirements.txt
dotnet run --project src/AeroBag.Core/AeroBag.Simulator -- --mode csv --flights 1000 --regime A --seed 1 --out data/synthetic/train_A.csv
dotnet run --project src/AeroBag.Core/AeroBag.Simulator -- --mode csv --flights 400  --regime B --seed 2 --out data/synthetic/test_B.csv
cd ml && python run_eval.py ../data/synthetic/train_A.csv ../data/synthetic/test_B.csv results.json
python -m pytest -q tests
```
