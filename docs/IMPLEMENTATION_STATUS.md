# AeroBag: Uygulama durumu (koda göre)

`ARCHITECTURE.md` hedef tasarımı anlatır. Bu belge, **şu an çalışan kodun** tasarımdan nerede ayrıştığını ve neyin henüz olmadığını açıkça listeler. Çelişki olursa bu belge koda daha yakındır.

Son güncelleme: 26 Ekim 2026. Kapsam: Faz 1, Faz 2 ve Faz 3 (dayanıklılık, ölçütü karşılandı) tamam; Faz 4'ün ilk dilimi (çıkış okuması ve süre dolumu) tamam. Faz numaraları `ARCHITECTURE.md` bölüm 1.1'dekidir.

| Faz (ARCHITECTURE.md) | Durum |
|---|---|
| 1. Çekirdek | Tamam |
| 2. Makine öğrenmesi | Tamam (bkz. aşağıda ve `EVALUATION.md`) |
| 3. Dayanıklılık | Ölçüt karşılandı; Kafka uyumlu broker yok (bkz. "Faz 3 durumu") |
| 4. Güvenlik ve çıkış doğrulama | Tamam: JWT ve roller, kaynak imzası, yalnızca eklemeli günlük, ExitGuard (bayraklı), yanlış alarm ölçümü, saklama süresi; `THREAT_MODEL.md` ve `KVKK.md` |
| 5. Opsiyonel donanım demosu | Başlanmadı |

## Çalışan parçalar

| Parça | Durum |
|---|---|
| Durum makinesi (monoton, ANOMALY, IGNORED_STALE) | Çalışıyor, birim testli |
| Type B ayrıştırıcı (BSM/BUM/BPM, sıkı sınırlar) | Çalışıyor, birim testli |
| Olay işleme (idempotent, bagaj başına advisory lock, dead-letter) | Çalışıyor, entegrasyon testli |
| Yolcu sorgusu (PNR + soyad, HMAC, tekdüze 404, IP başına 20/dk) | Çalışıyor |
| ETA tahmini (P10/P50/P90): ML_FLIGHT, ML, PHYSICS, HISTORY, FALLBACK | Çalışıyor, canlı doğrulandı (PHYSICS ekranda yakalanmadı) |
| SLA ve gecikme riski (`/v1/flights/sla`, DEMO hedefleri) | Çalışıyor, canlı doğrulandı; olasılıkların kalibrasyonu ölçülmedi |
| Operasyon panosu API'si ve arayüzü | Çalışıyor |
| Outbox → SignalR canlı bildirim | Çalışıyor, entegrasyon testli |
| React arayüzü (yolcu sayfası, operasyon panosu) | Çalışıyor |
| Simülatör (CSV ve canlı mod, tohumlu, gürültü ayarlı) | Çalışıyor |

## Tasarımdan ayrışan ve netleşen noktalar

- **BPM okuma noktası kuralı.** Biçim `[A-Z]{3,10}\d{2}` (iki rakam şart). Önek durumu belirler: `SEC` güvenlik, `LOAD` uçağa yükleme, `CHUTE` şut, `CAROUSEL` bant ağzı, `CLAIM` banttan alma. Bilinmeyen önek reddedilir.
- **Olay zamanı.** BSM'nin zamanı mesajda yoktur, alınma anı kullanılır. BUM ve BPM zamanı mesajdan gelir.
- **ETA tabanı.** Olay geçmişinden `percentile_cont` ile hesaplanır. En az 20 örnek varsa `HISTORY`, yoksa sabit varsayılanlar (`FALLBACK`: iniş→bant 300/540/900 sn, şut→bant 60/90/150 sn). ML kapalı ya da bağlam yokken ETA yalnızca bagaj indirildikten sonra verilir; ML açıkken indirme öncesinde de aralık gösterilir.
- **Referans an (`asOfUtc`).** Simülasyonda "şimdi", BSM olmayan olayların en son zamanıdır (sanal saat). Gerçek kurulumda gerçek saat olmalıdır.
- **Yolcu verisi.** Ad, soyad ve PNR düz metin saklanmaz; yalnızca `HMAC-SHA256(gizli anahtar, PNR|soyad)` arama anahtarı tutulur. Faz 4.3'e kadar olay günlüğündeki ham BSM metni ad ve PNR satırlarını içeriyordu; bu düzeltildi (aşağıda). Geliştirme varsayılan anahtarı kodda durur; gerçek kurulumda `Security__LookupKeySecret` verilmelidir.
- **Bant numarası** saklanmıyor, arayüzde gösterilmiyor (AODB bağlamı yok).
- **Canlı bildirim.** `/hubs/live` kanalı içeriksizdir: `bagChanged` (grup `bag:{etiket}`) ve `flightsChanged` (grup `ops`). İstemci ayrıntıyı REST ile yeniden çeker. Canlı bağlantı varken yoklama 20 sn'ye, yokken 3 sn'ye ayarlıdır.
- **Outbox garantisi.** En az bir kez teslim, `seq` sırası, 5 başarısız denemeden sonra mesaj bekletilir. Tek örnek varsayılır.

