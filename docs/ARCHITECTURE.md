# AeroBag: Havayolu için Bavul Takibi, ETA/SLA Tahmini ve Kayıp Bavul Uyarısı
## Mimari ve Veri Akışı Tasarım Dokümanı (v2.2)

> **Not:** Bu belge hedef tasarımdır. Şu an çalışan kodun tasarımdan ayrıştığı noktalar ve bilinen eksikler için [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) dosyasına bakın.

**Durum:** v2.2 taslak. Simülasyon verisiyle çalışan bir mühendislik portfolyo projesidir. Gerçek kurum verisi, gerçek yolcu verisi ya da canlı havalimanı sistemleri kullanılmaz.

Bu doküman bir bagaj yolculuğu platformunun yazılım mimarisini, veri akışını, girdi/çıktı sözleşmelerini, değerlendirme yöntemini ve güvenlik yaklaşımını tanımlar. Dokümandaki tüm firma, yolcu, PNR ve mesaj örnekleri **kurgusaldır**.

**Konumlandırma (v2.2, 31 Ekim 2026):** sistem bir **havayolunun** bavul takip sisteminin **tek istasyon modülüdür**. Bavul kaybolduğunda sorumlu olan ve yolcunun başvurduğu taraf havayoludur, yolcu verisi (PNR) onun sistemindedir ve IATA Resolution 753 bavul takibini havayollarından ister. Taraflar:

| Taraf | Rolü |
| --- | --- |
| Havayolu (istasyon operasyonu, hub kontrol) | Kullanıcı: yüklenmeyen, kaybolan ve bağlantıya yetişmeyen bavullar; ilk/son bavul teslim hedefi |
| Yolcu | Son kullanıcı: kendi bavulunun adımları ve tahmini bant süresi |
| Yer hizmetleri firması | Veri sağlayan taraf: okumalar (BPM/BUM) ve kilometre taşları; teslim hedefi onun sözleşmesidir |
| Havalimanı işletmesi | Veri sağlayan taraf: AODB bağlamı (bant, park yeri); isterse çıkış doğrulama modülünü (ExitGuard) kurar |

Ağa genişleme (hub ve istasyonlar, istasyon başına okuma kapsamı, aktarma bavulunun varış tarafı) bu sürümün kapsamı dışındadır; sektörden gelecek geri bildirime göre ele alınacaktır.

---

## 0. Amaç, kapsam ve iddia politikası

### 0.1 Amaç

1. Bavul olaylarından (check-in, yükleme, indirme, şut, bant, çıkış) bavul bazlı durum takibi yapmak.
2. Yolcuya "bavulunuz yaklaşık ne zaman bantta olacak" bilgisini **aralık** olarak göstermek.
3. Operasyon ekibine bant teslim süresi (SLA) ve gecikme risk panosu sunmak.
4. Uçağa yüklenmeyen, varışta indirilmeyen ve bağlantıya yetişmeyen bavulu tespit edip personele ve yolcuya bildirmek (Faz 6).
5. **İsteğe bağlı olarak**, havalimanının kurabileceği bir çıkış kapısında bavul ve yolcu eşleşmesini kontrol eden, **sessiz uyarı** üreten bir modül (ExitGuard) sunmak.

### 0.2 Kapsam dışı

- Gerçek RFID donanımı ve sahada ölçüm (Faz 5'te isteğe bağlı demo).
- Mobil uygulama (yolcu arayüzü mobil uyumlu web sayfasıdır).
- Gerçek yolcu verisi, gerçek kurum verisi, canlı sistemlere bağlantı.
- Ödeme ve karmaşık kullanıcı yönetimi.
- Çok istasyonlu ağ, havayolları arası (interline) bavul ve kayıp bavul dosyasının açılıp yönetilmesi (WorldTracer benzeri süreç). Sistem kayıp bavulu tespit eder, sonrasındaki süreci yönetmez.

### 0.3 İddia politikası

- Model, **kendi simülatörünün** ürettiği veriyle eğitilir ve sınanır. "Gerçek dünyada şu kadar doğru" iddiası yapılmaz.
- Mesaj formatları, IATA Type B (BSM/BPM/BUM) ve IATA Resolution 753 takip noktalarından **esinlenen, basitleştirilmiş** biçimlerdir. IATA BIX ya da başka bir standartla **tam uyumluluk iddia edilmez**.
- SLA hedefleri ve maliyet rakamları gerçek sözleşme değil, **demo varsayımıdır**.
- Proje özgeçmişte "simülasyon verisiyle doğrulanmış prototip" olarak tanımlanır.

---

## 1. Fazlar ve teknoloji yığını

### 1.1 Fazlar

| Faz | Süre (tahmini) | İçerik | Bitti sayılır ölçütü |
| --- | --- | --- | --- |
| **1. Çekirdek** | Hafta 1–5 | Type B ayrıştırıcı, durum makinesi, olay günlüğü + outbox, tek simülatör, medyan taban çizgisi ETA, yolcu sayfası, operasyon panosu | Simüle edilen bir uçuş uçtan uca izlenebiliyor, testler yeşil |
| **2. Makine öğrenmesi** | Hafta 6–8 | LightGBM kantil servisi (iki aşamalı model), değerlendirme raporu | Taban çizgisine karşı karşılaştırma tablosu ve dağılım kayması testi |
| **3. Dayanıklılık** | Hafta 9–10 | Kafka uyumlu broker, ölü mektup kuyruğu (DLQ), idempotent tüketici, gerekirse Redis önbellek | Çift gelen ve sırasız mesajlarda tutarlı durum |
| **4. Güvenlik ve çıkış doğrulama** | Hafta 11–12 | ExitGuard (özellik bayrağıyla), tehdit modeli, KVKK notu | Tehdit modeli ve sızma/yanlış alarm testleri raporda |
| **5. Opsiyonel** | Sonrası | Raspberry Pi + RFID çıkış kapısı demosu | Sahada çapraz okuma ölçümü |

Süreler haftada 8–10 saat çalışma varsayımıyla tahminidir.

### 1.2 Teknoloji yığını

| Katman | Faz 1 | Sonraki fazlar | Gerekçe |
| --- | --- | --- | --- |
| Simülasyon ve veri üretimi | .NET 10 (LTS) Worker Service (tek simülatör) | Aynı | Hem canlı akışı hem eğitim veri setini (CSV) üretir; iki ayrı üretici çıkmaz. .NET 9, 10 Kasım 2026'da desteğini bitirdiği için LTS seçildi (.NET 10: Kasım 2028'e kadar) |
| Çekirdek backend | ASP.NET Core 10 (LTS), modüler monolit | Gerekirse servislere ayrılır | Ayrıştırıcı, durum makinesi, SLA kuralları, ExitGuard tek kod tabanında test edilir |
| Veritabanı | PostgreSQL 16 (ilişkisel + JSONB) | Aynı | Olay günlüğü, outbox ve zaman aşımı tablosu tek yerde, işlemsel olarak tutarlı |
| Olay iletimi | Transactional outbox + süreç içi dağıtıcı | Faz 3: Kafka uyumlu broker (Kafka ya da Redpanda) | İlk sürümde altyapı yükü olmadan aynı garanti; topic adları önceden belirli |
| Makine öğrenmesi | Medyan taban çizgisi (.NET içinde) | Faz 2: Python 3.11 + FastAPI + LightGBM, HTTP/JSON | Basit sözleşme; gerekirse sonra gRPC |
| Önbellek | Yok | Faz 3+: Redis 7 (yalnızca okuma önbelleği) | Güvenlik alarmı ya da doğruluk kaynağı olarak kullanılmaz |
| Canlı yayın | ASP.NET Core SignalR | Aynı | Yolcu ve operasyon ekranlarına anlık güncelleme |
| Arayüz | React 18 + TypeScript + Tailwind CSS | Aynı | Yolcu sayfası ve operasyon panosu |
| Loglama | Serilog + Seq | OpenTelemetry (isteğe bağlı) | CorrelationId ile yapısal log |
| Test ve CI | xUnit, Testcontainers, GitHub Actions | pytest (ML) | Her değişiklikte otomatik test |
| Paketleme | Docker Compose | Aynı | Tek komutla çalışır |

