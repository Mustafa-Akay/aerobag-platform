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

---

## Bavul düzeyi ETA (indirmeden banta kalan süre)

Yolcunun asıl sorusu "bavulum indirildi, ne zaman gelir?" sorusudur. Bu bölüm, indirme anından banta gelişe kadar geçen süreyi (138,949 eğitim bavulu, 56,217 test bavulu) üç yöntemle karşılaştırır. Komut: `python run_bag_eval.py ../data/synthetic/train_A.csv ../data/synthetic/test_B.csv results_bag.json`.

- **Ampirik taban çizgisi** = sistemin eskiden kullandığı yöntem: geçmiş tüm bavulların indirme→bant sürelerinin P10/P50/P90'ı.
- **Kuantil LightGBM**: aynı uçuş özellikleriyle bavul düzeyinde.
- **LightGBM + canlı düzeltme**: her tahmin, yalnızca o ana kadar **banta gelmiş** son 1000 bavulun artıklarını kullanır (sızıntı yok, testle doğrulandı).

Bölme uçuş düzeyindedir (aynı uçuşun bavulları iki tarafa düşmez).

### A içi, 5 katlı uçuş bazlı çapraz doğrulama
| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Ampirik taban çizgisi | 1.60 | 0.49 | 80% | 10% | 5.0 |
| LightGBM | 0.72 | 0.24 | 79% | 10% | 2.2 |

### Kayma: A'da eğit, B'de test
| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Ampirik taban çizgisi | 2.53 | 0.84 | 60% | 37% | 5.0 |
| LightGBM (düzeltmesiz) | 1.77 | 0.59 | 45% | 55% | 2.2 |
| LightGBM + canlı düzeltme | 1.00 | 0.33 | 77% | 11% | 3.2 |

### Özellik çıkarma (kayma testi, düzeltmesiz, P50 MAE dk)
| Özellik kümesi | P50 MAE |
|---|---|
| ucus_ozellikleri | 1.77 |
| +indirme_sirasi | 1.77 |
| +onblock_sonrasi_sure | 1.77 |
| yalniz_govde_yagmur | 2.64 |
| fizik_yok | 2.61 |

Tohum değişkenliği (5 tohum): 1.76–1.77 dk.

### Şut → bant: ampirik ve fizik (kayma testi)
| Model | P50 MAE (dk) | Pinball (ort.) | [P10,P90] kapsama | P90'ı aşan | Aralık genişliği (dk) |
|---|---|---|---|---|---|
| Ampirik (şut→bant kuantilleri) | 0.33 | 0.09 | 75% | 8% | 0.9 |
| Fizik: mesafe / bant hızı ± 15 sn | 0.04 | 0.02 | 100% | 0% | 0.5 |