## Bilinen eksikler ve riskler

1. **Kimlik doğrulama:** personel ve yolcu tarafı JWT ile (aşağıda "Faz 4.1"), mesaj kaynakları HMAC imzasıyla (aşağıda "Faz 4.2") korunuyor. Olay günlüğü veritabanı düzeyinde yalnızca eklemelidir (aşağıda "Faz 4.3").
2. **Çıkışta yolcu eşleştirmesi yok.** Çıkış okuması ve süre dolumu çalışıyor (aşağıda "Faz 4, ilk dilim"), ama bavul ile sunulan PNR karşılaştırılmıyor; yanlış kişi kontrolü (`EXIT_REVIEW`) yok. Faz 4.
3. **Bantta takılı bagaj tespiti yok.** "Alındı" mesajı düşerse bagaj sonsuza dek `ON_CAROUSEL` kalır; çıkış süresi bunu çözmez çünkü sayaç alınma anında başlar.
4. **AODB bağlamı simülatörden gelir:** gövde tipi, hava, mesafeler ve yer hizmeti firması `POST /v1/flights/context` ile verilir; gerçek bir uçuş bilgi sistemi yok. Bant numarası hâlâ saklanmıyor.
5. **ML ölçümü yalnızca sentetik veride yapıldı.** Uçuş düzeyi ve bavul düzeyi (indirme→bant) sonuçları, rejim kayması ve fizik karşılaştırması `EVALUATION.md`'dedir. Bavul düzeyi modelin kazancı ortak uçuş etkisinden gelir; bavula özgü özellikler katkı sağlamadı. Fizik sonucu dairesel, canlı olarak ekranda yakalanmadı.
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
- **.NET entegrasyonu:** `POST /v1/flights/context` (AODB taklidi) uçuş bağlamını `flight_context` tablosuna yazar. Yolcu sorgusu duruma göre şunu verir: indirme öncesi uçuş düzeyi ML penceresi (`ML_FLIGHT`), indirildikten sonra bavul düzeyi ML (`ML`), şutta fizik (`PHYSICS`). Servis kapalı ya da bağlam yoksa indirme/şut durumlarında eski ampirik ETA'ya (`HISTORY`/`FALLBACK`) düşülür; indirme öncesinde tahmin gösterilmez. Biten uçuşların ve bavulların gerçek süreleri `MlOutcomeBackgroundService` ile servise bildirilir.

Bilinen sınırlar:
- Bavul düzeyindeki ML penceresi (ilk bavul P10 → son bavul P90, ortası P50) bir basitleştirmedir ve ayrıca ölçülmemiştir; değerlendirme uçuş düzeyindedir.
- "Biten uçuş" sezgiseldir: bavulların %90'ı banda gelmiş ve sanal saatte 10 dk geçmiş. Kayıp mesajlar son bavulu erken gösterebilir.
- ML servisinin düzeltme durumu tek örnek ve bellektedir.

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
- **Simülatör:** `--mode live` çıkış taramalarını varsayılan olarak üretir (`--exit-scans false` ile kapatılır). Bavulların %2'si çıkışta hiç okunmaz, kalanı banttan alındıktan 30 sn + üstel gecikmeyle okunur. Çıkış taramaları ayrı bir rastgele akıştan gelir; CSV ve ML eğitim verisi değişmez.
- **Operasyon panosu:** "Alındı" artık banttan alınan tüm bavulları sayar (çıkış durumları dahil). "Çıkış okundu" ve "Çıkış okunmadı" bunların alt kümeleridir.
- Yolcuya çıkış durumları ayrıca gösterilmez; "banttan alındı" mesajı geçerli kalır.

