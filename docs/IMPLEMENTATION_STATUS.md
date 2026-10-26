# AeroBag: Uygulama durumu (koda göre)

`ARCHITECTURE.md` hedef tasarımı anlatır. Bu belge, **şu an çalışan kodun** tasarımdan nerede ayrıştığını ve neyin henüz olmadığını açıkça listeler. Çelişki olursa bu belge koda daha yakındır.

Son güncelleme: 26 Ekim 2026. Kapsam: Faz 1, Faz 2 ve Faz 3 (dayanıklılık, ölçütü karşılandı) tamam; Faz 4'ün ilk dilimi (çıkış okuması ve süre dolumu) tamam. Faz numaraları `ARCHITECTURE.md` bölüm 1.1'dekidir.

| Faz (ARCHITECTURE.md) | Durum |
|---|---|
| 1. Çekirdek | Tamam |
| 2. Makine öğrenmesi | Tamam (bkz. aşağıda ve `EVALUATION.md`) |
| 3. Dayanıklılık | Ölçüt karşılandı; Kafka uyumlu broker yok (bkz. "Faz 3 durumu") |
| 4. Güvenlik ve çıkış doğrulama | Başladı: çıkış okuması ve süre dolumu var; PNR eşleştirme, tehdit modeli, KVKK notu, kimlik doğrulama yok |
| 5. Opsiyonel donanım demosu | Başlanmadı |

## Çalışan parçalar

| Parça | Durum |
|---|---|
| Durum makinesi (monoton, ANOMALY, IGNORED_STALE) | Çalışıyor, birim testli |
| Type B ayrıştırıcı (BSM/BUM/BPM, sıkı sınırlar) | Çalışıyor, birim testli |
| Olay işleme (idempotent, bagaj başına advisory lock, dead-letter) | Çalışıyor, entegrasyon testli |
| Yolcu sorgusu (PNR + soyad, HMAC, tekdüze 404, IP başına 20/dk) | Çalışıyor |
| ETA tahmini (deneysel P10/P50/P90) | Çalışıyor, canlı doğrulandı |
| Operasyon panosu API'si ve arayüzü | Çalışıyor |
| Outbox → SignalR canlı bildirim | Çalışıyor, entegrasyon testli |
| React arayüzü (yolcu sayfası, operasyon panosu) | Çalışıyor |
| Simülatör (CSV ve canlı mod, tohumlu, gürültü ayarlı) | Çalışıyor |

## Tasarımdan ayrışan ve netleşen noktalar

- **BPM okuma noktası kuralı.** Biçim `[A-Z]{3,10}\d{2}` (iki rakam şart). Önek durumu belirler: `SEC` güvenlik, `LOAD` uçağa yükleme, `CHUTE` şut, `CAROUSEL` bant ağzı, `CLAIM` banttan alma. Bilinmeyen önek reddedilir.
- **Olay zamanı.** BSM'nin zamanı mesajda yoktur, alınma anı kullanılır. BUM ve BPM zamanı mesajdan gelir.
- **ETA tabanı.** Olay geçmişinden `percentile_cont` ile hesaplanır. En az 20 örnek varsa `HISTORY`, yoksa sabit varsayılanlar (`FALLBACK`: iniş→bant 300/540/900 sn, şut→bant 60/90/150 sn). ETA yalnızca bagaj indirildikten sonra verilir.
- **Referans an (`asOfUtc`).** Simülasyonda "şimdi", BSM olmayan olayların en son zamanıdır (sanal saat). Gerçek kurulumda gerçek saat olmalıdır.
- **Yolcu verisi.** Ad, soyad ve PNR düz metin saklanmaz; yalnızca `HMAC-SHA256(gizli anahtar, PNR|soyad)` arama anahtarı tutulur. Geliştirme varsayılan anahtarı kodda durur; gerçek kurulumda `Security__LookupKeySecret` verilmelidir.
- **Bant numarası** saklanmıyor, arayüzde gösterilmiyor (AODB bağlamı yok).
- **Canlı bildirim.** `/hubs/live` kanalı içeriksizdir: `bagChanged` (grup `bag:{etiket}`) ve `flightsChanged` (grup `ops`). İstemci ayrıntıyı REST ile yeniden çeker. Canlı bağlantı varken yoklama 20 sn'ye, yokken 3 sn'ye ayarlıdır.
- **Outbox garantisi.** En az bir kez teslim, `seq` sırası, 5 başarısız denemeden sonra mesaj bekletilir. Tek örnek varsayılır.

## Bilinen eksikler ve riskler