---

## 2. Mimari ve veri akışı

### 2.1 Faz 1 akışı

```
Simülatör ──(Type B metin)──▶ Alma API'si ──▶ Ayrıştırıcı ──▶ Durum makinesi
                                  │ ayrıştırılamayan               │
                                  ▼                                ▼
                       dead_letter_messages        PostgreSQL: baggage_events
                                                   + bags (durum) + outbox_messages
                                                   + exit_deadlines
                                                                   │ outbox dağıtıcı
                                                                   ▼
                                       SignalR ──▶ Yolcu sayfası / Operasyon panosu
                                       ETA servisi (Faz 2, HTTP/JSON) ◀── olay: UNLOADED / CHUTE
```

### 2.2 Tasarım ilkeleri

1. **Olay günlüğü doğruluk kaynağıdır.** Durum, olaylardan türetilir; her olay ham metniyle birlikte saklanır ve yeniden oynatılabilir.
2. **Transactional outbox.** Durum güncellemesi ve dış dünyaya gidecek mesaj aynı veritabanı işleminde yazılır; ayrı bir dağıtıcı outbox'ı okur.
3. **Idempotency.** Aynı mesaj ikinci kez gelirse durum değişmez.
4. **Sırasız olay.** Geç gelen eski olay günlüğe yazılır ama durumu geriye almaz.
5. **Alarmlar bellekte değil, kalıcı depoda yaşar.** Zaman aşımları veritabanında tutulur (bkz. 7).

---

## 3. Bagaj yaşam döngüsü ve durum makinesi

### 3.1 Durumlar

Demoda tek platform uçtan uca olayları alır. Gerçekte bu olaylar farklı kurumların sistemlerinden gelir.

| Sıra | Durum | Tetikleyen olay | Yolcu ekranı | Operasyon panosu |
| --- | --- | --- | --- | --- |
| 1 | `CHECKED_IN` | Check-in'de bagaj teslimi (BSM) | "Bagajınız teslim alındı." | Uçuşun beklenen bagaj sayacı artar |
| 2 | `SECURITY_SCREENED` | Güvenlik taraması geçişi (BPM) | "Bagajınız güvenlik kontrolünden geçti." | Ayrıştırma hattı yükü güncellenir |
| 3 | `LOADED_ON_AIRCRAFT` | Uçağa yükleme (BPM) | "Bagajınız uçağa yüklendi." | Yüklenen / beklenen oranı eksikse kalkış öncesi uyarı |
| 4 | `UNLOADED_AT_ARRIVAL` | Varışta uçaktan indirme (BUM) | "Bagajınız uçaktan indirildi. Tahmini bekleme: yaklaşık 8–12 dk." | ETA tahmini tetiklenir; ilk bavul SLA sayacı işler |
| 5 | `INBOUND_CHUTE` | Şuta giriş (BPM) | "Bagajınız banda verilmek üzere. Yaklaşık 1–2 dk içinde 4. bantta olacak." | Şut→bant mesafesinden aralık daralır |
| 6 | `ON_CAROUSEL` | Bant ağzı okuyucudan geçiş | "Bagajınız 4. bantta, teslim alabilirsiniz." | İlk bavulsa `first_bag_actual_sec`, son bavulsa `last_bag_actual_sec` kaydedilir |
| 7 | `CLAIMED_FROM_BELT` | Bavulun banttan alındığı bilgisi | "Bagajınız banttan alındı." | `exit_deadlines` kaydı açılır (varsayılan 180 sn, havalimanına göre ayarlanabilir) |
| 8A | `EXIT_CLEARED` | Çıkışta etiket ve PNR eşleşti | "İyi yolculuklar." | Yaşam döngüsü kapanır |
| 8B | `EXIT_REVIEW` | Çıkışta etiket ve sunulan PNR uyuşmadı | Bilgilendirme (bkz. 6.3) | Güvenliğe **sessiz** inceleme kaydı |
| 8C | `EXIT_OVERDUE` | `exit_deadlines` süresi doldu, çıkış okunmadı | Bilgilendirme yok | Operasyon ekranına "çıkış okunmadı" kaydı |

`EXIT_REVIEW` ve `EXIT_OVERDUE` bir sonuç değil, **inceleme gerektiren durumdur**. Bavulun etiketi koparılmış, salonda unutulmuş ya da okuyucu kaçırmış olabilir; sistem bunların hangisi olduğunu bilemez.