Tasarımdan farklar ve bilinen sınırlar:
- **`EXIT_CLEARED` burada yalnızca "çıkışta okundu" demektir.** ARCHITECTURE.md'deki anlamı (etiket ve sunulan PNR eşleşti) bu aşamada uygulanmıyor. Yanlış kişi kontrolü (`EXIT_REVIEW`, sessiz inceleme kaydı) Faz 4'ün kalanıdır ve çıkış kapısı sensöründen sunulan PNR'ı gerektirir; tehdit modeli ve KVKK notu da buna dahildir.
- **"Şimdi" sanal saattir:** worker, BSM olmayan olayların en son zamanını kullanır (yolcu ETA'sı ve takılı bavul sayacıyla aynı kabul). Olay akışı durursa saat de durur ve süre dolumları o zamana kadar işlenmez. Örneğin son uçuşun son bavulları, ardından başka olay gelmezse "çıkış okunmadı" olmaz. Gerçek kurulumda gerçek saat kullanılmalıdır.
- Worker ile aynı anda gelen bir çıkış okuması için bavul satırında açık bir kilit yok; sonuç her iki sırada da `EXIT_CLEARED` olur, ama arada fazladan bir `EXIT_OVERDUE` olayı yazılmış olabilir.
- Çıkış süresi sabit 180 sn (`BaggageEventProcessor.ExitDeadline`), yapılandırılabilir değil.

## Söz #1: bavul bazlı ETA ve fizik

- Pazar `basis` değerleri: HISTORY, FALLBACK (ampirik), ML_FLIGHT (indirme öncesi uçuş düzeyi), ML (bavul bazlı: indirme anı + model saniyesi), PHYSICS (şutta mesafe / bant hızı ± 15 sn; ML kapalıyken de çalışır).
- Bavul düzeyi model yalnızca uçuş özelliklerini kullanır; indirme sırası ve on-block sonrası süre özellikleri kazanç sağlamadı (bkz. EVALUATION.md, "Bavul düzeyi ETA").
- Fizik sonucu simülatörün aynı formülle üretmesi nedeniyle dairesel; gerçek donanım kanıtı değildir.
- Servis uçları: `/v1/predict-bag`, `/v1/outcome-bags`. Şema değişti: `docker compose down -v` gerekir.

## Söz #2: SLA ve gecikme riski (DEMO hedefleri)

- Uç: `GET /v1/flights/sla`. Uçuş başına ilk ve son bagaj için hedef, gerçekleşen ya da tahmin, ihlal olasılığı, beklenen gecikme (P50/P90) ve durum (MET, ON_TRACK, AT_RISK, LIKELY_BREACH, BREACHED, UNKNOWN).
- Hedefler `flights.sla_first_bag_sec` (900) ve `sla_last_bag_sec` (1800); on-block'tan ölçülür. Rakamlar demo varsayımıdır, yanıtta `assumption: DEMO_ASSUMPTION` yazar.
- İhlal olasılığı: uçuş düzeyindeki modelin P10/P50/P90 kuantillerinden parça parça doğrusal bir dağılım kurulur (yaklaşıklık). Süre geçtikçe "henüz olmadı" koşuluyla güncellenir. Son bagaj, uçuştaki tüm bagajlar banda geçince gerçek değere döner.
- ML kapalıyken tahmin alanları boş, durum UNKNOWN olur; gerçekleşen ya da süresi dolmuş durumlar yine raporlanır.
- ARCHITECTURE 6.2'deki `suggestion` (kaynak artırımı önerisi) uygulanmadı.
- Bu olasılıkların kalibrasyonu (örneğin "%60 dediğimiz uçuşların kaçı gerçekten aştı") henüz ayrıca ölçülmedi.

## Faz 4.1: JWT ile kimlik doğrulama ve roller

- **Roller:** `passenger`, `ops`, `security`. Jetonlar HS256 imzalı, kısa ömürlü (personel 15 dk, yolcu 30 dk), `iss`/`aud`/süre doğrulanır, saat kayması payı 30 sn.
- **Personel girişi:** `POST /v1/auth/token` (IP başına dakikada 10). Parolalar PBKDF2-SHA256 (600.000 yineleme) ile saklanır, karşılaştırma sabit sürelidir. Yanlış kullanıcı adı ve yanlış parola aynı yanıtı verir; bilinmeyen kullanıcıda da aynı maliyette doğrulama yapılır.
- **Yolcu jetonu:** `/v1/lookup` (PNR + soyad) başarılıysa yanıtta yolcu jetonu döner. Jetonda kişisel veri yoktur, yalnızca o sorgudaki bagaj etiketleri vardır.
- **Uç başına politika:** `/v1/flights` ve `/v1/flights/sla` yalnızca personel. `/v1/bags/{etiket}` personel ya da jetonunda o etiket bulunan yolcu; yetkisiz bagaj için 404 (varlığı sızdırılmaz). Canlı kanal (`/hubs/live`) jeton ister; `JoinOps` yalnızca personel, `JoinBags` yalnızca yetkili etiketler.
- **Arayüz:** operasyon panosu giriş formu gösterir; jeton sekme oturumunda (`sessionStorage`) tutulur, 401/403 gelince oturum kapanır.
- **Testler:** parola özeti birim testleri; gerçek API ve PostgreSQL ile yetkisiz erişim, yanlış rol, başka bagaja erişim, değiştirilmiş ve başka anahtarla imzalanmış jeton, canlı kanal grup yetkisi.

Bilinen sınırlar:
- İmza anahtarı verilmezse her açılışta rastgele üretilir; çok örnekli kurulumda ortak anahtar gerekir. Anahtar döndürme (rotation) yok.
- Yenileme jetonu (refresh token) ve jeton iptali yok; jeton süresi dolana kadar geçerlidir. Canlı kanal bağlantısı kurulduktan sonra jeton süresi dolsa da açık kalır.
- WebSocket jetonu sorgu parametresiyle (`access_token`) gelir; ters vekil sunucu kayıtlarında görünebilir.
- `sessionStorage`'daki jeton bir XSS açığında okunabilir; HttpOnly çerez kullanılmadı.
- Demo kullanıcıları ve parolaları kodda ve README'de yazılıdır; yapılandırmada `Auth:Users` verilince devre dışı kalır.

## Faz 4.2: mesaj kaynaklarının imzası

- **Uçlar:** `/v1/events` (kapsam `events`) ve `/v1/flights/context` (kapsam `context`) yalnızca imzalı istek kabul eder. Kapı sensörü için `gate` kapsamı ayrıldı.
- **İmza:** başlıklar `X-AeroBag-Source`, `X-AeroBag-Timestamp` (unix saniye), `X-AeroBag-Nonce`, `X-AeroBag-Signature`. İmzalanan metin `v1\n{zaman}\n{nonce}\n{METHOD}\n{yol}\n{gövdenin SHA-256'sı}`, imza `v1=` + HMAC-SHA256 (kaynağın anahtarı). Biçim dile bağlı değildir; birim testinde Python ile üretilmiş bir örnek doğrulanır.
- **Kontroller:** bilinmeyen kaynak, yanlış anahtar, değiştirilmiş gövde, ±5 dakika dışındaki zaman damgası ve aynı nonce'un ikinci kez kullanılması 401; kaynağın izni olmayan uç 403; 64 KB üstü gövde 413. Hatalı imzada hangi kontrolün başarısız olduğu söylenmez.
- **Kaynak kaydı:** her olay satırında imzası doğrulanan kaynak adı (`baggage_events.source`) saklanır; çıkış süresi worker'ının yazdığı olaylarda `system`. Şema değişti: `docker compose down -v` gerekir.
- **Simülatör:** her isteği yeni bir nonce ile imzalar (`--source-id`, `--source-secret` ya da `AEROBAG_SOURCE_SECRET`). Bilerek gönderilen kopya mesajlar yine API'nin tekilleştirmesine ulaşır. Reddedilen istekler sonuç özetinde `UNAUTHORIZED` / `FORBIDDEN` olarak sayılır.
- **Testler:** imza biçimi, değişen her parçanın imzayı bozması, nonce ve kaynak adı biçimi, simülatör isteğinin doğrulanması (birim); imzasız, yanlış anahtar, bilinmeyen kaynak, değiştirilmiş gövde, zaman penceresi dışı, tekrar oynatma, yeni nonce ile kopya, kapsam dışı uç (entegrasyon, gerçek API).

Bilinen sınırlar:
- Nonce önbelleği bellektedir ve tek örnek varsayar; API yeniden başlarsa son 5 dakikanın nonce'ları unutulur (olaylar yine de idempotent olduğu için tekrar oynatılan olay durumu değiştirmez).
- Ortak gizli anahtar kullanılır (simetrik). Anahtar döndürme ve kaynak başına birden çok anahtar yok. Asimetrik imza ya da mTLS daha güçlü bir seçenektir.
- Demo kaynağının anahtarı kodda ve README'de yazılıdır; yapılandırmada `Ingest:Sources` verilince kabul edilmez.
- Alma uçlarında hız sınırı yok; imzasız istekler gövde okunmadan reddedilir, imzalı ama yoğun gönderime karşı ayrı bir sınır eklenmedi.

## Faz 4.3: yalnızca eklemeli olay günlüğü ve ham metinde kişisel veri

- **Bulunan açık:** olay günlüğü (`baggage_events.raw_text`) ve ölü mektup tablosu, gelen BSM'nin ham metnini saklıyordu; bu metinde yolcu adı (`.P/`) ve PNR (`.L/`) düz metin olarak vardı. Bu, "ad ve PNR düz metin saklanmaz" iddiasıyla çelişiyordu.
- **Düzeltme:** ham metin saklanmadan önce `.P/` ve `.L/` satırları maskelenir (`TypeBRedactor`). Ölü mektup kaydında hata metninden de bu değerler temizlenir. Arama için gereken bilgi zaten HMAC özetindedir.
- **Uygulama rolü:** API artık `aerobag_app` rolüyle bağlanır. Bu rolün hiçbir tabloda DELETE yetkisi yoktur; olay günlüğünde yalnızca SELECT ve INSERT, ölü mektup tablosunda yalnızca INSERT (okuyamaz), şemada tablo oluşturamaz. Diğer tablolarda yalnızca kodun kullandığı işlemler açıktır.
- **Tetikleyici:** olay günlüğünde UPDATE, DELETE ve TRUNCATE, tablo sahibi için de tetikleyiciyle engellenir.
- **Testler:** uygulama rolüyle olay yazılabilir ama değiştirilemez ve silinemez; hiçbir tabloda silme yok; ölü mektuplar okunamaz; tablo sahibi de olay günlüğünü değiştiremez; ham metinde ve ölü mektupta ad ve PNR kalmaz. Ayrıca tüm API testleri artık kısıtlı rolle çalışır, yani yetkiler API'nin işi için yeterlidir.
- Şema değişti: `docker compose down -v` gerekir. Bağlantı dizesi değişti: `Username=aerobag_app;Password=aerobag_app_dev` (yalnızca yerel demo).

Bilinen sınırlar:
- Süper kullanıcı tetikleyiciyi kapatabilir ya da tabloyu silebilir. Bu katman kazara ya da uygulama üzerinden yapılan değişikliği engeller; veritabanı yöneticisine karşı koruma değildir. Ona karşı olay zinciri özeti (hash chain) ya da harici, değiştirilemez bir kayıt deposu gerekir; yapılmadı.
- Daha önce oluşturulmuş veritabanlarındaki eski ham metinler bu değişiklikle temizlenmez; demo verisi olduğu için `docker compose down -v` ile sıfırlanır.
- Saklama süresi dolan kişisel verinin silinmesi (KVKK) için uygulama rolüne ileride yalnızca o işe özel bir yetki verilmesi gerekecek.

## Faz 4.4 (ilk dilim): ExitGuard arka ucu

- **Uç:** `POST /v1/gate/scans` (imzalı, kapsam `gate`). Paket ARCHITECTURE.md 4.3'teki gibidir: kapı, zaman, okunan etiketler (en çok 10), sunulan PNR, yöntem, sinyal kalitesi. Özellik bayrağı `ExitGuard:Enabled` (varsayılan açık); kapalıyken uç yoktur (404) ve eski çıkış okuması (BPM `EXIT`) aynen çalışır.
- **Kural (saf fonksiyon, `ExitGuardRules`):** sunulan PNR'ın özeti bavulun yolcusunun PNR özetiyle aynıysa `CLEARED_OWNER` (bavul `EXIT_CLEARED`). Eşleştirme PNR düzeyindedir: aynı rezervasyondaki yolcular birbirinin bavulunu alabilir, soyad karşılaştırılmaz. Uyuşmazsa `REVIEW_MISMATCH` (bavul `EXIT_REVIEW`, sessiz inceleme kaydı; kapıda alarm yok). Doğrulama yoksa, sinyal 0,6'nın altındaysa ya da bavulun sahip bilgisi yoksa `NO_DECISION`: durum değişmez, alarm üretilmez, çıkış süresi işlemeye devam eder. Bant öncesi bavul `OUT_OF_SEQUENCE`, sistemde olmayan etiket `UNKNOWN_TAG` (ikisi de inceleme kaydı, durum değişmez). Kapanmış bavul `ALREADY_CLOSED`.
- **Saklanan veri:** sunulan PNR ve soyad hiçbir tabloya yazılmaz; yalnızca bellekte HMAC özeti alınıp karşılaştırılır. Yolcu tablosunda PNR'ın ayrı bir HMAC özeti (`pnr_key`, arama anahtarından farklı önekle) tutulur. Her okuma kararıyla birlikte `gate_reads`'e (yalnızca eklemeli, denetim izi), durum değiştiren kararlar olay günlüğüne ve outbox'a yazılır. Aynı paket ikinci kez gelirse `DUPLICATE`.
- **Güvenlik ekranı uçları:** `GET /v1/security/reviews` (açık kayıtlar; `?status=all` tümü) ve `POST /v1/security/reviews/{id}/resolve` (`CONFIRMED` ya da `FALSE_ALARM`, not en çok 200 karakter). Yalnızca `security` rolü; `ops` rolü 403 alır. Kayıtta kişisel veri yoktur: etiket, uçuş, kapı, zaman, gerekçe, kimin sonuçlandırdığı.
- **Testler:** karar kuralı (birim); sahip, aynı rezervasyon, başkası, zayıf sinyal ve doğrulamasız okuma, bilinmeyen etiket, bant öncesi okuma, tekrar gelen paket, imza ve kapsam, geçersiz paket, bayrak kapalı, sunulan PNR'ın hiçbir tabloda bulunmaması, inceleme kaydını yalnızca güvenliğin görüp sonuçlandırabilmesi (entegrasyon).

Bilinen sınırlar:
- Kuralın kendisi deterministiktir; yanlış alarm ve kaçırma oranları sensörün okuma hatalarına bağlıdır. Bunlar sonraki dilimde simülatörle ölçülecek ve yalnızca varsayılan hata oranları altında geçerli olacak.
- PNR'ı paylaşan herkes (aynı rezervasyon) birbirinin bavulunu çıkarabilir; bu bilinçli bir kuraldır ve PNR'ı ele geçiren biri için açık bir yoldur.
- İnceleme kayıtları canlı kanaldan bildirilmiyor; güvenlik ekranı 5 sn'de bir yoklar.
- Şema değişti: `docker compose down -v` gerekir.

## Faz 4.4 (ikinci dilim): kapı paketleri, yanlış alarm ölçümü, güvenlik ekranı

- **Simülatör:** canlı modda çıkış okumaları artık varsayılan olarak ExitGuard kapı paketidir (`--exit-mode bpm` ile eski BPM EXIT). Sensör modeli `GateModel.cs`'tedir ve bütün oranlar demo varsayımıdır: bavulların %25'i bir öncekiyle aynı rezervasyonda, grup bavulunun %30'unu gruptaki başka biri çıkarır, %0,5'ini rezervasyonla ilgisi olmayan biri çıkarır, %2'si hiç okunmaz, %3'ünde doğrulama yoktur, okumaların %15'i zayıf sinyallidir (0,20-0,75), zayıf okumaların %30'unda PNR'ın bir karakteri yanlış okunur, %1'inde yan koridordaki bir bavul da pakete karışır. Bu modda yalnızca PNR'lar ve çıkış mesajları değişir; bavul zaman çizelgesi ve CSV/ML verisi aynıdır (birim testi).
- **Ölçüm:** `--mode gate-eval` API'siz, çok sayıda uçuşta (varsayılan 200) aynı karar kuralını (`ExitGuardRules`) çalıştırır ve farklı sinyal eşiklerinde yanlış alarm, kararsız okuma ve yakalanan uyuşmazlık sayılarını raporlar. Canlı modda simülatör, API'nin her etiket için verdiği kararı gerçek durumla birlikte sayar. Sonuçlar `EVALUATION.md`'dedir: varsayılan eşikte (0,60) yanlış alarm %1,38, kararsız okuma %13,5, başkasının yakalanması %87, başkasının sahip diye geçmesi 0. Açılan incelemelerin yalnızca yaklaşık %24'ü gerçek vaka (taban oran etkisi).
- **Arayüz:** güvenlik rolüyle girilen panoda "Çıkış inceleme kayıtları" bölümü (gerekçe, uçuş, kapı, zaman; "Yanlış alarm" ve "Doğrulandı" düğmeleri, isteğe bağlı not). Operasyon tablosunda "İncelemede" sütunu. Yolcu sayfası `EXIT_REVIEW` durumunda ARCHITECTURE.md 6.3'teki bilgilendirme mesajını gösterir (alarm dili yok).

Bilinen sınırlar:
- Yanlış alarm ve kaçırma oranları sensör varsayımlarının sonucudur; kural doğru çalışıyor mu sorusunu yanıtlar, sahadaki oranı vermez (Faz 5'te ölçülmesi gerekir).
- Yan koridordan okunan bavul, sahibi henüz çıkmadıysa `EXIT_REVIEW` olur; gerçek sahibi daha sonra geçtiğinde bavul zaten kapanmış sayılır. Bu bir yanlış alarmın gerçek maliyetidir ve inceleme kaydıyla personele bırakılır.

## Faz 4.5: tehdit modeli, KVKK notu ve saklama süresi

- **`docs/THREAT_MODEL.md`:** STRIDE tablosu; her tehdit için önlem, onu doğrulayan test ve açık kalanlar. Otomatik testi olmayan önlemler (hız sınırı, 413, canlı kanalın içeriksizliği, adres kayıtlarının kapatılması) açıkça "otomatik test yok" ya da "kod incelemesi" diye işaretlendi.
- **`docs/KVKK.md`:** hangi verinin nereye gittiği, uygulanan ilkeler, açık kalanlar. Hukuki danışmanlık değildir.
- **Bulunan ikinci açık:** yolcu sorgusu `GET /v1/lookup?pnr=...&surname=...` idi ve ASP.NET Core istek adresini sorgu parametreleriyle konsola yazıyordu; PNR ve soyad API kayıtlarına düşüyordu. Sorgu `POST /v1/lookup` (JSON gövde) oldu ve istek adresi kayıtları uyarı düzeyine çekildi (bu, canlı kanalın `access_token`'ını da kayıtlardan çıkarır).
- **Saklama süresi:** `RetentionService` saatte bir, bütün bavulları teslim edilmiş ve son güncellemesinden 30 gün geçmiş yolcuların `lookup_key` ve `pnr_key` değerlerini rastgele değerle değiştirir (`Retention:PersonalDataDays`). Uygulama rolü yolcu satırında yalnızca bu sütunları güncelleyebilir (sütun düzeyi yetki). Testler: süre dolunca yolcu bulunamaz, süre dolmadan ve teslim edilmemiş bavulda dokunulmaz, ikinci çalıştırma tekrar değiştirmez, uygulama rolü başka sütunu değiştiremez.
- Şema değişti: `docker compose down -v` gerekir.