1. **Kimlik doğrulama yok:** `/v1/events`, `/v1/flights` ve `/hubs/live` herkese açık. Yalnızca yerel demo için uygundur. Hub'da bagaj etiketini bilen o bagajın "değişti" sinyalini alabilir (içerik görmez). Faz 4.
2. **Çıkışta yolcu eşleştirmesi yok.** Çıkış okuması ve süre dolumu çalışıyor (aşağıda "Faz 4, ilk dilim"), ama bavul ile sunulan PNR karşılaştırılmıyor; yanlış kişi kontrolü (`EXIT_REVIEW`) yok. Faz 4.
3. **Bantta takılı bagaj tespiti yok.** "Alındı" mesajı düşerse bagaj sonsuza dek `ON_CAROUSEL` kalır; çıkış süresi bunu çözmez çünkü sayaç alınma anında başlar.
4. **AODB bağlamı simülatörden gelir:** gövde tipi, hava, mesafeler ve yer hizmeti firması `POST /v1/flights/context` ile verilir; gerçek bir uçuş bilgi sistemi yok. Bant numarası hâlâ saklanmıyor.
5. **ML ölçümü yalnızca sentetik veride ve uçuş düzeyindedir.** Doğruluk, kapsama ve rejim kayması sonuçları `EVALUATION.md`'dedir; durum bazlı ampirik ETA (iniş sonrası) ayrıca ölçülmedi.
6. **Doğrulama kapsamı:** sonuçlar yalnızca simülasyon verisiyle doğrulandı. Gerçek havalimanı verisi yoktur ve IATA BIX uyumluluğu iddia edilmez.
7. **Bağımlılık uyarısı:** `SSH.NET` (Testcontainers üzerinden, yalnızca test projesinde) için NU1903 uyarısı var. Üretime girmez; Testcontainers güncellenince kalkması beklenir.
8. **CI:** `dotnet`, `ui` ve `ml` işleri GitHub Actions'ta yeşil çalışıyor.

## Yerelde çalıştırma

```powershell
docker compose up -d                                   # Postgres (5433)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
cd src\aerobag-ui; npm install; npm run dev            # http://localhost:5173
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5
dotnet test                                            # Docker açık olmalı
```

Şema değişince veritabanı `docker compose down -v` ile sıfırlanır. Docker açıkken ve Postgres hazırken API'yi başlatın; hazır değilken gönderilen olaylar kaybolur ve sonraki olaylar "sıra dışı" görünür.

## Faz 2: ML tahmini

Çalışan parçalar:
- **Simülatör gerçekçiliği:** gizli ekip hızı, geç gelen ekipman, araba beklemesi, gözlenebilir yer hizmeti firması (`FlightGenerator.cs`). Gürültü parametreleri demo varsayımıdır.
- **`ml/` Python paketi:** uçuş düzeyinde veri, medyan baseline, kuantil LightGBM, rejim A→B kayma testi, özellik çıkarma deneyi, canlı artık düzeltmesi. Sonuçlar ve sınırlar: `docs/EVALUATION.md`.
- **ML servisi (`ml/service`, FastAPI):** `POST /v1/predict`, `POST /v1/outcome`, `GET /health`. Modeller `ml/models/` altında düz metin dosyalardır (pickle yok). Düzeltme durumu bellektedir; yeniden başlayınca sıfırlanır. Kimlik doğrulama yok, yalnızca `127.0.0.1`.
- **.NET entegrasyonu:** `POST /v1/flights/context` (AODB taklidi) uçuş bağlamını `flight_context` tablosuna yazar. İniş öncesi bavullar için yolcu sorgusu ML penceresini (`basis: "ML"`) döndürür. İniş sonrası bavullar eski, durum bazlı ampirik ETA'yı kullanır. Servis kapalı ya da bağlam yoksa ETA gösterilmez (eskisi gibi). Biten uçuşların gerçek süreleri `MlOutcomeBackgroundService` ile servise bildirilir.

Bilinen sınırlar:
- Bavul düzeyindeki ML penceresi (ilk bavul P10 → son bavul P90, ortası P50) bir basitleştirmedir ve ayrıca ölçülmemiştir; değerlendirme uçuş düzeyindedir.
- "Biten uçuş" sezgiseldir: bavulların %90'ı banda gelmiş ve sanal saatte 10 dk geçmiş. Kayıp mesajlar son bavulu erken gösterebilir.
- ML servisinin düzeltme durumu tek örnek ve bellektedir.
- Python testleri henüz CI'da çalışmıyor.

## Faz 3 durumu: dayanıklılık (ölçüt karşılandı, broker yok)