### 3.2 Geçiş kuralları

- Durum sırası **monoton**dur: bir olay mevcut durumdan daha geride bir duruma geçiriyorsa `IGNORED_STALE` olarak günlüğe yazılır, durum değişmez.
- Bir ya da birkaç ara durumun atlanmasına izin verilir (okuyucu kaçırması); atlanan adım `ANOMALY` olarak işaretlenir.
- Aynı `idempotency_key` ikinci kez gelirse tekrar işlenmez (bkz. 7).

### 3.3 İstisnai durumlar

| Durum | Davranış |
| --- | --- |
| **Aktarma bavulu** | W10a'dan beri iki bağlı satır: gelen uçuştaki satır indirildikten sonra `TRANSFERRED` ile kapanır; bağlantı uçuşundaki satır aktarma BSM'siyle açılır, `TRANSFER_SORTED` ve `LOADED_ON_AIRCRAFT` adımlarından geçer (güvenlik okuması isteğe bağlı). Bavul bu havalimanında banda gelmez, çıkış kuralları uygulanmaz (ayrıntı: CHANGELOG, W10a) |
| **Yüklenmemiş bavul** | `CHECKED_IN` var, uçuş kalktı ama `LOADED_ON_AIRCRAFT` yok → `ANOMALY: NOT_LOADED` |
| **Yolcu uçağa binmedi** | Bavul uçaktan indirilir (no-show) → durum `OFFLOADED_NO_SHOW`, ETA üretilmez |
| **Varışta eksik bavul** | Uçuşun tüm bavulları banta çıktı ama bu bavul çıkmadı → `ANOMALY: MISSING_AT_ARRIVAL`, operasyon ekranına kayıt |
| **Çift gelen mesaj** | Tekilleştirilir, ikinci kopya durumu değiştirmez |
| **Sırasız mesaj** | Günlüğe yazılır, durum geriye gitmez |
| **Ayrıştırılamayan mesaj** | `dead_letter_messages` tablosuna yazılır |

---

## 4. Sistem girdileri

Örnek mesajlar basitleştirilmiş ve kurgusaldır. Gerçek Type B alan düzeni için resmî IATA mesaj spesifikasyonuna bakılmalıdır; bu projede ayrıştırıcı yalnızca aşağıdaki biçimi destekler.

### 4.1 Girdi 1: Type B esinli bagaj mesajları

**BSM (check-in):**

```
BSM
.V/1LAYT
.F/ZZ0124/22OCT/AYT/Y
.N/0891458921001
.W/K/1/18
.P/YILMAZ/AYSE
.L/AB3K7Q
ENDBSM
```

- `.V/1LAYT`: Mesaj sürümü ve havalimanı kodu (AYT).
- `.F/ZZ0124/22OCT/AYT/Y`: Uçuş numarası, tarih, varış noktası, kabin sınıfı.
- `.N/0891458921001`: 10 haneli bagaj etiket numarası (`0891458921`) ve ardışık parça numarası (`001`).
- `.W/K/1/18`: Ağırlık birimi (kg), parça adedi, ağırlık.
- `.P/YILMAZ/AYSE`: Yolcu soyadı/adı (kurgusal).
- `.L/AB3K7Q`: PNR (kurgusal).

**BUM (uçaktan indirme):**

```
BUM
.V/1LAYT
.F/ZZ0124/22OCT/AYT
.N/0891458921001
.U/STAND104/20261022T211410Z
ENDBUM
```

- `.U/STAND104/...`: Bavulun indirildiği park pozisyonu ve UTC zaman damgası.

**BPM (şut ve bant okuması):**

```
BPM
.V/1LAYT
.J/S/CHUTE04/22OCT/212100Z
.F/ZZ0124/22OCT/AYT
.N/0891458921001
.Q/CAROUSEL04/BELT_ENTRY
ENDBPM
```

- `.J/S/CHUTE04/...`: Okuma noktası ve zamanı.
- `.Q/CAROUSEL04/BELT_ENTRY`: Hedef bant ve nokta tipi.

### 4.2 Girdi 2: Uçuş ve havalimanı operasyon bağlamı (kurgusal AODB JSON)

```json
{
  "flightId": "ZZ0124-20261022",
  "airlineCode": "ZZ",
  "aircraftType": "B738",
  "wideBody": false,
  "originAirport": "FRA",
  "destinationAirport": "AYT",
  "isInternational": true,
  "actualOnBlockUtc": "2026-10-22T21:08:00Z",
  "parkingStand": "STAND_104",
  "standDistanceToTerminalMeters": 1450,
  "assignedCarousel": "CAROUSEL_04",
  "chuteToCarouselMeters": 48,
  "conveyorSpeedMps": 0.55,
  "totalCheckedBags": 164,
  "groundHandler": "HANDLER_A",
  "weatherCondition": "RAIN",
  "arrivalsLast30Min": 5
}
```

`handlerShiftLoadFactor` gibi gerçekte erişilemeyecek değişkenler **yalnızca simülasyona özgü** kabul edilir ve model değerlendirmesinde ayrı bir "simülasyona özgü özellikler" kümesi olarak etiketlenir.

### 4.3 Girdi 3: Çıkış kapısı sensör paketi (Faz 4)

```json
{
  "gateId": "ARRIVAL_EXIT_GATE_B",
  "timestampUtc": "2026-10-22T21:26:45Z",
  "detectedBagTags": ["0891458921", "0891458922"],
  "passengerVerification": {
    "method": "SIMULATED_BOARDING_PASS",
    "presentedPnr": "AB3K7Q",
    "passengerSurname": "YILMAZ",
    "signalQuality": 0.92
  }
}
```

Telefonla bavul eşleştirmesi için kullanılacak yöntemin (BLE, QR, kontrollü şerit) kalabalık ortamda güvenilirliği Faz 5'te ölçülmeden iddia edilmez.

---

## 5. İç veri dönüşümü ve sözleşmeler

### 5.1 Dahili olay şeması: `BaggageEvent v1`

`TypeBMessageParser`, ham metni aşağıdaki dahili şemaya çevirir. Bu şema Type B'den esinlenmiştir, herhangi bir standardın uyumlu uygulaması değildir.