### Okuma
1. **Aynı rejimde** bavul düzeyi model, ampirik taban çizgisini P50 hatasında ~%55 (1,60 → 0,72 dk), pinball'da ~%50 yener ve aralığı yarıdan fazla daraltır (5,0 → 2,2 dk) ve kapsamayı hedefte (%79) tutar.
2. **Kaymada** düzeltmesiz model baseline'dan iyidir (2,53 → 1,77 dk) ama aralığı fazla dardır: kapsama %45, P90'ı aşma %55. Yağmurlu/yavaş günde sistem tutarlı biçimde erken söyler.
3. **Canlı düzeltme** bunu toparlar: P50 hatası 1,0 dk (baseline'a göre ~%60 düşüş), kapsama %77, P90'ı aşma %11. Bedel: aralık 2,2 → 3,2 dk, yine de baseline'ın (5,0) altında.
4. **Bavula özgü özellikler katkı sağlamıyor**: indirme sırası ve on-block'tan beri geçen süre eklenince hata değişmiyor (1,77 dk). Bu simülatörde bir bavulun süresi, uçuşun geri kalanından belirlenen ortak bir bileşen ve gözlenemeyen araba/bant gürültüsünden oluşuyor. Bu yüzden üretimde yalnızca uçuş özellikleri kullanılır ve tahmin uçuş içindeki tüm bavullar için aynıdır. Tasarım dokümanındaki "indirme sırasından türetilen bavul tahmini" bu veride bir fayda göstermedi; gerçek bir bantta farklı olabilir.
5. **Fizik, şutta ampirik yöntemi yener** (0,33 → 0,04 dk, kapsama %100 ve dar). Bu sonuç **dairesel**dir: simülatör şut→bant süresini tam olarak mesafe / hız (+%3 gürültü) ile üretir. Gerçek donanım hakkında bir kanıt değildir; yalnızca formülün doğru uygulandığını gösterir. ±15 sn pay bir tasarım varsayımıdır.

### Sınırlar
- Sentetik veri; eğitim ve test aynı üretici ailesinden. Sonuçlar iyimser olabilir.
- Canlı düzeltme, bavulların sıralı akışına ve bittikçe bildirilen sürelere dayanır; gerçekte geç ya da eksik veri bunu zorlaştırır. Servis durumu bellektedir (yeniden başlayınca düzeltme sıfırlanır).
- Tahmin indirme anına göre hesaplanır; bavul tahmin penceresini geçtiyse (gecikmiş bavul) pencere geçmişte kalır ve ayrıca ele alınmaz.

---

## Simülatörün kamuya açık referanslarla karşılaştırılması

**Bu bir kalibrasyon değil, bir makullük kontrolüdür.** Simülatör parametreleri bu referanslara göre ayarlanmadı. Referanslar çoğunlukla hedeflerdir (gerçekleşen dağılım değil), farklı havalimanlarına aittir ve ölçüm başlangıcı her yerde aynı tanımlanmaz. Gerçek bir havalimanının bavul dağılımı elimizde yok; bu yüzden aşağıdaki karşılaştırma yalnızca simülatörün gerçek dünyadaki büyüklük mertebesinden uzak olmadığını gösterir.

### Kaynaklar

| Kaynak | Ne söylüyor | Not |
|---|---|---|
| [BCAS (Hindistan), Onmanorama 18.02.2024](https://www.onmanorama.com/travel/travel-news/2024/02/18/airlines-air-india-indigo-akasa-air-spicejet-vistara-baggage-arrival-time-30-minutes-bcas.amp.html) | İlk bavul motor kapanışından sonra 10 dk, son bavul 30 dk içinde | Hedef; gerçekleşen değil |
| [Prag Havalimanı performans raporu (Ekim 2021)](https://www.prg.aero/sites/default/files/obsah/soubory/LPR/Performance_report_102021_GHA.pdf) | Son bavul sınırı bavul sayısına göre 30/40/50/60/70 dk (0-90, 91-150, 151-200, 201-350, 351+), uçuşların en az %90'ında; ölçüm AIBT'den (uçağın park yerinde durması). Ekim 2021'de gerçekleşen: %97,88, %98,60 ve %99,39 (üç işleyici) | Sözleşme hedefi ve gerçekleşen birlikte; ilk bavul yok |
| [Hong Kong, SCMP (2016/17)](https://amp.scmp.com/news/hong-kong/economy/article/2120460/why-your-luggage-takes-lot-longer-get-through-hong-kong) | İlk bavul 20 dk içinde %96,8, son bavul 40 dk içinde %96,6 | Gerçekleşen; ölçüm başlangıcı belirtilmemiş |
| [Hindistan, Deccan Herald (2024)](https://www.deccanherald.com/amp/story/india%2Fbaggage-delivery-time-improves-at-major-airports-2956817) | Uçuş inişinden sonra 30 dk içinde teslim edilen bavul oranı %62'den %84,8'e çıktı; son bavul hedefleri 30 dk (4 büyük havalimanı) ve 15 dk (Hyderabad, Bengaluru) | Ulusal toplam; havalimanı kırılımı yok |
| [Taoyuan Havalimanı açık veri seti](https://data.gov.tw/en/datasets/171382) | Aylık ilk/son bavul süresi alanları var | Sayfada rakam ve ölçüm tanımı yok, kullanılmadı |

### Simülatörün çıktısı (on-block'tan itibaren, uçuş düzeyi)

| | Rejim A (1000 uçuş) | Rejim B (400 uçuş) |
|---|---|---|
| İlk bavul ≤ 10 dk (BCAS hedefi) | %15,1 | %1,5 |
| İlk bavul ≤ 20 dk (Hong Kong ölçütü) | %91,7 | %68,0 |
| Son bavul ≤ 30 dk (BCAS hedefi) | %89,5 | %62,5 |
| Son bavul ≤ 40 dk (Hong Kong ölçütü) | %97,8 | %92,0 |
| Prag kuralı (sayıya bağlı sınır) | %98,5 | %95,3 |
| İlk bavul medyanı / son bavul medyanı | 13,0 / 20,9 dk | 18,1 / 27,8 dk |

### Okuma
1. **Rejim A, Prag ve Hong Kong'un bildirdiği gerçekleşen performansa yakın**: Prag kuralı %98,5 (Prag işleyicileri %97,9-99,4), son bavul ≤ 40 dk %97,8 (HK %96,6). İlk bavul ≤ 20 dk için simülatör %91,7, HK'nin %96,8'inden birkaç puan geride.
2. **Simülatörün ilk bavulu BCAS hedefinden yavaş**: 10 dk içinde yalnızca %15. Bu hedefin gerçekleşme oranı bilinmediği için bunun bir hata mı, farklı bir havalimanı mı olduğunu söyleyemeyiz. Hedef, bu simülatörün modellediği (uzak park yeri, mesafe, indirme sırası) bir operasyondan daha hızlı bir operasyonu varsayıyor olabilir.
3. **Rejim B bilerek stresli bir gün** (yağmur, yavaş işleyici). Hedeflerin altına düşmesi beklenen bir sonuçtur; gerçek bir günün dağılımı olarak sunulmuyor.
4. Referanslar farklı havalimanları, yıllar ve ölçüm başlangıçlarından geliyor (motor kapanışı, AIBT, iniş). Bu yüzden sayılar doğrudan karşılaştırılabilir değildir, yalnızca mertebe kontrolüdür.

### Sonuç
Simülatör, bildirilen gerçek performansla aynı büyüklük mertebesinde sonuç üretiyor (özellikle son bavul için). Parametreler bu verilerle **kalibre edilmedi** ve gerçek bir havalimanı verisiyle doğrulanmadı. ARCHITECTURE.md 8.5'teki "kalibre edilir" ifadesi buna göre düzeltildi. Gerçek veriyle kalibrasyon, bu projenin kapsamı dışında kalan bir adımdır.