`ARCHITECTURE.md` Faz 3 için DLQ, idempotent tüketici, Kafka uyumlu broker ve gerekirse Redis öngörür. Bitti ölçütü "çift gelen ve sırasız mesajlarda tutarlı durum"dur.

- **Karşılananlar:** ölü mektup tablosu (`dead_letter_messages`), mesaj anahtarına dayalı idempotency (eş zamanlı kopyada bile tek kayıt), sırasız mesajda durumun geri gitmemesi (`IGNORED_STALE`), bagaj başına advisory lock, transactional outbox (en az bir kez, sıralı, 5 denemede bekletme). Hepsi entegrasyon testlidir.
- **Yapılmayan: Kafka uyumlu broker.** Bilinçli sapma: olay iletimi Postgres outbox + SignalR ile yapılıyor. `ARCHITECTURE.md` zaten "ilk sürümde altyapı yükü olmadan aynı garanti" diyor. Tek makinede çalışan bu proje için broker kurmak ek işletme yükü getirirdi. Bedeli: outbox dağıtıcısı tek örnek varsayar ve birden çok tüketici grubunu desteklemez.
- **Yapılmayan: Redis.** Plana göre "gerekirse" idi; ölçülmüş bir ihtiyaç çıkmadı.

## Faz 4, ilk dilim: çıkış okuması ve çıkış süresi

Çalışan parçalar:
- **EXIT taraması:** BPM okuma noktası `EXIT01` gibi bir `EXIT` önekiyle gelirse bavul `EXIT_CLEARED` olur. Mevcut durum makinesi kuralları geçerlidir (monoton, banttan alınmadan gelirse sıra dışı sayılır).
- **Çıkış süresi:** banttan alınma olayından 180 sn sonra bekleyen bir kayıt (`exit_deadlines`) açılır. Süre, olayın kendi zamanına bağlıdır (alınma zamanı değil).
- **Süre dolum worker'ı (`ExitDeadlineService`):** süresi dolan ve hâlâ `CLAIMED_FROM_BELT` olan bavulu `EXIT_OVERDUE` yapar. Durum, olay günlüğü kaydı (`message_type = SYSTEM`, tekil anahtar) ve outbox mesajı tek işlemde yazılır; kayıtlar `FOR UPDATE SKIP LOCKED` ile alınır. İkinci çalıştırma ikinci bir olay üretmez.
- **`EXIT_OVERDUE` son durum değildir:** süre dolduktan sonra çıkış okunursa bavul `EXIT_CLEARED` olur.
- **Simülatör:** `--mode live` çıkış taramaları da üretir (`--exit-scans false` ile kapatılır). Bavulların %2'si çıkışta hiç okunmaz, kalanı banttan alındıktan 30 sn + üstel gecikmeyle okunur. Çıkış taramaları ayrı bir rastgele akıştan gelir; CSV ve ML eğitim verisi değişmez.
- **Operasyon panosu:** "Alındı" artık banttan alınan tüm bavulları sayar (çıkış durumları dahil). "Çıkış okundu" ve "Çıkış okunmadı" bunların alt kümeleridir.
- Yolcuya çıkış durumları ayrıca gösterilmez; "banttan alındı" mesajı geçerli kalır.

Tasarımdan farklar ve bilinen sınırlar:
- **`EXIT_CLEARED` burada yalnızca "çıkışta okundu" demektir.** ARCHITECTURE.md'deki anlamı (etiket ve sunulan PNR eşleşti) bu aşamada uygulanmıyor. Yanlış kişi kontrolü (`EXIT_REVIEW`, sessiz inceleme kaydı) Faz 4'ün kalanıdır ve çıkış kapısı sensöründen sunulan PNR'ı gerektirir; tehdit modeli ve KVKK notu da buna dahildir.
- **"Şimdi" sanal saattir:** worker, BSM olmayan olayların en son zamanını kullanır (yolcu ETA'sı ve takılı bavul sayacıyla aynı kabul). Olay akışı durursa saat de durur ve süre dolumları o zamana kadar işlenmez. Örneğin son uçuşun son bavulları, ardından başka olay gelmezse "çıkış okunmadı" olmaz. Gerçek kurulumda gerçek saat kullanılmalıdır.
- Worker ile aynı anda gelen bir çıkış okuması için bavul satırında açık bir kilit yok; sonuç her iki sırada da `EXIT_CLEARED` olur, ama arada fazladan bir `EXIT_OVERDUE` olayı yazılmış olabilir.
- Çıkış süresi sabit 180 sn (`BaggageEventProcessor.ExitDeadline`), yapılandırılabilir değil.