```json
{
  "eventId": "7b4f6a0e-3d1c-4d9a-8d5e-2f6c1a9b0e11",
  "idempotencyKey": "sim|ZZ0124|0891458921001|BPM|2026-10-22T21:21:00Z|CHUTE04",
  "correlationId": "corr_0891458921_20261022",
  "occurredAtUtc": "2026-10-22T21:21:00Z",
  "receivedAtUtc": "2026-10-22T21:21:01Z",
  "messageType": "BPM",
  "source": "simulator",
  "baggage": {
    "bagTagNumber": "0891458921",
    "sequenceNumber": "001",
    "weightKg": 18.0,
    "targetState": "INBOUND_CHUTE"
  },
  "passenger": {
    "lookupKey": "hmac:6f3a...c9",
    "cabinClass": "Y"
  },
  "flight": {
    "flightNumber": "ZZ0124",
    "flightDate": "2026-10-22",
    "airportCode": "AYT",
    "assignedCarousel": "CAROUSEL_04"
  },
  "checkpoint": { "scannerId": "CHUTE04", "zone": "MAKEUP_TO_RECLAIM" }
}
```

Yolcu adı, soyadı ve PNR dahili şemada **düz metin taşınmaz**; yalnızca doğrulama için HMAC özeti (`lookupKey`) bulunur (bkz. 9).

### 5.2 Kafka topic tasarımı (Faz 3)

Faz 1'de aynı akış outbox + süreç içi dağıtıcıyla çalışır; topic adları ve anahtarlar önceden sabitlenmiştir.

| Topic | Anahtar | Üretici | Tüketici | Tutma süresi |
| --- | --- | --- | --- | --- |
| `baggage.raw.typeb` | `flightNumber` | Simülatör | Alma API'si | 24 saat |
| `baggage.events` | `bagTagNumber` | Outbox dağıtıcı | ETA orkestratörü, SLA izleyici, ExitGuard | 7 gün |
| `baggage.predictions.eta` | `flightId` | ETA orkestratörü | SignalR bildirim hub'ı | 12 saat |
| `baggage.review.exit` | `gateId` | ExitGuard | SignalR operasyon hub'ı | 30 gün (denetim izi) |
| `baggage.deadletter` | `source` | Alma API'si | İnceleme aracı | 14 gün |

### 5.3 ETA servisi sözleşmesi (Faz 2, HTTP/JSON)

Python servisi FastAPI ile sunulur; OpenAPI dokümanı otomatik üretilir. gRPC'ye geçiş, HTTP gecikmesi ölçülüp sorun çıkarsa değerlendirilir.

`POST /v1/predict`

```json
{
  "flightId": "ZZ0124-20261022",
  "bagTagNumber": "0891458921",
  "currentState": "UNLOADED_AT_ARRIVAL",
  "features": {
    "aircraftType": "B738",
    "wideBody": false,
    "airlineCode": "ZZ",
    "groundHandler": "HANDLER_A",
    "parkingStand": "STAND_104",
    "standDistanceMeters": 1450,
    "chuteToCarouselMeters": 48,
    "conveyorSpeedMps": 0.55,
    "totalFlightBags": 164,
    "bagUnloadSequenceIndex": 37,
    "hourOfLocalDay": 0,
    "arrivalsLast30Min": 5,
    "weatherCondition": "RAIN",
    "elapsedSecondsSinceOnBlock": 372
  },
  "sla": { "firstBagTargetSec": 900, "lastBagTargetSec": 1800 }
}
```

Yanıt:

```json
{
  "bagTagNumber": "0891458921",
  "model": { "name": "eta-lgbm-quantile", "version": "0.1.0" },
  "basis": "ML",
  "remainingSeconds": { "p10": 420, "p50": 540, "p90": 780 },
  "carouselArrivalUtc": {
    "p10": "2026-10-22T21:21:00Z",
    "p50": "2026-10-22T21:23:00Z",
    "p90": "2026-10-22T21:26:00Z"
  },
  "slaFirstBagBreachProbability": 0.12,
  "projectedLastBagDelaySec": { "p50": 120, "p90": 300 }
}
```

### 5.4 Hibrit hesaplama (ML + fiziksel kural)

- **Bavul apronda (`UNLOADED_AT_ARRIVAL`):** İki aşamalı LightGBM kantil modeli (bkz. 5.5).
- **Bavul şutta (`INBOUND_CHUTE`):** Belirsizlik azalır. Süre, `chuteToCarouselMeters / conveyorSpeedMps` (örn. 48 / 0,55 ≈ 87 sn) ile hesaplanır; yine de bir tolerans eklenir (örn. ±15 sn) ve yolcuya **aralık** gösterilir, `basis` alanı `PHYSICS` olur.

### 5.5 İki aşamalı model

| Model | Hedef | Kullanım |
| --- | --- | --- |
| **Model 1: ilk bavul** | `ilk bavulun bantta göründüğü an − actualOnBlock` | Uçak yerleştikten sonra ilk bavulun ne zaman geleceği |
| **Model 2: yükleme süresi** | `son bavul − ilk bavul` | Son bavulun ne zaman geleceği |

Her model LightGBM `objective="quantile"` ile P10, P50, P90 için ayrı eğitilir. Bavul bazlı tahmin, bavulun indirme sırası ve bantta o ana kadar görünen bavul sayısıyla bu iki aralıktan türetilir. Münih Havalimanı verisiyle yapılan çalışma da ilk bavul ve ilk-son bavul farkı olarak iki aralık tahmin eder ve bilerek biraz fazla tahmin eden bir strateji kullanır. Bu projede gösterilen yüzdelik (örn. P75 ya da P90) **ayarlanabilir** bir parametredir.

---

## 6. Sistem çıktıları

### 6.1 Çıktı 1: Yolcu canlı takip (REST + SignalR `BagStatusUpdated`)

```json
{
  "flightNumber": "ZZ0124",
  "origin": "FRA",
  "destination": "AYT",
  "assignedCarousel": "4",
  "bags": [
    {
      "bagTagLast4": "1458921",
      "weightKg": 18.0,
      "currentState": "INBOUND_CHUTE",
      "statusTitleTr": "Bagajınız banda verilmek üzere",
      "statusDescriptionTr": "Yaklaşık 1–2 dakika içinde 4 numaralı bantta olacak.",
      "estimatedRemainingSeconds": { "low": 70, "high": 120 },
      "isRangeEstimate": true,
      "timeline": [
        { "step": "CHECKED_IN", "timeLocal": "17:40", "completed": true },
        { "step": "LOADED_ON_AIRCRAFT", "timeLocal": "18:35", "completed": true },
        { "step": "UNLOADED_AT_ARRIVAL", "timeLocal": "21:14", "completed": true },
        { "step": "INBOUND_CHUTE", "timeLocal": "21:21", "completed": true },
        { "step": "ON_CAROUSEL", "timeLocal": null, "completed": false }
      ]
    }
  ]
}
```

Yolcuya **ad ve tam PNR geri gösterilmez**; bavul etiketi de kısaltılmış gösterilir. Tahmin tutmazsa sayfa "güncel tahmin değişti" mesajı gösterir.

### 6.2 Çıktı 2: Operasyon SLA panosu (`FlightSlaUpdated`)

```json
{
  "flightId": "ZZ0124-20261022",
  "flightNumber": "ZZ0124",
  "parkingStand": "STAND_104",
  "carousel": "4",
  "groundHandler": "HANDLER_A",
  "baggageCounters": {
    "totalChecked": 164,
    "unloadedFromAircraft": 112,
    "onCarousel": 45,
    "claimedAndExited": 18
  },
  "slaMetrics": {
    "assumption": "DEMO_ASSUMPTION",
    "firstBagTargetSec": 900,
    "firstBagActualSec": 810,
    "firstBagStatus": "COMPLIANT",
    "lastBagTargetSec": 1800,
    "projectedLastBagSec": { "p50": 1900, "p90": 2150 },
    "slaBreachProbability": 0.65,
    "lastBagStatus": "AT_RISK"
  },
  "suggestion": "Stand 104 → Şut 04 hattında son bavullar için kaynak artırımı değerlendirilebilir."
}
```

Maliyet/ceza tutarı gösterilmez; sözleşme koşulları bilinmediği için uydurma rakam üretilmez. `suggestion` bir **öneridir**, komut değildir.

### 6.3 Çıktı 3: Çıkış kapısı doğrulama (`GateVerificationResult`, Faz 4)

| Çıktı | Tetiklenme koşulu | Kapı tepkisi | Operasyon/güvenlik çıktısı | Yolcuya bildirim |
| --- | --- | --- | --- | --- |
| `CLEARED_OWNER` | Bavul etiketi ve sunulan PNR eşleşti (tek yolcu ya da aynı PNR altındaki yolcular) | Yeşil, ses yok | Yaşam döngüsü kapanır | "Çıkış doğrulandı." |
| `REVIEW_MISMATCH` | Bavul etiketi ile sunulan PNR uyuşmadı | Sessiz; kapıda ışıklı/sesli alarm yok | Güvenlik ekranına **inceleme kaydı** (bavul, kapı, zaman) | Sahibine: "Bir bagajınızla ilgili beklenmedik bir çıkış kaydı algılandı. Lütfen bagaj hizmetleriyle iletişime geçin." |
| `REVIEW_OVERDUE` | Banttan alındıktan sonra tanımlı sürede hiçbir kapıdan okunmadı | Yok | Operasyon ekranına "çıkış okunmadı" kaydı (etiket sorunu, salonda unutulma ya da okuma kaçırma olabilir) | Yok |

Yolcuya hiçbir durumda "izinsiz çıkarılıyor" gibi müdahaleye teşvik eden bir mesaj gönderilmez. Alarm sonucu personel inceler.

---

## 7. Veritabanı modeli (PostgreSQL)

```sql
CREATE TABLE flights (
  flight_id            VARCHAR(40) PRIMARY KEY,       -- örn. ZZ0124-20261022
  airline_code         VARCHAR(3)  NOT NULL,
  flight_number        VARCHAR(10) NOT NULL,
  flight_date          DATE        NOT NULL,
  aircraft_type        VARCHAR(10) NOT NULL,
  wide_body            BOOLEAN     NOT NULL DEFAULT FALSE,
  origin_airport       CHAR(3)     NOT NULL,
  destination_airport  CHAR(3)     NOT NULL,
  is_international     BOOLEAN     NOT NULL,
  parking_stand        VARCHAR(20),
  assigned_carousel    VARCHAR(20),
  ground_handler       VARCHAR(40),
  on_block_utc         TIMESTAMPTZ,
  total_bags           INT         NOT NULL DEFAULT 0,
  sla_first_bag_sec    INT         NOT NULL DEFAULT 900,   -- demo varsayımı
  sla_last_bag_sec     INT         NOT NULL DEFAULT 1800,  -- demo varsayımı
  first_bag_actual_sec INT,
  last_bag_actual_sec  INT,
  UNIQUE (flight_number, flight_date)
);

CREATE TABLE passengers (
  passenger_id  UUID PRIMARY KEY,
  flight_id     VARCHAR(40) NOT NULL REFERENCES flights(flight_id),
  lookup_key    BYTEA       NOT NULL,   -- HMAC-SHA256(gizli_anahtar, upper(pnr) || '|' || normalize(soyad))
  display_name  VARCHAR(64),            -- demoda takma ad; gerçek kurulumda boş
  cabin_class   CHAR(1)     NOT NULL,
  UNIQUE (flight_id, lookup_key)
);

CREATE TABLE bags (
  bag_id          UUID PRIMARY KEY,
  bag_tag_number  CHAR(10)    NOT NULL,
  flight_id       VARCHAR(40) NOT NULL REFERENCES flights(flight_id),
  passenger_id    UUID REFERENCES passengers(passenger_id),
  weight_kg       NUMERIC(5,2),
  current_state   VARCHAR(32) NOT NULL,
  state_version   INT         NOT NULL DEFAULT 0,
  eta_p10_utc     TIMESTAMPTZ,
  eta_p50_utc     TIMESTAMPTZ,
  eta_p90_utc     TIMESTAMPTZ,
  exit_cleared    BOOLEAN     NOT NULL DEFAULT FALSE,
  updated_at_utc  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (bag_tag_number, flight_id)    -- etiket numaraları zamanla yeniden kullanılabilir
);

CREATE TABLE baggage_events (
  event_id         UUID PRIMARY KEY,
  idempotency_key  VARCHAR(160) NOT NULL UNIQUE,   -- tekrar gelen mesajı reddeder
  bag_id           UUID NOT NULL REFERENCES bags(bag_id),
  message_type     VARCHAR(10)  NOT NULL,
  from_state       VARCHAR(32),
  to_state         VARCHAR(32)  NOT NULL,
  outcome          VARCHAR(16)  NOT NULL,          -- APPLIED, IGNORED_STALE, ANOMALY
  scanner_id       VARCHAR(32),
  raw_text         TEXT,
  payload          JSONB        NOT NULL,
  occurred_at_utc  TIMESTAMPTZ  NOT NULL,
  received_at_utc  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE outbox_messages (
  id               UUID PRIMARY KEY,
  topic            VARCHAR(64) NOT NULL,
  partition_key    VARCHAR(40) NOT NULL,
  payload          JSONB       NOT NULL,
  created_at_utc   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  attempts         INT         NOT NULL DEFAULT 0,
  processed_at_utc TIMESTAMPTZ
);
CREATE INDEX idx_outbox_pending ON outbox_messages(created_at_utc) WHERE processed_at_utc IS NULL;

CREATE TABLE exit_deadlines (
  bag_id      UUID PRIMARY KEY REFERENCES bags(bag_id),
  due_at_utc  TIMESTAMPTZ NOT NULL,
  status      VARCHAR(12) NOT NULL DEFAULT 'PENDING'    -- PENDING, CLEARED, OVERDUE
);
CREATE INDEX idx_exit_deadlines_due ON exit_deadlines(due_at_utc) WHERE status = 'PENDING';

CREATE TABLE dead_letter_messages (
  id              UUID PRIMARY KEY,
  source          VARCHAR(32) NOT NULL,
  raw_text        TEXT        NOT NULL,
  error           TEXT        NOT NULL,
  received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### 7.1 Zaman aşımı (çıkış süresi) mekanizması

Redis anahtar süresi (keyspace bildirimi) güvenlik alarmı için güvenilir değildir; abone kopmuşsa ya da bildirim kaçarsa alarm kaybolur. Bunun yerine:

1. Bavul `CLAIMED_FROM_BELT` olduğunda `exit_deadlines` tablosuna `due_at_utc` ile kayıt eklenir.
2. Arka plan servisi periyodik olarak `SELECT ... WHERE status='PENDING' AND due_at_utc <= NOW() FOR UPDATE SKIP LOCKED` çalıştırır.
3. Süresi dolanlar için `REVIEW_OVERDUE` olayı outbox üzerinden üretilir ve kayıt `OVERDUE` olur.
4. Çıkış okunursa kayıt `CLEARED` olur.

Servis yeniden başlasa bile bekleyen kayıtlar kaybolmaz.

### 7.2 Redis (Faz 3 ve sonrası, isteğe bağlı)

Yalnızca okuma önbelleği: uçuş bazlı sayaçlar (`flight:stats:{flightId}`) ve anlık bavul durumu (`bag:state:{bagTag}`, TTL 12 saat). **Doğruluk kaynağı ya da alarm mekanizması değildir.**

---

## 8. Değerlendirme yöntemi (Faz 2)

Tahminin değeri, ölçülebilir bir karşılaştırmayla gösterilir.

### 8.1 Taban çizgisi

Uçak boyutu sınıfına (dar gövde / geniş gövde) göre ilk bavul süresinin ve ilk-son bavul farkının medyanı (`baseline-median`). Model bu taban çizgisini geçemezse özetlenen sonuç bu şekilde yazılır.

### 8.2 Metrikler

| Metrik | Açıklama | Örnek hedef |
| --- | --- | --- |
| P50 ortalama mutlak hata | Medyan tahmin ile gerçek süre farkı (dakika) | Taban çizgisinden düşük |
| Pinball (kantil) kaybı | P10/P50/P90 için | Taban çizgisinden düşük |
| Aralık kapsaması | Gerçek sürenin [P10, P90] içinde kalma oranı | Yaklaşık %80 (örn. %75–85) |
| Geç kalma oranı | Bavulun gösterilen üst sınırdan **sonra** gelme oranı | Düşük; yolcu için en maliyetli hata |
| Aralık genişliği | Ortalama P90−P10 | Küçük ama kapsamayı bozmadan |

### 8.3 Yöntem

1. Simülatör sabit tohum (`seed`) ile çalıştırılır; veri `data/synthetic/` altına CSV olarak yazılır.
2. Eğitim/test ayrımı **uçuş bazında** yapılır (aynı uçuşun bavulları iki kümeye bölünmez).
3. Model **rejim A** (normal operasyon) ile eğitilir; **rejim B** (yoğun saat, yağmur, farklı yer hizmetleri parametreleri) ile test edilir. Dağılım kaymasında metriklerin ne kadar bozulduğu raporlanır.
4. Özellik önemleri ve "simülasyona özgü özellikleri çıkarınca" sonuç değişimi (ablasyon) raporlanır.
5. Simülatör çıktısı, kamuya açık referanslarla (Prag, Hong Kong, BCAS hedefleri) büyüklük mertebesi açısından **karşılaştırılır**; bu bir makullük kontrolüdür, kalibrasyon değildir. Parametreler bu verilere göre ayarlanmadı ve gerçek havalimanı verisiyle doğrulanmadı. Kaynaklar ve sonuçlar `docs/EVALUATION.md` dosyasındadır.

Sonuçlar `docs/EVALUATION.md` dosyasında tekrarlanabilir komutlarla birlikte yayınlanır. Olumsuz sonuçlar (modelin taban çizgisini geçemediği durumlar) da raporlanır.

---

## 9. Güvenlik, gizlilik ve tehdit modeli

Bu bölüm Faz 1'den itibaren tasarım kararlarına yön verir. Çalışan kodun güncel durumu (önlem, test, açık kalanlar) `docs/THREAT_MODEL.md` ve `docs/KVKK.md`'dedir.

### 9.1 STRIDE özeti

| Tehdit | Örnek | Önlem |
| --- | --- | --- |
| **Spoofing** | Sahte kaynaktan sahte bavul olayı göndermek | Kaynak başına API anahtarı ya da imzalı istek (HMAC), kaynak adı olayda saklanır |
| **Tampering** | Olay günlüğünün değiştirilmesi | Günlük yalnızca ekleme (append-only), uygulama hesabında UPDATE/DELETE yetkisi yok |
| **Repudiation** | "Bu olayı ben göndermedim" | Olayın kaynağı, alınma zamanı ve `CorrelationId` kayıtlı |
| **Information disclosure** | PNR + soyad sorgusunu tarayarak başka yolcunun bilgisine ulaşmak | Hız sınırı, tekdüze hata yanıtı, kısa ömürlü oturum jetonu, yanıtta ad ve tam PNR yok |
| **Denial of service** | Alma API'sine yoğun mesaj yağdırmak | Hız sınırı, geri basınç (backpressure), ayrıştırılamayanlar DLQ'ya |
| **Elevation of privilege** | Yolcu jetonuyla operasyon uçlarına erişmek | Rol tabanlı yetkilendirme (yolcu / operasyon / güvenlik), uç başına politika |

### 9.2 Gizlilik ve KVKK notu

- Demoda **tüm veri sahtedir**; gerçek isim, PNR ya da kurum adı kullanılmaz.
- Gerçek kullanımda veri azaltma ilkesi: yolcu adı/soyadı saklanmaz, yalnızca `lookup_key` (HMAC özeti) tutulur.
- Saklama süresi tanımlı olmalıdır (örnek politika: teslimden sonra kişisel alanlar kısa sürede silinir). Süre, kurumun politikasına bırakılır.
- Aydınlatma metni ve açık rıza gereksinimi gerçek kullanımda kuruma ve hukuk danışmanlığına tabidir. Bu doküman hukuki danışmanlık değildir.

### 9.3 Çıkış doğrulamaya özgü riskler

- Etiket sökülmesi ve etiket değişimi tek bir okuyucuyla güvenilir tespit edilemez; sistem yalnızca **inceleme kaydı** üretir, sonuç çıkarmaz.
- Telefon–bavul eşleşmesi kalabalık koridorda hatalı olabilir; yanlış alarm oranı Faz 5 ölçümüyle belirlenir.
- Bavul sahibi yerine başkası alıyor olabilir (aile üyesi, şoför); aynı PNR altındaki yolcular için ortak çıkış izni kuralı vardır, diğer durumlar insan incelemesine gider.

---

## 10. Test ve CI stratejisi

| Katman | Kapsam |
| --- | --- |
| **Birim testleri** | Type B ayrıştırıcı (geçerli ve **bozuk** girdiler: eksik alan, bozuk tarih, kısa etiket, çok uzun satır), durum makinesi geçiş tablosu, idempotency, sırasız olay |
| **Entegrasyon testleri** | Testcontainers ile gerçek PostgreSQL: outbox yazımı, zaman aşımı servisi, çift mesaj ve eşzamanlılık |
| **ML testleri** | Veri sızıntısı kontrolü (uçuş bazlı ayrım), kapsama metriğinin hesabı, sabit tohumla yeniden üretilebilirlik |
| **Güvenlik testleri** | Hız sınırı, yetki ihlali denemeleri, hatalı imzalı istek |
| **Uçtan uca** | Simülatörle bir uçuşun tüm yaşam döngüsü + yolcu sayfasında doğrulama |
| **CI** | GitHub Actions: derleme, birim ve entegrasyon testleri, kod biçimi denetimi, Docker imajı derleme |

---

## 11. Antalya (AYT) senaryoları

Bunlar **modelleme varsayımlarıdır**, ölçüm değildir; gerçek operasyon yetkilileriyle konuşunca güncellenmeleri beklenir. Havayolu kurgusunda AYT, yüksek hacimli ve yaz yoğunluklu bir varış istasyonudur; çıkış doğrulamasıyla ilgili satırlar yalnızca havalimanı ExitGuard'ı kurarsa geçerlidir.

| Senaryo | Açıklama | Neden önemli |
| --- | --- | --- |
| Dış hat varışı | Bavul teslimi pasaport/gümrük sonrası akışla bağlantılı; çıkış doğrulaması anlamlılığı daha düşük | Çıkış doğrulama modülünün önceliği |
| İç hat varışı | Doğrudan çıkış; çıkış doğrulaması daha anlamlı | ExitGuard için ana senaryo |
| Yoğun sezon dalgası | Kısa aralıklarla art arda gelen uçuşlar aynı bant grubunu paylaşır | `arrivalsLast30Min` özelliğini ve bant çakışmasını test eder |
| Yağmurlu ve gecikmeli operasyon | Apron süreleri uzar | Dağılım kayması testi (rejim B) |
| Eksik bavul | Uçuşun bavulları bitti, bir bavul gelmedi | `MISSING_AT_ARRIVAL` akışı |
| Aktarma bavulu | Başka uçuştan gelen bavul | İki bağlı satır ve aktarma zinciri (W10a) |

---

## 12. Proje klasör yapısı (monorepo)

```
aerobag-platform/
├── .github/workflows/ci.yml
├── docker-compose.yml                # PostgreSQL, Seq, API, UI (Faz 2: ML; Faz 3: broker, Redis)
├── docs/
│   ├── ARCHITECTURE.md               # Bu doküman
│   ├── EVALUATION.md                 # Değerlendirme raporu (Faz 2)
│   ├── THREAT_MODEL.md               # Tehdit modeli (Faz 4)
│   └── adr/                          # Mimari karar kayıtları
├── data/
│   └── synthetic/                    # Simülatörün ürettiği CSV (git dışı ya da küçük örnek)
├── src/
│   ├── AeroBag.Core/
│   │   ├── AeroBag.Domain/           # Varlıklar, durum makinesi, değer nesneleri
│   │   ├── AeroBag.Application/      # Type B ayrıştırıcı, SLA motoru, ExitGuard kuralları
│   │   ├── AeroBag.Infrastructure/   # PostgreSQL (EF Core), outbox, zaman aşımı servisi
│   │   ├── AeroBag.Api/              # REST uçları, SignalR hub'ları, yetkilendirme
│   │   └── AeroBag.Simulator/        # Tek simülatör: canlı akış + veri seti dışa aktarımı
│   ├── aerobag-ml/                   # Faz 2: Python FastAPI + LightGBM
│   │   ├── app/
│   │   │   ├── main.py               # /v1/predict
│   │   │   ├── training.py           # İki aşamalı kantil model eğitimi
│   │   │   └── evaluation.py         # Metrikler ve rejim kayması testi
│   │   ├── requirements.txt
│   │   └── Dockerfile
│   └── aerobag-ui/                   # React + TypeScript + Tailwind
│       └── src/
│           ├── pages/PassengerTrack/ # PNR + soyad ile takip sayfası
│           ├── pages/OpsControlTower/# Bant, uçuş ve SLA panosu
│           └── services/signalr.ts
├── tests/
│   ├── AeroBag.UnitTests/
│   ├── AeroBag.IntegrationTests/
│   └── aerobag-ml-tests/
└── hardware-edge-demo/               # Faz 5, isteğe bağlı: Raspberry Pi + RFID
```

---

## 13. Sınırlamalar

- Tüm sonuçlar simülasyon verisine dayanır; gerçek veriyle doğrulanmamıştır.
- Type B mesajları basitleştirilmiştir; gerçek sistemlerin farklı alan düzenleri ve uç durumları vardır.
- Kantil modellerin kapsama oranı, simülatörün dağılımına bağlıdır.
- Çıkış doğrulama modülü, donanımla sahada ölçülmeden güvenilir kabul edilmez.
- Doküman hukuki, ticari ya da operasyonel bir garanti vermez.

---

## 14. v1 → v2 değişiklik günlüğü

| Konu | v1 | v2 | Gerekçe |
| --- | --- | --- | --- |
| Mimari kapsam | Mikroservis + Kafka + gRPC + Redis ilk günden | Modüler monolit + Postgres outbox; Kafka, gRPC ve Redis sonraki fazlarda | Bitirilebilirlik ve erken gösterilebilir çıktı |
| Simülatör | Canlı akış için .NET, eğitim için ayrı Python üretici | Tek .NET simülatör, veri seti dışa aktarımı | İki üreticinin birbirinden sapması riski |
| Standart iddiası | "IATA BIX ile tam uyumlu" | "Type B ve Resolution 753'ten esinlenen basitleştirilmiş şema" | Doğrulanamayan uyumluluk iddiasından kaçınma |
| Atıf | Belirli bir kurumun mühendislik ekibine atıf | Kaldırıldı | Doğrulanabilir kaynak yok |
| ETA çıktısı | Saniye hassasiyeti, tek sayı | Dakika düzeyi, P10/P50/P90 aralık | Sentetik veride sahte kesinlik üretmemek |
| Model | Tek LightGBM regresyon | İki aşamalı kantil model (ilk bavul + yükleme süresi) | Münih çalışmasıyla hizalı, ölçülebilir |
| Özellik seti | Hava, vardiya yük faktörü, apron mesafesi | Gerçek operatörlerin sahip olabileceği özelliklere öncelik; simülasyona özgü olanlar etiketli | Entegrasyon inandırıcılığı |
| Çıkış güvenliği | 95 dB siren, "izinsiz çıkarılıyor" bildirimi | Sessiz inceleme kaydı, yolcuya müdahaleye teşvik etmeyen mesaj, Faz 4'e ertelendi | Yanlış alarm ve güvenlik riski |
| Zaman aşımı | Redis anahtar süresi + keyspace bildirimi | Postgres `exit_deadlines` + `SKIP LOCKED` işçi | Alarmın kaybolmaması |
| Anahtar | `bag_tag_number` birincil anahtar | Yapay anahtar + `(bag_tag_number, flight_id)` tekil kısıtı, `idempotency_key` | Etiket yeniden kullanımı, tekrar gelen mesajlar |
| Kişisel veri | Düz metin ad, soyad, koltuk; gerçek ad örnekte | HMAC `lookup_key`, kurgusal örnekler | Gizlilik ve repo güvenliği |
| Gerçek firma adları | Gerçek yer hizmetleri ve havayolu adları | Kurgusal `HANDLER_A`, `ZZ` | Kuruma "ihlal" gibi okunmasın |
| Eklenen bölümler | Yok | Durum istisnaları, değerlendirme, tehdit modeli, test/CI, AYT senaryoları | Eksik alanlar |

### v2 → v2.1

| Konu | v2 | v2.1 | Gerekçe |
| --- | --- | --- | --- |
| .NET sürümü | .NET 9 / ASP.NET Core 9 | .NET 10 LTS / ASP.NET Core 10 | .NET 8 ve 9, 10 Kasım 2026'da destek dışı kalıyor; .NET 10 Kasım 2028'e kadar destekli |

### v2.1 → v2.2

| Konu | v2.1 | v2.2 | Gerekçe |
| --- | --- | --- | --- |
| Konumlandırma | Havayolu, yer hizmetleri ve havalimanı için ortak platform | Havayolunun tek istasyon modülü; yer hizmetleri ve havalimanı veri sağlayan taraf | Bavulun sorumluluğu ve yolcu verisi havayolunda; IATA 753 takibi havayollarından ister |
| Çıkış doğrulama | Faz 4'ün ana çıktısı | İsteğe bağlı havalimanı modülü | Kapıya cihaz ve personel koymak havalimanının kararıdır (REMEDIATION_PLAN, W11) |
| Amaç | Takip, aralık tahmini, SLA, çıkış doğrulama | Kayıp ve bağlantıyı kaçıran bavul tespiti eklendi | Faz 6'da yapıldı (W4, W10a) |

---

## 15. Referanslar

- De Falco, Gasco, Baruta, Wulkow: *Quantile-based Machine Learning Predictions of Luggage Arrival Time at Airport Reclaim Areas*, SESAR Innovation Days 2025, makale 2025-060. https://www.sesarju.eu/sites/default/files/documents/sid/2025/papers/SIDs_2025_paper_60-final.pdf
- IATA: *Baggage Tracking, Resolution 753 Implementation Guide*. https://Www.Iata.org/contentassets/5316edd3aafb4866876e37883211cfc4/baggage_tracking_implementation_guide.pdf
- Schiphol: *Real-time information about baggage waiting times with AI-driven capability* (2024). https://www.futuretravelexperience.com/2024/04/schiphol-gives-travellers-real-time-information-about-baggage-waiting-times-with-new-ai-driven-capability/
- Bükreş Henri Coandă Havalimanı için EUROCONTROL "Prediction of Luggage Delivery Time" uygulaması (2026). https://rhn.ro/en/ai-system-for-estimating-luggage-delivery-times-at-otopeni/
- SITA: *Baggage Management*. https://www.sita.aero/solutions-and-services/solutions/baggage-management
